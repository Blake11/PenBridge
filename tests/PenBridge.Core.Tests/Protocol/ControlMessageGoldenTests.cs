using PenBridge.Core.Input;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Protocol;

/// <summary>Golden frames for every control message. A failure here means the wire layout changed (skill protocol-change).</summary>
public class ControlMessageGoldenTests
{
    private static readonly byte[] DeviceId = Hex.Range(0x00, 16);

    [Fact]
    public void Hello()
    {
        var msg = new HelloMessage(new ProtocolVersion(1, 0), DeviceId, "S23", ProtocolCapabilities.None);
        HelloMessage back = Golden(msg, "0000001C 01 0100 000102030405060708090A0B0C0D0E0F 0003 533233 00000000");
        Assert.Equal(new ProtocolVersion(1, 0), back.Version);
        Assert.Equal(DeviceId, back.DeviceId.ToArray());
        Assert.Equal("S23", back.DeviceName);
        Assert.Equal(ProtocolCapabilities.None, back.Capabilities);
    }

    [Fact]
    public void HelloAck()
    {
        byte[] challenge = Hex.Range(0x20, 32);
        var msg = new HelloAckMessage(new ProtocolVersion(1, 0), "Mac", (ProtocolCapabilities)0x80000001, challenge);
        HelloAckMessage back = Golden(msg, "0000002C 02 0100 0003 4D6163 80000001" + Hex.Of(challenge));
        Assert.Equal("Mac", back.ServerName);
        Assert.Equal((ProtocolCapabilities)0x80000001, back.Capabilities);
        Assert.Equal(challenge, back.Challenge.ToArray());
    }

    [Fact]
    public void Hello_and_HelloAck_1_1_with_sample_dedupe()
    {
        Assert.Equal(1u, (uint)ProtocolCapabilities.SampleDedupe);
        var hello = new HelloMessage(new ProtocolVersion(1, 1), DeviceId, "S23", ProtocolCapabilities.SampleDedupe);
        HelloMessage h = Golden(hello, "0000001C 01 0101 000102030405060708090A0B0C0D0E0F 0003 533233 00000001");
        Assert.Equal(ProtocolCapabilities.SampleDedupe, h.Capabilities);
        Assert.True(h.Version.IsCompatibleWith(new ProtocolVersion(1, 0)));

        byte[] challenge = Hex.Range(0x20, 32);
        var ack = new HelloAckMessage(new ProtocolVersion(1, 1), "Mac", ProtocolCapabilities.SampleDedupe, challenge);
        HelloAckMessage a = Golden(ack, "0000002C 02 0101 0003 4D6163 00000001" + Hex.Of(challenge));
        Assert.Equal(ProtocolCapabilities.SampleDedupe, a.Capabilities);
    }

    [Fact]
    public void ClientPairRound1()
    {
        ClientPairRound1Message back = Golden(new ClientPairRound1Message(Hex.Bytes("AABBCC"), PairingSecretKind.Pin), "00000007 10 0003 AABBCC 01");
        Assert.Equal(PairingSecretKind.Pin, back.SecretKind);
        Assert.Equal("AABBCC", Hex.Of(back.Round1.Span));
        Assert.Equal(PairingSecretKind.QrToken, Golden(new ClientPairRound1Message(Hex.Bytes("01"), PairingSecretKind.QrToken), "00000005 10 0001 01 02").SecretKind);
    }

    [Fact]
    public void ServerPairRound1() =>
        Assert.Equal("0102", Hex.Of(Golden(new ServerPairRound1Message(Hex.Bytes("0102")), "00000005 11 0002 0102").Round1.Span));

    [Fact]
    public void ClientPairRound2() =>
        Assert.True(Golden(new ClientPairRound2Message(ReadOnlyMemory<byte>.Empty), "00000003 12 0000").Round2.IsEmpty);

    [Fact]
    public void ServerPairRound2() =>
        Assert.Equal("FF", Hex.Of(Golden(new ServerPairRound2Message(Hex.Bytes("FF")), "00000004 13 0001 FF").Round2.Span));

    [Fact]
    public void ClientPairConfirm() =>
        Assert.Equal("DEADBEEF", Hex.Of(Golden(new ClientPairConfirmMessage(Hex.Bytes("DEADBEEF")), "00000007 14 0004 DEADBEEF").Mac.Span));

    [Fact]
    public void ServerPairConfirm() =>
        Assert.Equal("CAFE", Hex.Of(Golden(new ServerPairConfirmMessage(Hex.Bytes("CAFE")), "00000005 15 0002 CAFE").Mac.Span));

    [Fact]
    public void PairResult()
    {
        Assert.Equal(PairResultStatus.Ok, Golden(PairResultMessage.Ok(), "00000003 16 00 00").Status);
        PairResultMessage failed = Golden(PairResultMessage.Failed(2), "00000003 16 01 02");
        Assert.Equal(PairResultStatus.Failed, failed.Status);
        Assert.Equal(2, failed.AttemptsLeft);
        Assert.Equal(PairResultStatus.Expired, Golden(PairResultMessage.Expired(), "00000003 16 02 00").Status);
    }

    [Fact]
    public void Auth()
    {
        AuthMessage back = Golden(new AuthMessage(DeviceId, Hex.Range(0x10, 32), Hex.Range(0x30, 32)), "00000051 20" + Hex.Of(Hex.Range(0, 0x50)));
        Assert.Equal(Hex.Range(0x10, 32), back.Hmac.ToArray());
        Assert.Equal(Hex.Range(0x30, 32), back.ClientNonce.ToArray());
    }

    [Fact]
    public void SessionStart()
    {
        byte[] nonce = Hex.Range(0x80, 32);
        SessionStartMessage back = Golden(new SessionStartMessage(47820, 0x01020304, nonce), "00000027 21 BACC 01020304" + Hex.Of(nonce));
        Assert.Equal(47820, back.UdpPort);
        Assert.Equal(0x01020304u, back.SessionId);
        Assert.Equal(nonce, back.ServerNonce.ToArray());
    }

    [Fact]
    public void SurfaceInfo()
    {
        var msg = new SurfaceInfoMessage(1440, 3088, 500f, 500.5f, SurfaceOrientation.Portrait, SurfaceCapabilities.FingerFallback);
        SurfaceInfoMessage back = Golden(msg, "00000012 22 05A0 0C10 43FA0000 43FA4000 00 00000001");
        Assert.Equal(1440, back.WidthPx);
        Assert.Equal(3088, back.HeightPx);
        Assert.Equal(500f, back.XDpi);
        Assert.Equal(500.5f, back.YDpi);
        Assert.Equal(SurfaceOrientation.Portrait, back.Orientation);
        Assert.Equal(SurfaceCapabilities.FingerFallback, back.Capabilities);
    }

    [Fact]
    public void UdpAck() =>
        Assert.Equal(0x0102030405060708ul, Golden(new UdpAckMessage(0x0102030405060708), "00000009 23 0102030405060708").HighestSequence);

    [Fact]
    public void InputFrame()
    {
        var payload = new InputPayload { TimeUs = 100 };
        payload.SetTouches([new TouchSample(1, true, 2, 3)]);
        InputFrameMessage back = Golden(new InputFrameMessage(payload), "0000000E 24 00000064 00 01 0102 0002 0003 00");
        Assert.Equal(100u, back.Payload.TimeUs);
        Assert.Null(back.Payload.Pen);
        Assert.Equal(new TouchSample(1, true, 2, 3), Assert.Single(back.Payload.Touches.ToArray()));
    }

    [Fact]
    public void Ping() => Assert.Equal(0x1234ul, Golden(new PingMessage(0x1234), "00000009 30 0000000000001234").TimestampUs);

    [Fact]
    public void Pong() => Assert.Equal(0x1234ul, Golden(new PongMessage(0x1234), "00000009 31 0000000000001234").TimestampUs);

    [Fact]
    public void Bye()
    {
        Assert.Equal(ByeReason.Normal, Golden(new ByeMessage(ByeReason.Normal), "00000002 70 00").Reason);
        Assert.Equal(ByeReason.TakenOver, Golden(new ByeMessage(ByeReason.TakenOver), "00000002 70 01").Reason);
        Assert.Equal(ByeReason.Unpaired, Golden(new ByeMessage(ByeReason.Unpaired), "00000002 70 02").Reason);
        Assert.Equal(ByeReason.Shutdown, Golden(new ByeMessage(ByeReason.Shutdown), "00000002 70 03").Reason);
    }

    [Fact]
    public void Busy() => Golden(default(BusyMessage), "00000001 7E");

    [Fact]
    public void Error()
    {
        ErrorMessage back = Golden(new ErrorMessage(ProtocolErrorCode.IncompatibleVersion, "v2"), "00000007 7F 0005 0002 7632");
        Assert.Equal(ProtocolErrorCode.IncompatibleVersion, back.Code);
        Assert.Equal("v2", back.Message);
    }

    [Fact]
    public void Error_and_bye_accept_unknown_codes_from_newer_peers()
    {
        Assert.True(ErrorMessage.TryRead(Hex.Bytes("FFFF 0000"), out ErrorMessage error));
        Assert.Equal((ProtocolErrorCode)0xFFFF, error.Code);
        Assert.True(ByeMessage.TryRead(Hex.Bytes("C8"), out ByeMessage bye));
        Assert.Equal((ByeReason)0xC8, bye.Reason);
    }

    [Fact]
    public void Error_codes_are_stable()
    {
        Assert.Equal(1, (int)ProtocolErrorCode.ProtocolViolation);
        Assert.Equal(2, (int)ProtocolErrorCode.UnknownMessageType);
        Assert.Equal(3, (int)ProtocolErrorCode.FrameTooLarge);
        Assert.Equal(4, (int)ProtocolErrorCode.UnexpectedMessage);
        Assert.Equal(5, (int)ProtocolErrorCode.IncompatibleVersion);
        Assert.Equal(6, (int)ProtocolErrorCode.AuthFailed);
        Assert.Equal(7, (int)ProtocolErrorCode.PairingUnavailable);
        Assert.Equal(8, (int)ProtocolErrorCode.Timeout);
        Assert.Equal(9, (int)ProtocolErrorCode.InternalError);
    }

    /// <summary>Writes the frame, compares with the golden hex, decodes it back and re-encodes to the same bytes.</summary>
    internal static T Golden<T>(T message, string expectedHex)
        where T : IControlMessage<T>
    {
        byte[] expected = Hex.Bytes(expectedHex);
        var buffer = new byte[ControlFrame.GetFrameSize(message)];
        Assert.Equal(expected.Length, ControlFrame.Write(buffer, message));
        Assert.Equal(Hex.Of(expected), Hex.Of(buffer));

        Assert.Equal(ControlFrameStatus.Frame, ControlFrame.TryRead(expected, out ControlMessageType type, out ReadOnlySpan<byte> payload, out int consumed));
        Assert.Equal(T.Type, type);
        Assert.Equal(expected.Length, consumed);
        Assert.True(T.TryRead(payload, out T decoded));

        var again = new byte[expected.Length];
        ControlFrame.Write(again, decoded);
        Assert.Equal(Hex.Of(expected), Hex.Of(again));

        // Every strict prefix of the payload is rejected (truncation), and the frame needs more data.
        for (int i = 0; i < payload.Length; i++)
        {
            Assert.False(T.TryRead(payload[..i], out _));
        }

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(ControlFrameStatus.NeedMoreData, ControlFrame.TryRead(expected.AsSpan(0, i), out _, out _, out _));
        }

        return decoded;
    }
}
