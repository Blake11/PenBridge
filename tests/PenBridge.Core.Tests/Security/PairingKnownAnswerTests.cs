using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Security;
using PenBridge.Core.Security;
using BigInteger = Org.BouncyCastle.Math.BigInteger;

namespace PenBridge.Core.Tests.Security;

/// <summary>
/// Known-answer tests for the pairing key schedule (ADR-0009). Both sides of a real pairing run the
/// same code, so a change applied to both (transcript order, a dropped context field) still pairs;
/// only a fixed answer catches it.
/// </summary>
/// <remarks>
/// J-PAKE draws all its scalars from the <see cref="SecureRandom"/> each participant is given. A
/// <see cref="DigestRandomGenerator"/> seeded with fixed bytes and never reseeded makes every blob, and
/// so the whole transcript and every key, a pure function of the inputs below.
/// </remarks>
public class PairingKnownAnswerTests
{
    private const int Pin = 123456;
    private const int ElementLength = 384;
    private const int ScalarLength = 32;
    private static readonly Guid DeviceId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly byte[] Challenge = KeyScheduleTests.Fill(32, 21);
    private static readonly byte[] Fingerprint = KeyScheduleTests.Fill(32, 22);
    private static readonly byte[] ClientSeed = KeyScheduleTests.Fill(32, 101);
    private static readonly byte[] ServerSeed = KeyScheduleTests.Fill(32, 202);

    // Pinned outputs of Pair() for the inputs above.
    private const string ExpectedBlobsSha256 = "a225b755d550c1ab5829301ef03da743c34a3b26837fcec7844f3627e95bdfe0";
    private const string ExpectedDeviceKey = "d035fb213d6fe103eabc07c8922a3820dcf664398d9f7869367b103218ecfbb7";
    private const string ExpectedClientConfirm = "1a291c9865b6f50caefcd5a45fec2def16c3bab801f231cadfaba4dd006a6bc2";
    private const string ExpectedServerConfirm = "762a403255a645d3a187806f2be425fa4581619917152dd7f8ae8f31dbf03af7";

    private static SecureRandom FixedRandom(byte[] seed)
    {
        var generator = new DigestRandomGenerator(new Sha256Digest());
        generator.AddSeedMaterial(seed);
        return new SecureRandom(generator);
    }

    private sealed record Transcript(
        byte[] C1, byte[] S1, byte[] C2, byte[] S2, byte[] ClientConfirm, byte[] ServerConfirm, byte[] ClientKey, byte[] ServerKey);

    private static Transcript Pair(Guid serverDeviceId, byte[] serverChallenge)
    {
        var issuer = new PinIssuer(new FakeTime(), () => Pin);
        issuer.OpenPairingWindow();
        Assert.Equal(PairingAttemptStart.Started, issuer.TryBeginAttempt(PairingSecretKind.Pin, out PinAttempt? attempt));
        using var client = new PairingSession(
            PairingRole.Client, PairingSecretKind.Pin, "123456"u8, DeviceId, Challenge, Fingerprint, FixedRandom(ClientSeed));
        using var server = PairingSession.ForServer(attempt!, serverDeviceId, serverChallenge, Fingerprint, FixedRandom(ServerSeed));

        byte[] c1 = client.CreateRound1();
        byte[] s1 = server.CreateRound1();
        Assert.True(server.TryProcessPeerRound1(c1));
        Assert.True(client.TryProcessPeerRound1(s1));
        byte[] c2 = client.CreateRound2();
        Assert.True(server.TryProcessPeerRound2(c2));
        byte[] s2 = server.CreateRound2();
        Assert.True(client.TryProcessPeerRound2(s2));
        byte[] cConfirm = client.CreateConfirm();
        if (!server.TryVerifyPeerConfirm(cConfirm))
        {
            return new Transcript(c1, s1, c2, s2, cConfirm, [], [], []);
        }

        byte[] sConfirm = server.CreateConfirm();
        Assert.True(client.TryVerifyPeerConfirm(sConfirm));
        byte[] kc = new byte[32], ks = new byte[32];
        client.CopyDeviceKey(kc);
        server.CopyDeviceKey(ks);
        return new Transcript(c1, s1, c2, s2, cConfirm, sConfirm, kc, ks);
    }

    [Fact]
    public void Fixed_randomness_makes_pairing_deterministic()
    {
        Transcript a = Pair(DeviceId, Challenge);
        Transcript b = Pair(DeviceId, Challenge);

        Assert.Equal(a.C1, b.C1);
        Assert.Equal(a.S1, b.S1);
        Assert.Equal(a.C2, b.C2);
        Assert.Equal(a.S2, b.S2);
        Assert.Equal(a.ClientKey, b.ClientKey);
        Assert.Equal(a.ClientKey, a.ServerKey);
    }

    [Fact]
    public void Pairing_matches_the_documented_key_schedule()
    {
        Transcript t = Pair(DeviceId, Challenge);
        Reference r = Reference.Run();

        // Wire encoding: the same J-PAKE payloads, fixed width, big-endian, in the documented order.
        Assert.Equal(r.C1, t.C1);
        Assert.Equal(r.S1, t.S1);
        Assert.Equal(r.C2, t.C2);
        Assert.Equal(r.S2, t.S2);

        // transcriptHash = SHA-256("pb-pair-v1" ‖ kind ‖ deviceId ‖ challenge ‖ C.R1 ‖ S.R1 ‖ C.R2 ‖ S.R2).
        byte[] deviceId = new byte[16];
        Assert.True(DeviceId.TryWriteBytes(deviceId, bigEndian: true, out _));
        byte[] transcriptHash = SHA256.HashData(
            (byte[])[.. "pb-pair-v1"u8, (byte)PairingSecretKind.Pin, .. deviceId, .. Challenge, .. t.C1, .. t.S1, .. t.C2, .. t.S2]);
        byte[] deviceKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, r.KeyingMaterial, 32, transcriptHash, "pb-device-key-v1"u8.ToArray());
        byte[] confirmKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, r.KeyingMaterial, 32, transcriptHash, "pb-confirm-v1"u8.ToArray());
        byte[] clientConfirm = HMACSHA256.HashData(confirmKey, (byte[])[.. "pb-confirm-client-v1"u8, .. transcriptHash, .. Fingerprint]);
        byte[] serverConfirm = HMACSHA256.HashData(confirmKey, (byte[])[.. "pb-confirm-server-v1"u8, .. transcriptHash, .. Fingerprint]);

        Assert.Equal(deviceKey, t.ClientKey);
        Assert.Equal(deviceKey, t.ServerKey);
        Assert.Equal(clientConfirm, t.ClientConfirm);
        Assert.Equal(serverConfirm, t.ServerConfirm);
    }

    [Fact]
    public void Pairing_reproduces_the_pinned_answer()
    {
        // If only the first assert fails, J-PAKE consumed the fixed randomness differently (new inputs),
        // not a key-schedule change: re-pin all four values after checking the test above still passes.
        Transcript t = Pair(DeviceId, Challenge);

        Assert.Equal(ExpectedBlobsSha256, Convert.ToHexStringLower(SHA256.HashData((byte[])[.. t.C1, .. t.S1, .. t.C2, .. t.S2])));
        Assert.Equal(ExpectedDeviceKey, Convert.ToHexStringLower(t.ClientKey));
        Assert.Equal(ExpectedClientConfirm, Convert.ToHexStringLower(t.ClientConfirm));
        Assert.Equal(ExpectedServerConfirm, Convert.ToHexStringLower(t.ServerConfirm));
    }

    // One case per transcript context field. The secret kind is the third field, but a kind mismatch
    // also changes the J-PAKE password ("pb-pin:" vs "pb-qr:"), so it can't be isolated here; the
    // known answers above cover the kind byte.
    [Fact]
    public void Server_with_a_different_device_id_rejects_the_client_confirm()
    {
        Transcript t = Pair(Guid.Parse("00112233-4455-6677-8899-aabbccddeef0"), Challenge);
        Assert.Empty(t.ServerConfirm);
    }

    [Fact]
    public void Server_with_a_different_challenge_rejects_the_client_confirm()
    {
        byte[] other = (byte[])Challenge.Clone();
        other[^1] ^= 1;
        Transcript t = Pair(DeviceId, other);
        Assert.Empty(t.ServerConfirm);
    }

    /// <summary>The same J-PAKE run straight on BouncyCastle, encoded by hand from the documented layout.</summary>
    private sealed record Reference(byte[] C1, byte[] S1, byte[] C2, byte[] S2, byte[] KeyingMaterial)
    {
        public static Reference Run()
        {
            JPakePrimeOrderGroup group = JPakePrimeOrderGroups.NIST_3072;
            char[] password = "pb-pin:123456".ToCharArray();
            var client = new JPakeParticipant("pb-client", password, group, new Sha256Digest(), FixedRandom(ClientSeed));
            var server = new JPakeParticipant("pb-server", password, group, new Sha256Digest(), FixedRandom(ServerSeed));

            JPakeRound1Payload c1 = client.CreateRound1PayloadToSend();
            JPakeRound1Payload s1 = server.CreateRound1PayloadToSend();
            server.ValidateRound1PayloadReceived(c1);
            client.ValidateRound1PayloadReceived(s1);
            JPakeRound2Payload c2 = client.CreateRound2PayloadToSend();
            JPakeRound2Payload s2 = server.CreateRound2PayloadToSend();
            server.ValidateRound2PayloadReceived(c2);
            client.ValidateRound2PayloadReceived(s2);
            BigInteger keying = client.CalculateKeyingMaterial();
            Assert.Equal(keying, server.CalculateKeyingMaterial());

            return new Reference(Round1(c1), Round1(s1), Round2(c2), Round2(s2), Fixed(keying, ElementLength));
        }

        private static byte[] Round1(JPakeRound1Payload p) =>
        [
            .. Fixed(p.Gx1, ElementLength), .. Fixed(p.Gx2, ElementLength),
            .. Fixed(p.KnowledgeProofForX1[0], ElementLength), .. Fixed(p.KnowledgeProofForX1[1], ScalarLength),
            .. Fixed(p.KnowledgeProofForX2[0], ElementLength), .. Fixed(p.KnowledgeProofForX2[1], ScalarLength),
        ];

        private static byte[] Round2(JPakeRound2Payload p) =>
        [
            .. Fixed(p.A, ElementLength), .. Fixed(p.KnowledgeProofForX2s[0], ElementLength), .. Fixed(p.KnowledgeProofForX2s[1], ScalarLength),
        ];

        private static byte[] Fixed(BigInteger value, int width)
        {
            byte[] bytes = value.ToByteArrayUnsigned();
            Assert.InRange(bytes.Length, 0, width);
            byte[] result = new byte[width];
            bytes.CopyTo(result, width - bytes.Length);
            return result;
        }
    }
}
