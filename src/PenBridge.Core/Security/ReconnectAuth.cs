using System.Security.Cryptography;

namespace PenBridge.Core.Security;

/// <summary>
/// Reconnect authentication of a paired phone (control message <c>Auth</c>):
/// <c>proof = HMAC-SHA256(K_dev, "pb-auth-v1" ‖ challenge(32) ‖ clientNonce(32) ‖ certFingerprint(32))</c>.
/// The client uses the fingerprint it pinned; the server uses its own, so a relay fails.
/// </summary>
public static class ReconnectAuth
{
    /// <summary>Length of the challenge sent in HelloAck.</summary>
    public const int ChallengeLength = 32;

    /// <summary>Length of the proof.</summary>
    public const int ProofLength = 32;

    private static ReadOnlySpan<byte> Label => "pb-auth-v1"u8;

    /// <summary>Client side: computes the proof into <paramref name="proof"/> (32 bytes).</summary>
    public static void ComputeProof(
        ReadOnlySpan<byte> deviceKey,
        ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> certFingerprint,
        Span<byte> proof)
    {
        KeySchedule.RequireLength(deviceKey, KeySchedule.KeyLength, nameof(deviceKey));
        KeySchedule.RequireLength(challenge, ChallengeLength, nameof(challenge));
        KeySchedule.RequireLength(clientNonce, KeySchedule.NonceLength, nameof(clientNonce));
        KeySchedule.RequireLength(certFingerprint, CertificateFingerprint.Length, nameof(certFingerprint));
        if (proof.Length < ProofLength)
        {
            throw new ArgumentException("Destination must hold 32 bytes.", nameof(proof));
        }

        Span<byte> message = stackalloc byte[10 + ChallengeLength + KeySchedule.NonceLength + CertificateFingerprint.Length];
        Label.CopyTo(message);
        challenge.CopyTo(message[10..]);
        clientNonce.CopyTo(message[(10 + ChallengeLength)..]);
        certFingerprint.CopyTo(message[(10 + ChallengeLength + KeySchedule.NonceLength)..]);
        HMACSHA256.HashData(deviceKey, message, proof[..ProofLength]);
    }

    /// <summary>
    /// Server side: constant-time verification of <paramref name="proof"/>. Returns
    /// <see langword="false"/> (never throws) for a proof of the wrong length.
    /// </summary>
    public static bool Verify(
        ReadOnlySpan<byte> deviceKey,
        ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> serverFingerprint,
        ReadOnlySpan<byte> proof)
    {
        if (proof.Length != ProofLength || clientNonce.Length != KeySchedule.NonceLength)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[ProofLength];
        ComputeProof(deviceKey, challenge, clientNonce, serverFingerprint, expected);
        bool ok = CryptographicOperations.FixedTimeEquals(expected, proof);
        CryptographicOperations.ZeroMemory(expected);
        return ok;
    }
}
