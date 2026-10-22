using System.Text;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using PenBridge.Core.Settings;

namespace PenBridge.Fuzz;

/// <summary>Seed corpus: valid inputs written by the protocol writers, plus a mutation dictionary per target.</summary>
internal static class Seeds
{
    private static readonly byte[] DeviceId = Range(0x00, 16);
    private static readonly byte[] Key32 = Range(0x20, 32);

    public static List<byte[]> For(string target) => target switch
    {
        Targets.Control => ControlSeeds(),
        Targets.Payload => [.. PayloadSamples().Select(Encode)],
        Targets.Datagram => DatagramSeeds(),
        Targets.Qr => QrSeeds(),
        _ => throw new ArgumentException($"Unknown target '{target}'.", nameof(target)),
    };

    /// <summary>Tokens the mutator splices in: type bytes, magic values, and the text grammar of the QR/TXT target.</summary>
    public static List<byte[]> Dictionary(string target)
    {
        var tokens = new List<byte[]>();
        if (target == Targets.Qr)
        {
            foreach (string token in (string[])["penbridge://pair?", "h=", "&p=", "&fp=", "&t=", "&v=", "%", "%3A", "%25", "%C3%A9", "%FF", "%00", "&", "=", "1.2", "1.0", "255.255", "\n", "[", "]", "::1", "localhost", "192.168.1.10", "65535", "65536", "0", Base64UrlBytes.Encode(Key32)])
            {
                tokens.Add(Encoding.UTF8.GetBytes(token));
            }

            return tokens;
        }

        foreach (ControlMessageType type in Enum.GetValues<ControlMessageType>())
        {
            tokens.Add([(byte)type]);
        }

        tokens.Add([0x00, 0x01, 0x00, 0x00]);
        tokens.Add([0x00, 0x01, 0x00, 0x01]);
        tokens.Add([0x7F, 0xC0, 0x00, 0x00]); // NaN
        tokens.Add([0x7F, 0x80, 0x00, 0x00]); // +Inf
        tokens.Add([0x3F, 0x80, 0x00, 0x00]); // 1.0f
        tokens.Add([0x23, 0x28]); // 9000 (max tilt)
        tokens.Add([0xDC, 0xD8]); // -9000
        tokens.Add([0x8C, 0x9F]); // 35999 (max rotation)
        tokens.Add([0x01, 0x00, 0x00, 0x00, 0x0A, 0x0B, 0x0C, 0x0D]); // datagram header start with our session id
        return tokens;
    }

    // ---------------------------------------------------------------- control

    private static List<byte[]> ControlSeeds()
    {
        var payload = new InputPayload { TimeUs = 100, Pen = Pen(1000) };
        payload.SetTouches([new TouchSample(1, true, 2, 3), new TouchSample(2, false, 400, 500)]);
        payload.SetHistory([Pen(900), Pen(950)]);
        var defaults = new MacSettingValues().ToEntries();
        SettingEntry[] update =
        [
            SettingEntry.Float(MacSettingKey.MouseSpeed, 1.5f),
            SettingEntry.Bool(MacSettingKey.Zoom, false),
            SettingEntry.Int(MacSettingKey.StylusMode, 1),
            SettingEntry.String(MacSettingKey.TargetDisplay, "2"),
            SettingEntry.Rect(MacSettingKey.Region, new NormalizedRect(0.25, 0.25, 0.5, 0.5)),
            SettingEntry.Float(MacSettingKey.ClickThreshold, 0.2f),
            SettingEntry.Float(MacSettingKey.FullPressureAt, 0.9f),
        ];

        List<byte[]> frames =
        [
            Frame(new HelloMessage(ProtocolVersion.Current, DeviceId, "Galaxy S23 Ultra", ProtocolCapabilities.SampleDedupe | ProtocolCapabilities.FingerTouches | ProtocolCapabilities.RemoteSettings | ProtocolCapabilities.ActiveArea)),
            Frame(new HelloAckMessage(ProtocolVersion.Current, "Mac mini", ProtocolCapabilities.RemoteSettings, Key32)),
            Frame(new ClientPairRound1Message(Range(0x40, 64), PenBridge.Core.Protocol.PairingSecretKind.Pin)),
            Frame(new ClientPairRound1Message(Range(0x40, 8), PenBridge.Core.Protocol.PairingSecretKind.QrToken)),
            Frame(new ServerPairRound1Message(Range(0x50, 64))),
            Frame(new ClientPairRound2Message(Range(0x60, 48))),
            Frame(new ServerPairRound2Message(Range(0x70, 48))),
            Frame(new ClientPairConfirmMessage(Key32)),
            Frame(new ServerPairConfirmMessage(Key32)),
            Frame(PairResultMessage.Ok()),
            Frame(PairResultMessage.Failed(2)),
            Frame(PairResultMessage.Expired()),
            Frame(new AuthMessage(DeviceId, Key32, Range(0x30, 32))),
            Frame(new SessionStartMessage(47820, Targets.SessionId, Key32)),
            Frame(new SurfaceInfoMessage(1440, 3088, 500f, 500.5f, SurfaceOrientation.Landscape, SurfaceCapabilities.FingerFallback)),
            Frame(new UdpAckMessage(42)),
            Frame(new InputFrameMessage(payload)),
            Frame(new PingMessage(0x1234)),
            Frame(new PongMessage(0x1234)),
            Frame(new SettingsSnapshotMessage(defaults)),
            Frame(new SettingsUpdateMessage(update)),
            Frame(new DisplayListMessage([new DisplayListEntry("1", "Built-in Retina", true, 1512, 982), new DisplayListEntry("2", "LG UltraFine", false, 2560, 1440)])),
            Frame(new ActiveAreaMessage(1000, 2000, 30000, 40000)),
            Frame(ActiveAreaMessage.Full),
            Frame(new ByeMessage(ByeReason.TakenOver)),
            Frame(default(BusyMessage)),
            Frame(new ErrorMessage(ProtocolErrorCode.IncompatibleVersion, "Protocol 2.0 is not supported")),
        ];

        byte[] reserved = new byte[ControlFrame.HeaderSize + 3];
        ControlFrame.WriteRaw(reserved, (ControlMessageType)0xF3, [1, 2, 3]);
        var seeds = new List<byte[]>(frames) { reserved };

        // A whole handshake in one buffer, so multi-frame sequences are in the corpus from the start.
        seeds.Add([.. frames[0], .. reserved, .. frames[12], .. frames[14], .. frames[16], .. frames[20], .. frames[17]]);
        return seeds;
    }

    private static byte[] Frame<T>(T message)
        where T : IControlMessage<T>
    {
        var buffer = new byte[ControlFrame.GetFrameSize(message)];
        ControlFrame.Write(buffer, message);
        return buffer;
    }

    // ---------------------------------------------------------------- payload / datagram

    private static IEnumerable<InputPayload> PayloadSamples()
    {
        yield return new InputPayload { TimeUs = 1 };
        yield return new InputPayload { TimeUs = 2, Pen = Pen(30000) };

        var touches = new InputPayload { TimeUs = 3 };
        touches.SetTouches([new TouchSample(0, true, 100, 200), new TouchSample(1, true, 300, 400), new TouchSample(7, false, 65535, 0)]);
        yield return touches;

        var full = new InputPayload { TimeUs = uint.MaxValue, Pen = Pen(65535) };
        var many = new TouchSample[InputPayload.MaxTouches];
        for (int i = 0; i < many.Length; i++)
        {
            many[i] = new TouchSample((byte)i, i % 2 == 0, (ushort)(i * 6000), (ushort)(65535 - (i * 6000)));
        }

        full.SetTouches(many);
        var history = new PenSample[InputPayload.MaxHistory];
        for (int i = 0; i < history.Length; i++)
        {
            history[i] = Pen((ushort)(i * 8000)) with { Tool = (PenTool)(1 + (i % 3)), TiltX = -9000, TiltY = 9000, Rotation = 35999 };
        }

        full.SetHistory(history);
        yield return full;
    }

    private static byte[] Encode(InputPayload payload)
    {
        var buffer = new byte[InputPayload.MaxSize];
        return buffer[..payload.Write(buffer)];
    }

    private static List<byte[]> DatagramSeeds()
    {
        var seeds = new List<byte[]>();
        using var cipher = new InputCipher(Targets.InputKey, Targets.SessionId);
        ulong sequence = 0;
        foreach (InputPayload payload in PayloadSamples())
        {
            byte[] plain = Encode(payload);

            // The raw plaintext too: the target also seals its input as a paired phone would.
            seeds.Add(plain);
            var datagram = new byte[InputDatagram.GetSize(plain.Length)];
            InputDatagram.Slice(datagram, plain.Length, out Span<byte> header, out Span<byte> body, out _);
            new InputDatagramHeader(InputDatagramOptions.None, Targets.SessionId, ++sequence).Write(header);
            plain.CopyTo(body);
            seeds.Add(datagram[..cipher.Seal(datagram, plain.Length)]);
        }

        return seeds;
    }

    private static PenSample Pen(ushort pressure) =>
        new(12000, 34000, pressure, 1500, -2500, 9000, PenTool.Pen, PenState.InRange | PenState.Contact, PenButtons.Primary);

    // ---------------------------------------------------------------- qr / discovery text

    private static List<byte[]> QrSeeds()
    {
        string[] texts =
        [
            new PairingQrPayload("192.168.1.10", 47820, Key32, Range(0x60, 32), ProtocolVersion.Current).Format(),
            new PairingQrPayload("fe80::1", 1, Key32, Key32, new ProtocolVersion(1, 0)).Format(),
            new PairingQrPayload("paul-mac-mini.local", 65535, Range(0, 32), Key32, new ProtocolVersion(1, 1)).Format(),
            $"{ProtocolVersion.Current}\n{Base64UrlBytes.Encode(Key32)}\nPaul’s Mac mini",
            "1.2",
        ];
        return [.. texts.Select(Encoding.UTF8.GetBytes)];
    }

    private static byte[] Range(int start, int count)
    {
        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }
}
