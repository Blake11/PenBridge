using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PenBridge.Core.Security;

/// <summary>SHA-256 fingerprint of a certificate's DER encoding (32 bytes), as pinned by the phone.</summary>
public static class CertificateFingerprint
{
    /// <summary>Fingerprint length in bytes.</summary>
    public const int Length = 32;

    /// <summary>Computes the fingerprint of <paramref name="certificate"/>.</summary>
    public static byte[] Compute(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return SHA256.HashData(certificate.GetRawCertData());
    }

    /// <summary>Writes the fingerprint of the DER-encoded certificate into <paramref name="destination"/>.</summary>
    public static void Compute(ReadOnlySpan<byte> certificateDer, Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException("Destination must hold 32 bytes.", nameof(destination));
        }

        SHA256.HashData(certificateDer, destination);
    }

    /// <summary>
    /// Constant-time check that <paramref name="certificate"/> has the pinned fingerprint. Returns
    /// <see langword="false"/> for a null certificate or a pin of the wrong length.
    /// </summary>
    public static bool Matches(X509Certificate? certificate, ReadOnlySpan<byte> pinnedFingerprint)
    {
        if (certificate is null || pinnedFingerprint.Length != Length)
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[Length];
        SHA256.HashData(certificate.GetRawCertData(), actual);
        return CryptographicOperations.FixedTimeEquals(actual, pinnedFingerprint);
    }

    /// <summary>Unpadded base64url form used in the Bonjour TXT record and QR payload.</summary>
    public static string ToBase64Url(ReadOnlySpan<byte> fingerprint) => Base64Url.EncodeToString(fingerprint);

    /// <summary>Parses the base64url form; fails unless it decodes to exactly 32 bytes.</summary>
    public static bool TryParseBase64Url(ReadOnlySpan<char> text, Span<byte> destination)
    {
        if (destination.Length < Length || text.Length > 64)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[48];
        OperationStatus status = Base64Url.DecodeFromChars(text, buffer, out int consumed, out int written);
        if (status != OperationStatus.Done || consumed != text.Length || written != Length)
        {
            return false;
        }

        buffer[..Length].CopyTo(destination);
        return true;
    }
}
