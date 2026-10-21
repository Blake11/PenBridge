using System.Net;
using System.Net.Sockets;
using System.Text;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using SecretKind = PenBridge.Core.Security.PairingSecretKind;
using WireSecretKind = PenBridge.Core.Protocol.PairingSecretKind;

namespace PenBridge.Core.Tests.Net;

/// <summary>Deadlines (handshake, per-read, pairing, liveness), port fallback and protocol refusals.</summary>
public class TimeoutAndProtocolTests
{
    private static Func<PenBridgeServerOptions, PenBridgeServerOptions> Timeouts(
        TimeSpan? handshake = null, TimeSpan? read = null, TimeSpan? pairing = null, TimeSpan? peer = null, TimeSpan? ping = null) =>
        o => new PenBridgeServerOptions
        {
            Identity = o.Identity,
            Registry = o.Registry,
            InputSink = o.InputSink,
            BindAddress = o.BindAddress,
            Port = 0,
            TimeProvider = o.TimeProvider,
            HandshakeTimeout = handshake ?? o.HandshakeTimeout,
            ReadTimeout = read ?? o.ReadTimeout,
            PairingTimeout = pairing ?? o.PairingTimeout,
            PeerTimeout = peer ?? o.PeerTimeout,
            PingInterval = ping ?? o.PingInterval,
        };

    [Fact]
    public async Task A_silent_tcp_connection_is_closed_at_the_handshake_deadline()
    {
        await using var h = new NetHarness(Timeouts(handshake: TimeSpan.FromSeconds(10), read: TimeSpan.FromHours(1)));
        await using RawClient raw = await RawClient.ConnectTcpAsync(h.Port);
        TimeSpan handshake = TimeSpan.FromSeconds(10);
        await Wait.Until(() => h.Time.CountDeadlines((s, armedFor, _) => s is ControlChannel && armedFor == handshake) == 1, "handshake deadline armed");

        // Timers fire only inside Advance: still armed with 1 s left means it didn't fire.
        h.Time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(1, h.Time.CountDeadlines((s, armedFor, remaining) => s is ControlChannel && armedFor == handshake && remaining == TimeSpan.FromSeconds(1)));
        Assert.False(raw.IsClosed);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.Until(() => raw.IsClosed, "server closed the silent connection");
        await Wait.Until(() => h.Server.PendingConnections == 0, "pending slot released");
    }

    [Fact]
    public async Task A_stalled_read_before_auth_is_closed_at_the_read_deadline()
    {
        await using var h = new NetHarness(Timeouts(handshake: TimeSpan.FromHours(1), read: TimeSpan.FromSeconds(5)));
        await using RawClient raw = await RawClient.ConnectTlsAsync(h.Port);
        await raw.HelloAsync(Guid.NewGuid()); // then stall before Auth/pairing

        Task<ReceivedControlFrame?> read = raw.ReadAsync();
        await Wait.AdvanceUntil(h.Time, TimeSpan.FromSeconds(1), () => read.IsCompleted, "read deadline");

        ReceivedControlFrame? frame = await read;
        Assert.NotNull(frame);
        Assert.Equal(ProtocolErrorCode.Timeout, frame.Value.Read<ErrorMessage>().Code);
        Assert.Null(await raw.ReadAsync());
    }

    [Fact]
    public async Task A_stalled_pairing_attempt_times_out_frees_the_slot_and_costs_a_strike()
    {
        await using var h = new NetHarness(Timeouts(handshake: TimeSpan.FromHours(1), read: TimeSpan.FromHours(1), pairing: TimeSpan.FromSeconds(20)));
        h.Server.OpenPairingWindow();
        string pin = h.Server.CurrentPin!;
        await using RawClient raw = await RawClient.ConnectTlsAsync(h.Port);
        Guid deviceId = Guid.NewGuid();
        byte[] challenge = await raw.HelloAsync(deviceId);
        using var session = new PairingSession(PairingRole.Client, SecretKind.Pin, Encoding.ASCII.GetBytes(pin), deviceId, challenge, new byte[32]);
        await raw.WriteAsync(new ClientPairRound1Message(session.CreateRound1(), WireSecretKind.Pin));
        Assert.Equal(ControlMessageType.ServerPairRound1, (await raw.ReadAsync())!.Value.Type);

        // While the slot is held, another phone is told pairing is unavailable.
        var busy = await Assert.ThrowsAsync<PenBridgeConnectException>(() => h.CreateClient().PairWithPinAsync("127.0.0.1", h.Port, pin, TestContext.Current.CancellationToken).Bounded());
        Assert.Equal(ConnectFailure.PairingUnavailable, busy.Failure);

        h.Time.Advance(TimeSpan.FromSeconds(20)); // the raw client never sends Round2

        await Wait.Until(() => raw.IsClosed, "stalled pairing closed");
        await Wait.Until(() => h.Server.PairingAttemptsLeft, left => left == 2, "the strike for the stalled attempt");
        PenBridgeClient client = h.CreateClient();
        await client.PairWithPinAsync("127.0.0.1", h.Port, pin, TestContext.Current.CancellationToken).Bounded();
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task A_silent_session_peer_is_dropped_after_the_peer_timeout_and_its_input_released()
    {
        await using var h = new NetHarness(Timeouts(peer: TimeSpan.FromSeconds(6), ping: Timeout.InfiniteTimeSpan));
        var ended = new TaskCompletionSource<SessionEndedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Server.SessionEnded += (_, e) => ended.TrySetResult(e);
        PenBridgeClient client = await h.PairAsync(configure: o => new PenBridgeClientOptions
        {
            SecretStore = o.SecretStore,
            Surface = o.Surface,
            TimeProvider = o.TimeProvider,
            DatagramFilter = _ => false,
            PingInterval = Timeout.InfiniteTimeSpan,
            PeerTimeout = TimeSpan.FromHours(1),
            UdpAckTimeout = TimeSpan.FromHours(1),
            ContactKeepaliveInterval = TimeSpan.FromHours(1),
            HoverKeepaliveInterval = TimeSpan.FromHours(1),
        });

        await Wait.AdvanceUntil(h.Time, TimeSpan.FromSeconds(1), () => ended.Task.IsCompleted, "peer timeout");

        SessionEndedEventArgs e = await ended.Task.Bounded();
        Assert.Equal(SessionEndReason.Timeout, e.Reason);
        await Wait.Until(() => !client.IsConnected, "client saw the close");
    }

    [Fact]
    public async Task Pings_keep_an_idle_session_alive()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.PairAsync();

        // Both session read loops are waiting under a peer-timeout deadline, so the waits below are not vacuous.
        await Wait.Until(() => h.Time.CountDeadlines(static (s, armedFor, _) => s is ControlChannel && armedFor == Wait.PeerTimeout) == 2, "both read deadlines armed");

        // 20 s of fake time (> the 6 s peer timeout) in 1 s steps. Each step waits for the pings to reach both peers:
        // under CPU load a fixed real pause is not enough, and a few steps without the I/O completing add up to a
        // peer timeout.
        await Wait.Step(h.Time, TimeSpan.FromSeconds(20));

        Assert.True(client.IsConnected);
        Assert.True(h.Server.GetStats().HasSession);
    }

    [Fact]
    public async Task An_incompatible_major_version_is_refused_with_an_error()
    {
        await using var h = new NetHarness();
        await using RawClient raw = await RawClient.ConnectTlsAsync(h.Port);
        await raw.WriteAsync(new HelloMessage(new ProtocolVersion(2, 0), new byte[16], "Future", ProtocolCapabilities.None));

        ReceivedControlFrame? frame = await raw.ReadAsync();

        Assert.Equal(ProtocolErrorCode.IncompatibleVersion, frame!.Value.Read<ErrorMessage>().Code);
        Assert.Null(await raw.ReadAsync());
    }

    [Fact]
    public async Task A_newer_minor_version_is_accepted()
    {
        await using var h = new NetHarness();
        await using RawClient raw = await RawClient.ConnectTlsAsync(h.Port);
        byte[] challenge = await raw.HelloAsync(Guid.NewGuid(), new ProtocolVersion(1, 9));
        Assert.Equal(32, challenge.Length);
    }

    [Fact]
    public async Task An_unexpected_message_before_auth_closes_the_connection_with_an_error()
    {
        await using var h = new NetHarness();
        await using RawClient raw = await RawClient.ConnectTlsAsync(h.Port);
        await raw.HelloAsync(Guid.NewGuid());
        await raw.WriteAsync(NetHarness.Surface); // SurfaceInfo before Auth

        ReceivedControlFrame? frame = await raw.ReadAsync();

        Assert.Equal(ProtocolErrorCode.UnexpectedMessage, frame!.Value.Read<ErrorMessage>().Code);
        Assert.Null(await raw.ReadAsync());
    }

    [Fact]
    public async Task Auth_with_a_wrong_hmac_fails()
    {
        await using var h = new NetHarness();
        await using RawClient raw = await RawClient.ConnectTlsAsync(h.Port);
        Guid id = Guid.NewGuid();
        h.Registry.Add(id, "Victim", new byte[32]);
        await raw.HelloAsync(id);
        await raw.WriteAsync(new AuthMessage(id.ToByteArray(bigEndian: true), new byte[32], new byte[32]));

        ReceivedControlFrame? frame = await raw.ReadAsync();

        Assert.Equal(ProtocolErrorCode.AuthFailed, frame!.Value.Read<ErrorMessage>().Code);
        Assert.Null(await raw.ReadAsync());
    }

    [Fact]
    public async Task Non_tls_garbage_is_dropped()
    {
        await using var h = new NetHarness();
        await using RawClient raw = await RawClient.ConnectTcpAsync(h.Port);
        await raw.Socket.SendAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray());
        await Wait.Until(() => raw.IsClosed, "garbage connection closed");
    }

    [Fact]
    public async Task One_ip_cannot_take_all_pending_slots()
    {
        await using var h = new NetHarness();
        await using RawClient a = await RawClient.ConnectTcpAsync(h.Port);
        await using RawClient b = await RawClient.ConnectTcpAsync(h.Port);
        await Wait.Until(() => h.Server.PendingConnections == 2, "two pending");

        await using RawClient c = await RawClient.ConnectTcpAsync(h.Port);

        await Wait.Until(() => c.IsClosed, "third connection from the same IP refused");
        Assert.False(a.IsClosed);
        Assert.Equal(2, h.Server.PendingConnections); // 6 of 8 global slots stay free for other hosts

        // Another host still gets in (admission is per remote IP).
        IPAddress other = IPAddress.Parse("192.0.2.7");
        Assert.True(h.Server.TryAdmit(other));
        Assert.True(h.Server.TryAdmit(other));
        Assert.False(h.Server.TryAdmit(other));
        h.Server.ReleasePending(other);
        h.Server.ReleasePending(other);
    }

    [Fact]
    public void The_global_pending_cap_holds_across_ips()
    {
        var server = new PenBridgeServer(new PenBridgeServerOptions
        {
            Identity = NetHarness.SharedServerIdentity,
            Registry = new PairedDeviceRegistry(new InMemorySecretStore()),
            InputSink = new ConcurrentSink(),
        });
        for (int i = 0; i < 8; i++)
        {
            Assert.True(server.TryAdmit(IPAddress.Parse("192.0.2." + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        Assert.False(server.TryAdmit(IPAddress.Parse("192.0.2.200")));
    }

    [Fact]
    public async Task Connections_reset_right_after_connect_dont_stop_the_accept_loop()
    {
        await using var h = new NetHarness();
        for (int i = 0; i < 20; i++)
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.LingerState = new LingerOption(true, 0);
            try
            {
                await s.ConnectAsync(IPAddress.Loopback, h.Port, TestContext.Current.CancellationToken);
            }
            catch (SocketException)
            {
                // A full accept backlog under a loaded test run may refuse one; that's fine here.
                continue;
            }

            s.Close(); // RST
        }

        await Wait.Until(() => h.Server.PendingConnections == 0, "reset connections released");
        PenBridgeClient client = await h.PairAsync();
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void The_default_port_range_is_47820_to_47829()
    {
        Assert.Equal(Enumerable.Range(47820, 10), PenBridgeServer.PortRange(ProtocolInfo.DefaultPort, 9));
        Assert.Equal([65534, 65535], PenBridgeServer.PortRange(65534, 9));
    }

    [Fact]
    public async Task The_server_skips_ports_where_tcp_or_udp_is_taken()
    {
        // The test holds both blocked ports for its whole duration; the last candidate is ephemeral (always free).
        using var tcpBlocker = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        tcpBlocker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        tcpBlocker.Listen();
        int tcpTaken = ((IPEndPoint)tcpBlocker.LocalEndPoint!).Port;
        using var udpBlocker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udpBlocker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int udpTaken = ((IPEndPoint)udpBlocker.LocalEndPoint!).Port;

        await using var h = new NetHarness(o => new PenBridgeServerOptions
        {
            Identity = o.Identity,
            Registry = o.Registry,
            InputSink = o.InputSink,
            BindAddress = o.BindAddress,
            TimeProvider = o.TimeProvider,
            PortCandidates = [tcpTaken, udpTaken, 0],
        });

        Assert.Equal([tcpTaken, udpTaken], h.Server.FailedPorts);
        Assert.NotEqual(tcpTaken, h.Port);
        Assert.NotEqual(udpTaken, h.Port);
        PenBridgeClient client = await h.PairAsync();
        EndToEndTests.DrawStroke(client);
        await EndToEndTests.AssertStrokeAsync(h.Sink);
    }

    [Fact]
    public async Task Start_fails_when_every_candidate_port_is_taken()
    {
        using var tcpBlocker = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        tcpBlocker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        tcpBlocker.Listen();
        using var udpBlocker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udpBlocker.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        await using var server = new PenBridgeServer(new PenBridgeServerOptions
        {
            Identity = NetHarness.SharedServerIdentity,
            Registry = new PairedDeviceRegistry(new InMemorySecretStore()),
            InputSink = new ConcurrentSink(),
            BindAddress = IPAddress.Loopback,
            PortCandidates = [((IPEndPoint)tcpBlocker.LocalEndPoint!).Port, ((IPEndPoint)udpBlocker.LocalEndPoint!).Port],
        });
        Assert.Throws<SocketException>(server.Start);
    }
}
