using PenBridge.Core.Mirror;
using PenBridge.Core.Tests.Input;

namespace PenBridge.Core.Tests.Mirror;

public sealed class AnnexBBufferTests
{
    [Fact]
    public void AppendNal_puts_a_start_code_before_each_unit()
    {
        var buffer = new AnnexBBuffer(4);
        buffer.AppendNal([0x40, 0x01]);
        buffer.AppendNal([0x42]);

        Assert.Equal([0, 0, 0, 1, 0x40, 0x01, 0, 0, 0, 1, 0x42], buffer.Written.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void AppendLengthPrefixed_converts_every_unit(int lengthSize)
    {
        byte[] first = [0x26, 0x01, 0xAF];
        byte[] second = [0x02];
        byte[] data = [.. Prefix(first.Length, lengthSize), .. first, .. Prefix(second.Length, lengthSize), .. second];
        var buffer = new AnnexBBuffer();

        Assert.True(buffer.AppendLengthPrefixed(data, lengthSize));
        Assert.Equal([0, 0, 0, 1, 0x26, 0x01, 0xAF, 0, 0, 0, 1, 0x02], buffer.Written.ToArray());
    }

    [Fact]
    public void AppendLengthPrefixed_skips_empty_units_and_accepts_empty_input()
    {
        var buffer = new AnnexBBuffer();

        Assert.True(buffer.AppendLengthPrefixed([], 4));
        Assert.True(buffer.AppendLengthPrefixed([0, 0, 0, 0, 0, 0, 0, 1, 0x7F], 4));
        Assert.Equal([0, 0, 0, 1, 0x7F], buffer.Written.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(-1)]
    public void AppendLengthPrefixed_rejects_an_unsupported_length_size(int lengthSize)
    {
        var buffer = new AnnexBBuffer();

        Assert.False(buffer.AppendLengthPrefixed([0, 0, 0, 1, 0x11], lengthSize));
        Assert.Equal(0, buffer.Written.Length);
    }

    [Fact]
    public void AppendLengthPrefixed_rejects_a_length_past_the_end_and_keeps_earlier_units()
    {
        var buffer = new AnnexBBuffer();

        Assert.False(buffer.AppendLengthPrefixed([0, 1, 0xAA, 0, 5, 0xBB], 2));
        Assert.Equal([0, 0, 0, 1, 0xAA], buffer.Written.ToArray());
    }

    [Fact]
    public void AppendLengthPrefixed_rejects_a_truncated_length_field()
    {
        var buffer = new AnnexBBuffer();

        Assert.False(buffer.AppendLengthPrefixed([0, 0, 0, 1, 0xAA, 0, 0], 4));
        Assert.Equal([0, 0, 0, 1, 0xAA], buffer.Written.ToArray());
    }

    [Fact]
    public void AppendLengthPrefixed_rejects_a_huge_length_without_reading_past_the_input()
    {
        var buffer = new AnnexBBuffer();

        Assert.False(buffer.AppendLengthPrefixed([0xFF, 0xFF, 0xFF, 0xFF, 0x01], 4));
        Assert.Equal(0, buffer.Written.Length);
    }

    [Fact]
    public void Reset_starts_a_new_access_unit()
    {
        var buffer = new AnnexBBuffer();
        buffer.AppendNal([1, 2, 3]);
        buffer.Reset();
        buffer.AppendNal([9]);

        Assert.Equal([0, 0, 0, 1, 9], buffer.Written.ToArray());
    }

    [Fact]
    public void Grows_for_a_frame_larger_than_its_capacity_and_keeps_the_bytes()
    {
        var buffer = new AnnexBBuffer(8);
        byte[] small = [1, 2, 3];
        byte[] large = new byte[10_000];
        for (int i = 0; i < large.Length; i++)
        {
            large[i] = (byte)i;
        }

        buffer.AppendNal(small);
        buffer.AppendNal(large);

        Assert.Equal([0, 0, 0, 1, 1, 2, 3, 0, 0, 0, 1, .. large], buffer.Written.ToArray());
    }

    [Fact]
    public void Constructor_rejects_a_capacity_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnnexBBuffer(0));
    }

    private static byte[] Prefix(int length, int size)
    {
        var bytes = new byte[size];
        for (int i = size - 1; i >= 0; i--)
        {
            bytes[i] = (byte)length;
            length >>= 8;
        }

        return bytes;
    }
}

/// <summary>Steady state (frames no larger than before) allocates nothing: the encoder callback runs per frame.</summary>
[Collection(AllocationGroup.Name)]
public sealed class AnnexBBufferAllocationTests
{
    [Fact]
    public void Reset_and_append_do_not_allocate_once_grown()
    {
        var buffer = new AnnexBBuffer(64);
        byte[] avcc = new byte[3 * (4 + 500)];
        for (int n = 0; n < 3; n++)
        {
            avcc[n * 504 + 2] = 500 >> 8;
            avcc[n * 504 + 3] = 500 & 0xFF;
        }

        byte[] parameterSet = [0x67, 0x42, 0x00, 0x1F];
        long ran = 0;
        Alloc.AssertZero(2_000, i =>
        {
            buffer.Reset();
            if (i % 2 == 0)
            {
                buffer.AppendNal(parameterSet);
            }

            if (buffer.AppendLengthPrefixed(avcc, 4))
            {
                ran++;
            }
        }, () => ran);
    }
}
