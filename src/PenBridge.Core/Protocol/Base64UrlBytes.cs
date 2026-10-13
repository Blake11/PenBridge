using System.Buffers;
using System.Buffers.Text;

namespace PenBridge.Core.Protocol;

/// <summary>Base64url (no padding) helpers for 32-byte values: the SHA-256 cert fingerprint and the QR token.</summary>
public static class Base64UrlBytes
{
    /// <summary>Size of a SHA-256 fingerprint and of the QR one-time token.</summary>
    public const int Length32 = 32;

    /// <summary>Base64url length of 32 bytes without padding.</summary>
    public const int EncodedLength32 = 43;

    /// <summary>Encodes without padding.</summary>
    public static string Encode(ReadOnlySpan<byte> value) => Base64Url.EncodeToString(value);

    /// <summary>Decodes exactly <see cref="Length32"/> bytes from unpadded base64url; false for anything else.</summary>
    public static bool TryDecode32(ReadOnlySpan<char> text, out byte[] value)
    {
        value = [];
        if (text.Length != EncodedLength32)
        {
            return false;
        }

        // The Try* decoders throw FormatException on invalid input; the OperationStatus overload doesn't.
        Span<byte> buffer = stackalloc byte[Length32];
        if (Base64Url.DecodeFromChars(text, buffer, out int consumed, out int written) != OperationStatus.Done
            || consumed != text.Length
            || written != Length32)
        {
            return false;
        }

        // Reject non-canonical encodings (non-zero unused bits in the last character).
        Span<char> canonical = stackalloc char[EncodedLength32];
        if (!Base64Url.TryEncodeToChars(buffer, canonical, out int charsWritten)
            || charsWritten != EncodedLength32
            || !canonical.SequenceEqual(text))
        {
            return false;
        }

        value = buffer.ToArray();
        return true;
    }
}
