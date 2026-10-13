using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Security;
using BigInteger = Org.BouncyCastle.Math.BigInteger;

namespace PenBridge.Core.Security;

/// <summary>State of a <see cref="PairingSession"/>.</summary>
public enum PairingState
{
    /// <summary>Rounds or confirmation still in progress.</summary>
    InProgress,

    /// <summary>The peer's confirmation MAC verified; <c>K_dev</c> is available.</summary>
    Confirmed,

    /// <summary>The peer sent invalid data or a wrong confirmation (wrong secret or relay). Terminal.</summary>
    Failed,
}

/// <summary>
/// One side of J-PAKE pairing (RFC 8236, NIST 3072-bit prime-order group, SHA-256) with key
/// confirmation bound to the Mac's TLS certificate fingerprint (ADR-0009).
/// </summary>
/// <remarks>
/// <para><b>Message order</b> (the protocol layer carries the blobs opaquely):
/// C→S Round1 (+ secretKind) · S→C Round1 · C→S Round2 · S→C Round2 · C→S Confirm · S→C Confirm.
/// The server verifies the client's confirmation before it may create its own.</para>
/// <para><b>Server:</b> on the client's Round1, get the secret from <see cref="PinIssuer.TryBeginAttempt"/>,
/// construct the session, call <see cref="CreateRound1"/> then <see cref="TryProcessPeerRound1"/>, and so on.
/// Report the result with <see cref="PinAttempt.Complete"/>.</para>
/// <para><b>Wire encoding</b> (all integers unsigned big-endian, fixed width; E = 384, S = 32):
/// Round1 = gx1(E) ‖ gx2(E) ‖ zkp1.gv(E) ‖ zkp1.r(S) ‖ zkp2.gv(E) ‖ zkp2.r(S) = 1600 B.
/// Round2 = A(E) ‖ zkp.gv(E) ‖ zkp.r(S) = 800 B. Confirm = 32 B. Any other length is rejected.
/// Participant ids are the fixed role names "pb-client"/"pb-server" and are not sent.</para>
/// <para><b>Key schedule:</b> secret chars = "pb-pin:" + 6 digits, or "pb-qr:" + lowercase hex of the
/// 32-byte token. transcriptHash = SHA-256("pb-pair-v1" ‖ u8 secretKind ‖ deviceId(16, big-endian
/// GUID) ‖ challenge(32) ‖ C.Round1 ‖ S.Round1 ‖ C.Round2 ‖ S.Round2). With ikm = the J-PAKE keying
/// material (E bytes): K_dev = HKDF(ikm, transcriptHash, "pb-device-key-v1");
/// K_confirm = HKDF(ikm, transcriptHash, "pb-confirm-v1").
/// Confirm = HMAC-SHA256(K_confirm, "pb-confirm-client-v1" | "pb-confirm-server-v1" ‖ transcriptHash ‖
/// fingerprint), where the client uses the fingerprint of the certificate it sees and the server its own.</para>
/// <para>Not thread-safe. Order violations throw <see cref="InvalidOperationException"/>; bad peer data
/// returns <see langword="false"/> and moves to <see cref="PairingState.Failed"/>.</para>
/// </remarks>
public sealed class PairingSession : IDisposable
{
    /// <summary>Confirmation MAC length.</summary>
    public const int ConfirmLength = 32;

    /// <summary>Length of the HelloAck challenge mixed into the transcript.</summary>
    public const int ChallengeLength = 32;

    private const string ClientId = "pb-client";
    private const string ServerId = "pb-server";

    private static readonly JPakePrimeOrderGroup Group = JPakePrimeOrderGroups.NIST_3072;
    private static readonly int ElementLength = (Group.P.BitLength + 7) / 8;
    private static readonly int ScalarLength = (Group.Q.BitLength + 7) / 8;

    /// <summary>Encoded Round1 length.</summary>
    public static int Round1Length { get; } = (4 * ElementLength) + (2 * ScalarLength);

    /// <summary>Encoded Round2 length.</summary>
    public static int Round2Length { get; } = (2 * ElementLength) + ScalarLength;

    private readonly PairingRole _role;
    private readonly PairingSecretKind _kind;
    private readonly JPakeParticipant _participant;
    private readonly string _peerId;
    private readonly byte[] _context;
    private readonly byte[] _fingerprint;
    private byte[]? _ownRound1;
    private byte[]? _peerRound1;
    private byte[]? _ownRound2;
    private byte[]? _peerRound2;
    private byte[]? _transcriptHash;
    private readonly byte[] _deviceKey = new byte[KeySchedule.KeyLength];
    private readonly byte[] _confirmKey = new byte[KeySchedule.KeyLength];
    private bool _keysReady;
    private bool _ownRound2Returned;
    private bool _ownConfirmSent;
    private bool _peerConfirmChecked;

    /// <summary>Creates a session.</summary>
    /// <param name="role">This side's role.</param>
    /// <param name="kind">Secret kind; the client sends it with its Round1.</param>
    /// <param name="secret">Pin: the 6 ASCII digits. QrToken: the raw 32-byte token.</param>
    /// <param name="deviceId">The phone's id from Hello.</param>
    /// <param name="challenge">The 32-byte challenge from HelloAck.</param>
    /// <param name="certificateFingerprint">Server: its own certificate fingerprint. Client: the fingerprint
    /// of the certificate presented on this TLS connection.</param>
    /// <param name="random">Randomness source; defaults to the OS CSPRNG.</param>
    public PairingSession(
        PairingRole role,
        PairingSecretKind kind,
        ReadOnlySpan<byte> secret,
        Guid deviceId,
        ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> certificateFingerprint,
        SecureRandom? random = null)
    {
        if (role is not (PairingRole.Client or PairingRole.Server))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        KeySchedule.RequireLength(challenge, ChallengeLength, nameof(challenge));
        KeySchedule.RequireLength(certificateFingerprint, CertificateFingerprint.Length, nameof(certificateFingerprint));

        _role = role;
        _kind = kind;
        _peerId = role == PairingRole.Client ? ServerId : ClientId;
        _fingerprint = certificateFingerprint.ToArray();

        _context = new byte[1 + 16 + ChallengeLength];
        _context[0] = (byte)kind;
        deviceId.TryWriteBytes(_context.AsSpan(1, 16), bigEndian: true, out _);
        challenge.CopyTo(_context.AsSpan(17));

        char[] password = EncodeSecret(kind, secret);
        try
        {
            _participant = new JPakeParticipant(
                role == PairingRole.Client ? ClientId : ServerId,
                password,
                Group,
                new Sha256Digest(),
                random ?? new SecureRandom());
        }
        finally
        {
            Array.Clear(password);
        }
    }

    /// <summary>This side's role.</summary>
    public PairingRole Role => _role;

    /// <summary>The secret kind in use.</summary>
    public PairingSecretKind SecretKind => _kind;

    /// <summary>Current state.</summary>
    public PairingState State { get; private set; }

    /// <summary>Creates this side's Round1 blob.</summary>
    public byte[] CreateRound1()
    {
        EnsureInProgress();
        Require(_ownRound1 is null, "Round1 already created.");
        JPakeRound1Payload payload = _participant.CreateRound1PayloadToSend();
        byte[] blob = new byte[Round1Length];
        var w = new Writer(blob);
        w.Element(payload.Gx1);
        w.Element(payload.Gx2);
        w.Element(payload.KnowledgeProofForX1[0]);
        w.Scalar(payload.KnowledgeProofForX1[1]);
        w.Element(payload.KnowledgeProofForX2[0]);
        w.Scalar(payload.KnowledgeProofForX2[1]);
        _ownRound1 = blob;
        return (byte[])blob.Clone();
    }

    /// <summary>Validates the peer's Round1 blob (requires <see cref="CreateRound1"/> first).</summary>
    public bool TryProcessPeerRound1(ReadOnlySpan<byte> blob)
    {
        EnsureInProgress();
        Require(_ownRound1 is not null, "Create Round1 first.");
        Require(_peerRound1 is null, "Peer Round1 already processed.");
        if (blob.Length != Round1Length)
        {
            return Fail();
        }

        byte[] copy = blob.ToArray();
        var r = new Reader(copy);
        if (!r.Element(out var gx1) || !r.Element(out var gx2) ||
            !r.Element(out var gv1) || !r.Scalar(out var r1) ||
            !r.Element(out var gv2) || !r.Scalar(out var r2))
        {
            return Fail();
        }

        try
        {
            _participant.ValidateRound1PayloadReceived(new JPakeRound1Payload(_peerId, gx1, gx2, [gv1, r1], [gv2, r2]));
        }
        catch (CryptoException)
        {
            return Fail();
        }

        _peerRound1 = copy;
        return true;
    }

    /// <summary>Creates this side's Round2 blob (requires the peer's Round1).</summary>
    public byte[] CreateRound2()
    {
        EnsureInProgress();
        Require(_peerRound1 is not null, "Process the peer's Round1 first.");
        Require(!_ownRound2Returned, "Round2 already created.");
        EnsureOwnRound2();
        _ownRound2Returned = true;
        return (byte[])_ownRound2!.Clone();
    }

    // BouncyCastle requires a participant to create its own Round2 before validating the peer's,
    // so it is generated on first need and cached until CreateRound2 hands it out.
    private void EnsureOwnRound2()
    {
        if (_ownRound2 is not null)
        {
            return;
        }

        JPakeRound2Payload payload = _participant.CreateRound2PayloadToSend();
        byte[] blob = new byte[Round2Length];
        var w = new Writer(blob);
        w.Element(payload.A);
        w.Element(payload.KnowledgeProofForX2s[0]);
        w.Scalar(payload.KnowledgeProofForX2s[1]);
        _ownRound2 = blob;
    }

    /// <summary>Validates the peer's Round2 blob (requires the peer's Round1).</summary>
    public bool TryProcessPeerRound2(ReadOnlySpan<byte> blob)
    {
        EnsureInProgress();
        Require(_peerRound1 is not null, "Process the peer's Round1 first.");
        Require(_peerRound2 is null, "Peer Round2 already processed.");
        if (blob.Length != Round2Length)
        {
            return Fail();
        }

        EnsureOwnRound2();
        byte[] copy = blob.ToArray();
        var r = new Reader(copy);
        if (!r.Element(out var a) || !r.Element(out var gv) || !r.Scalar(out var rr))
        {
            return Fail();
        }

        try
        {
            _participant.ValidateRound2PayloadReceived(new JPakeRound2Payload(_peerId, a, [gv, rr]));
        }
        catch (CryptoException)
        {
            return Fail();
        }

        _peerRound2 = copy;
        return true;
    }

    /// <summary>
    /// Creates this side's 32-byte confirmation. Requires both Round2 blobs; the server additionally
    /// requires the client's confirmation to have verified.
    /// </summary>
    public byte[] CreateConfirm()
    {
        EnsureNotFailed();
        Require(!_ownConfirmSent, "Confirm already created.");
        Require(_role == PairingRole.Client || State == PairingState.Confirmed, "Server must verify the client's confirmation first.");
        EnsureKeys();
        byte[] mac = new byte[ConfirmLength];
        ComputeConfirm(_role, mac);
        _ownConfirmSent = true;
        ZeroConfirmKeyIfDone();
        return mac;
    }

    private void ZeroConfirmKeyIfDone()
    {
        if (_ownConfirmSent && State == PairingState.Confirmed)
        {
            CryptographicOperations.ZeroMemory(_confirmKey);
        }
    }

    /// <summary>
    /// Verifies the peer's confirmation in constant time. A mismatch means a wrong secret or a relay
    /// (different certificate fingerprints). The client must have created its own confirmation first.
    /// </summary>
    public bool TryVerifyPeerConfirm(ReadOnlySpan<byte> mac)
    {
        EnsureInProgress();
        Require(!_peerConfirmChecked, "Peer confirmation already checked.");
        Require(_role == PairingRole.Server || _ownConfirmSent, "Client must create its confirmation first.");
        EnsureKeys();
        _peerConfirmChecked = true;
        if (mac.Length != ConfirmLength)
        {
            return Fail();
        }

        Span<byte> expected = stackalloc byte[ConfirmLength];
        ComputeConfirm(_role == PairingRole.Client ? PairingRole.Server : PairingRole.Client, expected);
        bool ok = CryptographicOperations.FixedTimeEquals(expected, mac);
        CryptographicOperations.ZeroMemory(expected);
        if (!ok)
        {
            return Fail();
        }

        State = PairingState.Confirmed;
        ZeroConfirmKeyIfDone();
        return true;
    }

    /// <summary>Copies the 32-byte <c>K_dev</c> (only after <see cref="PairingState.Confirmed"/>).</summary>
    public void CopyDeviceKey(Span<byte> destination)
    {
        Require(State == PairingState.Confirmed, "Pairing is not confirmed.");
        if (destination.Length < KeySchedule.KeyLength)
        {
            throw new ArgumentException("Destination must hold 32 bytes.", nameof(destination));
        }

        _deviceKey.CopyTo(destination);
    }

    /// <summary>Zeroes key material.</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_deviceKey);
        CryptographicOperations.ZeroMemory(_confirmKey);
    }

    internal static char[] EncodeSecret(PairingSecretKind kind, ReadOnlySpan<byte> secret)
    {
        switch (kind)
        {
            case PairingSecretKind.Pin:
                if (secret.Length != PinIssuer.PinLength)
                {
                    throw new ArgumentException("PIN must be 6 ASCII digits.", nameof(secret));
                }

                char[] pin = new char[7 + PinIssuer.PinLength];
                "pb-pin:".CopyTo(pin);
                for (int i = 0; i < secret.Length; i++)
                {
                    if (!char.IsAsciiDigit((char)secret[i]))
                    {
                        Array.Clear(pin);
                        throw new ArgumentException("PIN must be 6 ASCII digits.", nameof(secret));
                    }

                    pin[7 + i] = (char)secret[i];
                }

                return pin;

            case PairingSecretKind.QrToken:
                if (secret.Length != PinIssuer.QrTokenLength)
                {
                    throw new ArgumentException("QR token must be 32 bytes.", nameof(secret));
                }

                char[] token = new char[6 + (2 * PinIssuer.QrTokenLength)];
                "pb-qr:".CopyTo(token);
                if (!Convert.TryToHexStringLower(secret, token.AsSpan(6), out _))
                {
                    throw new InvalidOperationException("Hex encoding failed.");
                }

                return token;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private void EnsureKeys()
    {
        if (_keysReady)
        {
            return;
        }

        Require(_ownRound2Returned && _peerRound2 is not null, "Both Round2 messages are required.");
        byte[] clientR1 = _role == PairingRole.Client ? _ownRound1! : _peerRound1!;
        byte[] serverR1 = _role == PairingRole.Client ? _peerRound1! : _ownRound1!;
        byte[] clientR2 = _role == PairingRole.Client ? _ownRound2! : _peerRound2!;
        byte[] serverR2 = _role == PairingRole.Client ? _peerRound2! : _ownRound2!;

        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            hash.AppendData("pb-pair-v1"u8);
            hash.AppendData(_context);
            hash.AppendData(clientR1);
            hash.AppendData(serverR1);
            hash.AppendData(clientR2);
            hash.AppendData(serverR2);
            _transcriptHash = hash.GetHashAndReset();
        }

        BigInteger keying = _participant.CalculateKeyingMaterial();
        byte[] ikm = new byte[ElementLength];
        try
        {
            new Writer(ikm).Element(keying);
            KeySchedule.DeriveDeviceKey(ikm, _transcriptHash, _deviceKey);
            KeySchedule.Hkdf(ikm, _transcriptHash, KeySchedule.ConfirmKeyInfo, _confirmKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
        }

        _keysReady = true;
    }

    private void ComputeConfirm(PairingRole sender, Span<byte> destination)
    {
        ReadOnlySpan<byte> label = sender == PairingRole.Client ? "pb-confirm-client-v1"u8 : "pb-confirm-server-v1"u8;
        Span<byte> message = stackalloc byte[20 + 32 + CertificateFingerprint.Length];
        label.CopyTo(message);
        _transcriptHash.CopyTo(message[20..]);
        _fingerprint.CopyTo(message[52..]);
        HMACSHA256.HashData(_confirmKey, message, destination);
    }

    private bool Fail()
    {
        State = PairingState.Failed;
        CryptographicOperations.ZeroMemory(_deviceKey);
        CryptographicOperations.ZeroMemory(_confirmKey);
        return false;
    }

    private void EnsureInProgress()
    {
        EnsureNotFailed();
        Require(State == PairingState.InProgress, "Pairing already confirmed.");
    }

    private void EnsureNotFailed() => Require(State != PairingState.Failed, "Pairing has failed.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private ref struct Writer(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;
        private int _offset;

        public void Element(BigInteger value) => Put(value, ElementLength);

        public void Scalar(BigInteger value) => Put(value, ScalarLength);

        private void Put(BigInteger value, int width)
        {
            byte[] bytes = value.ToByteArrayUnsigned();
            if (value.SignValue < 0 || bytes.Length > width)
            {
                throw new InvalidOperationException("Value does not fit its fixed width.");
            }

            Span<byte> slot = _buffer.Slice(_offset, width);
            slot[..(width - bytes.Length)].Clear();
            bytes.CopyTo(slot[(width - bytes.Length)..]);
            _offset += width;
        }
    }

    private ref struct Reader(byte[] buffer)
    {
        private readonly byte[] _buffer = buffer;
        private int _offset;

        public bool Element(out BigInteger value) => Take(ElementLength, Group.P, out value);

        public bool Scalar(out BigInteger value) => Take(ScalarLength, Group.Q, out value);

        private bool Take(int width, BigInteger bound, out BigInteger value)
        {
            value = new BigInteger(1, _buffer, _offset, width);
            _offset += width;
            return value.CompareTo(bound) < 0;
        }
    }
}
