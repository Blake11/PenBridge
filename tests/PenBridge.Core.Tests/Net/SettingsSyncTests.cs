using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using PenBridge.Core.Settings;

#pragma warning disable xUnit1051 // Network waits are bounded by Wait.Limit instead of the test token.

namespace PenBridge.Core.Tests.Net;

/// <summary>Protocol 1.2 over the engine (ADR-0030, ADR-0031): settings snapshot/update, display list, active area, compatibility.</summary>
public class SettingsSyncTests
{
    private const ProtocolCapabilities OlderPhone = ProtocolCapabilities.SampleDedupe | ProtocolCapabilities.FingerTouches;

    private static readonly DisplayListEntry[] Displays =
    [
        new("uuid-a", "Studio Display", true, 2560, 1440),
        new("uuid-b", "Side", false, 1920, 1080),
    ];

    private static NetHarness Harness(bool remoteSettings = true, bool activeArea = true) => new(o => new PenBridgeServerOptions
    {
        Identity = o.Identity,
        Registry = o.Registry,
        InputSink = o.InputSink,
        ServerName = o.ServerName,
        BindAddress = o.BindAddress,
        Port = o.Port,
        TargetDisplay = o.TargetDisplay,
        TimeProvider = o.TimeProvider,
        RemoteSettings = remoteSettings,
        ActiveArea = activeArea,
    });

    private static Func<PenBridgeClientOptions, PenBridgeClientOptions> WithCapabilities(ProtocolCapabilities capabilities) => o => new PenBridgeClientOptions
    {
        SecretStore = o.SecretStore,
        Surface = o.Surface,
        DeviceName = o.DeviceName,
        TimeProvider = o.TimeProvider,
        Capabilities = capabilities,
    };

    [Fact]
    public async Task Snapshot_and_displays_follow_session_start_and_an_update_is_answered_with_a_snapshot()
    {
        await using NetHarness h = Harness();
        var mac = new MacSettingValues();
        await h.Server.SendDisplayListAsync(Displays); // no session yet: stored for the next one
        await h.Server.SendSettingsSnapshotAsync(mac.ToEntries());
        var requests = new ConcurrentQueue<SettingsUpdateRequestedEventArgs>();
        h.Server.SettingsUpdateRequested += (_, e) =>
        {
            requests.Enqueue(e);
            IReadOnlyList<SettingEntry> reply;
            lock (mac)
            {
                mac.ApplyUpdate(e.Entries, Displays);
                reply = mac.ToEntries();
            }

            _ = h.Server.SendSettingsSnapshotAsync(reply);
        };

        var log = new ConcurrentQueue<string>();
        var snapshots = new ConcurrentQueue<IReadOnlyList<SettingEntry>>();
        var lists = new ConcurrentQueue<IReadOnlyList<DisplayListEntry>>();
        PenBridgeClient client = await PairAsync(h, c =>
        {
            c.SettingsSnapshotReceived += (_, e) =>
            {
                log.Enqueue("snapshot");
                snapshots.Enqueue(e.Entries);
            };
            c.DisplayListReceived += (_, e) =>
            {
                log.Enqueue("displays");
                lists.Enqueue(e.Displays);
            };
        });

        Assert.Equal(ProtocolCapabilities.RemoteSettings | ProtocolCapabilities.ActiveArea, client.NegotiatedCapabilities & (ProtocolCapabilities.RemoteSettings | ProtocolCapabilities.ActiveArea));
        await Wait.Until(() => snapshots.Count == 1, "first snapshot");
        Assert.Equal(["displays", "snapshot"], log);
        Assert.Equal(Displays, client.LastDisplayList);
        Assert.Equal(MacSettings.Defaults, client.LastSettingsSnapshot);

        // An update with one valid and one invalid entry (a display that isn't listed): the reply carries the truth.
        Assert.True(await client.UpdateSettingsAsync(
        [
            SettingEntry.Int(MacSettingKey.StylusMode, 1),
            SettingEntry.String(MacSettingKey.TargetDisplay, "uuid-z"),
        ]));
        await Wait.Until(() => snapshots.Count == 2, "snapshot in reply to the update");
        SettingsUpdateRequestedEventArgs request = Assert.Single(requests);
        Assert.Equal(client.DeviceId, request.Session.DeviceId);
        Assert.Equal(2, request.Entries.Count);
        MacSettingValues seen = MacSettingValues.FromEntries(client.LastSettingsSnapshot!);
        Assert.Equal(1, seen.StylusMode);
        Assert.Equal(MacSettings.MainDisplay, seen.TargetDisplay);

        // A display change reaches the running session.
        await h.Server.SendDisplayListAsync([Displays[1]]);
        await Wait.Until(() => lists.Count == 2, "second display list");
        Assert.Equal([Displays[1]], client.LastDisplayList);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task The_active_area_reaches_the_phone_and_only_changes_are_sent()
    {
        await using NetHarness h = Harness();
        var areas = new ConcurrentQueue<NormalizedRect>();
        PenBridgeClient client = await PairAsync(h, c => c.ActiveAreaReceived += (_, e) => areas.Enqueue(e.Area));
        await Wait.Until(() => !areas.IsEmpty, "the area after SurfaceInfo");
        areas.Clear();

        var first = new NormalizedRect(0.125, 0, 0.75, 1);
        h.Server.SendActiveArea(first);
        await Wait.Until(() => !areas.IsEmpty, "first active area");
        h.Server.SendActiveArea(first); // unchanged: not sent again
        h.Server.SendActiveArea(NormalizedRect.Full);
        await Wait.Until(() => areas.Count >= 2 && client.LastActiveArea == NormalizedRect.Full, "active areas");
        await PingRoundTripAsync(h);

        NormalizedRect[] got = [.. areas];
        Assert.Equal(2, got.Length);
        Assert.Equal(first.X, got[0].X, 4);
        Assert.Equal(first.Width, got[0].Width, 4);
        Assert.Equal(NormalizedRect.Full, got[1]);
    }

    [Fact]
    public async Task A_server_without_the_capabilities_sends_none_of_the_messages()
    {
        await using NetHarness h = Harness(remoteSettings: false, activeArea: false);
        int received = 0;
        PenBridgeClient client = await PairAsync(h, c =>
        {
            c.SettingsSnapshotReceived += (_, _) => Interlocked.Increment(ref received);
            c.DisplayListReceived += (_, _) => Interlocked.Increment(ref received);
            c.ActiveAreaReceived += (_, _) => Interlocked.Increment(ref received);
        });

        Assert.Equal(ProtocolCapabilities.None, client.ServerCapabilities & (ProtocolCapabilities.RemoteSettings | ProtocolCapabilities.ActiveArea));
        Assert.Equal(ProtocolCapabilities.None, client.NegotiatedCapabilities & (ProtocolCapabilities.RemoteSettings | ProtocolCapabilities.ActiveArea));
        await h.Server.SendSettingsSnapshotAsync(MacSettings.Defaults);
        await h.Server.SendDisplayListAsync(Displays);
        h.Server.SendActiveArea(NormalizedRect.Full);
        Assert.False(await client.UpdateSettingsAsync([SettingEntry.Bool(MacSettingKey.Zoom, false)]));
        await PingRoundTripAsync(h);

        Assert.Equal(0, received);
        Assert.Null(client.LastSettingsSnapshot);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task A_phone_without_the_capabilities_gets_none_of_the_messages()
    {
        await using NetHarness h = Harness();
        await h.Server.SendSettingsSnapshotAsync(MacSettings.Defaults);
        await h.Server.SendDisplayListAsync(Displays);
        int received = 0;
        var sessions = new ConcurrentQueue<SessionInfo>();
        h.Server.SessionStarted += (_, e) => sessions.Enqueue(e.Session);
        PenBridgeClient client = await PairAsync(
            h,
            c =>
            {
                c.SettingsSnapshotReceived += (_, _) => Interlocked.Increment(ref received);
                c.DisplayListReceived += (_, _) => Interlocked.Increment(ref received);
                c.ActiveAreaReceived += (_, _) => Interlocked.Increment(ref received);
            },
            WithCapabilities(OlderPhone));

        Assert.Equal(ProtocolCapabilities.SampleDedupe, client.NegotiatedCapabilities); // the server doesn't offer FingerTouches here
        Assert.Equal(ProtocolCapabilities.SampleDedupe, Assert.Single(sessions).Capabilities);
        await h.Server.SendSettingsSnapshotAsync(MacSettings.Defaults);
        h.Server.SendActiveArea(NormalizedRect.Full);
        await PingRoundTripAsync(h);

        Assert.Equal(0, received);
        Assert.True(client.SendState(Pens.Contact(100, 100), []));
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task A_settings_update_without_the_capability_is_a_protocol_error()
    {
        await using NetHarness h = Harness();
        PenBridgeClient client = await PairAsync(h, configure: WithCapabilities(OlderPhone));
        var ended = new TaskCompletionSource<ClientDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, e) => ended.TrySetResult(e);

        await client.SendRawControlAsync(new SettingsUpdateMessage([SettingEntry.Bool(MacSettingKey.Zoom, false)]));

        ClientDisconnectedEventArgs e = await ended.Task.Bounded();
        Assert.Equal(ProtocolErrorCode.UnexpectedMessage, e.ErrorCode);
    }

    [Fact]
    public async Task A_phone_sending_a_server_message_is_a_protocol_error()
    {
        await using NetHarness h = Harness();
        PenBridgeClient client = await PairAsync(h);
        var ended = new TaskCompletionSource<ClientDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, e) => ended.TrySetResult(e);

        await client.SendRawControlAsync(new SettingsSnapshotMessage(MacSettings.Defaults));

        Assert.Equal(ProtocolErrorCode.UnexpectedMessage, (await ended.Task.Bounded()).ErrorCode);
    }

    [Fact]
    public async Task A_settings_update_before_session_start_is_a_protocol_error()
    {
        await using NetHarness h = Harness();
        await using RawClient raw = await RawClient.ConnectTlsAsync(h.Port);
        await raw.HelloAsync(Guid.NewGuid());
        await raw.WriteAsync(new SettingsUpdateMessage([SettingEntry.Bool(MacSettingKey.Zoom, false)]));

        ReceivedControlFrame? frame = await raw.ReadAsync();

        Assert.Equal(ProtocolErrorCode.UnexpectedMessage, frame!.Value.Read<ErrorMessage>().Code);
        Assert.Null(await raw.ReadAsync());
    }

    [Fact]
    public async Task A_throwing_update_handler_doesnt_end_the_session()
    {
        await using NetHarness h = Harness();
        int calls = 0;
        h.Server.SettingsUpdateRequested += (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("UI bug");
        };
        PenBridgeClient client = await PairAsync(h);

        Assert.True(await client.UpdateSettingsAsync([SettingEntry.Bool(MacSettingKey.Zoom, false)]));
        await Wait.Until(() => Volatile.Read(ref calls) == 1, "update handled");
        await PingRoundTripAsync(h);

        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task A_phone_spamming_updates_is_capped_and_gets_the_latest_snapshot()
    {
        await using NetHarness h = Harness();
        int handled = 0;
        h.Server.SettingsUpdateRequested += (_, e) =>
        {
            int n = Interlocked.Increment(ref handled);
            _ = h.Server.SendSettingsSnapshotAsync([SettingEntry.Int(MacSettingKey.SideButton, n)]);
        };
        var snapshots = new ConcurrentQueue<IReadOnlyList<SettingEntry>>();
        PenBridgeClient client = await PairAsync(h, c => c.SettingsSnapshotReceived += (_, e) => snapshots.Enqueue(e.Entries));

        // Time stands still: the burst passes, the rest is dropped without ending the session.
        for (int i = 0; i < 100; i++)
        {
            Assert.True(await client.UpdateSettingsAsync([SettingEntry.Bool(MacSettingKey.Zoom, i % 2 == 0)]));
        }

        ServerConnection connection = h.Server.ActiveConnection!;
        await Wait.Until(() => Volatile.Read(ref handled) + connection.SettingsUpdatesDropped == 100, "every update read");
        Assert.Equal(ServerConnection.SettingsUpdateBurst, Volatile.Read(ref handled));
        Assert.Equal(100 - ServerConnection.SettingsUpdateBurst, connection.SettingsUpdatesDropped);
        // Wait on the event, not LastSettingsSnapshot: the client stores the snapshot before it raises the event.
        await Wait.Until(
            () => snapshots.LastOrDefault() is [{ } last] && last == SettingEntry.Int(MacSettingKey.SideButton, ServerConnection.SettingsUpdateBurst),
            "the latest snapshot");
        Assert.Equal([SettingEntry.Int(MacSettingKey.SideButton, ServerConnection.SettingsUpdateBurst)], client.LastSettingsSnapshot);
        Assert.InRange(snapshots.Count, 1, ServerConnection.SettingsUpdateBurst); // replies coalesce, never multiply
        Assert.Equal(0, connection.Pushes.Pending);

        // The bucket refills with time.
        await Wait.Step(h.Time, TimeSpan.FromSeconds(1));
        Assert.True(await client.UpdateSettingsAsync([SettingEntry.Bool(MacSettingKey.Zoom, true)]));
        await Wait.Until(() => Volatile.Read(ref handled) == ServerConnection.SettingsUpdateBurst + 1, "an update after the refill");
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Rapid_server_sends_coalesce_and_the_latest_state_wins()
    {
        await using NetHarness h = Harness();
        var snapshots = new ConcurrentQueue<IReadOnlyList<SettingEntry>>();
        var areas = new ConcurrentQueue<NormalizedRect>();
        PenBridgeClient client = await PairAsync(h, c =>
        {
            c.SettingsSnapshotReceived += (_, e) => snapshots.Enqueue(e.Entries);
            c.ActiveAreaReceived += (_, e) => areas.Enqueue(e.Area);
        });

        const int Count = 2_000;
        Task last = Task.CompletedTask;
        for (int i = 1; i <= Count; i++)
        {
            last = h.Server.SendSettingsSnapshotAsync([SettingEntry.Int(MacSettingKey.SideButton, i)]);
            h.Server.SendActiveArea(new NormalizedRect(0, 0, 0.5 + (0.5 * i / Count), 1));
        }

        await last.Bounded();
        await PingRoundTripAsync(h);

        Assert.Equal([SettingEntry.Int(MacSettingKey.SideButton, Count)], client.LastSettingsSnapshot);
        Assert.Equal(NormalizedRect.Full, client.LastActiveArea);
        Assert.InRange(snapshots.Count, 1, Count);
        Assert.InRange(areas.Count, 1, Count);
        Assert.Equal(0, h.Server.ActiveConnection!.Pushes.Pending);

        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task A_snapshot_read_after_a_new_display_list_goes_out_behind_that_list()
    {
        await using NetHarness h = Harness();
        await h.Server.SendDisplayListAsync([Displays[0]]);
        await h.Server.SendSettingsSnapshotAsync(Snapshot(1));
        var log = new ConcurrentQueue<string>();
        await PairAsync(h, c =>
        {
            c.DisplayListReceived += (_, e) => log.Enqueue("displays " + e.Displays.Count);
            c.SettingsSnapshotReceived += (_, e) => log.Enqueue("snapshot " + e.Entries.Count);
        });
        await Wait.Until(() => log.Count == 2, "the first list and snapshot");
        Assert.Equal(["displays 1", "snapshot 1"], log);
        log.Clear();

        // The writer takes the snapshot bit, then (paused here) the list and a snapshot naming one of its displays
        // change: the snapshot it reads must not overtake the list whose bit is still pending.
        ServerConnection connection = h.Server.ActiveConnection!;
        (TaskCompletionSource entered, TaskCompletionSource release) = PauseNextDrain(connection);
        Task push = h.Server.SendSettingsSnapshotAsync(Snapshot(2));
        await entered.Task.Bounded();
        _ = h.Server.SendDisplayListAsync(Displays);
        _ = h.Server.SendSettingsSnapshotAsync(Snapshot(3));
        release.SetResult();
        await push.Bounded();
        await PingRoundTripAsync(h);

        // Each goes out once: the pending bits find nothing new.
        Assert.Equal(["displays 2", "snapshot 3"], log);
        Assert.Equal(0, connection.Pushes.Pending);
    }

    [Fact]
    public async Task A_push_racing_the_close_sends_nothing_after_the_bye_and_keeps_the_linger()
    {
        await using NetHarness h = Harness();
        await using RawClient raw = await StartRawSessionAsync(h);
        ServerConnection connection = h.Server.ActiveConnection!;
        (TaskCompletionSource entered, TaskCompletionSource release) = PauseNextDrain(connection);
        Task push = h.Server.SendSettingsSnapshotAsync(Snapshot(1));
        await entered.Task.Bounded();

        // The raw peer never closes, so the server lingers after its Bye until the linger timeout.
        var lingering = Stopwatch.StartNew();
        Task close = h.Server.DisconnectSessionAsync();
        Assert.Equal(ControlMessageType.Bye, (await raw.ReadAsync())!.Value.Type);
        release.SetResult();
        await push.Bounded();

        // A write after the half-close would fail and abort the channel at once, ending the linger early.
        Assert.True(!connection.IsChannelAborted || lingering.Elapsed >= ControlChannel.LingerTimeout, "aborted during the linger");
        Assert.Null(await raw.ReadAsync()); // the Bye was the last frame
        raw.Socket.Shutdown(System.Net.Sockets.SocketShutdown.Send); // the peer closes its side: the linger ends
        await close.Bounded();
    }

    [Fact]
    public async Task A_push_writer_fault_ends_the_session_instead_of_leaving_it_without_pushes()
    {
        await using NetHarness h = Harness();
        var ended = new ConcurrentQueue<SessionEndReason>();
        h.Server.SessionEnded += (_, e) => ended.Enqueue(e.Reason);
        await PairAsync(h);
        ServerConnection connection = h.Server.ActiveConnection!;
        connection.BeforePushDrain = _ => throw new ArgumentException("a bug in the drain");

        await h.Server.SendSettingsSnapshotAsync(Snapshot(1)).Bounded();

        // Closed like an input fault: Bye, then reported as an internal error (not a lost connection).
        await Wait.Until(() => !ended.IsEmpty, "the session ended");
        Assert.Equal(SessionEndReason.InternalError, Assert.Single(ended));
    }

    [Fact]
    public async Task Server_sends_validate_their_arguments()
    {
        await using NetHarness h = Harness();
        SettingEntry[] tooMany = [.. Enumerable.Repeat(SettingEntry.Bool(MacSettingKey.Zoom, true), SettingsWire.MaxEntries + 1)];
        await Assert.ThrowsAsync<ArgumentException>(() => h.Server.SendSettingsSnapshotAsync(tooMany));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Server.SendDisplayListAsync([.. Enumerable.Repeat(Displays[1], SettingsWire.MaxDisplays + 1)]));
        await Assert.ThrowsAsync<ArgumentException>(() => new PenBridgeClient(new PenBridgeClientOptions { SecretStore = new PenBridge.Core.Security.InMemorySecretStore(), Surface = NetHarness.Surface }).UpdateSettingsAsync([]));
    }

    private static async Task<PenBridgeClient> PairAsync(NetHarness h, Action<PenBridgeClient>? subscribe = null, Func<PenBridgeClientOptions, PenBridgeClientOptions>? configure = null)
    {
        PenBridgeClient client = h.CreateClient(configure: configure);
        subscribe?.Invoke(client);
        h.Server.OpenPairingWindow();
        await client.PairWithPinAsync("127.0.0.1", h.Port, h.Server.CurrentPin!).Bounded();
        await h.WaitForSurfaceAsync();
        return client;
    }

    private static SettingEntry[] Snapshot(int count) =>
        [.. new[] { SettingEntry.Int(MacSettingKey.SideButton, 1), SettingEntry.Bool(MacSettingKey.Zoom, true), SettingEntry.Int(MacSettingKey.StylusMode, 1) }.Take(count)];

    /// <summary>Holds the push writer's next drain after it took its bits, before it reads the state.</summary>
    private static (TaskCompletionSource Entered, TaskCompletionSource Release) PauseNextDrain(ServerConnection connection)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int armed = 1;
        connection.BeforePushDrain = async _ =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                entered.SetResult();
                await release.Task;
            }
        };
        return (entered, release);
    }

    /// <summary>A hand-driven 1.2 session that never closes its side (Hello with RemoteSettings, Auth, SessionStart).</summary>
    private static async Task<RawClient> StartRawSessionAsync(NetHarness h)
    {
        Guid id = Guid.NewGuid();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        h.Registry.Add(id, "Raw", key);
        RawClient raw = await RawClient.ConnectTlsAsync(h.Port);
        byte[] challenge = await raw.HelloAsync(id, capabilities: ProtocolCapabilities.RemoteSettings);
        byte[] nonce = RandomNumberGenerator.GetBytes(WireSizes.Nonce);
        byte[] proof = new byte[ReconnectAuth.ProofLength];
        ReconnectAuth.ComputeProof(key, challenge, nonce, h.Identity.Fingerprint, proof);
        await raw.WriteAsync(new AuthMessage(id.ToByteArray(bigEndian: true), proof, nonce));
        Assert.Equal(ControlMessageType.SessionStart, (await raw.ReadAsync())!.Value.Type);
        await Wait.Until(() => h.Server.ActiveConnection is not null, "the raw session");
        return raw;
    }

    /// <summary>
    /// An ordering barrier: the server pings, and its RTT appears only after the phone's read loop handled every
    /// frame sent before the Ping (the control channel is in order).
    /// </summary>
    private static async Task PingRoundTripAsync(NetHarness h)
    {
        Assert.Null(h.Server.GetStats().RoundTripTime);
        await Wait.Step(h.Time, Wait.PingInterval);
        await Wait.Until(() => h.Server.GetStats().RoundTripTime is not null, "a server ping round trip");
    }
}
