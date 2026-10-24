using PenBridge.Core.Protocol;
using static PenBridge.Core.Tests.Protocol.ControlMessageGoldenTests;

namespace PenBridge.Core.Tests.Protocol;

/// <summary>Protocol 1.3 mirroring messages 0x50–0x55 (docs/specs/screen-mirroring.md "Control messages").</summary>
public class MirrorMessageTests
{
    [Fact]
    public void Codes_are_stable()
    {
        Assert.Equal(16u, (uint)ProtocolCapabilities.ScreenMirror);
        Assert.Equal(new ProtocolVersion(1, 3), ProtocolVersion.Current);
        Assert.Equal(0x50, (int)ControlMessageType.MirrorStart);
        Assert.Equal(0x51, (int)ControlMessageType.MirrorStop);
        Assert.Equal(0x52, (int)ControlMessageType.MirrorConfig);
        Assert.Equal(0x53, (int)ControlMessageType.MirrorState);
        Assert.Equal(0x54, (int)ControlMessageType.KeyframeRequest);
        Assert.Equal(0x55, (int)ControlMessageType.MirrorReport);
        for (byte t = 0x50; t <= 0x55; t++)
        {
            Assert.True(ControlMessageTypes.IsKnown(t));
        }

        Assert.False(ControlMessageTypes.IsKnown(0x4F));
        Assert.False(ControlMessageTypes.IsKnown(0x56));
        Assert.Equal([1, 2], Enum.GetValues<MirrorCodecs>().Where(c => c != MirrorCodecs.None).Select(c => (int)c));
        Assert.Equal([1, 2], Enum.GetValues<MirrorCodec>().Select(c => (int)c));
        Assert.Equal([0, 1, 2, 3, 4, 5], Enum.GetValues<MirrorState>().Select(s => (int)s));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7], Enum.GetValues<MirrorReason>().Select(r => (int)r));
        Assert.Equal(
            [MirrorState.Off, MirrorState.Starting, MirrorState.Streaming, MirrorState.Paused, MirrorState.Throttled, MirrorState.Unavailable],
            Enum.GetValues<MirrorState>());
        Assert.Equal(
            [MirrorReason.None, MirrorReason.SettingOff, MirrorReason.PenBridgePaused, MirrorReason.PermissionDenied, MirrorReason.TlsFallback, MirrorReason.NoCommonCodec, MirrorReason.CaptureFailed, MirrorReason.PhoneStopped],
            Enum.GetValues<MirrorReason>());
    }

    [Fact]
    public void MirrorStart_golden()
    {
        MirrorStartMessage back = Golden(new MirrorStartMessage(MirrorCodecs.H264 | MirrorCodecs.Hevc, 2340, 1080, 120, 50000), "00000009 50 03 0924 0438 78 C350");
        Assert.Equal(MirrorCodecs.H264 | MirrorCodecs.Hevc, back.Codecs);
        Assert.Equal(2340, back.MaxWidthPx);
        Assert.Equal(1080, back.MaxHeightPx);
        Assert.Equal(120, back.MaxFps);
        Assert.Equal(50000, back.VideoUdpPort);

        // Unknown codec bits and an empty mask are kept (the Mac answers NoCommonCodec).
        Assert.Equal((MirrorCodecs)0x84, Golden(new MirrorStartMessage((MirrorCodecs)0x84, 1, 1, 1, 1), "00000009 50 84 0001 0001 01 0001").Codecs);
        Assert.Equal(MirrorCodecs.None, Golden(new MirrorStartMessage(MirrorCodecs.None, 1, 1, 1, 1), "00000009 50 00 0001 0001 01 0001").Codecs);
    }

    [Fact]
    public void MirrorStop_golden() => Golden(default(MirrorStopMessage), "00000001 51");

    [Fact]
    public void MirrorConfig_golden()
    {
        MirrorConfigMessage back = Golden(new MirrorConfigMessage(7, MirrorCodec.Hevc, 2338, 1080, 120, 18000, 47830), "0000000E 52 07 02 0922 0438 78 00004650 BAD6");
        Assert.Equal(7, back.Epoch);
        Assert.Equal(MirrorCodec.Hevc, back.Codec);
        Assert.Equal(2338, back.WidthPx);
        Assert.Equal(1080, back.HeightPx);
        Assert.Equal(120, back.Fps);
        Assert.Equal(18000u, back.BitrateKbps);
        Assert.Equal(47830, back.VideoUdpPort);
        Assert.Equal(MirrorCodec.H264, Golden(new MirrorConfigMessage(255, MirrorCodec.H264, 2, 2, 30, 2000, 1), "0000000E 52 FF 01 0002 0002 1E 000007D0 0001").Codec);
    }

    [Fact]
    public void MirrorState_golden()
    {
        MirrorStateMessage back = Golden(new MirrorStateMessage(MirrorState.Paused, MirrorReason.PenBridgePaused), "00000003 53 03 02");
        Assert.Equal(MirrorState.Paused, back.State);
        Assert.Equal(MirrorReason.PenBridgePaused, back.Reason);
        Assert.Equal(MirrorState.Streaming, Golden(new MirrorStateMessage(MirrorState.Streaming, MirrorReason.None), "00000003 53 02 00").State);
        Assert.Equal(MirrorReason.NoCommonCodec, Golden(new MirrorStateMessage(MirrorState.Unavailable, MirrorReason.NoCommonCodec), "00000003 53 05 05").Reason);
    }

    [Fact]
    public void KeyframeRequest_golden()
    {
        KeyframeRequestMessage back = Golden(new KeyframeRequestMessage(7, 0x01020304), "00000006 54 07 01020304");
        Assert.Equal(7, back.Epoch);
        Assert.Equal(0x01020304u, back.LastCompleteFrame);
    }

    [Fact]
    public void MirrorReport_golden()
    {
        MirrorReportMessage back = Golden(
            new MirrorReportMessage(7, 100, 2, 5, 99, 0x0000000123456789),
            "0000001A 55 07 00000064 00000002 00000005 00000063 0000000123456789");
        Assert.Equal(7, back.Epoch);
        Assert.Equal(100u, back.FramesComplete);
        Assert.Equal(2u, back.FramesDropped);
        Assert.Equal(5u, back.FragmentsLost);
        Assert.Equal(99u, back.LastRenderedFrame);
        Assert.Equal(0x0000000123456789UL, back.LastRenderedAtUs);
    }

    [Theory]
    [InlineData(5, 7, 5, 7)]         // Unavailable keeps a known reason
    [InlineData(0, 1, 0, 1)]         // Off/SettingOff
    [InlineData(3, 2, 3, 2)]         // Paused/PenBridgePaused
    [InlineData(2, 3, 2, 0)]         // Streaming ignores a reason
    [InlineData(1, 9, 1, 0)]         // Starting ignores even an unknown reason
    [InlineData(4, 0, 4, 0)]         // Throttled
    [InlineData(6, 0, 5, 0)]         // unknown state → Unavailable
    [InlineData(0xFF, 3, 5, 3)]      // unknown state keeps a known reason
    [InlineData(0xFF, 0xFF, 5, 0)]   // both unknown
    [InlineData(0, 8, 5, 0)]         // Off with an unknown reason → Unavailable
    [InlineData(3, 0x80, 5, 0)]      // Paused with an unknown reason → Unavailable
    public void Unknown_state_and_reason_values_read_as_unavailable(byte state, byte reason, byte shownState, byte shownReason)
    {
        Assert.True(MirrorStateMessage.TryRead([state, reason], out MirrorStateMessage message));
        Assert.Equal((MirrorState)shownState, message.State);
        Assert.Equal((MirrorReason)shownReason, message.Reason);
        Assert.Equal(state, message.RawState);
        Assert.Equal(reason, message.RawReason);

        // The raw bytes are kept on re-write (a relay never rewrites what it can't interpret).
        byte[] written = new byte[2];
        Assert.Equal(2, message.WritePayload(written));
        Assert.Equal([state, reason], written);
    }

    [Fact]
    public void Writers_reject_invalid_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorStartMessage(MirrorCodecs.H264, 0, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorStartMessage(MirrorCodecs.H264, 1, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorStartMessage(MirrorCodecs.H264, 1, 1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorStartMessage(MirrorCodecs.H264, 1, 1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorConfigMessage(0, (MirrorCodec)0, 2, 2, 30, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorConfigMessage(0, (MirrorCodec)3, 2, 2, 30, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorConfigMessage(0, MirrorCodec.H264, 0, 2, 30, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorConfigMessage(0, MirrorCodec.H264, 2, 0, 30, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorConfigMessage(0, MirrorCodec.H264, 2, 2, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorConfigMessage(0, MirrorCodec.H264, 2, 2, 30, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorConfigMessage(0, MirrorCodec.H264, 2, 2, 30, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorStateMessage(MirrorState.Streaming, MirrorReason.SettingOff));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorStateMessage(MirrorState.Throttled, MirrorReason.TlsFallback));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorStateMessage((MirrorState)6, MirrorReason.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorStateMessage(MirrorState.Off, (MirrorReason)8));

        // A default struct has zero fields: writing it is an error, not garbage on the wire.
        Assert.Throws<InvalidOperationException>(() => default(MirrorStartMessage).PayloadSize);
        Assert.Throws<InvalidOperationException>(() => default(MirrorConfigMessage).PayloadSize);
        byte[] small = new byte[4];
        Assert.Equal("destination", Assert.Throws<ArgumentOutOfRangeException>(() => new KeyframeRequestMessage(1, 1).WritePayload(small)).ParamName);
        Assert.Equal("destination", Assert.Throws<ArgumentOutOfRangeException>(() => new MirrorStateMessage(MirrorState.Off, MirrorReason.None).WritePayload(small.AsSpan(0, 1))).ParamName);
    }

    [Theory]
    [InlineData("00 0001 0001 01 0001")]     // no codecs is fine
    [InlineData("03 0001 0001 01 0001 FFFF")] // trailing bytes ignored
    public void MirrorStart_reads(string hex) => Assert.True(MirrorStartMessage.TryRead(Hex.Bytes(hex), out _));

    [Theory]
    [InlineData("03 0000 0001 01 0001")]
    [InlineData("03 0001 0000 01 0001")]
    [InlineData("03 0001 0001 00 0001")]
    [InlineData("03 0001 0001 01 0000")]
    public void MirrorStart_rejects_zero_fields(string hex) => Assert.False(MirrorStartMessage.TryRead(Hex.Bytes(hex), out _));

    [Theory]
    [InlineData("07 00 0002 0002 1E 000007D0 0001")] // codec 0
    [InlineData("07 03 0002 0002 1E 000007D0 0001")] // codec 3
    [InlineData("07 02 0000 0002 1E 000007D0 0001")]
    [InlineData("07 02 0002 0000 1E 000007D0 0001")]
    [InlineData("07 02 0002 0002 00 000007D0 0001")]
    [InlineData("07 02 0002 0002 1E 00000000 0001")]
    [InlineData("07 02 0002 0002 1E 000007D0 0000")]
    public void MirrorConfig_rejects_invalid_fields(string hex) => Assert.False(MirrorConfigMessage.TryRead(Hex.Bytes(hex), out _));

    [Fact]
    public void Every_truncation_fails_and_trailing_bytes_are_ignored()
    {
        Check<MirrorStartMessage>("03 0924 0438 78 C350");
        Check<MirrorConfigMessage>("07 02 0922 0438 78 00004650 BAD6");
        Check<MirrorStateMessage>("03 02");
        Check<KeyframeRequestMessage>("07 01020304");
        Check<MirrorReportMessage>("07 00000064 00000002 00000005 00000063 0000000123456789");
        Assert.True(MirrorStopMessage.TryRead(Hex.Bytes("AABB"), out _));

        static void Check<T>(string hex)
            where T : IControlMessage<T>
        {
            byte[] full = Hex.Bytes(hex);
            for (int n = 0; n < full.Length; n++)
            {
                Assert.False(T.TryRead(full.AsSpan(0, n), out _));
            }

            Assert.True(T.TryRead(full, out _));
            Assert.True(T.TryRead([.. full, 0xDE, 0xAD], out T extended));
            Assert.True(T.TryRead(full, out T exact));
            Assert.Equal(exact, extended);
        }
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var random = new Random(1234);
        byte[] buffer = new byte[64];
        for (int i = 0; i < 20_000; i++)
        {
            int length = random.Next(buffer.Length + 1);
            random.NextBytes(buffer);
            ReadOnlySpan<byte> payload = buffer.AsSpan(0, length);
            if (MirrorStartMessage.TryRead(payload, out MirrorStartMessage start))
            {
                Assert.Equal(MirrorStartMessage.Size, start.PayloadSize);
            }

            if (MirrorConfigMessage.TryRead(payload, out MirrorConfigMessage config))
            {
                Assert.Equal(MirrorConfigMessage.Size, config.PayloadSize);
            }

            if (MirrorStateMessage.TryRead(payload, out MirrorStateMessage state))
            {
                Assert.True(Enum.IsDefined(state.State));
                Assert.True(Enum.IsDefined(state.Reason));
            }

            _ = KeyframeRequestMessage.TryRead(payload, out _);
            _ = MirrorReportMessage.TryRead(payload, out _);
        }
    }

    [Fact]
    public void Capability_gating_table()
    {
        foreach (ControlMessageType type in Enum.GetValues<ControlMessageType>())
        {
            ProtocolCapabilities expected = type switch
            {
                ControlMessageType.SettingsSnapshot or ControlMessageType.SettingsUpdate or ControlMessageType.DisplayList => ProtocolCapabilities.RemoteSettings,
                ControlMessageType.ActiveArea => ProtocolCapabilities.ActiveArea,
                >= ControlMessageType.MirrorStart and <= ControlMessageType.MirrorReport => ProtocolCapabilities.ScreenMirror,
                _ => ProtocolCapabilities.None,
            };
            Assert.Equal(expected, ControlMessageTypes.RequiredCapability(type));
            Assert.Equal(expected == ProtocolCapabilities.ScreenMirror, ControlMessageTypes.IsMirror(type));
            Assert.True(ControlMessageTypes.IsNegotiated(type, expected));
            Assert.Equal(expected == ProtocolCapabilities.None, ControlMessageTypes.IsNegotiated(type, ProtocolCapabilities.None));
        }

        const ProtocolCapabilities OneTwo = ProtocolCapabilities.SampleDedupe | ProtocolCapabilities.FingerTouches | ProtocolCapabilities.RemoteSettings | ProtocolCapabilities.ActiveArea;
        Assert.False(ControlMessageTypes.IsNegotiated(ControlMessageType.MirrorStart, OneTwo));
        Assert.True(ControlMessageTypes.IsNegotiated(ControlMessageType.MirrorReport, OneTwo | ProtocolCapabilities.ScreenMirror));
        Assert.True(ControlMessageTypes.IsNegotiated(ControlMessageType.Ping, ProtocolCapabilities.None));
    }

    [Fact]
    public void Codec_helpers()
    {
        Assert.True(MirrorWire.IsKnown(MirrorCodec.H264));
        Assert.True(MirrorWire.IsKnown(MirrorCodec.Hevc));
        Assert.False(MirrorWire.IsKnown((MirrorCodec)0));
        Assert.False(MirrorWire.IsKnown((MirrorCodec)3));
        Assert.Equal(MirrorCodecs.H264, MirrorWire.ToMask(MirrorCodec.H264));
        Assert.Equal(MirrorCodecs.Hevc, MirrorWire.ToMask(MirrorCodec.Hevc));
        Assert.Equal(MirrorCodecs.None, MirrorWire.ToMask((MirrorCodec)7));
        Assert.Equal(MirrorCodecs.H264 | MirrorCodecs.Hevc, MirrorWire.KnownCodecs);
    }
}
