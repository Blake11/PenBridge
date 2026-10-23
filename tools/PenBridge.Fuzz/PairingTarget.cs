using System.Text;
using Org.BouncyCastle.Security;
using PenBridge.Core.Security;

namespace PenBridge.Fuzz;

/// <summary>
/// The J-PAKE blobs a peer sends before pairing completes (untrusted until key confirmation): the peer's Round1,
/// Round2 and confirmation MAC, fed to <see cref="PairingSession.TryProcessPeerRound1"/>,
/// <see cref="PairingSession.TryProcessPeerRound2"/> and <see cref="PairingSession.TryVerifyPeerConfirm"/>.
/// Input: <c>u8 selector ‖ blob</c>. Bit 7 picks the side under test: 0 = the Mac (server) reading the phone's
/// blobs, 1 = the phone (client) reading the Mac's. The low bits (mod 3) pick the step; the earlier steps replay a
/// real exchange recorded at startup, so the fuzzed blob meets a session in the right state.
/// </summary>
/// <remarks>
/// Each execution builds fresh sessions with fixed-seed randomness, so they write the same blobs as the recorded
/// exchange, and the recorded peer blobs stay valid. That holds fully on the client side. The server side gets a
/// new <see cref="PinIssuer"/> (a random PIN) each time, so its Round1/Round2 checks see real blobs, but a
/// recorded client confirmation never matches there.
/// Contract: the Try* methods return a bool and never throw; on false the session is Failed; and a blob that
/// differs from the recorded one is never accepted (that would mean a forged proof or MAC).
/// </remarks>
internal static class PairingTarget
{
    private static readonly Guid DeviceId = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly byte[] Challenge = [.. Enumerable.Range(0x40, 32).Select(i => (byte)i)];
    private static readonly byte[] Fingerprint = [.. Enumerable.Range(0x80, 32).Select(i => (byte)i)];
    private static readonly byte[] ClientSeed = Encoding.ASCII.GetBytes("penbridge-fuzz-client");
    private static readonly byte[] ServerSeed = Encoding.ASCII.GetBytes("penbridge-fuzz-server");
    private static readonly Exchange Recorded = Record();

    private sealed record Exchange(byte[] Pin, byte[] C1, byte[] C2, byte[] CConfirm, byte[] S1, byte[] S2, byte[] SConfirm);

    public static int MaxLength => 1 + PairingSession.Round1Length + 64;

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        bool client = (data[0] & 0x80) != 0;
        int step = (data[0] & 0x7F) % 3;
        ReadOnlySpan<byte> blob = data[1..];
        if (client)
        {
            RunClient(step, blob);
        }
        else
        {
            RunServer(step, blob);
        }
    }

    public static List<byte[]> Seeds()
    {
        Exchange e = Recorded;
        return
        [
            [0x00, .. e.C1], [0x01, .. e.C2], [0x02, .. e.CConfirm],
            [0x80, .. e.S1], [0x81, .. e.S2], [0x82, .. e.SConfirm],
            [0x00, .. e.C1.AsSpan(0, 400)], [0x81, .. e.S2.AsSpan(0, e.S2.Length - 1)], [0x82],
        ];
    }

    private static void RunServer(int step, ReadOnlySpan<byte> blob)
    {
        var issuer = new PinIssuer();
        issuer.OpenPairingWindow();
        if (issuer.TryBeginAttempt(PenBridge.Core.Security.PairingSecretKind.Pin, out PinAttempt? attempt) != PairingAttemptStart.Started)
        {
            throw new ContractViolationException("a fresh issuer did not start an attempt");
        }

        using PairingSession server = PairingSession.ForServer(attempt!, DeviceId, Challenge, Fingerprint, Random(ServerSeed));
        _ = server.CreateRound1();
        if (step == 0)
        {
            Verify(server, server.TryProcessPeerRound1(blob), blob, Recorded.C1);
            return;
        }

        Require(server.TryProcessPeerRound1(Recorded.C1), "recorded client Round1 rejected");
        if (step == 1)
        {
            Verify(server, server.TryProcessPeerRound2(blob), blob, Recorded.C2);
            return;
        }

        Require(server.TryProcessPeerRound2(Recorded.C2), "recorded client Round2 rejected");
        _ = server.CreateRound2();

        // The PIN differs from the recorded one, so even the recorded MAC must fail here.
        bool ok = server.TryVerifyPeerConfirm(blob);
        Require(!ok && server.State == PairingState.Failed, "server accepted a confirmation for another PIN");
    }

    private static void RunClient(int step, ReadOnlySpan<byte> blob)
    {
        using PairingSession client = NewClient();
        _ = client.CreateRound1();
        if (step == 0)
        {
            Verify(client, client.TryProcessPeerRound1(blob), blob, Recorded.S1);
            return;
        }

        Require(client.TryProcessPeerRound1(Recorded.S1), "recorded server Round1 rejected");
        _ = client.CreateRound2();
        if (step == 1)
        {
            Verify(client, client.TryProcessPeerRound2(blob), blob, Recorded.S2);
            return;
        }

        Require(client.TryProcessPeerRound2(Recorded.S2), "recorded server Round2 rejected");
        _ = client.CreateConfirm();
        Verify(client, client.TryVerifyPeerConfirm(blob), blob, Recorded.SConfirm);
    }

    private static void Verify(PairingSession session, bool accepted, ReadOnlySpan<byte> blob, byte[] recorded)
    {
        if (accepted)
        {
            Require(blob.SequenceEqual(recorded), "a blob other than the recorded one was accepted");
            Require(session.State != PairingState.Failed, "accepted, but the session failed");
        }
        else
        {
            Require(session.State == PairingState.Failed, "rejected, but the session did not fail");
        }
    }

    private static PairingSession NewClient() =>
        new(PairingRole.Client, PenBridge.Core.Security.PairingSecretKind.Pin, Recorded.Pin, DeviceId, Challenge, Fingerprint, Random(ClientSeed));

    /// <summary>A full exchange with fixed client randomness and a real server; its blobs seed the corpus and the replays.</summary>
    private static Exchange Record()
    {
        var issuer = new PinIssuer();
        issuer.OpenPairingWindow();
        byte[] pin = Encoding.ASCII.GetBytes(issuer.CurrentPin!);
        _ = issuer.TryBeginAttempt(PenBridge.Core.Security.PairingSecretKind.Pin, out PinAttempt? attempt);
        using var client = new PairingSession(PairingRole.Client, PenBridge.Core.Security.PairingSecretKind.Pin, pin, DeviceId, Challenge, Fingerprint, Random(ClientSeed));
        using PairingSession server = PairingSession.ForServer(attempt!, DeviceId, Challenge, Fingerprint, Random(ServerSeed));
        byte[] c1 = client.CreateRound1();
        byte[] s1 = server.CreateRound1();
        Require(server.TryProcessPeerRound1(c1) && client.TryProcessPeerRound1(s1), "recording: Round1");
        byte[] c2 = client.CreateRound2();
        byte[] s2 = server.CreateRound2();
        Require(server.TryProcessPeerRound2(c2) && client.TryProcessPeerRound2(s2), "recording: Round2");
        byte[] cConfirm = client.CreateConfirm();
        Require(server.TryVerifyPeerConfirm(cConfirm), "recording: client confirm");
        byte[] sConfirm = server.CreateConfirm();
        Require(client.TryVerifyPeerConfirm(sConfirm), "recording: server confirm");
        return new Exchange(pin, c1, c2, cConfirm, s1, s2, sConfirm);
    }

    private static SecureRandom Random(byte[] seed)
    {
        SecureRandom random = SecureRandom.GetInstance("SHA256PRNG", autoSeed: false);
        random.SetSeed(seed);
        return random;
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new ContractViolationException(what);
        }
    }
}
