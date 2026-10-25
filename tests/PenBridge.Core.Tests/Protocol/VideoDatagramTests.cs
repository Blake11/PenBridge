using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Protocol;

/// <summary>The 24-byte video datagram header (docs/specs/screen-mirroring.md "Video datagram").</summary>
public class VideoDatagramTests
{
    [Fact]
    public void Sizes_match_the_spec()
    {
        Assert.Equal(24, VideoDatagramHeader.Size);
        Assert.Equal(1200, VideoDatagram.MaxSize);
        Assert.Equal(1160, VideoDatagram.MaxPayloadSize);
        Assert.Equal(1152, VideoDatagram.FirstFragmentDataSize);
        Assert.Equal(40, VideoDatagram.MinSize);
        Assert.Equal(1024, VideoDatagram.MaxFragments);
        Assert.Equal(1 << 20, VideoDatagram.MaxFrameLength);
        Assert.Equal(24 + 1 + 16, VideoDatagram.GetSize(1));
        Assert.Equal(1, (int)VideoDatagramKind.VideoFragment);
        Assert.Equal(2, (int)VideoDatagramKind.Probe);
        Assert.Equal(1, (int)VideoDatagramOptions.Keyframe);
    }

    [Fact]
    public void Fragment_header_golden()
    {
        var header = VideoDatagramHeader.Fragment(0x07, true, 0xA1B2C3D4, 0x0102030405060708, 0x11223344, 3, 10);
        byte[] bytes = new byte[24];
        header.Write(bytes);
        Assert.Equal("01 01 07 01 A1B2C3D4 0102030405060708 11223344 0003 000A".Replace(" ", string.Empty, StringComparison.Ordinal), Hex.Of(bytes));
        Assert.True(VideoDatagramHeader.TryRead(bytes, out VideoDatagramHeader back));
        Assert.Equal(header, back);
        Assert.True(back.IsKeyframe);
    }

    [Fact]
    public void Probe_header_golden()
    {
        var header = VideoDatagramHeader.Probe(0xFE, 5, 9);
        byte[] bytes = new byte[24];
        header.Write(bytes);
        Assert.Equal("01 02 FE 00 00000005 0000000000000009 00000000 0000 0000".Replace(" ", string.Empty, StringComparison.Ordinal), Hex.Of(bytes));
        Assert.True(VideoDatagramHeader.TryRead(bytes, out VideoDatagramHeader back));
        Assert.Equal(header, back);
        Assert.False(back.IsKeyframe);
    }

    [Theory]
    [InlineData("00 01 07 00 00000001 0000000000000001 00000001 0000 0001")] // version 0
    [InlineData("02 01 07 00 00000001 0000000000000001 00000001 0000 0001")] // version 2
    [InlineData("01 00 07 00 00000001 0000000000000001 00000001 0000 0001")] // kind 0
    [InlineData("01 03 07 00 00000001 0000000000000001 00000001 0000 0001")] // kind 3
    [InlineData("01 01 07 00 00000001 0000000000000001 00000001 0000 0000")] // count 0
    [InlineData("01 01 07 00 00000001 0000000000000001 00000001 0000 0401")] // count 1025
    [InlineData("01 01 07 00 00000001 0000000000000001 00000001 0001 0001")] // index == count
    [InlineData("01 01 07 00 00000001 0000000000000001 00000001 FFFF 0400")] // index past count
    [InlineData("01 02 07 00 00000001 0000000000000001 00000001 0000 0000")] // probe with a frame number
    [InlineData("01 02 07 00 00000001 0000000000000001 00000000 0001 0000")] // probe with an index
    [InlineData("01 02 07 00 00000001 0000000000000001 00000000 0000 0001")] // probe with a count
    [InlineData("01 01 07 00 00000001 0000000000000001 00000001 0000")]      // truncated
    [InlineData("01 01 07 00 00000001 0000000000000001 00000000 0000 0001")] // a fragment of frame 0 (none)
    public void Invalid_headers_are_dropped(string hex)
    {
        Assert.False(VideoDatagramHeader.TryRead(Hex.Bytes(hex), out VideoDatagramHeader header));
        Assert.Equal(default, header);
    }

    [Theory]
    [InlineData("01 01 07 FE 00000001 0000000000000001 00000001 0000 0001")] // unknown flag bits are ignored
    [InlineData("01 01 07 00 00000001 0000000000000000 00000001 03FF 0400")] // last of 1024
    [InlineData("01 02 07 FF 00000001 0000000000000001 00000000 0000 0000")] // probe flags ignored
    public void Valid_edge_headers_are_read(string hex) => Assert.True(VideoDatagramHeader.TryRead(Hex.Bytes(hex), out _));

    [Fact]
    public void Write_rejects_inconsistent_headers_and_short_buffers()
    {
        byte[] bytes = new byte[24];
        Assert.Throws<ArgumentException>(() => VideoDatagramHeader.Fragment(1, false, 1, 1, 1, 1, 1).Write(bytes));
        Assert.Throws<ArgumentException>(() => VideoDatagramHeader.Fragment(1, false, 1, 1, 1, 0, 1025).Write(bytes));
        Assert.Throws<ArgumentException>(() => VideoDatagramHeader.Fragment(1, false, 1, 1, 0, 0, 1).Write(bytes));
        Assert.Throws<ArgumentException>(() => new VideoDatagramHeader(VideoDatagramKind.Probe, 1, 0, 1, 1, 1, 0, 0).Write(bytes));
        Assert.Throws<ArgumentException>(() => new VideoDatagramHeader((VideoDatagramKind)9, 1, 0, 1, 1, 0, 0, 0).Write(bytes));
        Assert.Equal("destination", Assert.Throws<ArgumentOutOfRangeException>(() => VideoDatagramHeader.Probe(1, 1, 1).Write(bytes.AsSpan(0, 23))).ParamName);
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var random = new Random(7);
        byte[] buffer = new byte[30];
        for (int i = 0; i < 20_000; i++)
        {
            random.NextBytes(buffer);
            buffer[0] = (byte)(i % 3);
            int length = random.Next(buffer.Length + 1);
            if (VideoDatagramHeader.TryRead(buffer.AsSpan(0, length), out VideoDatagramHeader header))
            {
                Assert.True(header.IsConsistent);
            }
        }
    }
}
