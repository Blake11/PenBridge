using System.Buffers;
using System.Text;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using PenBridge.Core.Settings;
using SharpFuzz;

namespace PenBridge.Fuzz;

/// <summary>A contract the parser broke. Any exception that escapes a target is a finding.</summary>
internal sealed class ContractViolationException(string message) : Exception(message);

/// <summary>One fuzz target: a name, the code under test, and the largest input worth generating.</summary>
internal sealed record FuzzTarget(string Name, ReadOnlySpanAction Run, int MaxLength, string Description);

/// <summary>
/// The fuzz targets. Each one feeds untrusted bytes to a parser and checks its documented contract:
/// <list type="bullet">
/// <item>only the documented exceptions (<see cref="ProtocolException"/> from the frame layer) are caught here;
/// any other exception escapes and is a finding;</item>
/// <item>Try* parsers return false on bad input and never throw;</item>
/// <item>whatever a parser accepts, its writer can encode, and decoding that again gives the same bytes;</item>
/// <item>a hang is caught by the driver's watchdog.</item>
/// </list>
/// </summary>
internal static class Targets
{
    public const string Control = "control";
    public const string Payload = "payload";
    public const string Datagram = "datagram";
    public const string Qr = "qr";
    public const string Pairing = "pairing";

    /// <summary>Session id the datagram seeds and the receiving cipher share.</summary>
    internal const uint SessionId = 0x0A0B0C0D;

    /// <summary>A fixed K_in: the fuzzer's "paired" phone and the Mac use it in the sealed half of the datagram target.</summary>
    internal static readonly byte[] InputKey = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    private static readonly DisplayListEntry[] Displays =
    [
        new("1", "Built-in", true, 1512, 982),
        new("2", "External", false, 2560, 1440),
    ];

    // The raw receiver has a key no seed was sealed with: nothing may open there (that would be a forged GCM tag),
    // so its replay window stays at 0 and every input reaches the tag check. The sealed pair simulates a paired
    // phone that sends garbage plaintext.
    private static readonly InputCipher RawReceiver = new([.. Enumerable.Repeat((byte)0xEE, 32)], SessionId);
    private static readonly InputCipher SealSender = new(InputKey, SessionId);
    private static readonly InputCipher SealReceiver = new(InputKey, SessionId);
    private static ulong s_sealSequence;

    public static IReadOnlyList<FuzzTarget> All { get; } =
    [
        new(Control, RunControl, 4096, "control channel: frame decoder, stream reader, every message parser, settings apply"),
        new(Payload, RunPayload, InputPayload.MaxSize + 32, "input payload after decryption (UDP and InputFrame)"),
        new(Datagram, RunDatagram, InputDatagram.MaxSize + 32, "UDP datagram header, split and AEAD open before decryption"),
        new(Qr, RunQr, PairingQrPayload.MaxLength + 64, "pairing QR text, Bonjour TXT values, protocol version text"),
        new(Pairing, PairingTarget.Run, PairingTarget.MaxLength, "J-PAKE Round1/Round2/confirm blobs from the peer, before pairing completes"),
    ];

    public static FuzzTarget? Find(string name)
    {
        foreach (FuzzTarget target in All)
        {
            if (target.Name == name)
            {
                return target;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- control

    private static void RunControl(ReadOnlySpan<byte> data)
    {
        // 1. The buffer codec, as a stream of frames. Only ControlFrame.TryRead may throw (ProtocolException); the
        // message parsers and the round trip run outside the catch, so an exception from them is a finding.
        ReadOnlySpan<byte> rest = data;
        while (true)
        {
            ControlFrameStatus status;
            ControlMessageType type;
            ReadOnlySpan<byte> payload;
            int consumed;
            try
            {
                status = ControlFrame.TryRead(rest, out type, out payload, out consumed);
            }
            catch (ProtocolException e)
            {
                CheckCode(e);
                break;
            }

            if (status == ControlFrameStatus.NeedMoreData)
            {
                Check(consumed == 0, "NeedMoreData consumed bytes");
                break;
            }

            Check(consumed >= ControlFrame.HeaderSize && consumed <= rest.Length, "consumed out of range");
            Check(payload.Length == consumed - ControlFrame.HeaderSize, "payload length != consumed - header");
            Check(payload.Length <= ControlFrame.MaxPayloadSize, "payload over the cap");
            if (status == ControlFrameStatus.Frame)
            {
                Check(ControlMessageTypes.IsKnown((byte)type), "Frame status for an unknown type");
                Dispatch(type, payload);
            }
            else
            {
                Check(ControlMessageTypes.IsReserved((byte)type), "Ignored status for a non-reserved type");
            }

            rest = rest[consumed..];
        }

        // 2. The stream reader over the same bytes, parsing every frame the way the client and server do (Read<T>).
        // ReadAsync and Read<T> both document ProtocolException; each gets its own catch.
        using var reader = new ControlFrameReader(new MemoryStream(data.ToArray(), writable: false));
        while (true)
        {
            ReceivedControlFrame? next;
            try
            {
                ValueTask<ReceivedControlFrame?> pending = reader.ReadAsync();
                next = pending.IsCompleted
                    ? pending.Result
                    : throw new ContractViolationException("ReadAsync over a MemoryStream did not complete synchronously");
            }
            catch (ProtocolException e)
            {
                CheckCode(e);
                break;
            }

            if (next is not { } frame)
            {
                break;
            }

            try
            {
                ReadTyped(frame);
            }
            catch (ProtocolException e)
            {
                CheckCode(e);
                break;
            }
        }
    }

    private static void Dispatch(ControlMessageType type, ReadOnlySpan<byte> p)
    {
        switch (type)
        {
            case ControlMessageType.Hello:
                if (HelloMessage.TryRead(p, out HelloMessage hello))
                {
                    _ = hello.Version.IsCompatibleWith(ProtocolVersion.Current);
                    Check(hello.DeviceName is not null, "null device name");
                    RoundTrip(hello);
                }

                break;
            case ControlMessageType.HelloAck:
                Accept<HelloAckMessage>(p);
                break;
            case ControlMessageType.ClientPairRound1:
                Accept<ClientPairRound1Message>(p);
                break;
            case ControlMessageType.ServerPairRound1:
                Accept<ServerPairRound1Message>(p);
                break;
            case ControlMessageType.ClientPairRound2:
                Accept<ClientPairRound2Message>(p);
                break;
            case ControlMessageType.ServerPairRound2:
                Accept<ServerPairRound2Message>(p);
                break;
            case ControlMessageType.ClientPairConfirm:
                Accept<ClientPairConfirmMessage>(p);
                break;
            case ControlMessageType.ServerPairConfirm:
                Accept<ServerPairConfirmMessage>(p);
                break;
            case ControlMessageType.PairResult:
                Accept<PairResultMessage>(p);
                break;
            case ControlMessageType.Auth:
                Accept<AuthMessage>(p);
                break;
            case ControlMessageType.SessionStart:
                Accept<SessionStartMessage>(p);
                break;
            case ControlMessageType.SurfaceInfo:
                Accept<SurfaceInfoMessage>(p);
                break;
            case ControlMessageType.UdpAck:
                Accept<UdpAckMessage>(p);
                break;
            case ControlMessageType.InputFrame:
                if (InputFrameMessage.TryRead(p, out InputFrameMessage frame))
                {
                    Check(InputPayload.TryRead(p, out _), "InputFrame accepted a payload InputPayload rejects");
                    RoundTrip(frame);
                }
                else
                {
                    Check(!InputPayload.TryRead(p, out _), "InputFrame rejected a payload InputPayload accepts");
                }

                break;
            case ControlMessageType.Ping:
                Accept<PingMessage>(p);
                break;
            case ControlMessageType.Pong:
                Accept<PongMessage>(p);
                break;
            case ControlMessageType.SettingsSnapshot:
                if (SettingsSnapshotMessage.TryRead(p, out SettingsSnapshotMessage snapshot))
                {
                    CheckEntries(snapshot.Entries);

                    // The phone side: values from the Mac, then the input settings they describe.
                    MacSettingValues.FromEntries(snapshot.Entries).ToInputSettings().Validate();
                    RoundTrip(snapshot);
                }

                break;
            case ControlMessageType.SettingsUpdate:
                if (SettingsUpdateMessage.TryRead(p, out SettingsUpdateMessage update))
                {
                    CheckEntries(update.Entries);

                    // The Mac side: apply the phone's update to the current values; the result must stay valid.
                    var values = new MacSettingValues();
                    _ = values.ApplyUpdate(update.Entries, Displays);
                    values.ToInputSettings().Validate();
                    foreach (SettingEntry entry in update.Entries)
                    {
                        _ = values.Clone().TryApply(entry, Displays);
                    }

                    // Documented: a wire count of 1+ can decode to zero entries (all skipped); the writer then emits
                    // count 0, which the reader rejects. Only non-empty updates must round-trip.
                    if (update.Entries.Count > 0)
                    {
                        RoundTrip(update);
                    }
                }

                break;
            case ControlMessageType.DisplayList:
                if (DisplayListMessage.TryRead(p, out DisplayListMessage displays))
                {
                    Check(displays.Displays.Count <= SettingsWire.MaxDisplays, "display count over the cap");
                    _ = new MacSettingValues().ApplyUpdate(new MacSettingValues().ToEntries(), displays.Displays);
                    RoundTrip(displays);
                }

                break;
            case ControlMessageType.ActiveArea:
                if (ActiveAreaMessage.TryRead(p, out ActiveAreaMessage area))
                {
                    _ = area.ToNormalizedRect();
                    RoundTrip(area);
                }

                break;
            case ControlMessageType.Bye:
                Accept<ByeMessage>(p);
                break;
            case ControlMessageType.Busy:
                Accept<BusyMessage>(p);
                break;
            case ControlMessageType.Error:
                Accept<ErrorMessage>(p);
                break;
            default:
                throw new ContractViolationException($"Frame status for unhandled type 0x{(byte)type:X2}");
        }
    }

    /// <summary>The <see cref="ReceivedControlFrame.Read{T}"/> path: only <see cref="ProtocolException"/> may escape.</summary>
    private static void ReadTyped(ReceivedControlFrame frame)
    {
        _ = frame.Type switch
        {
            ControlMessageType.Hello => frame.Read<HelloMessage>().Version.Major,
            ControlMessageType.HelloAck => frame.Read<HelloAckMessage>().Version.Major,
            ControlMessageType.ClientPairRound1 => frame.Read<ClientPairRound1Message>().Round1.Length,
            ControlMessageType.ServerPairRound1 => frame.Read<ServerPairRound1Message>().Round1.Length,
            ControlMessageType.ClientPairRound2 => frame.Read<ClientPairRound2Message>().Round2.Length,
            ControlMessageType.ServerPairRound2 => frame.Read<ServerPairRound2Message>().Round2.Length,
            ControlMessageType.ClientPairConfirm => frame.Read<ClientPairConfirmMessage>().Mac.Length,
            ControlMessageType.ServerPairConfirm => frame.Read<ServerPairConfirmMessage>().Mac.Length,
            ControlMessageType.PairResult => (int)frame.Read<PairResultMessage>().Status,
            ControlMessageType.Auth => frame.Read<AuthMessage>().Hmac.Length,
            ControlMessageType.SessionStart => frame.Read<SessionStartMessage>().UdpPort,
            ControlMessageType.SurfaceInfo => frame.Read<SurfaceInfoMessage>().WidthPx,
            ControlMessageType.UdpAck => (int)frame.Read<UdpAckMessage>().HighestSequence,
            ControlMessageType.InputFrame => frame.Read<InputFrameMessage>().Payload.Touches.Length,
            ControlMessageType.Ping => (int)frame.Read<PingMessage>().TimestampUs,
            ControlMessageType.Pong => (int)frame.Read<PongMessage>().TimestampUs,
            ControlMessageType.SettingsSnapshot => frame.Read<SettingsSnapshotMessage>().Entries.Count,
            ControlMessageType.SettingsUpdate => frame.Read<SettingsUpdateMessage>().Entries.Count,
            ControlMessageType.DisplayList => frame.Read<DisplayListMessage>().Displays.Count,
            ControlMessageType.ActiveArea => frame.Read<ActiveAreaMessage>().Width,
            ControlMessageType.Bye => (int)frame.Read<ByeMessage>().Reason,
            ControlMessageType.Busy => Busy(frame),
            ControlMessageType.Error => (int)frame.Read<ErrorMessage>().Code,
            _ => throw new ContractViolationException($"Reader returned unhandled type 0x{(byte)frame.Type:X2}"),
        };
    }

    private static int Busy(ReceivedControlFrame frame)
    {
        _ = frame.Read<BusyMessage>();
        return 0;
    }

    private static void Accept<T>(ReadOnlySpan<byte> payload)
        where T : IControlMessage<T>
    {
        if (T.TryRead(payload, out T message))
        {
            RoundTrip(message);
        }
    }

    /// <summary>
    /// What a parser accepts, the writer can encode; decoding that encoding succeeds and re-encodes to the same bytes.
    /// (Not byte-equal to the input: parsers ignore trailing bytes and skip settings entries they can't decode.)
    /// </summary>
    private static void RoundTrip<T>(T message)
        where T : IControlMessage<T>
    {
        int size = ControlFrame.GetFrameSize(message);
        byte[] first = ArrayPool<byte>.Shared.Rent(size);
        byte[] second = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            Check(ControlFrame.Write(first, message) == size, "Write returned a different size");
            Check(ControlFrame.TryRead(first.AsSpan(0, size), out ControlMessageType type, out ReadOnlySpan<byte> payload, out int consumed) == ControlFrameStatus.Frame, "re-encoded frame not decodable");
            Check(type == T.Type && consumed == size, "re-encoded frame header mismatch");
            Check(T.TryRead(payload, out T again), $"{typeof(T).Name}: parser rejects its own writer's output");
            Check(ControlFrame.GetFrameSize(again) == size, $"{typeof(T).Name}: size changed on the second round trip");
            ControlFrame.Write(second, again);
            Check(first.AsSpan(0, size).SequenceEqual(second.AsSpan(0, size)), $"{typeof(T).Name}: encoding not stable across a round trip");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(first);
            ArrayPool<byte>.Shared.Return(second);
        }
    }

    private static void CheckEntries(IReadOnlyList<SettingEntry> entries)
    {
        Check(entries.Count <= SettingsWire.MaxEntries, "entry count over the cap");
        foreach (SettingEntry entry in entries)
        {
            Check(entry.Type is >= SettingType.Bool and <= SettingType.Rect, "entry of unknown type kept");
            Check(entry.Size >= SettingEntry.HeaderSize, "entry size below the header");
            _ = entry.FormatValue();
            _ = entry.ToString();
        }
    }

    private static void CheckCode(ProtocolException e) =>
        Check(Enum.IsDefined(e.Code), "ProtocolException with an undefined error code");

    // ---------------------------------------------------------------- payload

    /// <summary>The plaintext payload parser, as the server runs it after a datagram opened (and for InputFrame).</summary>
    private static void RunPayload(ReadOnlySpan<byte> data)
    {
        if (!InputPayload.TryRead(data, out InputPayload payload))
        {
            return;
        }

        CheckPayload(in payload);
        Span<byte> first = stackalloc byte[InputPayload.MaxSize];
        Span<byte> second = stackalloc byte[InputPayload.MaxSize];
        int n = payload.Write(first);
        Check(n <= data.Length, "re-encoded payload longer than the input");
        Check(InputPayload.TryRead(first[..n], out InputPayload again), "payload parser rejects its own writer's output");
        Check(again.Write(second) == n && first[..n].SequenceEqual(second[..n]), "payload encoding not stable across a round trip");
    }

    private static void CheckPayload(in InputPayload payload)
    {
        Check(payload.Touches.Length <= InputPayload.MaxTouches, "touch count over the cap");
        Check(payload.History.Length <= InputPayload.MaxHistory, "history count over the cap");
        if (payload.Pen is { } pen)
        {
            Check(InputSampleCodec.IsValid(pen), "invalid pen sample accepted");
        }

        foreach (PenBridge.Core.Input.PenSample sample in payload.History)
        {
            Check(InputSampleCodec.IsValid(sample), "invalid history sample accepted");
        }
    }

    // ---------------------------------------------------------------- datagram

    /// <summary>
    /// Before decryption: header parse, split and the AEAD open of an unauthenticated datagram (must fail cleanly).
    /// Then the same bytes as the plaintext of a datagram from a paired phone: seal, open, parse.
    /// </summary>
    private static void RunDatagram(ReadOnlySpan<byte> data)
    {
        bool headerOk = InputDatagramHeader.TryRead(data, out InputDatagramHeader header);
        bool split = InputDatagram.TrySplit(data, out InputDatagramHeader splitHeader, out ReadOnlySpan<byte> aad, out ReadOnlySpan<byte> ciphertext, out ReadOnlySpan<byte> tag);
        if (split)
        {
            Check(headerOk && header == splitHeader, "TrySplit accepted a header TryRead rejects");
            Check(data.Length is >= InputDatagram.MinSize and <= InputDatagram.MaxSize, "TrySplit accepted a bad length");
            Check(aad.Length == InputDatagramHeader.Size && tag.Length == InputDatagram.TagSize, "bad AAD or tag slice");
            Check(ciphertext.Length == data.Length - InputDatagramHeader.Size - InputDatagram.TagSize, "bad ciphertext slice");
        }
        else
        {
            Check(splitHeader == default, "TrySplit left a header behind on failure");
        }

        Span<byte> buffer = stackalloc byte[InputDatagram.MaxSize + 64];
        if (data.Length <= buffer.Length)
        {
            data.CopyTo(buffer);
            Check(!RawReceiver.TryOpen(buffer[..data.Length], out int opened) && opened == 0, "an unauthenticated datagram opened");
        }

        // Sealed: the fuzz input is the plaintext a (compromised or buggy) paired phone sends.
        if (data.Length > InputDatagram.MaxPayloadSize)
        {
            return;
        }

        var sealedHeader = new InputDatagramHeader(InputDatagramOptions.None, SessionId, ++s_sealSequence);
        sealedHeader.Write(buffer);
        data.CopyTo(buffer[InputDatagramHeader.Size..]);
        int length = SealSender.Seal(buffer, data.Length);
        Span<byte> datagram = buffer[..length];
        bool receivable = InputDatagram.TrySplit(datagram, out _, out _, out _, out _);
        Check(receivable == data.Length >= InputPayload.MinSize, "TrySplit disagrees with the payload size");
        if (!receivable)
        {
            return;
        }

        Check(SealReceiver.TryOpen(datagram, out int payloadLength) && payloadLength == data.Length, "a sealed datagram did not open");
        Check(datagram.Slice(InputDatagramHeader.Size, payloadLength).SequenceEqual(data), "open returned different plaintext");
        RunPayload(datagram.Slice(InputDatagramHeader.Size, payloadLength));
    }

    // ---------------------------------------------------------------- qr / discovery text

    /// <summary>
    /// Untrusted text: a scanned QR code, and Bonjour TXT values from the LAN (the input split on newlines into
    /// version, fingerprint and name). Accepted values must format and parse back to the same thing.
    /// </summary>
    private static void RunQr(ReadOnlySpan<byte> data)
    {
        string text = Encoding.UTF8.GetString(data);
        if (PairingQrPayload.TryParse(text, out PairingQrPayload? qr))
        {
            Check(qr is not null, "TryParse true with a null payload");
            string formatted = qr!.Format();
            Check(PairingQrPayload.TryParse(formatted, out PairingQrPayload? back), "QR parser rejects its own Format() output");
            Check(back!.Host == qr.Host && back.Port == qr.Port && back.Version == qr.Version, "QR round trip changed a field");
            Check(back.Fingerprint.Span.SequenceEqual(qr.Fingerprint.Span) && back.Token.Span.SequenceEqual(qr.Token.Span), "QR round trip changed a key");
            string token = Base64UrlBytes.Encode(qr.Token.Span);
            Check(qr.Host.Contains(token, StringComparison.Ordinal) || !qr.ToString().Contains(token, StringComparison.Ordinal), "QR ToString leaks the token");
        }
        else
        {
            Check(qr is null, "TryParse false with a payload");
        }

        string[] parts = text.Split('\n', 3);
        string? version = parts.Length > 0 ? parts[0] : null;
        string? fingerprint = parts.Length > 1 ? parts[1] : null;
        string? name = parts.Length > 2 ? parts[2] : null;
        if (BonjourTxtRecord.TryParse(version, fingerprint, name, out BonjourTxtRecord? record))
        {
            Check(BonjourTxtRecord.TryParse(record!.ToEntries(), out BonjourTxtRecord? back), "TXT parser rejects its own entries");
            Check(back!.Version == record.Version && back.Name == record.Name && back.Fingerprint.Span.SequenceEqual(record.Fingerprint.Span), "TXT round trip changed a field");
        }

        if (ProtocolVersion.TryParse(text, out ProtocolVersion parsed))
        {
            Check(ProtocolVersion.TryParse(parsed.ToString(), out ProtocolVersion again) && again == parsed, "version text round trip failed");
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition)
        {
            throw new ContractViolationException(what);
        }
    }
}
