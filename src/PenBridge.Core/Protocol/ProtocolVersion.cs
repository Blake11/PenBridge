using System.Globalization;

namespace PenBridge.Core.Protocol;

/// <summary>
/// Protocol version as <c>major.minor</c>. On the wire it is two bytes (<c>u8 major ‖ u8 minor</c>);
/// in text (Bonjour TXT <c>v=</c>, QR <c>v=</c>) it is <c>"1.0"</c>.
/// </summary>
public readonly record struct ProtocolVersion(byte Major, byte Minor)
{
    /// <summary>Size of the binary encoding in bytes.</summary>
    public const int Size = 2;

    /// <summary>Version spoken by this build.</summary>
    public static ProtocolVersion Current { get; } = new(1, 0);

    /// <summary>Peers are compatible when the majors match; minors only gate optional features.</summary>
    public bool IsCompatibleWith(ProtocolVersion other) => Major == other.Major;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");

    /// <summary>Parses strictly <c>digits "." digits</c> with each part 0..255 (no signs, spaces or leading zeros beyond "0").</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out ProtocolVersion version)
    {
        version = default;
        int dot = text.IndexOf('.');
        if (dot <= 0 || dot == text.Length - 1)
        {
            return false;
        }

        if (!TryParsePart(text[..dot], out byte major) || !TryParsePart(text[(dot + 1)..], out byte minor))
        {
            return false;
        }

        version = new ProtocolVersion(major, minor);
        return true;
    }

    private static bool TryParsePart(ReadOnlySpan<char> part, out byte value)
    {
        value = 0;
        if (part.Length is 0 or > 3 || (part.Length > 1 && part[0] == '0'))
        {
            return false;
        }

        int acc = 0;
        foreach (char c in part)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            acc = (acc * 10) + (c - '0');
        }

        if (acc > byte.MaxValue)
        {
            return false;
        }

        value = (byte)acc;
        return true;
    }

    /// <summary>Writes <c>u8 major ‖ u8 minor</c>.</summary>
    public void Write(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Size, nameof(destination));
        destination[0] = Major;
        destination[1] = Minor;
    }

    /// <summary>Reads <c>u8 major ‖ u8 minor</c>.</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out ProtocolVersion version)
    {
        if (source.Length < Size)
        {
            version = default;
            return false;
        }

        version = new ProtocolVersion(source[0], source[1]);
        return true;
    }
}
