using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PenBridge.Core.Security;

/// <summary>
/// HKDF-SHA256 key derivations of the key schedule (Phase 1, plus the 1.3 video keys). All outputs are 32 bytes.
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
    internal static ReadOnlySpan<byte> VideoServerToClientInfo => "pb-video-s2c-v1"u8;
    internal static ReadOnlySpan<byte> VideoClientToServerInfo => "pb-video-c2s-v1"u8;

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
        => DeriveSessionKey(deviceKey, clientNonce, serverNonce, InputKeyInfo, sessionId, destination);

    /// <summary>
    /// Per-session video key (protocol 1.3): <c>K_vs</c> (Mac → phone video, <see cref="VideoKeyDirection.ServerToClient"/>)
    /// or <c>K_vc</c> (phone → Mac probes, <see cref="VideoKeyDirection.ClientToServer"/>) = HKDF-SHA256(ikm = <c>K_dev</c>,
    /// salt = clientNonce ‖ serverNonce, info = "pb-video-s2c-v1" / "pb-video-c2s-v1" ‖ u32 BE sessionId). Independent
    /// of <c>K_in</c>, so the video and input nonces never share a key.
    /// </summary>
    public static void DeriveVideoKey(
        ReadOnlySpan<byte> deviceKey,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> serverNonce,
        uint sessionId,
        VideoKeyDirection direction,
        Span<byte> destination)
    {
        ReadOnlySpan<byte> info = direction switch
        {
            VideoKeyDirection.ServerToClient => VideoServerToClientInfo,
            VideoKeyDirection.ClientToServer => VideoClientToServerInfo,
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };
        DeriveSessionKey(deviceKey, clientNonce, serverNonce, info, sessionId, destination);
    }

    private static void DeriveSessionKey(
        ReadOnlySpan<byte> deviceKey,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> serverNonce,
        ReadOnlySpan<byte> label,
        uint sessionId,
        Span<byte> destination)
    {
        RequireLength(deviceKey, KeyLength, nameof(deviceKey));
        RequireLength(clientNonce, NonceLength, nameof(clientNonce));
        RequireLength(serverNonce, NonceLength, nameof(serverNonce));

        Span<byte> salt = stackalloc byte[NonceLength * 2];
        clientNonce.CopyTo(salt);
        serverNonce.CopyTo(salt[NonceLength..]);

        Span<byte> info = stackalloc byte[label.Length + 4];
        label.CopyTo(info);
        BinaryPrimitives.WriteUInt32BigEndian(info[label.Length..], sessionId);

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

/// <summary>Which of the two per-session video keys (<see cref="KeySchedule.DeriveVideoKey"/>).</summary>
public enum VideoKeyDirection
{
    /// <summary><c>K_vs</c>: Mac → phone video fragments.</summary>
    ServerToClient = 1,

    /// <summary><c>K_vc</c>: phone → Mac probes.</summary>
    ClientToServer = 2,
}
