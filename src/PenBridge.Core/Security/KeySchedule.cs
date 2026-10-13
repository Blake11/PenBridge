using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PenBridge.Core.Security;

/// <summary>
/// HKDF-SHA256 key derivations of the Phase 1 key schedule. All outputs are 32 bytes.
/// </summary>
public static class KeySchedule
{
    /// <summary>Length of every derived key.</summary>
    public const int KeyLength = 32;

    /// <summary>Length of the client and server nonces.</summary>
    public const int NonceLength = 32;

    internal static ReadOnlySpan<byte> DeviceKeyInfo => "pb-device-key-v1"u8;
    internal static ReadOnlySpan<byte> ConfirmKeyInfo => "pb-confirm-v1"u8;
    internal static ReadOnlySpan<byte> InputKeyInfo => "pb-input-v1"u8;

    /// <summary>
    /// <c>K_dev</c> = HKDF-SHA256(ikm = J-PAKE shared key, salt = transcript hash, info = "pb-device-key-v1").
    /// </summary>
    public static void DeriveDeviceKey(ReadOnlySpan<byte> sharedKey, ReadOnlySpan<byte> transcriptHash, Span<byte> destination)
        => Hkdf(sharedKey, transcriptHash, DeviceKeyInfo, Exact(destination));

    /// <summary>
    /// Per-session <c>K_in</c> = HKDF-SHA256(ikm = <c>K_dev</c>, salt = clientNonce ‖ serverNonce,
    /// info = "pb-input-v1" ‖ u32 BE sessionId).
    /// </summary>
    public static void DeriveInputKey(
        ReadOnlySpan<byte> deviceKey,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> serverNonce,
        uint sessionId,
        Span<byte> destination)
    {
        RequireLength(deviceKey, KeyLength, nameof(deviceKey));
        RequireLength(clientNonce, NonceLength, nameof(clientNonce));
        RequireLength(serverNonce, NonceLength, nameof(serverNonce));

        Span<byte> salt = stackalloc byte[NonceLength * 2];
        clientNonce.CopyTo(salt);
        serverNonce.CopyTo(salt[NonceLength..]);

        Span<byte> info = stackalloc byte[11 + 4];
        InputKeyInfo.CopyTo(info);
        BinaryPrimitives.WriteUInt32BigEndian(info[11..], sessionId);

        Hkdf(deviceKey, salt, info, Exact(destination));
    }

    /// <summary>Raw HKDF-SHA256 (RFC 5869) extract-and-expand.</summary>
    internal static void Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, Span<byte> output)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt, info);

    internal static void RequireLength(ReadOnlySpan<byte> value, int length, string name)
    {
        if (value.Length != length)
        {
            throw new ArgumentException($"Expected {length} bytes.", name);
        }
    }

    private static Span<byte> Exact(Span<byte> destination)
    {
        if (destination.Length < KeyLength)
        {
            throw new ArgumentException("Destination must hold 32 bytes.", nameof(destination));
        }

        return destination[..KeyLength];
    }
}
