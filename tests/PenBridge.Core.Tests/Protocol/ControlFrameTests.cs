using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Protocol;

public class ControlFrameTests
{
    [Fact]
    public void Zero_length_is_a_protocol_violation()
    {
        var ex = Assert.Throws<ProtocolException>(() => ControlFrame.TryRead(Hex.Bytes("00000000"), out _, out _, out _));
        Assert.Equal(ProtocolErrorCode.ProtocolViolation, ex.Code);
    }

    [Fact]
    public void Length_over_64_KiB_is_rejected_before_buffering()
    {
        var ex = Assert.Throws<ProtocolException>(() => ControlFrame.TryRead(Hex.Bytes("00010001"), out _, out _, out _));
        Assert.Equal(ProtocolErrorCode.FrameTooLarge, ex.Code);
        Assert.Throws<ProtocolException>(() => ControlFrame.TryRead(Hex.Bytes("FFFFFFFF"), out _, out _, out _));
    }

    [Fact]
    public void Max_length_frame_is_accepted()
    {
        var frame = new byte[ControlFrame.LengthPrefixSize + ControlFrame.MaxLength];
        Hex.Bytes("00010000 F0").CopyTo(frame, 0);
        Assert.Equal(ControlFrameStatus.Ignored, ControlFrame.TryRead(frame, out _, out ReadOnlySpan<byte> payload, out int consumed));
        Assert.Equal(ControlFrame.MaxPayloadSize, payload.Length);
        Assert.Equal(frame.Length, consumed);
    }

    [Fact]
    public void Oversized_message_cannot_be_written()
    {
        var msg = new ClientPairRound1Message(new byte[WireSizes.MaxBlob], PairingSecretKind.Pin);
        Assert.Throws<ArgumentException>(() => ControlFrame.GetFrameSize(msg));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x03)]
    [InlineData(0x17)]
    [InlineData(0x25)]
    [InlineData(0x32)]
    [InlineData(0x7D)]
    [InlineData(0x80)]
    [InlineData(0xEF)]
    public void Unknown_non_reserved_type_is_rejected(byte type)
    {
        byte[] frame = [0, 0, 0, 1, type];
        var ex = Assert.Throws<ProtocolException>(() => ControlFrame.TryRead(frame, out _, out _, out _));
        Assert.Equal(ProtocolErrorCode.UnknownMessageType, ex.Code);
    }

    [Theory]
    [InlineData(0xF0)]
    [InlineData(0xF7)]
    [InlineData(0xFF)]
    public void Reserved_type_is_ignored(byte type)
    {
        byte[] frame = [0, 0, 0, 3, type, 0xAA, 0xBB, 0x99];
        Assert.Equal(ControlFrameStatus.Ignored, ControlFrame.TryRead(frame, out _, out ReadOnlySpan<byte> payload, out int consumed));
        Assert.Equal(7, consumed);
        Assert.Equal("AABB", Hex.Of(payload));
    }

    [Fact]
    public void Known_types_match_the_spec_table()
    {
        int known = 0;
        for (int t = 0; t <= 0xFF; t++)
        {
            if (ControlMessageTypes.IsKnown((byte)t))
            {
                known++;
                Assert.True(Enum.IsDefined((ControlMessageType)t));
            }
        }

        Assert.Equal(Enum.GetValues<ControlMessageType>().Length, known);
        Assert.Equal(23, known);
    }

    [Fact]
    public void Newer_minor_appended_fields_are_ignored()
    {
        byte[] payload = Hex.Bytes("0103 000102030405060708090A0B0C0D0E0F 0000 00000000 DEADBEEF");
        Assert.True(HelloMessage.TryRead(payload, out HelloMessage hello));
        Assert.Equal(new ProtocolVersion(1, 3), hello.Version);
        Assert.True(PingMessage.TryRead(Hex.Bytes("0000000000000001 FF"), out _));
    }

    [Fact]
    public void Strings_are_capped_at_256_bytes_and_must_be_utf8()
    {
        string max = new('a', WireString.MaxBytes);
        Assert.Equal(max, ControlMessageGoldenTests.Golden(new ErrorMessage(ProtocolErrorCode.InternalError, max), "00000105 7F 0009 0100" + Hex.Of(System.Text.Encoding.ASCII.GetBytes(max))).Message);
        Assert.Throws<ArgumentException>(() => new ErrorMessage(ProtocolErrorCode.InternalError, max + "a"));

        byte[] tooLong = new byte[2 + 2 + 257];
        tooLong[2] = 0x01;
        tooLong[3] = 0x01;
        Assert.False(ErrorMessage.TryRead(tooLong, out _));
        Assert.False(ErrorMessage.TryRead(Hex.Bytes("0001 0002 C328"), out _));
        Assert.False(ErrorMessage.TryRead(Hex.Bytes("0001 0001 FF"), out _));
    }

    [Fact]
    public void Truncate_cuts_at_a_character_boundary()
    {
        string name = new string('a', 255) + "é";
        string cut = WireString.Truncate(name);
        Assert.Equal(new string('a', 255), cut);
        Assert.Equal("Paul’s Mac", WireString.Truncate("Paul’s Mac"));
        Assert.Equal("ab", WireString.Truncate("ab😀", 5));
    }

    [Fact]
    public void Utf8_names_round_trip()
    {
        var msg = new HelloAckMessage(ProtocolInfo.Version, "Paul’s MacBook", ProtocolCapabilities.None, new byte[32]);
        var buffer = new byte[ControlFrame.GetFrameSize(msg)];
        ControlFrame.Write(buffer, msg);
        Assert.Equal(ControlFrameStatus.Frame, ControlFrame.TryRead(buffer, out _, out ReadOnlySpan<byte> payload, out _));
        Assert.True(HelloAckMessage.TryRead(payload, out HelloAckMessage back));
        Assert.Equal("Paul’s MacBook", back.ServerName);
    }

    [Fact]
    public void Invalid_field_values_are_rejected()
    {
        Assert.False(ClientPairRound1Message.TryRead(Hex.Bytes("0000 00"), out _));
        Assert.False(ClientPairRound1Message.TryRead(Hex.Bytes("0000 03"), out _));
        Assert.False(PairResultMessage.TryRead(Hex.Bytes("03 00"), out _));
        Assert.False(SessionStartMessage.TryRead(new byte[SessionStartMessage.Size], out _));
        Assert.False(SurfaceInfoMessage.TryRead(Hex.Bytes("05A0 0C10 43FA0000 43FA0000 02 00000000"), out _));
        Assert.False(SurfaceInfoMessage.TryRead(Hex.Bytes("0000 0C10 43FA0000 43FA0000 00 00000000"), out _));
        Assert.False(SurfaceInfoMessage.TryRead(Hex.Bytes("05A0 0C10 7FC00000 43FA0000 00 00000000"), out _));
        Assert.False(SurfaceInfoMessage.TryRead(Hex.Bytes("05A0 0C10 43FA0000 BF800000 00 00000000"), out _));
        Assert.False(SurfaceInfoMessage.TryRead(Hex.Bytes("05A0 0C10 43FA0000 7F800000 00 00000000"), out _));
    }

    [Fact]
    public void Constructors_reject_wrong_fixed_sizes()
    {
        Assert.Throws<ArgumentException>(() => new HelloMessage(ProtocolInfo.Version, new byte[15], "x", ProtocolCapabilities.None));
        Assert.Throws<ArgumentException>(() => new HelloAckMessage(ProtocolInfo.Version, "x", ProtocolCapabilities.None, new byte[31]));
        Assert.Throws<ArgumentException>(() => new AuthMessage(new byte[16], new byte[32], new byte[33]));
        Assert.Throws<ArgumentException>(() => new SessionStartMessage(1, 1, Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => new SurfaceInfoMessage(1, 1, float.NaN, 1, SurfaceOrientation.Portrait, SurfaceCapabilities.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientPairRound1Message(new byte[1], (PairingSecretKind)9));
    }

    [Fact]
    public void Default_instances_cannot_be_written()
    {
        var buffer = new byte[256];
        Assert.Throws<ArgumentException>(() => ControlFrame.Write(buffer, default(HelloMessage)));
        Assert.Throws<ArgumentException>(() => ControlFrame.Write(buffer, default(AuthMessage)));
        Assert.Throws<InvalidOperationException>(() => ControlFrame.Write(buffer, default(SurfaceInfoMessage)));
        Assert.Throws<InvalidOperationException>(() => ControlFrame.Write(buffer, default(ClientPairRound1Message)));
        Assert.ThrowsAny<ArgumentException>(() => ControlFrame.Write(buffer, default(ErrorMessage)));
    }

    [Fact]
    public void Write_raw_frames_a_prebuilt_payload()
    {
        var buffer = new byte[16];
        int n = ControlFrame.WriteRaw(buffer, ControlMessageType.InputFrame, Hex.Bytes("00000064 00 00 00"));
        Assert.Equal("0000000824000000640000" + "00", Hex.Of(buffer.AsSpan(0, n)));
    }

    [Fact]
    public async Task Stream_reader_reads_frames_skips_reserved_and_ends_cleanly()
    {
        using var stream = new MemoryStream();
        await ControlFrameWriter.WriteAsync(stream, new PingMessage(7), TestContext.Current.CancellationToken);
        stream.Write(Hex.Bytes("00000003 F5 0102"));
        await ControlFrameWriter.WriteAsync(stream, new ByeMessage(ByeReason.Shutdown), TestContext.Current.CancellationToken);
        stream.Position = 0;

        using var reader = new ControlFrameReader(new TrickleStream(stream));
        ReceivedControlFrame? first = await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(7ul, first!.Value.Read<PingMessage>().TimestampUs);
        ReceivedControlFrame? second = await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ByeReason.Shutdown, second!.Value.Read<ByeMessage>().Reason);
        Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stream_reader_caps_consecutive_reserved_frames()
    {
        byte[] reserved = Hex.Bytes("00000001 F0");
        using var ok = new MemoryStream();
        for (int i = 0; i < ControlFrameReader.MaxConsecutiveReservedFrames; i++)
        {
            ok.Write(reserved);
        }

        await ControlFrameWriter.WriteAsync(ok, new PingMessage(1), TestContext.Current.CancellationToken);
        for (int i = 0; i < ControlFrameReader.MaxConsecutiveReservedFrames; i++)
        {
            ok.Write(reserved);
        }

        await ControlFrameWriter.WriteAsync(ok, new PingMessage(2), TestContext.Current.CancellationToken);
        ok.Position = 0;
        using (var reader = new ControlFrameReader(ok))
        {
            Assert.Equal(1ul, (await reader.ReadAsync(TestContext.Current.CancellationToken))!.Value.Read<PingMessage>().TimestampUs);
            Assert.Equal(2ul, (await reader.ReadAsync(TestContext.Current.CancellationToken))!.Value.Read<PingMessage>().TimestampUs);
        }

        using var flood = new MemoryStream();
        for (int i = 0; i <= ControlFrameReader.MaxConsecutiveReservedFrames; i++)
        {
            flood.Write(reserved);
        }

        await ControlFrameWriter.WriteAsync(flood, new PingMessage(1), TestContext.Current.CancellationToken);
        flood.Position = 0;
        using var floodReader = new ControlFrameReader(flood);
        var ex = await Assert.ThrowsAsync<ProtocolException>(async () => await floodReader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ProtocolErrorCode.ProtocolViolation, ex.Code);
    }

    [Fact]
    public async Task Stream_reader_grows_for_large_frames()
    {
        var blob = new byte[40_000];
        new Random(7).NextBytes(blob);
        using var stream = new MemoryStream();
        await ControlFrameWriter.WriteAsync(stream, new ServerPairRound1Message(blob), TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var reader = new ControlFrameReader(stream);
        ReceivedControlFrame? frame = await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(blob, frame!.Value.Read<ServerPairRound1Message>().Round1.ToArray());
    }

    [Theory]
    [InlineData("00")]
    [InlineData("000000")]
    [InlineData("00000009 30 00000000")]
    public async Task Stream_ending_mid_frame_is_a_protocol_violation(string hex)
    {
        using var reader = new ControlFrameReader(new MemoryStream(Hex.Bytes(hex)));
        var ex = await Assert.ThrowsAsync<ProtocolException>(async () => await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ProtocolErrorCode.ProtocolViolation, ex.Code);
    }

    [Fact]
    public async Task Stream_reader_rejects_oversize_and_unknown_types()
    {
        using var big = new ControlFrameReader(new MemoryStream(Hex.Bytes("00010001 01")));
        Assert.Equal(ProtocolErrorCode.FrameTooLarge, (await Assert.ThrowsAsync<ProtocolException>(async () => await big.ReadAsync(TestContext.Current.CancellationToken))).Code);

        using var unknown = new ControlFrameReader(new MemoryStream(Hex.Bytes("00000001 03")));
        Assert.Equal(ProtocolErrorCode.UnknownMessageType, (await Assert.ThrowsAsync<ProtocolException>(async () => await unknown.ReadAsync(TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public void Read_checks_the_type_and_payload()
    {
        var ping = new ReceivedControlFrame(ControlMessageType.Ping, Hex.Bytes("0000000000000001"));
        Assert.Equal(ProtocolErrorCode.UnexpectedMessage, Assert.Throws<ProtocolException>(() => ping.Read<PongMessage>()).Code);
        var shortPing = new ReceivedControlFrame(ControlMessageType.Ping, Hex.Bytes("00"));
        Assert.Equal(ProtocolErrorCode.ProtocolViolation, Assert.Throws<ProtocolException>(() => shortPing.Read<PingMessage>()).Code);
    }

    /// <summary>Returns at most one byte per read, to exercise partial reads.</summary>
    private sealed class TrickleStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
