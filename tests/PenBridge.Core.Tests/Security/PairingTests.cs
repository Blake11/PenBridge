using System.Text;
using PenBridge.Core.Security;

namespace PenBridge.Core.Tests.Security;

public class PairingTests
{
    private static readonly Guid DeviceId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private readonly byte[] _challenge = KeyScheduleTests.Fill(32, 21);
    private readonly byte[] _serverFp = KeyScheduleTests.Fill(32, 22);

    /// <summary>Runs the full message flow; returns the server-side result and both sessions.</summary>
    private (PairingResult Result, PairingSession Client, PairingSession Server) Run(
        PinIssuer issuer,
        PairingSecretKind kind,
        byte[] clientSecret,
        byte[]? clientSeesFingerprint = null,
        Action? beforeServerVerify = null)
    {
        var client = new PairingSession(PairingRole.Client, kind, clientSecret, DeviceId, _challenge, clientSeesFingerprint ?? _serverFp);
        byte[] c1 = client.CreateRound1();

        Assert.Equal(PairingAttemptStart.Started, issuer.TryBeginAttempt(kind, out PinAttempt? attempt));
        var server = PairingSession.ForServer(attempt!, DeviceId, _challenge, _serverFp);
        // Not disposed here: tests read K_dev afterwards. Verify/Fail already completed the attempt.
        {
            byte[] s1 = server.CreateRound1();
            Assert.True(server.TryProcessPeerRound1(c1));
            Assert.True(client.TryProcessPeerRound1(s1));

            byte[] c2 = client.CreateRound2();
            Assert.True(server.TryProcessPeerRound2(c2));
            byte[] s2 = server.CreateRound2();
            Assert.True(client.TryProcessPeerRound2(s2));

            byte[] cConfirm = client.CreateConfirm();
            beforeServerVerify?.Invoke();
            if (!server.TryVerifyPeerConfirm(cConfirm))
            {
                Assert.Equal(PairingState.Failed, server.State);
                Assert.Throws<InvalidOperationException>(() => server.CreateConfirm()); // never confirms
                return (server.AttemptResult!.Value, client, server);
            }

            byte[] sConfirm = server.CreateConfirm();
            Assert.True(client.TryVerifyPeerConfirm(sConfirm));
            return (server.AttemptResult!.Value, client, server);
        }
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static string WrongPin(string? pin) => pin == "000000" ? "000001" : "000000";

    private static PinIssuer Open(FakeTime time, Func<int>? pins = null)
    {
        int n = 0;
        var issuer = new PinIssuer(time, pins ?? (() => (++n * 135791) % 1_000_000));
        issuer.OpenPairingWindow();
        return issuer;
    }

    [Fact]
    public void Pin_pairing_succeeds_and_both_sides_derive_the_same_device_key()
    {
        var issuer = Open(new FakeTime());
        string pin = issuer.CurrentPin!;

        var (result, client, server) = Run(issuer, PairingSecretKind.Pin, Ascii(pin!));

        Assert.Equal(PairingOutcome.Ok, result.Outcome);
        Assert.Equal(PairingState.Confirmed, client.State);
        Assert.Equal(PairingState.Confirmed, server.State);
        byte[] kc = new byte[32], ks = new byte[32];
        client.CopyDeviceKey(kc);
        server.CopyDeviceKey(ks);
        Assert.Equal(kc, ks);
        Assert.NotEqual(new byte[32], kc);
        Assert.Null(issuer.CurrentPin); // one-time: consumed, window closed
        Assert.False(issuer.IsWindowOpen);
        Assert.Equal(PairingAttemptStart.WindowClosed, issuer.TryBeginAttempt(PairingSecretKind.Pin, out _));
    }

    [Fact]
    public void Qr_token_pairing_succeeds()
    {
        var issuer = Open(new FakeTime());
        byte[] token = System.Buffers.Text.Base64Url.DecodeFromChars(issuer.CurrentQrTokenBase64Url!);
        Assert.Equal(32, token.Length);

        var (result, client, _) = Run(issuer, PairingSecretKind.QrToken, token);

        Assert.Equal(PairingOutcome.Ok, result.Outcome);
        Assert.Equal(PairingState.Confirmed, client.State);
    }

    [Fact]
    public void Wrong_qr_token_fails()
    {
        var issuer = Open(new FakeTime());
        var (result, _, server) = Run(issuer, PairingSecretKind.QrToken, new byte[32]);
        Assert.Equal(PairingOutcome.Failed, result.Outcome);
        Assert.Equal(PairingState.Failed, server.State);
    }

    [Fact]
    public void Pin_digits_do_not_work_as_a_qr_secret_kind()
    {
        // Same kind on both ends but the client claims QrToken: the server feeds the token, not the PIN.
        var issuer = Open(new FakeTime());
        byte[] pinAsToken = new byte[32];
        Ascii(issuer.CurrentPin!).CopyTo(pinAsToken, 0);
        var (result, _, _) = Run(issuer, PairingSecretKind.QrToken, pinAsToken);
        Assert.Equal(PairingOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void Wrong_pin_fails_at_server_confirmation_with_attempts_left()
    {
        var issuer = Open(new FakeTime());
        string pin = issuer.CurrentPin!;

        var (result, client, server) = Run(issuer, PairingSecretKind.Pin, Ascii(WrongPin(pin)));

        Assert.Equal(new PairingResult(PairingOutcome.Failed, 2), result);
        Assert.Equal(PairingState.Failed, server.State);
        Assert.Equal(PairingState.InProgress, client.State); // never receives a server confirm
        Assert.Throws<InvalidOperationException>(() => server.CreateConfirm());
        Assert.Throws<InvalidOperationException>(() => server.CopyDeviceKey(new byte[32]));
        Assert.Equal(pin, issuer.CurrentPin); // still valid for a retry
    }

    [Fact]
    public void Three_failures_lock_out_and_regenerate_the_pin()
    {
        int seq = 0;
        var time = new FakeTime();
        var issuer = Open(time, () => 111111 * (++seq % 9));
        string pin = issuer.CurrentPin!;
        int changed = 0;
        issuer.SecretsChanged += (_, _) => changed++;

        Assert.Equal(new PairingResult(PairingOutcome.Failed, 2), Run(issuer, PairingSecretKind.Pin, Ascii(WrongPin(pin))).Result);
        Assert.Equal(new PairingResult(PairingOutcome.Failed, 1), Run(issuer, PairingSecretKind.Pin, Ascii(WrongPin(pin))).Result);
        Assert.Equal(new PairingResult(PairingOutcome.Failed, 0), Run(issuer, PairingSecretKind.Pin, Ascii(WrongPin(pin))).Result);

        Assert.Equal(1, changed);
        Assert.NotEqual(pin, issuer.CurrentPin);
        Assert.Equal(3, issuer.AttemptsLeft);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(30), issuer.LockedUntil);
        Assert.Equal(PairingAttemptStart.LockedOut, issuer.TryBeginAttempt(PairingSecretKind.Pin, out PinAttempt? none));
        Assert.Null(none);

        // After the backoff, the old PIN is useless even if guessed correctly.
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(PairingOutcome.Failed, Run(issuer, PairingSecretKind.Pin, Ascii(pin!)).Result.Outcome);
    }

    [Fact]
    public void Expired_pin_is_rejected_and_replaced()
    {
        var time = new FakeTime();
        var issuer = Open(time, () => 123456);
        Assert.Equal("123456", issuer.CurrentPin);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(2), issuer.ExpiresAt);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(PairingAttemptStart.Expired, issuer.TryBeginAttempt(PairingSecretKind.Pin, out PinAttempt? attempt));
        Assert.Null(attempt);
        Assert.False(issuer.IsWindowOpen);
        Assert.Null(issuer.CurrentPin);
        Assert.Equal(PairingAttemptStart.WindowClosed, issuer.TryBeginAttempt(PairingSecretKind.Pin, out _));

        issuer.OpenPairingWindow();
        time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(PairingAttemptStart.Started, issuer.TryBeginAttempt(PairingSecretKind.Pin, out attempt));
        attempt!.Dispose();
    }

    [Fact]
    public void Display_access_after_expiry_closes_the_window()
    {
        var time = new FakeTime();
        int n = 0;
        var issuer = Open(time, () => ++n);
        Assert.Equal("000001", issuer.CurrentPin);
        Assert.NotNull(issuer.CurrentQrTokenBase64Url);
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(issuer.CurrentPin);
        Assert.Null(issuer.CurrentQrTokenBase64Url);
        issuer.OpenPairingWindow();
        Assert.Equal("000002", issuer.CurrentPin);
    }

    [Fact]
    public void Regenerated_mid_attempt_reports_expired_without_striking_the_new_pin()
    {
        var issuer = Open(new FakeTime());
        Assert.Equal(PairingAttemptStart.Started, issuer.TryBeginAttempt(PairingSecretKind.Pin, out PinAttempt? attempt));
        issuer.OpenPairingWindow(); // reopened: new secrets
        Assert.Equal(PairingOutcome.Expired, attempt!.Complete(true).Outcome);
        Assert.Equal(3, issuer.AttemptsLeft);
    }

    [Fact]
    public void Only_one_attempt_at_a_time_and_abandoned_attempt_costs_a_strike()
    {
        var issuer = Open(new FakeTime());
        Assert.Equal(PairingAttemptStart.Started, issuer.TryBeginAttempt(PairingSecretKind.Pin, out PinAttempt? first));
        Assert.Equal(PairingAttemptStart.Busy, issuer.TryBeginAttempt(PairingSecretKind.QrToken, out PinAttempt? second));
        Assert.Null(second);

        first!.Dispose();
        Assert.Equal(2, issuer.AttemptsLeft);
        Assert.Equal(-1, first.Secret.IndexOfAnyExcept((byte)0)); // secret copy zeroed
        Assert.Equal(PairingAttemptStart.Started, issuer.TryBeginAttempt(PairingSecretKind.Pin, out second));
        Assert.Equal(new PairingResult(PairingOutcome.Failed, 1), second!.Complete(false));
        Assert.Equal(new PairingResult(PairingOutcome.Failed, 1), second.Complete(true)); // idempotent
    }

    [Fact]
    public void Pins_are_six_digits()
    {
        var issuer = new PinIssuer(new FakeTime());
        for (int i = 0; i < 50; i++)
        {
            issuer.OpenPairingWindow();
            string pin = issuer.CurrentPin!;
            Assert.Equal(6, pin.Length);
            Assert.All(pin, c => Assert.True(char.IsAsciiDigit(c)));
        }

        Assert.Equal("000042", Open(new FakeTime(), () => 42).CurrentPin);
    }

    [Fact]
    public void Relay_with_its_own_certificate_fails_confirmation()
    {
        // The relay forwards every pairing byte verbatim, but the phone's TLS session terminates at
        // the relay, so the phone sees the relay's certificate fingerprint, not the Mac's.
        var issuer = Open(new FakeTime());
        byte[] relayFp = KeyScheduleTests.Fill(32, 99);

        var (result, _, server) = Run(issuer, PairingSecretKind.Pin, Ascii(issuer.CurrentPin!), relayFp);

        Assert.Equal(PairingOutcome.Failed, result.Outcome);
        Assert.Equal(PairingState.Failed, server.State);
    }

    [Fact]
    public void Server_confirm_is_checked_by_the_client()
    {
        // A server whose transcript differs (different challenge) produces a confirm the client rejects.
        byte[] pin = Ascii("424242");
        var client = new PairingSession(PairingRole.Client, PairingSecretKind.Pin, pin, DeviceId, _challenge, _serverFp);
        Open(new FakeTime(), () => 424242).TryBeginAttempt(PairingSecretKind.Pin, out PinAttempt? attempt);
        var server = PairingSession.ForServer(attempt!, DeviceId, KeyScheduleTests.Fill(32, 1), _serverFp);
        Exchange(client, server);

        // The server would reject the client's confirm; force-check the client side with a forged MAC.
        client.CreateConfirm();
        Assert.False(client.TryVerifyPeerConfirm(new byte[32]));
        Assert.Equal(PairingState.Failed, client.State);
        Assert.False(server.TryVerifyPeerConfirm(new byte[32]));
    }

    [Fact]
    public void Reflected_client_round1_is_rejected()
    {
        byte[] pin = Ascii("424242");
        var client = new PairingSession(PairingRole.Client, PairingSecretKind.Pin, pin, DeviceId, _challenge, _serverFp);
        byte[] c1 = client.CreateRound1();
        Assert.False(client.TryProcessPeerRound1(c1)); // proofs are bound to the "pb-client" id
        Assert.Equal(PairingState.Failed, client.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1599)]
    [InlineData(1601)]
    public void Malformed_round1_lengths_are_rejected(int length)
    {
        var server = NewServer();
        server.CreateRound1();
        Assert.False(server.TryProcessPeerRound1(new byte[length]));
        Assert.Equal(PairingState.Failed, server.State);
    }

    [Fact]
    public void Garbage_round_payloads_are_rejected_without_throwing()
    {
        var rng = new Random(1234);
        for (int i = 0; i < 5; i++)
        {
            var server = NewServer();
            server.CreateRound1();
            byte[] junk = new byte[PairingSession.Round1Length];
            rng.NextBytes(junk);
            Assert.False(server.TryProcessPeerRound1(junk));
        }

        var all = NewServer();
        all.CreateRound1();
        Assert.False(all.TryProcessPeerRound1(Enumerable.Repeat((byte)0xFF, PairingSession.Round1Length).ToArray()));

        var zero = NewServer();
        zero.CreateRound1();
        Assert.False(zero.TryProcessPeerRound1(new byte[PairingSession.Round1Length]));
    }

    [Fact]
    public void Garbage_round2_and_confirm_are_rejected()
    {
        byte[] pin = Ascii("424242");
        var client = new PairingSession(PairingRole.Client, PairingSecretKind.Pin, pin, DeviceId, _challenge, _serverFp);
        var server = NewServer();
        byte[] c1 = client.CreateRound1();
        byte[] s1 = server.CreateRound1();
        Assert.True(server.TryProcessPeerRound1(c1));
        Assert.True(client.TryProcessPeerRound1(s1));
        byte[] c2 = client.CreateRound2();
        c2[500] ^= 1;
        Assert.False(server.TryProcessPeerRound2(c2));
        Assert.False(client.TryProcessPeerRound2(new byte[10]));
    }

    [Fact]
    public void Blob_sizes_match_the_documented_encoding()
    {
        Assert.Equal(1600, PairingSession.Round1Length);
        Assert.Equal(800, PairingSession.Round2Length);
        var s = NewServer();
        Assert.Equal(1600, s.CreateRound1().Length);
    }

    [Fact]
    public void Out_of_order_calls_throw()
    {
        var server = NewServer();
        Assert.Throws<InvalidOperationException>(() => server.TryProcessPeerRound1(new byte[1600]));
        Assert.Throws<InvalidOperationException>(() => server.CreateRound2());
        Assert.Throws<InvalidOperationException>(() => server.CreateConfirm());
        server.CreateRound1();
        Assert.Throws<InvalidOperationException>(() => server.CreateRound1());
    }

    [Fact]
    public void Secret_encoding_is_validated_and_domain_separated()
    {
        Assert.Equal("pb-pin:012345", new string(PairingSession.EncodeSecret(PairingSecretKind.Pin, Ascii("012345"))));
        Assert.StartsWith("pb-qr:0000", new string(PairingSession.EncodeSecret(PairingSecretKind.QrToken, new byte[32])), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => PairingSession.EncodeSecret(PairingSecretKind.Pin, Ascii("12345")));
        Assert.Throws<ArgumentException>(() => PairingSession.EncodeSecret(PairingSecretKind.Pin, Ascii("12345a")));
        Assert.Throws<ArgumentException>(() => PairingSession.EncodeSecret(PairingSecretKind.QrToken, new byte[31]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PairingSession.EncodeSecret((PairingSecretKind)9, new byte[6]));
    }

    [Fact]
    public void Attempts_outside_the_pairing_window_are_refused_without_a_strike()
    {
        var issuer = new PinIssuer(new FakeTime());
        Assert.False(issuer.IsWindowOpen);
        Assert.Null(issuer.CurrentPin);
        Assert.Equal(PairingAttemptStart.WindowClosed, issuer.TryBeginAttempt(PairingSecretKind.Pin, out PinAttempt? a));
        Assert.Null(a);
        Assert.Equal(PairingAttemptStart.WindowClosed, issuer.TryBeginAttempt(PairingSecretKind.QrToken, out _));
        Assert.Equal(3, issuer.AttemptsLeft);

        issuer.OpenPairingWindow();
        Assert.NotNull(issuer.CurrentPin);
        issuer.ClosePairingWindow();
        Assert.Null(issuer.CurrentPin);
        Assert.Equal(PairingAttemptStart.WindowClosed, issuer.TryBeginAttempt(PairingSecretKind.Pin, out _));
        issuer.OpenPairingWindow();
        Assert.NotNull(issuer.CurrentPin);
    }

    [Fact]
    public void Lockout_backoff_doubles_to_a_cap_and_resets_only_on_success()
    {
        var time = new FakeTime();
        var issuer = Open(time);
        TimeSpan[] expected = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(480), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)];

        foreach (TimeSpan backoff in expected)
        {
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(PairingAttemptStart.Started, issuer.TryBeginAttempt(PairingSecretKind.Pin, out PinAttempt? a));
                a!.Complete(false);
            }

            Assert.Equal(time.GetUtcNow() + backoff, issuer.LockedUntil);
            time.Advance(backoff - TimeSpan.FromSeconds(1));
            issuer.OpenPairingWindow(); // reopening does not lift the backoff
            Assert.Equal(PairingAttemptStart.LockedOut, issuer.TryBeginAttempt(PairingSecretKind.Pin, out _));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        // Success resets the backoff ladder.
        Assert.Equal(PairingOutcome.Ok, Run(issuer, PairingSecretKind.Pin, Ascii(issuer.CurrentPin!)).Result.Outcome);
        issuer.OpenPairingWindow();
        for (int i = 0; i < 3; i++)
        {
            issuer.TryBeginAttempt(PairingSecretKind.Pin, out PinAttempt? a);
            a!.Complete(false);
        }

        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(30), issuer.LockedUntil);
    }

    [Fact]
    public void Attempt_completing_after_expiry_never_produces_a_server_confirm()
    {
        var time = new FakeTime();
        var issuer = Open(time);
        var (result, client, server) = Run(
            issuer, PairingSecretKind.Pin, Ascii(issuer.CurrentPin!), beforeServerVerify: () => time.Advance(TimeSpan.FromMinutes(2)));

        Assert.Equal(PairingOutcome.Expired, result.Outcome);
        Assert.Equal(PairingState.Failed, server.State);
        Assert.Equal(PairingState.InProgress, client.State);
        Assert.False(issuer.IsWindowOpen);
    }

    [Fact]
    public void Attempt_invalidated_by_window_close_never_produces_a_server_confirm()
    {
        var issuer = Open(new FakeTime());
        var (result, _, server) = Run(
            issuer, PairingSecretKind.Pin, Ascii(issuer.CurrentPin!), beforeServerVerify: issuer.ClosePairingWindow);

        Assert.Equal(PairingOutcome.Expired, result.Outcome);
        Assert.Equal(PairingState.Failed, server.State);
    }

    [Fact]
    public void Server_session_completes_its_attempt_on_failure_or_dispose()
    {
        var issuer = Open(new FakeTime());
        issuer.TryBeginAttempt(PairingSecretKind.Pin, out PinAttempt? a);
        var server = PairingSession.ForServer(a!, DeviceId, _challenge, _serverFp);
        server.CreateRound1();
        Assert.False(server.TryProcessPeerRound1(new byte[3]));
        Assert.Equal(new PairingResult(PairingOutcome.Failed, 2), server.AttemptResult);

        issuer.TryBeginAttempt(PairingSecretKind.Pin, out a);
        PairingSession.ForServer(a!, DeviceId, _challenge, _serverFp).Dispose();
        Assert.Equal(1, issuer.AttemptsLeft);
        Assert.Throws<ArgumentException>(() => PairingSession.ForServer(a!, DeviceId, _challenge, _serverFp));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PairingSession(PairingRole.Server, PairingSecretKind.Pin, Ascii("424242"), DeviceId, _challenge, _serverFp));
    }

    private PairingSession NewServer()
    {
        Open(new FakeTime(), () => 424242).TryBeginAttempt(PairingSecretKind.Pin, out PinAttempt? attempt);
        return PairingSession.ForServer(attempt!, DeviceId, _challenge, _serverFp);
    }

    private static void Exchange(PairingSession client, PairingSession server)
    {
        byte[] c1 = client.CreateRound1();
        byte[] s1 = server.CreateRound1();
        Assert.True(server.TryProcessPeerRound1(c1));
        Assert.True(client.TryProcessPeerRound1(s1));
        Assert.True(server.TryProcessPeerRound2(client.CreateRound2()));
        Assert.True(client.TryProcessPeerRound2(server.CreateRound2()));
    }
}
