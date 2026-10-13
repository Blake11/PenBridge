using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

#pragma warning disable CA5359 // Test relay/raw clients deliberately accept any certificate (they are the attackers/probes).
#pragma warning disable xUnit1051 // Network waits are bounded by Wait.Limit instead of the test token.

namespace PenBridge.Core.Tests.Net;

/// <summary>
/// Deterministic clock with timers: nothing fires until <see cref="Advance"/>, which runs due callbacks in order
/// on the calling thread. Wall clock, timestamps and timers all derive from the same virtual time.
/// </summary>
internal sealed class FakeTime : TimeProvider
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly Lock _lock = new();
    private readonly List<FakeTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => Epoch + TimeSpan.FromTicks(Volatile.Read(ref _ticks));

    public override long GetTimestamp() => 1_000_000_000 + Volatile.Read(ref _ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time forward, firing every timer that comes due (including re-armed ones) in due order.</summary>
    public void Advance(TimeSpan by)
    {
        long target;
        lock (_lock)
        {
            target = _ticks + by.Ticks;
        }

        while (true)
        {
            FakeTimer? next = null;
            lock (_lock)
            {
                foreach (FakeTimer t in _timers)
                {
                    if (t.Due <= target && (next is null || t.Due < next.Due))
                    {
                        next = t;
                    }
                }

                if (next is null)
                {
                    Volatile.Write(ref _ticks, target);
                    return;
                }

                Volatile.Write(ref _ticks, Math.Max(_ticks, next.Due));
                next.Due = next.Period > 0 ? next.Due + next.Period : long.MaxValue;
            }

            next.Callback(next.State);
        }
    }

    private sealed class FakeTimer(FakeTime owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public long Due { get; set; } = long.MaxValue;

        public long Period { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + Math.Max(0, dueTime.Ticks);
                Period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                owner._timers.Remove(this);
                Due = long.MaxValue;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Thread-safe recording sink (the server calls it from pool threads and timer callbacks).</summary>
internal sealed class ConcurrentSink : IInputSink
{
    private readonly Lock _lock = new();
    private readonly List<PenEvent> _pen = [];
    private readonly List<GestureEvent> _gestures = [];

    public void OnPenEvent(in PenEvent e)
    {
        lock (_lock)
        {
            _pen.Add(e);
        }
    }

    public void OnGestureEvent(in GestureEvent e)
    {
        lock (_lock)
        {
            _gestures.Add(e);
        }
    }

    public PenEvent[] Pen()
    {
        lock (_lock)
        {
            return [.. _pen];
        }
    }

    public GestureEvent[] Gestures()
    {
        lock (_lock)
        {
            return [.. _gestures];
        }
    }

    public PenEventKind[] PenKinds() => [.. Pen().Select(e => e.Kind)];

    public void Clear()
    {
        lock (_lock)
        {
            _pen.Clear();
            _gestures.Clear();
        }
    }
}

internal static class Wait
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Polls <paramref name="condition"/> (real time, bounded) for effects of asynchronous network I/O.</summary>
    public static async Task Until(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting: " + because);
            }

            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Advances fake time in steps until <paramref name="condition"/> holds. For deadlines armed by a server
    /// continuation that may not have run yet when the test proceeds.
    /// </summary>
    public static async Task AdvanceUntil(FakeTime time, TimeSpan step, Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out advancing: " + because);
            }

            time.Advance(step);
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Advances fake time in 1 s steps with a short real pause between them, so pings and pongs actually cross the
    /// loopback before the next liveness deadline: one big jump would fire every deadline before any I/O ran.
    /// </summary>
    public static async Task Step(FakeTime time, TimeSpan total)
    {
        TimeSpan step = TimeSpan.FromSeconds(1);
        for (TimeSpan done = TimeSpan.Zero; done < total; done += step)
        {
            time.Advance(total - done < step ? total - done : step);
            await Task.Delay(20);
        }
    }

    public static Task<T> Bounded<T>(this Task<T> task) => task.WaitAsync(Limit);

    public static Task Bounded(this Task task) => task.WaitAsync(Limit);
}

/// <summary>A server on 127.0.0.1 (ephemeral port) with in-memory stores and a fake clock, plus client factories.</summary>
internal sealed class NetHarness : IAsyncDisposable
{
    public static readonly SurfaceInfoMessage Surface = new(1440, 3088, 500f, 500f, SurfaceOrientation.Portrait, SurfaceCapabilities.None);

    /// <summary>
    /// One identity for all harnesses: loading a PKCS#12 key on macOS goes through the keychain and costs most of a
    /// second, and the tests don't depend on distinct server certificates (the relay has its own).
    /// </summary>
    private static readonly Lazy<ServerIdentity> SharedIdentity = new(() => ServerIdentity.FromPkcs12(ServerIdentity.CreatePkcs12(DateTimeOffset.UtcNow)));

    private readonly List<PenBridgeClient> _clients = [];

    public static ServerIdentity SharedServerIdentity => SharedIdentity.Value;

    public NetHarness(Func<PenBridgeServerOptions, PenBridgeServerOptions>? configure = null)
    {
        Identity = SharedIdentity.Value;
        Registry = new PairedDeviceRegistry(new InMemorySecretStore(), Time);
        var options = new PenBridgeServerOptions
        {
            Identity = Identity,
            Registry = Registry,
            InputSink = Sink,
            ServerName = "Test Mac",
            BindAddress = IPAddress.Loopback,
            Port = 0,
            TargetDisplay = new DisplayRect(0, 0, 1000, 2000),
            TimeProvider = Time,
        };
        Server = new PenBridgeServer(configure is null ? options : configure(options));
        Server.Start();
    }

    public FakeTime Time { get; } = new();

    public ConcurrentSink Sink { get; } = new();

    public ServerIdentity Identity { get; }

    public PairedDeviceRegistry Registry { get; }

    public PenBridgeServer Server { get; }

    public int Port => Server.Port;

    public PenBridgeClient CreateClient(ISecretStore? store = null, DatagramFilter? filter = null, Func<PenBridgeClientOptions, PenBridgeClientOptions>? configure = null)
    {
        var options = new PenBridgeClientOptions
        {
            SecretStore = store ?? new InMemorySecretStore(),
            Surface = Surface,
            DeviceName = "Test Phone",
            DatagramFilter = filter,
            TimeProvider = Time,
        };
        var client = new PenBridgeClient(configure is null ? options : configure(options));
        _clients.Add(client);
        return client;
    }

    /// <summary>Pairs a new client with the PIN and waits until the server applied its SurfaceInfo.</summary>
    public async Task<PenBridgeClient> PairAsync(ISecretStore? store = null, DatagramFilter? filter = null, Func<PenBridgeClientOptions, PenBridgeClientOptions>? configure = null)
    {
        PenBridgeClient client = CreateClient(store, filter, configure);
        Server.OpenPairingWindow();
        await client.PairWithPinAsync("127.0.0.1", Port, Server.CurrentPin!).Bounded();
        await WaitForSurfaceAsync();
        return client;
    }

    public Task WaitForSurfaceAsync() =>
        Wait.Until(() => Server.ActiveInput is { IsSurfaceConfigured: true }, "server applied SurfaceInfo");

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync().AsTask().Bounded();
        foreach (PenBridgeClient client in _clients)
        {
            await client.DisposeAsync().AsTask().Bounded();
        }
    }
}

internal static class Pens
{
    public static PenSample Hover(ushort x, ushort y) => new(x, y, 0, 0, 0, 0, PenTool.Pen, PenState.InRange, PenButtons.None);

    public static PenSample Contact(ushort x, ushort y, ushort pressure = 30000, PenButtons buttons = PenButtons.None) =>
        new(x, y, pressure, 0, 0, 0, PenTool.Pen, PenState.InRange | PenState.Contact, buttons);
}

/// <summary>
/// A man-in-the-middle on loopback: terminates TLS with its own certificate and forwards the plaintext to the real
/// server over a second TLS connection (accepting any certificate). Pairing through it must fail confirmation.
/// </summary>
internal sealed class TlsRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private static readonly Lazy<ServerIdentity> RelayIdentity = new(() => ServerIdentity.FromPkcs12(ServerIdentity.CreatePkcs12(DateTimeOffset.UtcNow)));
    private readonly ServerIdentity _identity = RelayIdentity.Value;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _targetPort;
    private readonly Task _loop;

    public TlsRelay(int targetPort)
    {
        _targetPort = targetPort;
        _listener.Start();
        _loop = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ReadOnlySpan<byte> Fingerprint => _identity.Fingerprint;

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop.WaitAsync(Wait.Limit);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient inbound;
            try
            {
                inbound = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = RelayAsync(inbound);
        }
    }

    private async Task RelayAsync(TcpClient inbound)
    {
        using (inbound)
        using (var outbound = new TcpClient())
        {
            try
            {
                using var front = new SslStream(inbound.GetStream());
                await front.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _identity.Certificate }, _cts.Token);
                await outbound.ConnectAsync(IPAddress.Loopback, _targetPort, _cts.Token);
                using var back = new SslStream(outbound.GetStream());
                await back.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = "relay", RemoteCertificateValidationCallback = (_, _, _, _) => true },
                    _cts.Token);
                Task a = front.CopyToAsync(back, _cts.Token);
                Task b = back.CopyToAsync(front, _cts.Token);
                await Task.WhenAny(a, b);
            }
            catch (Exception)
            {
                // The relay just drops on any failure.
            }
        }
    }
}

/// <summary>A hand-driven control client for timeout and protocol-violation tests.</summary>
internal sealed class RawClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private SslStream? _ssl;
    private ControlFrameReader? _reader;
    private Task? _drain;

    public Socket Socket => _tcp.Client;

    public SslStream Ssl => _ssl!;

    public static async Task<RawClient> ConnectTcpAsync(int port)
    {
        var client = new RawClient();
        await client._tcp.ConnectAsync(IPAddress.Loopback, port).Bounded();
        return client;
    }

    public static async Task<RawClient> ConnectTlsAsync(int port)
    {
        RawClient client = await ConnectTcpAsync(port);
        client._ssl = new SslStream(client._tcp.GetStream());
        await client._ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "penbridge",
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        }).Bounded();
        client._reader = new ControlFrameReader(client._ssl);
        return client;
    }

    public Task WriteAsync<T>(T message)
        where T : IControlMessage<T> => ControlFrameWriter.WriteAsync(_ssl!, message).AsTask().Bounded();

    public async Task<ReceivedControlFrame?> ReadAsync() => await _reader!.ReadAsync().AsTask().Bounded();

    public async Task<byte[]> HelloAsync(Guid deviceId, ProtocolVersion? version = null)
    {
        byte[] id = deviceId.ToByteArray(bigEndian: true);
        await WriteAsync(new HelloMessage(version ?? ProtocolVersion.Current, id, "Raw", ProtocolCapabilities.None));
        ReceivedControlFrame? frame = await ReadAsync();
        Assert.NotNull(frame);
        return frame.Value.Read<HelloAckMessage>().Challenge.ToArray();
    }

    /// <summary>
    /// True once the server closed the connection. The first call starts draining the stream in the background,
    /// so don't use <see cref="ReadAsync"/> afterwards.
    /// </summary>
    public bool IsClosed
    {
        get
        {
            _drain ??= DrainAsync();
            return _drain.IsCompleted;
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            byte[] buffer = new byte[256];
            Stream stream = _ssl is not null ? _ssl : _tcp.GetStream();
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        }
        catch (Exception)
        {
            // Any failure means the connection is gone.
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        _ssl?.Dispose();
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A plain TCP pass-through to the server. While <see cref="Paused"/>, it stops reading from the client, so the
/// client's TCP send buffer fills and its writes stall like against a server that stopped reading.
/// </summary>
internal sealed class PausableProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly int _targetPort;
    private readonly Task _loop;
    private volatile bool _paused;

    public PausableProxy(int targetPort)
    {
        _targetPort = targetPort;
        _listener.Start();
        _loop = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop.WaitAsync(Wait.Limit);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket inbound;
            try
            {
                inbound = await _listener.AcceptSocketAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            inbound.ReceiveBufferSize = 4096;
            _ = PumpPairAsync(inbound);
        }
    }

    private async Task PumpPairAsync(Socket inbound)
    {
        using (inbound)
        using (var outbound = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            try
            {
                await outbound.ConnectAsync(IPAddress.Loopback, _targetPort, _cts.Token);
                Task up = PumpAsync(inbound, outbound, pausable: true);
                Task down = PumpAsync(outbound, inbound, pausable: false);
                await Task.WhenAny(up, down);
            }
            catch (Exception)
            {
                // Dropped on any failure.
            }
        }
    }

    private async Task PumpAsync(Socket from, Socket to, bool pausable)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (true)
            {
                while (pausable && _paused)
                {
                    await Task.Delay(10, _cts.Token);
                }

                int n = await from.ReceiveAsync(buffer, SocketFlags.None, _cts.Token);
                if (n == 0)
                {
                    return;
                }

                await to.SendAsync(buffer.AsMemory(0, n), SocketFlags.None, _cts.Token);
            }
        }
        catch (Exception)
        {
            // Either side closed.
        }
    }
}
