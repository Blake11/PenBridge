using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;

namespace PenBridge.Core.Protocol;

/// <summary>Bounds-checked big-endian cursor over untrusted bytes. Every read returns false instead of throwing.</summary>
internal ref struct WireReader(ReadOnlySpan<byte> source)
{
    private readonly ReadOnlySpan<byte> _source = source;
    private int _position;

    public readonly int Remaining => _source.Length - _position;

    public bool TryReadU8(out byte value)
    {
        if (Remaining < 1)
        {
            value = 0;
            return false;
        }

        value = _source[_position++];
        return true;
    }

    public bool TryReadU16(out ushort value)
    {
        if (Remaining < 2)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16BigEndian(_source.Slice(_position, 2));
        _position += 2;
        return true;
    }

    public bool TryReadI16(out short value)
    {
        bool ok = TryReadU16(out ushort raw);
        value = unchecked((short)raw);
        return ok;
    }

    public bool TryReadU32(out uint value)
    {
        if (Remaining < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(_source.Slice(_position, 4));
        _position += 4;
        return true;
    }

    public bool TryReadU64(out ulong value)
    {
        if (Remaining < 8)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64BigEndian(_source.Slice(_position, 8));
        _position += 8;
        return true;
    }

    public bool TryReadF32(out float value)
    {
        if (Remaining < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadSingleBigEndian(_source.Slice(_position, 4));
        _position += 4;
        return true;
    }

    public bool TryReadSpan(int length, out ReadOnlySpan<byte> value)
    {
        if (length < 0 || Remaining < length)
        {
            value = default;
            return false;
        }

        value = _source.Slice(_position, length);
        _position += length;
        return true;
    }

    /// <summary>Reads a fixed-length field into a new array.</summary>
    public bool TryReadFixed(int length, out ReadOnlyMemory<byte> value)
    {
        if (!TryReadSpan(length, out ReadOnlySpan<byte> span))
        {
            value = default;
            return false;
        }

        value = span.ToArray();
        return true;
    }

    /// <summary>Reads a <c>u16</c> length-prefixed opaque blob into a new array.</summary>
    public bool TryReadBlob(out ReadOnlyMemory<byte> value)
    {
        value = default;
        return TryReadU16(out ushort length) && TryReadFixed(length, out value);
    }

    /// <summary>Reads a <c>u16</c> length-prefixed UTF-8 string, at most <see cref="WireString.MaxBytes"/> bytes, strictly valid UTF-8.</summary>
    public bool TryReadString(out string value)
    {
        value = string.Empty;
        if (!TryReadU16(out ushort length) || length > WireString.MaxBytes || !TryReadSpan(length, out ReadOnlySpan<byte> bytes))
        {
            return false;
        }

        if (!Utf8.IsValid(bytes))
        {
            return false;
        }

        value = Encoding.UTF8.GetString(bytes);
        return true;
    }
}

/// <summary>Big-endian cursor over a destination sized by the caller beforehand.</summary>
internal ref struct WireWriter(Span<byte> destination)
{
    private readonly Span<byte> _destination = destination;
    private int _position;

    public readonly int Written => _position;

    public void WriteU8(byte value) => _destination[_position++] = value;

    public void WriteU16(ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_destination.Slice(_position, 2), value);
        _position += 2;
    }

    public void WriteI16(short value) => WriteU16(unchecked((ushort)value));

    public void WriteU32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_destination.Slice(_position, 4), value);
        _position += 4;
    }

    public void WriteU64(ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(_destination.Slice(_position, 8), value);
        _position += 8;
    }

    public void WriteF32(float value)
    {
        BinaryPrimitives.WriteSingleBigEndian(_destination.Slice(_position, 4), value);
        _position += 4;
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(_destination[_position..]);
        _position += value.Length;
    }

    public void WriteBlob(ReadOnlySpan<byte> value)
    {
        WriteU16(checked((ushort)value.Length));
        WriteBytes(value);
    }

    public void WriteString(string value)
    {
        int length = Encoding.UTF8.GetBytes(value, _destination[(_position + 2)..]);
        WriteU16(checked((ushort)length));
        _position += length;
    }
}

/// <summary>Rules for wire strings: <c>u16</c> length + UTF-8, at most 256 bytes.</summary>
public static class WireString
{
    /// <summary>Maximum UTF-8 byte length of a wire string.</summary>
    public const int MaxBytes = 256;

    /// <summary>Encoded size including the <c>u16</c> prefix. Throws when the string is longer than <see cref="MaxBytes"/>.</summary>
    internal static int GetSize(string? value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        int bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > MaxBytes)
        {
            throw new ArgumentException($"String exceeds {MaxBytes} UTF-8 bytes; use {nameof(WireString)}.{nameof(Truncate)}.", paramName);
        }

        return 2 + bytes;
    }

    /// <summary>Truncates <paramref name="value"/> at a character boundary so it fits in <paramref name="maxBytes"/> UTF-8 bytes.</summary>
    public static string Truncate(string value, int maxBytes = MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        int used = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }

            used += rune.Utf8SequenceLength;
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }
}
