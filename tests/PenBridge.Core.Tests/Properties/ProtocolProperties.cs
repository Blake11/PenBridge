using System.Text;
using CsCheck;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Properties;

/// <summary>
/// Codec properties for every control message with a writer, the input payload and the discovery texts: write then
/// read gives the same message, any truncation is rejected, trailing bytes are ignored, and parsers fed mutated or
/// random bytes only ever return false (anything they accept can be written again unchanged).
/// </summary>
public class ProtocolProperties
{
    /// <summary>Text pieces including multi-byte UTF-8 and a surrogate pair, joined so no pair is ever split.</summary>
    private static readonly Gen<string> Text =
        Gen.OneOfConst("a", "Z", "0", " ", "-", ".", "é", "ß", "漢", "😀", "\u0000", "'", "{0}")
            .Array[0, 40].Select(parts => string.Concat(parts));

    private static readonly Gen<byte[]> Blob = Gen.Frequency(
        (8, Gen.Byte.Array[0, 400]),
        (1, Gen.Byte.Array[1000, 3000]));

    private static readonly Gen<ProtocolVersion> Version = Gen.Select(Gen.Byte, Gen.Byte).Select((a, b) => new ProtocolVersion(a, b));

    private static readonly Gen<PenSample> Pen =
        Gen.Select(Gen.UShort, Gen.UShort, Gen.UShort, Gen.Int[-InputSampleCodec.MaxTilt, InputSampleCodec.MaxTilt], Gen.Int[-InputSampleCodec.MaxTilt, InputSampleCodec.MaxTilt], Gen.Int[0, InputSampleCodec.MaxRotation])
            .SelectMany((x, y, p, tx, ty, rot) => Gen.Select(Gen.OneOfConst(PenTool.Pen, PenTool.Eraser, PenTool.FingerFallback), Gen.Int[0, 3], Gen.Int[0, 3])
                .Select((tool, state, buttons) => new PenSample(x, y, p, (short)tx, (short)ty, (ushort)rot, tool, (PenState)state, (PenButtons)buttons)));

    private static readonly Gen<TouchSample[]> Touches =
        Gen.Byte.ArrayUnique[0, InputPayload.MaxTouches].SelectMany(ids =>
            Gen.Select(Gen.Bool, Gen.UShort, Gen.UShort).Array[ids.Length]
                .Select(rest =>
                {
                    var touches = new TouchSample[ids.Length];
                    for (int i = 0; i < ids.Length; i++)
                    {
                        touches[i] = new TouchSample(ids[i], rest[i].Item1, rest[i].Item2, rest[i].Item3);
                    }

                    return touches;
                }));

    private static readonly Gen<InputPayload> Payload =
        Gen.Select(Gen.UInt, Gen.Bool, Pen, Touches, Pen.Array[0, InputPayload.MaxHistory])
            .Select((time, hasPen, pen, touches, history) =>
            {
                var payload = new InputPayload { TimeUs = time, Pen = hasPen ? pen : null };
                payload.SetTouches(touches);
                payload.SetHistory(history);
                return payload;
            });

    private static readonly Gen<SettingEntry> Entry = Gen.Select(Gen.UShort, Gen.Int[0, 4]).SelectMany((key, kind) => kind switch
    {
        0 => Gen.Bool.Select(v => SettingEntry.Bool(key, v)),
        1 => Gen.Int.Select(v => SettingEntry.Int(key, v)),
        2 => Gen.Float[-1e6f, 1e6f].Select(v => SettingEntry.Float(key, v)),
        3 => Text.Select(v => SettingEntry.String(key, v)),
        _ => Gen.Select(Gen.Float[0f, 0.9f], Gen.Float[0f, 0.9f])
            .SelectMany((x, y) => Gen.Select(Gen.Float[0.01f, 1f - x], Gen.Float[0.01f, 1f - y])
                .Select((w, h) => SettingEntry.Rect(key, new NormalizedRect(x, y, w, h)))),
    });

    private static readonly Gen<DisplayListEntry> Display =
        Gen.Select(Text.Where(id => id.Length > 0 && id != "all"), Text, Gen.Bool, Gen.Float[1f, 10_000f], Gen.Float[1f, 10_000f])
            .Select((id, name, main, w, h) => new DisplayListEntry(id, name, main, w, h));

    /// <summary>One valid message of every type that has a writer, encoded by its own writer.</summary>
    private static readonly Gen<Encoded> Message = Gen.OneOf(
        Gen.Select(Version, Gen.Byte.Array[16], Text, Gen.UInt).Select((v, id, name, caps) => Encode(new HelloMessage(v, id, name, (ProtocolCapabilities)caps))),
        Gen.Select(Version, Text, Gen.UInt, Gen.Byte.Array[32]).Select((v, name, caps, challenge) => Encode(new HelloAckMessage(v, name, (ProtocolCapabilities)caps, challenge))),
        Gen.Select(Blob, Gen.OneOfConst(PairingSecretKind.Pin, PairingSecretKind.QrToken)).Select((b, kind) => Encode(new ClientPairRound1Message(b, kind))),
        Blob.Select(b => Encode(new ServerPairRound1Message(b))),
        Blob.Select(b => Encode(new ClientPairRound2Message(b))),
        Blob.Select(b => Encode(new ServerPairRound2Message(b))),
        Blob.Select(b => Encode(new ClientPairConfirmMessage(b))),
        Blob.Select(b => Encode(new ServerPairConfirmMessage(b))),
        Gen.Select(Gen.Int[0, 2], Gen.Byte).Select((s, left) => Encode(s switch { 0 => PairResultMessage.Ok(), 1 => PairResultMessage.Failed(left), _ => PairResultMessage.Expired() })),
        Gen.Select(Gen.Byte.Array[16], Gen.Byte.Array[32], Gen.Byte.Array[32]).Select((id, mac, nonce) => Encode(new AuthMessage(id, mac, nonce))),
        Gen.Select(Gen.UShort, Gen.UInt, Gen.Byte.Array[32]).Select((port, id, nonce) => Encode(new SessionStartMessage(port, id, nonce))),
        Gen.Select(Gen.UShort[1, ushort.MaxValue], Gen.UShort[1, ushort.MaxValue], Gen.Float[0.01f, 5000f], Gen.Float[0.01f, 5000f], Gen.OneOfConst(SurfaceOrientation.Portrait, SurfaceOrientation.Landscape), Gen.UInt)
            .Select((w, h, xd, yd, o, caps) => Encode(new SurfaceInfoMessage(w, h, xd, yd, o, (SurfaceCapabilities)caps))),
        Gen.ULong.Select(s => Encode(new UdpAckMessage(s))),
        Payload.Select(p => Encode(new InputFrameMessage(p))),
        Gen.ULong.Select(t => Encode(new PingMessage(t))),
        Gen.ULong.Select(t => Encode(new PongMessage(t))),
        Gen.Const(0).Select(_ => Encode(default(BusyMessage))),
        Gen.Select(Gen.UShort, Text).Select((code, text) => Encode(new ErrorMessage((ProtocolErrorCode)code, text))),
        Gen.Byte.Select(r => Encode(new ByeMessage((ByeReason)r))),
        Entry.Array[0, SettingsWire.MaxEntries].Select(e => Encode(new SettingsSnapshotMessage(e))),
        Entry.Array[1, SettingsWire.MaxEntries].Select(e => Encode(new SettingsUpdateMessage(e))),
        Display.Array[0, SettingsWire.MaxDisplays].Select(d => Encode(new DisplayListMessage(d))),
        Gen.Select(Gen.UShort[0, ActiveAreaMessage.Max - 1], Gen.UShort[0, ActiveAreaMessage.Max - 1])
            .SelectMany((x, y) => Gen.Select(Gen.UShort[1, (ushort)(ActiveAreaMessage.Max - x)], Gen.UShort[1, (ushort)(ActiveAreaMessage.Max - y)])
                .Select((w, h) => Encode(new ActiveAreaMessage(x, y, w, h)))));

    [Fact]
    public void Every_message_reads_back_as_written_and_does_not_alias_its_input()
    {
        Message.Sample(m =>
        {
            byte[] input = [.. m.Payload];
            Assert.True(TryRecode(m.Type, input, out byte[] again, out Action<byte[]>? rewrite), $"{m.Type} rejected its own encoding");
            Assert.Equal(m.Payload, again);

            // Variable-size fields are copied: scribbling over the input doesn't change the parsed message.
            Array.Fill(input, (byte)0xA5);
            byte[] after = new byte[again.Length];
            rewrite!(after);
            Assert.Equal(m.Payload, after);
        }, iter: PropertyGens.CodecIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void Every_message_survives_the_frame_codec()
    {
        Message.Sample(m =>
        {
            var frame = new byte[ControlFrame.HeaderSize + m.Payload.Length + 3];
            ControlFrame.WriteHeader(frame, m.Type, m.Payload.Length);
            m.Payload.CopyTo(frame.AsSpan(ControlFrame.HeaderSize));
            ControlFrameStatus status = ControlFrame.TryRead(frame, out ControlMessageType type, out ReadOnlySpan<byte> payload, out int consumed);
            Assert.Equal(ControlFrameStatus.Frame, status);
            Assert.Equal(m.Type, type);
            Assert.Equal(ControlFrame.HeaderSize + m.Payload.Length, consumed);
            Assert.True(payload.SequenceEqual(m.Payload), "payload differs");

            // Every cut inside the frame needs more data and consumes nothing.
            for (int cut = 0; cut < consumed; cut++)
            {
                Assert.Equal(ControlFrameStatus.NeedMoreData, ControlFrame.TryRead(frame.AsSpan(0, cut), out _, out _, out int none));
                Assert.Equal(0, none);
            }
        }, iter: PropertyGens.CodecIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void Every_truncation_of_a_message_is_rejected()
    {
        Message.Sample(m =>
        {
            for (int cut = 0; cut < m.Payload.Length; cut++)
            {
                Assert.False(TryRecode(m.Type, m.Payload.AsSpan(0, cut), out _, out _), $"{m.Type} accepted {cut} of {m.Payload.Length} bytes");
            }
        }, iter: PropertyGens.CodecIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void Bytes_after_the_known_fields_are_ignored()
    {
        Gen.Select(Message, Gen.Byte.Array[1, 64]).Sample((m, extra) =>
        {
            byte[] longer = [.. m.Payload, .. extra];
            Assert.True(TryRecode(m.Type, longer, out byte[] again, out _), $"{m.Type} rejected {extra.Length} trailing bytes");
            Assert.Equal(m.Payload, again);
        }, iter: PropertyGens.CodecIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void Mutated_messages_are_rejected_or_read_as_something_the_writer_writes_again_unchanged()
    {
        Gen.Select(Message, Gen.Select(Gen.Int[0, int.MaxValue], Gen.Byte).Array[1, 8]).Sample((m, edits) =>
        {
            byte[] bytes = [.. m.Payload];
            if (bytes.Length == 0)
            {
                return;
            }

            foreach ((int at, byte value) in edits)
            {
                bytes[at % bytes.Length] = value;
            }

            if (TryRecode(m.Type, bytes, out byte[] first, out _) && !IsEmptyUpdate(m.Type, first))
            {
                Assert.True(TryRecode(m.Type, first, out byte[] second, out _), $"{m.Type} rejected its own re-encoding");
                Assert.Equal(first, second);
            }
        }, iter: PropertyGens.CodecIterations * 4, threads: PropertyGens.Threads);
    }

    [Fact]
    public void Random_bytes_are_rejected_or_read_as_something_the_writer_writes_again_unchanged()
    {
        Gen.Select(Gen.OneOfConst(MessageTypes), Gen.Byte.Array[0, 300]).Sample((type, bytes) =>
        {
            if (TryRecode(type, bytes, out byte[] first, out _) && !IsEmptyUpdate(type, first))
            {
                Assert.True(TryRecode(type, first, out byte[] second, out _), $"{type} rejected its own re-encoding");
                Assert.Equal(first, second);
            }
        }, iter: PropertyGens.CodecIterations * 4, threads: PropertyGens.Threads);
    }

    [Fact]
    public void Datagram_headers_read_back_as_written()
    {
        Gen.Select(Gen.Byte, Gen.UInt, Gen.ULong).Sample((flags, session, sequence) =>
        {
            var header = new InputDatagramHeader((InputDatagramOptions)flags, session, sequence);
            var bytes = new byte[InputDatagramHeader.Size];
            header.Write(bytes);
            Assert.True(InputDatagramHeader.TryRead(bytes, out InputDatagramHeader back));
            Assert.Equal(header, back);
            for (int cut = 0; cut < bytes.Length; cut++)
            {
                Assert.False(InputDatagramHeader.TryRead(bytes.AsSpan(0, cut), out _));
            }
        }, iter: PropertyGens.CodecIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void Versions_round_trip_through_text_and_bytes()
    {
        Version.Sample(v =>
        {
            Assert.True(ProtocolVersion.TryParse(v.ToString(), out ProtocolVersion parsed));
            Assert.Equal(v, parsed);
            Span<byte> bytes = stackalloc byte[ProtocolVersion.Size];
            v.Write(bytes);
            Assert.True(ProtocolVersion.TryRead(bytes, out ProtocolVersion read));
            Assert.Equal(v, read);
        }, iter: PropertyGens.CodecIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void Qr_payloads_round_trip_and_their_truncations_never_throw()
    {
        Gen<string> host = Gen.OneOf(
            Gen.Select(Gen.Byte, Gen.Byte, Gen.Byte, Gen.Byte).Select((a, b, c, d) => $"{a}.{b}.{c}.{d}"),
            Gen.Char["abcdefghijklmnopqrstuvwxyz0123456789"].Array[1, 20].Select(c => new string(c) + ".local"),
            Gen.Const("fe80::1"));
        Gen.Select(host, Gen.UShort[1, ushort.MaxValue], Gen.Byte.Array[32], Gen.Byte.Array[32], Version).Sample((h, port, fp, token, v) =>
        {
            var qr = new PairingQrPayload(h, port, fp, token, v);
            string text = qr.Format();
            Assert.True(PairingQrPayload.TryParse(text, out PairingQrPayload? back), text);
            Assert.Equal(h, back!.Host);
            Assert.Equal(port, back.Port);
            Assert.Equal(fp, back.Fingerprint.ToArray());
            Assert.Equal(token, back.Token.ToArray());
            Assert.Equal(v, back.Version);
            for (int cut = 0; cut < text.Length; cut++)
            {
                _ = PairingQrPayload.TryParse(text[..cut], out _);
            }
        }, iter: PropertyGens.CodecIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void Bonjour_records_round_trip()
    {
        Gen<string> name = Text.Where(n => Encoding.UTF8.GetByteCount(n) <= BonjourTxtRecord.MaxNameBytes);
        Gen.Select(Version, Gen.Byte.Array[32], name).Sample((v, fp, n) =>
        {
            var record = new BonjourTxtRecord(v, fp, n);
            Assert.True(BonjourTxtRecord.TryParse(record.ToEntries(), out BonjourTxtRecord? back));
            Assert.Equal(v, back!.Version);
            Assert.Equal(fp, back.Fingerprint.ToArray());
            Assert.Equal(n, back.Name);
        }, iter: PropertyGens.CodecIterations, threads: PropertyGens.Threads);
    }

    /// <summary>
    /// A SettingsUpdate whose entries were all undecodable reads as an update with none (documented: newer-minor
    /// entries are skipped), which writes a count of 0 that the 1..64 rule rejects. Nothing sends a parsed update on.
    /// </summary>
    private static bool IsEmptyUpdate(ControlMessageType type, byte[] encoded) =>
        type == ControlMessageType.SettingsUpdate && encoded is [0, ..];

    private static readonly ControlMessageType[] MessageTypes =
    [
        ControlMessageType.Hello, ControlMessageType.HelloAck, ControlMessageType.ClientPairRound1, ControlMessageType.ServerPairRound1,
        ControlMessageType.ClientPairRound2, ControlMessageType.ServerPairRound2, ControlMessageType.ClientPairConfirm,
        ControlMessageType.ServerPairConfirm, ControlMessageType.PairResult, ControlMessageType.Auth, ControlMessageType.SessionStart,
        ControlMessageType.SurfaceInfo, ControlMessageType.UdpAck, ControlMessageType.InputFrame, ControlMessageType.Ping,
        ControlMessageType.Pong, ControlMessageType.Busy, ControlMessageType.Error, ControlMessageType.Bye,
        ControlMessageType.SettingsSnapshot, ControlMessageType.SettingsUpdate, ControlMessageType.DisplayList, ControlMessageType.ActiveArea,
    ];

    /// <summary>
    /// Reads <paramref name="payload"/> as <paramref name="type"/> and, when accepted, writes it again. Only a
    /// <see cref="ProtocolException"/>-free false is a valid rejection: any exception fails the property.
    /// <paramref name="rewrite"/> writes the parsed message again later (for the aliasing check).
    /// </summary>
    private static bool TryRecode(ControlMessageType type, ReadOnlySpan<byte> payload, out byte[] encoded, out Action<byte[]>? rewrite)
    {
        switch (type)
        {
            case ControlMessageType.Hello: return Recode<HelloMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.HelloAck: return Recode<HelloAckMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.ClientPairRound1: return Recode<ClientPairRound1Message>(payload, out encoded, out rewrite);
            case ControlMessageType.ServerPairRound1: return Recode<ServerPairRound1Message>(payload, out encoded, out rewrite);
            case ControlMessageType.ClientPairRound2: return Recode<ClientPairRound2Message>(payload, out encoded, out rewrite);
            case ControlMessageType.ServerPairRound2: return Recode<ServerPairRound2Message>(payload, out encoded, out rewrite);
            case ControlMessageType.ClientPairConfirm: return Recode<ClientPairConfirmMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.ServerPairConfirm: return Recode<ServerPairConfirmMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.PairResult: return Recode<PairResultMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.Auth: return Recode<AuthMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.SessionStart: return Recode<SessionStartMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.SurfaceInfo: return Recode<SurfaceInfoMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.UdpAck: return Recode<UdpAckMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.InputFrame: return Recode<InputFrameMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.Ping: return Recode<PingMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.Pong: return Recode<PongMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.Busy: return Recode<BusyMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.Error: return Recode<ErrorMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.Bye: return Recode<ByeMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.SettingsSnapshot: return Recode<SettingsSnapshotMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.SettingsUpdate: return Recode<SettingsUpdateMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.DisplayList: return Recode<DisplayListMessage>(payload, out encoded, out rewrite);
            case ControlMessageType.ActiveArea: return Recode<ActiveAreaMessage>(payload, out encoded, out rewrite);
            default: throw new ArgumentOutOfRangeException(nameof(type), type, "No codec in this test.");
        }
    }

    private static bool Recode<T>(ReadOnlySpan<byte> payload, out byte[] encoded, out Action<byte[]>? rewrite)
        where T : IControlMessage<T>
    {
        encoded = [];
        rewrite = null;
        if (!T.TryRead(payload, out T message))
        {
            return false;
        }

        encoded = new byte[message.PayloadSize];
        Assert.Equal(encoded.Length, message.WritePayload(encoded));
        rewrite = destination => message.WritePayload(destination);
        return true;
    }

    private static Encoded Encode<T>(T message)
        where T : IControlMessage<T>
    {
        var payload = new byte[message.PayloadSize];
        Assert.Equal(payload.Length, message.WritePayload(payload));
        return new Encoded(T.Type, payload);
    }

    /// <summary>A message as its type and the payload its writer produced.</summary>
    private sealed record Encoded(ControlMessageType Type, byte[] Payload)
    {
        public override string ToString() => $"{Type} [{Convert.ToHexString(Payload)}]";
    }
}
