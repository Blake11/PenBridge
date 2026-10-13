using PenBridge.Core.Input;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Protocol;

public class InputDatagramTests
{
    private static readonly PenSample Pen = new(0x1000, 0x2000, 0x8000, -100, 4500, 9000, PenTool.Pen, PenState.InRange | PenState.Contact, PenButtons.Primary);
    private static readonly PenSample Earlier = new(0x0FF0, 0x1FF0, 0x7000, 0, 0, 0, PenTool.Eraser, PenState.InRange, PenButtons.None);
    private static readonly TouchSample[] Touches = [new(3, true, 0x0100, 0x0200), new(5, false, 0xFFFF, 0)];

    private const string GoldenPayload =
        "01020304 01 02" +
        "1000 2000 8000 FF9C 1194 2328 01 03 01 00" +
        "03 02 0100 0200" +
        "05 00 FFFF 0000" +
        "01" +
        "0FF0 1FF0 7000 0000 0000 0000 02 01 00 00";

    [Fact]
    public void Header_golden_bytes()
    {
        Span<byte> buffer = stackalloc byte[InputDatagramHeader.Size];
        new InputDatagramHeader(InputDatagramOptions.None, 0xA1B2C3D4, 0x0102).Write(buffer);
        Assert.Equal("01000000A1B2C3D40000000000000102", Hex.Of(buffer));
        Assert.True(InputDatagramHeader.TryRead(buffer, out InputDatagramHeader back));
        Assert.Equal(new InputDatagramHeader(InputDatagramOptions.None, 0xA1B2C3D4, 0x0102), back);
    }

    [Fact]
    public void Header_rejects_other_versions_and_truncation_but_ignores_flags_and_reserved()
    {
        Assert.False(InputDatagramHeader.TryRead(Hex.Bytes("02000000A1B2C3D40000000000000102"), out _));
        Assert.False(InputDatagramHeader.TryRead(Hex.Bytes("01000000A1B2C3D400000000000001"), out _));
        Assert.True(InputDatagramHeader.TryRead(Hex.Bytes("0180FFFFA1B2C3D40000000000000102"), out InputDatagramHeader h));
        Assert.Equal((InputDatagramOptions)0x80, h.Flags);
    }

    [Fact]
    public void Payload_golden_bytes()
    {
        var buffer = new byte[InputPayload.MaxSize];
        int n = InputPayload.Write(buffer, 0x01020304, Pen, Touches, [Earlier]);
        Assert.Equal(Hex.Of(Hex.Bytes(GoldenPayload)), Hex.Of(buffer.AsSpan(0, n)));
        Assert.Equal(51, n);

        Assert.True(InputPayload.TryRead(Hex.Bytes(GoldenPayload), out InputPayload back));
        Assert.Equal(0x01020304u, back.TimeUs);
        Assert.Equal(Pen, back.Pen);
        Assert.Equal(Touches, back.Touches.ToArray());
        Assert.Equal([Earlier], back.History.ToArray());
        Assert.Equal(51, back.Size);
    }

    [Fact]
    public void Empty_state_is_seven_bytes()
    {
        Span<byte> buffer = stackalloc byte[InputPayload.MinSize];
        Assert.Equal(7, InputPayload.Write(buffer, 5, null, [], []));
        Assert.Equal("00000005000000", Hex.Of(buffer));
        Assert.True(InputPayload.TryRead(buffer, out InputPayload back));
        Assert.Null(back.Pen);
        Assert.True(back.Touches.IsEmpty);
        Assert.True(back.History.IsEmpty);
    }

    [Fact]
    public void Datagram_layout_slices_header_payload_and_tag()
    {
        var datagram = new byte[InputDatagram.MaxSize];
        int payloadLength = InputPayload.GetSize(true, 2, 1);
        InputDatagram.Slice(datagram, payloadLength, out Span<byte> header, out Span<byte> payload, out Span<byte> tag);
        new InputDatagramHeader(InputDatagramOptions.None, 0xA1B2C3D4, 0x0102).Write(header);
        InputPayload.Write(payload, 0x01020304, Pen, Touches, [Earlier]);
        tag.Fill(0xEE);
        int total = InputDatagram.GetSize(payloadLength);
        Assert.Equal(16 + 51 + 16, total);

        string expected = "01000000A1B2C3D40000000000000102" + Hex.Of(Hex.Bytes(GoldenPayload)) + new string('E', 32);
        Assert.Equal(expected, Hex.Of(datagram.AsSpan(0, total)));

        Assert.True(InputDatagram.TrySplit(datagram.AsSpan(0, total), out InputDatagramHeader h, out ReadOnlySpan<byte> aad, out ReadOnlySpan<byte> ct, out ReadOnlySpan<byte> t));
        Assert.Equal(0x0102ul, h.Sequence);
        Assert.Equal(16, aad.Length);
        Assert.Equal(Hex.Of(Hex.Bytes(GoldenPayload)), Hex.Of(ct));
        Assert.Equal(new string('E', 32), Hex.Of(t));
    }

    [Fact]
    public void Max_payload_fits_in_a_datagram()
    {
        Assert.Equal(211, InputPayload.MaxSize);
        Assert.True(InputDatagram.GetSize(InputPayload.MaxSize) <= InputDatagram.MaxSize);
    }

    [Fact]
    public void Split_rejects_wrong_sizes()
    {
        byte[] valid = new byte[InputDatagram.MinSize];
        valid[0] = 1;
        Assert.True(InputDatagram.TrySplit(valid, out _, out _, out _, out _));
        Assert.False(InputDatagram.TrySplit(valid.AsSpan(0, InputDatagram.MinSize - 1), out _, out _, out _, out _));
        byte[] big = new byte[InputDatagram.MaxSize + 1];
        big[0] = 1;
        Assert.False(InputDatagram.TrySplit(big, out _, out _, out _, out _));
    }

    [Fact]
    public void Full_payload_round_trips()
    {
        var touches = new TouchSample[InputPayload.MaxTouches];
        for (int i = 0; i < touches.Length; i++)
        {
            touches[i] = new TouchSample((byte)i, i % 2 == 0, (ushort)(i * 1000), (ushort)(65535 - i));
        }

        var history = new PenSample[InputPayload.MaxHistory];
        for (int i = 0; i < history.Length; i++)
        {
            history[i] = Pen with { X = (ushort)i, TiltX = -9000, TiltY = 9000, Rotation = 35999, Tool = PenTool.FingerFallback };
        }

        var payload = new InputPayload { TimeUs = uint.MaxValue, Pen = Pen };
        payload.SetTouches(touches);
        payload.SetHistory(history);
        var buffer = new byte[InputPayload.MaxSize];
        Assert.Equal(InputPayload.MaxSize, payload.Write(buffer));
        Assert.True(InputPayload.TryRead(buffer, out InputPayload back));
        Assert.Equal(touches, back.Touches.ToArray());
        Assert.Equal(history, back.History.ToArray());
        Assert.Equal(uint.MaxValue, back.TimeUs);
    }

    [Theory]
    [InlineData("00000000 02 00 00")]
    [InlineData("00000000 00 0B 00")]
    [InlineData("00000000 00 00 09")]
    [InlineData("00000000 00 00 01")]
    [InlineData("00000000 00 01 010200000000")]
    [InlineData("00000000 00 02 010200000000 010200000000 00")]
    [InlineData("00000000 01 00 0000 0000 0000 0000 0000 0000 00 00 00 00 00")]
    [InlineData("00000000 01 00 0000 0000 0000 0000 0000 0000 04 00 00 00 00")]
    [InlineData("00000000 01 00 0000 0000 0000 0000 0000 0000 01 04 00 00 00")]
    [InlineData("00000000 01 00 0000 0000 0000 0000 0000 0000 01 00 04 00 00")]
    [InlineData("00000000 01 00 0000 0000 0000 DCD7 0000 0000 01 00 00 00 00")]
    [InlineData("00000000 01 00 0000 0000 0000 0000 2329 0000 01 00 00 00 00")]
    [InlineData("00000000 01 00 0000 0000 0000 0000 0000 8CA0 01 00 00 00 00")]
    [InlineData("00000000 00 01 01 01 0000 0000 00")]
    public void Invalid_payloads_are_rejected(string hex) => Assert.False(InputPayload.TryRead(Hex.Bytes(hex), out _));

    [Fact]
    public void Writer_rejects_invalid_state()
    {
        var buffer = new byte[InputPayload.MaxSize];
        Assert.Throws<ArgumentOutOfRangeException>(() => InputPayload.Write(buffer, 0, null, new TouchSample[11], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => InputPayload.Write(buffer, 0, null, [], new PenSample[9]));
        Assert.Throws<ArgumentException>(() => InputPayload.Write(buffer, 0, null, [new(1, true, 0, 0), new(1, true, 1, 1)], []));
        Assert.Throws<ArgumentException>(() => InputPayload.Write(buffer, 0, Pen with { TiltX = 9001 }, [], []));
        Assert.Throws<ArgumentException>(() => InputPayload.Write(buffer, 0, Pen with { Tool = PenTool.None }, [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => InputPayload.Write(buffer.AsSpan(0, 10), 0, Pen, [], []));
    }

    [Fact]
    public void Payload_write_and_read_do_not_allocate()
    {
        Span<byte> datagram = stackalloc byte[InputDatagram.MaxSize];
        TouchSample[] touches = Touches;
        PenSample[] history = [Earlier, Earlier];
        int payloadLength = InputPayload.GetSize(true, touches.Length, history.Length);

        void Once(Span<byte> buffer, ulong sequence)
        {
            InputDatagram.Slice(buffer, payloadLength, out Span<byte> header, out Span<byte> payload, out _);
            new InputDatagramHeader(InputDatagramOptions.None, 42, sequence).Write(header);
            InputPayload.Write(payload, (uint)sequence, Pen, touches, history);
            Assert.True(InputDatagram.TrySplit(buffer[..InputDatagram.GetSize(payloadLength)], out InputDatagramHeader h, out _, out ReadOnlySpan<byte> body, out _));
            Assert.True(InputPayload.TryRead(body, out InputPayload decoded));
            var frame = new InputFrameMessage(decoded);
            Span<byte> frameBuffer = stackalloc byte[ControlFrame.HeaderSize + InputPayload.MaxSize];
            ControlFrame.Write(frameBuffer, frame);
            if (decoded.Touches.Length != 2 || h.Sequence != sequence || decoded.Pen is null)
            {
                Assert.Fail("decode mismatch");
            }
        }

        Once(datagram, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong i = 2; i < 10_002; i++)
        {
            Once(datagram, i);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
