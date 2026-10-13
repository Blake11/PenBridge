using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Protocol;

public class ProtocolVersionTests
{
    [Fact]
    public void Same_major_is_compatible_and_different_major_is_not()
    {
        Assert.True(new ProtocolVersion(1, 0).IsCompatibleWith(new ProtocolVersion(1, 7)));
        Assert.False(new ProtocolVersion(1, 0).IsCompatibleWith(new ProtocolVersion(2, 0)));
    }

    [Theory]
    [InlineData("1.0", 1, 0)]
    [InlineData("2.15", 2, 15)]
    [InlineData("255.255", 255, 255)]
    [InlineData("0.0", 0, 0)]
    public void Parses_valid_text(string text, int major, int minor)
    {
        Assert.True(ProtocolVersion.TryParse(text, out ProtocolVersion v));
        Assert.Equal(new ProtocolVersion((byte)major, (byte)minor), v);
        Assert.Equal(text, v.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.")]
    [InlineData(".0")]
    [InlineData("1.0.0")]
    [InlineData("256.0")]
    [InlineData("01.0")]
    [InlineData("-1.0")]
    [InlineData(" 1.0")]
    [InlineData("1.0 ")]
    [InlineData("a.b")]
    [InlineData("1000.0")]
    public void Rejects_invalid_text(string text) => Assert.False(ProtocolVersion.TryParse(text, out _));

    [Fact]
    public void Binary_is_major_then_minor()
    {
        Span<byte> buffer = stackalloc byte[2];
        new ProtocolVersion(1, 2).Write(buffer);
        Assert.Equal("0102", Hex.Of(buffer));
        Assert.True(ProtocolVersion.TryRead(buffer, out ProtocolVersion v));
        Assert.Equal(new ProtocolVersion(1, 2), v);
        Assert.False(ProtocolVersion.TryRead(buffer[..1], out _));
    }
}
