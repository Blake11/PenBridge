using System.Security.Authentication;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

#pragma warning disable xUnit1051 // Network waits are bounded by Wait.Limit instead of the test token.

namespace PenBridge.Core.Tests.Net;

/// <summary>Loopback end-to-end: pairing, auth, streaming, Busy/takeover, reconnect.</summary>
public class EndToEndTests
{
    internal static void DrawStroke(PenBridgeClient client)
    {
        Assert.True(client.SendState(Pens.Hover(1000, 1000), []));
        Assert.True(client.SendState(Pens.Contact(1000, 1000), []));
        for (ushort i = 1; i <= 5; i++)
        {
            Assert.True(client.SendState(Pens.Contact((ushort)(1000 + (i * 500)), 1000, (ushort)(20000 + (i * 1000))), []));
        }

        Assert.True(client.SendState(Pens.Hover(4000, 1000), []));
        Assert.True(client.SendState(null, []));
    }

    internal static async Task AssertStrokeAsync(ConcurrentSink sink)
    {
        await Wait.Until(() => sink.PenKinds().Contains(PenEventKind.ProximityLeave), "the stroke's ProximityLeave");
        PenEventKind[] kinds = sink.PenKinds();
        Assert.Equal(PenEventKind.ProximityEnter, kinds[0]);
        int down = Array.IndexOf(kinds, PenEventKind.Down);
        int up = Array.IndexOf(kinds, PenEventKind.Up);
        Assert.InRange(down, 1, kinds.Length);
        Assert.True(up > down, "Up after Down");
        Assert.Contains(PenEventKind.Drag, kinds[down..up]);
        Assert.Equal(PenEventKind.ProximityLeave, kinds[^1]);
        Assert.All(sink.Pen(), e => Assert.False(e.Synthetic));

        // Mapped into the 1000 x 2000 target (the surface is taller than wide: pillarbox centred horizontally).
        PenEvent first = sink.Pen()[0];
        Assert.InRange(first.Position.X, 0, 1000);
        Assert.InRange(first.Position.Y, 0, 2000);
    }

    [Fact]
    public async Task Pin_pairing_then_auth_then_a_stroke_yields_the_expected_edge_events()
    {
        await using var h = new NetHarness();
        var started = new List<SessionInfo>();
        var paired = new List<PairedDevice>();
        h.Server.SessionStarted += (_, e) => started.Add(e.Session);
        h.Server.DevicePaired += (_, e) => paired.Add(e.Device);

        PenBridgeClient client = await h.PairAsync();

        Assert.True(client.IsConnected);
        Assert.Equal("Test Mac", client.ServerName);
        Assert.Equal(h.Identity.Fingerprint.ToArray(), client.ServerFingerprint);
        Assert.True(client.Credentials.IsPaired(h.Identity.Fingerprint));
        Assert.Null(h.Server.CurrentPin); // one-time: the window closed on success
        PairedDevice device = Assert.Single(h.Registry.List());
        Assert.Equal(client.DeviceId, device.DeviceId);
        Assert.Equal("Test Phone", device.Name);
        Assert.Equal(client.DeviceId, Assert.Single(paired).DeviceId);
        Assert.Equal(client.DeviceId, Assert.Single(started).DeviceId);
        Assert.True(client.NegotiatedTlsProtocol is SslProtocols.Tls12 or SslProtocols.Tls13, client.NegotiatedTlsProtocol.ToString());

        DrawStroke(client);
        await AssertStrokeAsync(h.Sink);
        await Wait.Until(() => client.Transport == InputTransport.Udp, "UdpAck");

        PenBridgeServerStats stats = h.Server.GetStats();
        Assert.True(stats.HasSession);
        Assert.Equal(client.DeviceId, stats.DeviceId);
        Assert.Equal(InputTransport.Udp, stats.Transport);
        Assert.True(stats.Counters.Accepted >= 5);
        Assert.Equal(0, stats.Counters.AuthFailures);
    }

    [Fact]
    public async Task Qr_pairing_then_a_stroke_works()
    {
        await using var h = new NetHarness();
        h.Server.OpenPairingWindow();
        PairingQrPayload qr = h.Server.CreateQrPayload("127.0.0.1")!;
        Assert.Equal(h.Port, qr.Port);
        Assert.Equal(h.Identity.Fingerprint.ToArray(), qr.Fingerprint.ToArray());

        PenBridgeClient client = h.CreateClient();
        await client.PairWithQrAsync(qr.Format()).Bounded();
        await h.WaitForSurfaceAsync();

        Assert.Null(h.Server.CreateQrPayload("127.0.0.1")); // token consumed
        DrawStroke(client);
        await AssertStrokeAsync(h.Sink);
    }

    [Fact]
    public async Task Reconnect_authenticates_with_the_stored_key_and_pinned_fingerprint()
    {
        await using var h = new NetHarness();
        var store = new InMemorySecretStore();
        PenBridgeClient first = await h.PairAsync(store);
        uint firstSession = h.Server.ActiveInput!.SessionId;
        await first.DisconnectAsync().Bounded();
        await Wait.Until(() => !h.Server.GetStats().HasSession, "server saw the disconnect");

        PenBridgeClient again = h.CreateClient(store);
        Assert.Equal(first.DeviceId, again.DeviceId);
        await again.ConnectAsync("127.0.0.1", h.Port, h.Identity.Fingerprint.ToArray()).Bounded();
        await h.WaitForSurfaceAsync();

        Assert.NotEqual(firstSession, h.Server.ActiveInput!.SessionId); // fresh session id (and nonces) per session
        DrawStroke(again);
        await AssertStrokeAsync(h.Sink);
    }

    [Fact]
    public async Task Wrong_pin_fails_and_reports_attempts_left()
    {
        await using var h = new NetHarness();
        h.Server.OpenPairingWindow();
        string wrong = h.Server.CurrentPin == "000000" ? "000001" : "000000";
        PenBridgeClient client = h.CreateClient();

        var ex = await Assert.ThrowsAsync<PenBridgeConnectException>(() => client.PairWithPinAsync("127.0.0.1", h.Port, wrong).Bounded());

        Assert.Equal(ConnectFailure.PairingFailed, ex.Failure);
        Assert.Equal(2, ex.AttemptsLeft);
        Assert.Empty(h.Registry.List());
        Assert.False(client.Credentials.IsPaired(h.Identity.Fingerprint));
        Assert.False(client.IsConnected);
        Assert.Equal(2, h.Server.PairingAttemptsLeft);
    }

    [Fact]
    public async Task Three_wrong_pins_replace_the_pin_and_lock_out_until_the_backoff_passes()
    {
        await using var h = new NetHarness();
        h.Server.OpenPairingWindow();
        string original = h.Server.CurrentPin!;
        string wrong = original == "000000" ? "000001" : "000000";

        for (int expectedLeft = 2; expectedLeft >= 0; expectedLeft--)
        {
            var ex = await Assert.ThrowsAsync<PenBridgeConnectException>(() => h.CreateClient().PairWithPinAsync("127.0.0.1", h.Port, wrong).Bounded());
            Assert.Equal(ConnectFailure.PairingFailed, ex.Failure);
            Assert.Equal(expectedLeft, ex.AttemptsLeft);
        }

        string replaced = h.Server.CurrentPin!;
        Assert.NotNull(replaced);

        // Locked out: even the new, correct PIN is refused without a strike.
        var locked = await Assert.ThrowsAsync<PenBridgeConnectException>(() => h.CreateClient().PairWithPinAsync("127.0.0.1", h.Port, replaced).Bounded());
        Assert.Equal(ConnectFailure.PairingFailed, locked.Failure);
        Assert.Equal(0, locked.AttemptsLeft);
        Assert.Equal(replaced, h.Server.CurrentPin);

        h.Time.Advance(PinIssuer.InitialBackoff);
        PenBridgeClient client = h.CreateClient();
        await client.PairWithPinAsync("127.0.0.1", h.Port, replaced).Bounded();
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Pairing_is_refused_while_the_window_is_closed()
    {
        await using var h = new NetHarness();
        var ex = await Assert.ThrowsAsync<PenBridgeConnectException>(() => h.CreateClient().PairWithPinAsync("127.0.0.1", h.Port, "123456").Bounded());
        Assert.Equal(ConnectFailure.PairingExpired, ex.Failure);

        h.Server.OpenPairingWindow();
        Assert.Equal(PinIssuer.MaxFailedAttempts, h.Server.PairingAttemptsLeft); // no strike

        h.Time.Advance(PinIssuer.Lifetime); // window expires
        var expired = await Assert.ThrowsAsync<PenBridgeConnectException>(() => h.CreateClient().PairWithPinAsync("127.0.0.1", h.Port, "123456").Bounded());
        Assert.Equal(ConnectFailure.PairingExpired, expired.Failure);
    }

    [Fact]
    public async Task Pin_pairing_through_a_relay_with_another_certificate_fails_confirmation()
    {
        await using var h = new NetHarness();
        await using var relay = new TlsRelay(h.Port);
        h.Server.OpenPairingWindow();
        PenBridgeClient client = h.CreateClient();

        var ex = await Assert.ThrowsAsync<PenBridgeConnectException>(() => client.PairWithPinAsync("127.0.0.1", relay.Port, h.Server.CurrentPin!).Bounded());

        Assert.Equal(ConnectFailure.PairingFailed, ex.Failure);
        Assert.Empty(h.Registry.List());
        Assert.False(client.Credentials.IsPaired(relay.Fingerprint));
        Assert.Equal(2, h.Server.PairingAttemptsLeft); // a relay costs a strike like a wrong PIN
    }

    [Fact]
    public async Task Qr_pairing_and_reconnect_through_a_relay_fail_the_fingerprint_pin()
    {
        await using var h = new NetHarness();
        var store = new InMemorySecretStore();
        PenBridgeClient paired = await h.PairAsync(store);
        await paired.DisconnectAsync().Bounded();
        await using var relay = new TlsRelay(h.Port);

        var reconnect = await Assert.ThrowsAsync<PenBridgeConnectException>(() =>
            h.CreateClient(store).ConnectAsync("127.0.0.1", relay.Port, h.Identity.Fingerprint.ToArray()).Bounded());
        Assert.Equal(ConnectFailure.ServerIdentityMismatch, reconnect.Failure);

        h.Server.OpenPairingWindow();
        PairingQrPayload qr = h.Server.CreateQrPayload("127.0.0.1")!;
        var viaRelay = new PairingQrPayload("127.0.0.1", (ushort)relay.Port, qr.Fingerprint, qr.Token, qr.Version);
        var pairing = await Assert.ThrowsAsync<PenBridgeConnectException>(() => h.CreateClient().PairWithQrAsync(viaRelay).Bounded());
        Assert.Equal(ConnectFailure.ServerIdentityMismatch, pairing.Failure);
        Assert.Equal(PinIssuer.MaxFailedAttempts, h.Server.PairingAttemptsLeft); // never reached the server
    }

    [Fact]
    public async Task Connect_without_a_stored_key_is_refused_locally()
    {
        await using var h = new NetHarness();
        var ex = await Assert.ThrowsAsync<PenBridgeConnectException>(() =>
            h.CreateClient().ConnectAsync("127.0.0.1", h.Port, h.Identity.Fingerprint.ToArray()));
        Assert.Equal(ConnectFailure.NotPaired, ex.Failure);
    }

    [Fact]
    public async Task A_forgotten_device_is_unpaired_and_then_fails_auth()
    {
        await using var h = new NetHarness();
        var store = new InMemorySecretStore();
        PenBridgeClient client = await h.PairAsync(store);
        var disconnected = new TaskCompletionSource<ClientDisconnectedEventArgs>();
        client.Disconnected += (_, e) => disconnected.TrySetResult(e);

        Assert.True(h.Server.ForgetDevice(client.DeviceId));

        ClientDisconnectedEventArgs e = await disconnected.Task.Bounded();
        Assert.Equal(SessionEndReason.Unpaired, e.Reason);
        Assert.Equal(ByeReason.Unpaired, e.ByeReason);
        Assert.False(client.Credentials.IsPaired(h.Identity.Fingerprint)); // the useless key was dropped

        // A phone that kept its key (e.g. offline when forgotten) gets AuthFailed.
        var stale = new InMemorySecretStore();
        PenBridgeClient other = await h.PairAsync(stale);
        await other.DisconnectAsync().Bounded();
        h.Registry.Remove(other.DeviceId);
        var ex = await Assert.ThrowsAsync<PenBridgeConnectException>(() =>
            h.CreateClient(stale).ConnectAsync("127.0.0.1", h.Port, h.Identity.Fingerprint.ToArray()).Bounded());
        Assert.Equal(ConnectFailure.AuthFailed, ex.Failure);
    }

    [Fact]
    public async Task A_second_device_gets_busy_while_takeover_is_off()
    {
        await using var h = new NetHarness();
        PenBridgeClient first = await h.PairAsync();

        PenBridgeClient second = h.CreateClient();
        h.Server.OpenPairingWindow();
        var ex = await Assert.ThrowsAsync<PenBridgeConnectException>(() => second.PairWithPinAsync("127.0.0.1", h.Port, h.Server.CurrentPin!).Bounded());

        Assert.Equal(ConnectFailure.Busy, ex.Failure);
        Assert.True(second.Credentials.IsPaired(h.Identity.Fingerprint)); // pairing itself succeeded
        Assert.True(first.IsConnected);
        Assert.Equal(first.DeviceId, h.Server.GetStats().DeviceId);
    }

    [Fact]
    public async Task Takeover_closes_the_old_session_with_bye_and_releases_its_input_first()
    {
        await using var h = new NetHarness(o => new PenBridgeServerOptions
        {
            Identity = o.Identity,
            Registry = o.Registry,
            InputSink = o.InputSink,
            BindAddress = o.BindAddress,
            Port = 0,
            TimeProvider = o.TimeProvider,
            AllowTakeover = true,
        });
        var ended = new List<SessionEndedEventArgs>();
        h.Server.SessionEnded += (_, e) => { lock (ended) { ended.Add(e); } };
        PenBridgeClient first = await h.PairAsync();
        var firstGone = new TaskCompletionSource<ClientDisconnectedEventArgs>();
        first.Disconnected += (_, e) => firstGone.TrySetResult(e);
        first.SendState(Pens.Contact(2000, 2000), []);
        await Wait.Until(() => h.Sink.PenKinds().Contains(PenEventKind.Down), "first device's Down");

        PenBridgeClient second = await h.PairAsync();

        ClientDisconnectedEventArgs gone = await firstGone.Task.Bounded();
        Assert.Equal(SessionEndReason.TakenOver, gone.Reason);
        Assert.Equal(ByeReason.TakenOver, gone.ByeReason);
        await Wait.Until(() => { lock (ended) { return ended.Count == 1; } }, "SessionEnded for the first device");
        Assert.Equal(SessionEndReason.TakenOver, ended[0].Reason);
        Assert.Equal(first.DeviceId, ended[0].Session.DeviceId);
        Assert.Equal(second.DeviceId, h.Server.GetStats().DeviceId);

        // The old pen was released (synthetic Up + Leave) before the new session's input.
        PenEvent[] events = h.Sink.Pen();
        Assert.Equal(PenEventKind.Up, events[^2].Kind);
        Assert.True(events[^2].Synthetic);
        Assert.Equal(PenEventKind.ProximityLeave, events[^1].Kind);

        h.Sink.Clear();
        DrawStroke(second);
        await AssertStrokeAsync(h.Sink);
    }

    /// <summary>
    /// Regression for #74: on takeover the old device or the server sometimes reported PeerClosed instead of
    /// TakenOver. Two paired devices take the session from each other back to back; every displaced session must end
    /// as TakenOver on both sides. A fixed number of takeovers (no wall-clock budget, so a loaded runner can't make it
    /// run fewer); every step is bounded by <see cref="Wait.Limit"/>.
    /// </summary>
    [Fact]
    public async Task Repeated_takeovers_always_report_taken_over_on_both_sides()
    {
        await using var h = new NetHarness(o => new PenBridgeServerOptions
        {
            Identity = o.Identity,
            Registry = o.Registry,
            InputSink = o.InputSink,
            BindAddress = o.BindAddress,
            Port = 0,
            TimeProvider = o.TimeProvider,
            AllowTakeover = true,
        });
        var clientEnds = new List<(SessionEndReason Reason, ByeReason? Bye)>();
        var serverEnds = new List<SessionEndReason>();
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnGone(object? sender, ClientDisconnectedEventArgs e)
        {
            lock (clientEnds)
            {
                clientEnds.Add((e.Reason, e.ByeReason));
            }

            gone.TrySetResult();
        }

        h.Server.SessionEnded += (_, e) => { lock (serverEnds) { serverEnds.Add(e.Reason); } };
        PenBridgeClient a = await h.PairAsync();
        a.Disconnected += OnGone;
        PenBridgeClient b = await h.PairAsync(); // the first takeover
        b.Disconnected += OnGone;
        await gone.Task.Bounded();
        byte[] fingerprint = h.Identity.Fingerprint.ToArray();

        const int takeovers = 6; // the pairing above, then five reconnects back to back
        for (int i = 1; i < takeovers; i++)
        {
            PenBridgeClient next = i % 2 == 1 ? a : b;
            gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await next.ConnectAsync("127.0.0.1", h.Port, fingerprint).Bounded();
            await gone.Task.Bounded();
        }

        await Wait.Until(() => { lock (serverEnds) { return serverEnds.Count >= takeovers; } }, "SessionEnded for every displaced session");
        lock (clientEnds)
        {
            Assert.Equal(takeovers, clientEnds.Count);
            Assert.All(clientEnds, e => Assert.Equal((SessionEndReason.TakenOver, (ByeReason?)ByeReason.TakenOver), e));
        }

        lock (serverEnds)
        {
            Assert.Equal(takeovers, serverEnds.Count);
            Assert.All(serverEnds, r => Assert.Equal(SessionEndReason.TakenOver, r));
        }
    }

    [Fact]
    public async Task The_same_device_reconnecting_replaces_its_stale_session()
    {
        await using var h = new NetHarness();
        var store = new InMemorySecretStore();
        PenBridgeClient stale = await h.PairAsync(store);

        PenBridgeClient fresh = h.CreateClient(store);
        await fresh.ConnectAsync("127.0.0.1", h.Port, h.Identity.Fingerprint.ToArray()).Bounded();

        await Wait.Until(() => !stale.IsConnected, "the stale session was closed");
        Assert.True(fresh.IsConnected);
    }

    [Fact]
    public async Task Ping_pong_measures_rtt_on_both_sides()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.PairAsync();

        h.Time.Advance(TimeSpan.FromSeconds(2));

        await Wait.Until(() => h.Server.GetStats().RoundTripTime is not null, "server RTT");
        await Wait.Until(() => client.RoundTripTime is not null, "client RTT");
    }

    [Fact]
    public async Task Server_dispose_sends_bye_shutdown()
    {
        var h = new NetHarness();
        PenBridgeClient client = await h.PairAsync();
        var gone = new TaskCompletionSource<ClientDisconnectedEventArgs>();
        client.Disconnected += (_, e) => gone.TrySetResult(e);

        await h.Server.DisposeAsync(); // only the server: the client must read the Bye itself

        ClientDisconnectedEventArgs e = await gone.Task.Bounded();
        Assert.Equal(ByeReason.Shutdown, e.ByeReason);
        await h.DisposeAsync();
    }

    private sealed class ForgettingRegistry(IPairedDeviceRegistry inner) : IPairedDeviceRegistry
    {
        public volatile bool ForgetOnNextKeyRead;

        public IReadOnlyList<PairedDevice> List() => inner.List();

        public void Add(Guid deviceId, string name, ReadOnlySpan<byte> deviceKey) => inner.Add(deviceId, name, deviceKey);

        public bool Remove(Guid deviceId) => inner.Remove(deviceId);

        public bool Touch(Guid deviceId) => inner.Touch(deviceId);

        public bool TryGetDeviceKey(Guid deviceId, Span<byte> destination)
        {
            bool found = inner.TryGetDeviceKey(deviceId, destination);
            if (found && ForgetOnNextKeyRead)
            {
                ForgetOnNextKeyRead = false;
                inner.Remove(deviceId); // the user hits "Forget" right after the HMAC verified
            }

            return found;
        }
    }

    [Fact]
    public async Task A_device_forgotten_between_auth_and_activation_cannot_start_a_session()
    {
        ForgettingRegistry? registry = null;
        await using var h = new NetHarness(o => new PenBridgeServerOptions
        {
            Identity = o.Identity,
            Registry = registry = new ForgettingRegistry(o.Registry),
            InputSink = o.InputSink,
            BindAddress = o.BindAddress,
            Port = 0,
            TimeProvider = o.TimeProvider,
        });
        var store = new InMemorySecretStore();
        PenBridgeClient first = await h.PairAsync(store);
        await first.DisconnectAsync().Bounded();
        await Wait.Until(() => !h.Server.GetStats().HasSession, "first session ended");

        registry!.ForgetOnNextKeyRead = true;
        var ex = await Assert.ThrowsAsync<PenBridgeConnectException>(() =>
            h.CreateClient(store).ConnectAsync("127.0.0.1", h.Port, h.Identity.Fingerprint.ToArray()).Bounded());

        Assert.Equal(ConnectFailure.AuthFailed, ex.Failure);
        Assert.False(h.Server.GetStats().HasSession);
    }

    private sealed class FailOnceSink(IInputSink inner) : IInputSink
    {
        private int _failed;

        public void OnPenEvent(in PenEvent e)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0)
            {
                throw new InvalidOperationException("injector failure");
            }

            inner.OnPenEvent(e);
        }

        public void OnGestureEvent(in GestureEvent e) => inner.OnGestureEvent(e);
    }

    [Fact]
    public async Task A_throwing_sink_closes_only_that_session_and_the_server_keeps_serving()
    {
        await using var h = new NetHarness(o => new PenBridgeServerOptions
        {
            Identity = o.Identity,
            Registry = o.Registry,
            InputSink = new FailOnceSink(o.InputSink),
            BindAddress = o.BindAddress,
            Port = 0,
            TimeProvider = o.TimeProvider,
        });
        var ended = new TaskCompletionSource<SessionEndedEventArgs>();
        h.Server.SessionEnded += (_, e) => ended.TrySetResult(e);
        var store = new InMemorySecretStore();
        PenBridgeClient first = await h.PairAsync(store);
        var gone = new TaskCompletionSource<ClientDisconnectedEventArgs>();
        first.Disconnected += (_, e) => gone.TrySetResult(e);

        first.SendState(Pens.Hover(1000, 1000), []); // the sink throws on ProximityEnter

        Assert.Equal(SessionEndReason.InternalError, (await ended.Task.Bounded()).Reason);
        await gone.Task.Bounded();

        PenBridgeClient again = h.CreateClient(store);
        await again.ConnectAsync("127.0.0.1", h.Port, h.Identity.Fingerprint.ToArray()).Bounded();
        await h.WaitForSurfaceAsync();
        DrawStroke(again);
        await AssertStrokeAsync(h.Sink);
    }
}
