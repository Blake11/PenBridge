using System.Net;
using System.Net.Sockets;
using System.Text;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using SecretKind = PenBridge.Core.Security.PairingSecretKind;
using WireSecretKind = PenBridge.Core.Protocol.PairingSecretKind;

#pragma warning disable xUnit1051 // Network waits are bounded by Wait.Limit instead of the test token.

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
        await Wait.Until(() => h.Server.ConnectionCount == 1, "connection accepted");

        h.Time.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(50);
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
        var busy = await Assert.ThrowsAsync<PenBridgeConnectException>(() => h.CreateClient().PairWithPinAsync("127.0.0.1", h.Port, pin).Bounded());
        Assert.Equal(ConnectFailure.PairingUnavailable, busy.Failure);

        h.Time.Advance(TimeSpan.FromSeconds(20)); // the raw client never sends Round2

        await Wait.Until(() => raw.IsClosed, "stalled pairing closed");
        Assert.Equal(2, h.Server.PairingAttemptsLeft);
        PenBridgeClient client = h.CreateClient();
        await client.PairWithPinAsync("127.0.0.1", h.Port, pin).Bounded();
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task A_silent_session_peer_is_dropped_after_the_peer_timeout_and_its_input_released()
    {
        await using var h = new NetHarness(Timeouts(peer: TimeSpan.FromSeconds(6), ping: Timeout.InfiniteTimeSpan));
        var ended = new TaskCompletionSource<SessionEndedEventArgs>();
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
        for (int i = 0; i < 10; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(10); // let the pings and pongs cross the loopback
        }

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
    public async Task Too_many_pending_connections_are_refused_on_accept()
    {
        await using var h = new NetHarness(o => new PenBridgeServerOptions
        {
            Identity = o.Identity,
            Registry = o.Registry,
            InputSink = o.InputSink,
            BindAddress = o.BindAddress,
            Port = 0,
            TimeProvider = o.TimeProvider,
            MaxPendingConnections = 2,
        });
        await using RawClient a = await RawClient.ConnectTcpAsync(h.Port);
        await using RawClient b = await RawClient.ConnectTcpAsync(h.Port);
        await Wait.Until(() => h.Server.PendingConnections == 2, "two pending");

        await using RawClient c = await RawClient.ConnectTcpAsync(h.Port);

        await Wait.Until(() => c.IsClosed, "third connection refused");
        Assert.False(a.IsClosed);
    }

    [Fact]
    public async Task The_server_falls_back_to_the_next_port_when_tcp_or_udp_is_taken()
    {
        int basePort = FindFreePortRange();
        using var tcpBlocker = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        tcpBlocker.Bind(new IPEndPoint(IPAddress.Loopback, basePort));
        tcpBlocker.Listen();
        using var udpBlocker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udpBlocker.Bind(new IPEndPoint(IPAddress.Loopback, basePort + 1));

        await using var h = new NetHarness(o => new PenBridgeServerOptions
        {
            Identity = o.Identity,
            Registry = o.Registry,
            InputSink = o.InputSink,
            BindAddress = o.BindAddress,
            Port = basePort,
            TimeProvider = o.TimeProvider,
        });

        Assert.InRange(h.Port, basePort + 2, basePort + 9);

        // The TCP socket tried on the UDP-blocked port was released again.
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, basePort + 1));

        PenBridgeClient client = await h.PairAsync();
        EndToEndTests.DrawStroke(client);
        await EndToEndTests.AssertStrokeAsync(h.Sink);
    }

    [Fact]
    public async Task Start_fails_when_every_port_in_the_range_is_taken()
    {
        int basePort = FindFreePortRange();
        var blockers = new List<Socket>();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                s.Bind(new IPEndPoint(IPAddress.Loopback, basePort + i));
                s.Listen();
                blockers.Add(s);
            }

            ServerIdentity identity = NetHarness.SharedServerIdentity;
            await using var server = new PenBridgeServer(new PenBridgeServerOptions
            {
                Identity = identity,
                Registry = new PairedDeviceRegistry(new InMemorySecretStore()),
                InputSink = new ConcurrentSink(),
                BindAddress = IPAddress.Loopback,
                Port = basePort,
                PortFallbackCount = 2,
            });
            Assert.Throws<SocketException>(server.Start);
        }
        finally
        {
            foreach (Socket s in blockers)
            {
                s.Dispose();
            }
        }
    }

    /// <summary>An ephemeral base port whose next 9 ports are currently free for TCP and UDP (best effort).</summary>
    private static int FindFreePortRange()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            probe.Dispose();
            if (port + 9 > IPEndPoint.MaxPort)
            {
                continue;
            }

            bool free = true;
            for (int i = 0; i <= 9 && free; i++)
            {
                try
                {
                    using var t = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    t.Bind(new IPEndPoint(IPAddress.Loopback, port + i));
                    using var u = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    u.Bind(new IPEndPoint(IPAddress.Loopback, port + i));
                }
                catch (SocketException)
                {
                    free = false;
                }
            }

            if (free)
            {
                return port;
            }
        }

        throw new InvalidOperationException("No free port range found.");
    }
}
