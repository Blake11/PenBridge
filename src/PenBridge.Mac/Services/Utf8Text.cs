using System.Text;

namespace PenBridge.Mac.Services;

/// <summary>UTF-8 length limits for DNS-SD labels and TXT strings.</summary>
public static class Utf8Text
{
    /// <summary>
    /// The longest prefix of <paramref name="value"/> whose UTF-8 encoding fits <paramref name="maxBytes"/>, cut on a
    /// code-point boundary (a surrogate pair is never split). Returns <paramref name="value"/> itself when it fits.
    /// </summary>
    public static string Truncate(string value, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        int length = value.Length;
        while (length > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > maxBytes)
        {
            length--;
            if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            {
                length--;
            }
        }

        return length == value.Length ? value : value[..length];
    }
}
