using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

#pragma warning disable CA5359 // Test relay/raw clients deliberately accept any certificate (they are the attackers/probes).

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

    /// <summary>
    /// Counts the armed one-shot timers (deadlines) for which <paramref name="match"/>(state, armed for, remaining)
    /// holds; periodic timers (pings) are skipped. Lets a test wait for real I/O to finish or re-arm a deadline
    /// instead of guessing a real delay. Not called under any product lock: <paramref name="match"/> must be pure.
    /// </summary>
    public int CountDeadlines(Func<object?, TimeSpan, TimeSpan, bool> match)
    {
        lock (_lock)
        {
            int n = 0;
            foreach (FakeTimer t in _timers)
            {
                if (t.Period == 0 && t.Due != long.MaxValue
                    && match(t.State, TimeSpan.FromTicks(t.ArmedFor), TimeSpan.FromTicks(t.Due - _ticks)))
                {
                    n++;
                }
            }

            return n;
        }
    }

    private sealed class FakeTimer(FakeTime owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public long Due { get; set; } = long.MaxValue;

        public long Period { get; set; }

        /// <summary>The due time of the last <see cref="Change"/>, in ticks (-1 when disarmed).</summary>
        public long ArmedFor { get; private set; } = -1;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                ArmedFor = dueTime == Timeout.InfiniteTimeSpan ? -1 : dueTime.Ticks;
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
    private readonly List<object> _all = [];

    public void OnPenEvent(in PenEvent e)
    {
        lock (_lock)
        {
            _pen.Add(e);
            _all.Add(e);
        }
    }

    public void OnGestureEvent(in GestureEvent e)
    {
        lock (_lock)
        {
            _gestures.Add(e);
            _all.Add(e);
        }
    }

    /// <summary>Pen and gesture events interleaved in the order the sink received them (boxed <see cref="PenEvent"/>/<see cref="GestureEvent"/>).</summary>
    public object[] All()
    {
        lock (_lock)
        {
            return [.. _all];
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
            _all.Clear();
        }
    }
}

internal static class Wait
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>The running test's token: a run timeout or Ctrl+C stops a wait at once instead of after <see cref="Limit"/>.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Polls <paramref name="condition"/> (real time, bounded) for effects of asynchronous network I/O.</summary>
    public static Task Until(Func<bool> condition, string because) => Until(condition, static ok => ok, because);

    /// <summary>
    /// Polls <paramref name="probe"/> (real time, bounded by <see cref="Limit"/>) until <paramref name="ok"/> holds
    /// for its value, and returns that value. On timeout the failure shows the last value seen.
    /// </summary>
    public static async Task<T> Until<T>(Func<T> probe, Func<T, bool> ok, string because)
    {
        var elapsed = Stopwatch.StartNew();
        for (int polls = 0; ; polls++)
        {
            Token.ThrowIfCancellationRequested();
            T value = probe();
            if (ok(value))
            {
                return value;
            }

            if (elapsed.Elapsed > Limit)
            {
                Assert.Fail($"Timed out after {elapsed.Elapsed.TotalSeconds:0.0} s waiting: {because}; last seen: {Describe(value)}");
            }

            await Poll(polls);
        }
    }

    /// <summary>
    /// Advances fake time in steps until <paramref name="condition"/> holds. For deadlines armed by a server
    /// continuation that may not have run yet when the test proceeds.
    /// </summary>
    public static async Task AdvanceUntil(FakeTime time, TimeSpan step, Func<bool> condition, string because)
    {
        var elapsed = Stopwatch.StartNew();
        for (int polls = 0; !condition(); polls++)
        {
            if (elapsed.Elapsed > Limit)
            {
                Assert.Fail("Timed out advancing: " + because);
            }

            Token.ThrowIfCancellationRequested();
            time.Advance(step);
            await Poll(polls);
        }
    }

    /// <summary>A few yields first (most waits end within microseconds), then 1 ms delays so a busy machine isn't starved.</summary>
    private static async Task Poll(int polls)
    {
        if (polls < 16)
        {
            await Task.Yield();
        }
        else
        {
            await Task.Delay(1, Token);
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => '"' + s + '"',
        System.Collections.IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Describe)) + "]",
        _ => value.ToString() ?? "?",
    };

    /// <summary>The default peer timeout and ping interval (both sides) the liveness waits assume.</summary>
    public static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(6);

    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Waits until the last ping round reached both peers: every control-channel read deadline (armed for the
    /// default peer timeout) is more than <c>PeerTimeout - PingInterval</c> away, so the next step can't expire it.
    /// Before a round's frames arrive, a deadline is at most that far away.
    /// </summary>
    private static Task PeersAlive(FakeTime time) =>
        Until(() => time.CountDeadlines(static (state, armedFor, remaining) =>
            state is ControlChannel && armedFor == PeerTimeout && remaining <= PeerTimeout - PingInterval) == 0,
            "pings crossed the loopback");

    /// <summary>
    /// Waits until no control-channel write is in flight. A write's deadline counts fake time
    /// (<see cref="ControlChannel.WriteTimeout"/>), so a write that is merely slow in real time would expire if the
    /// test kept stepping.
    /// </summary>
    private static Task WritesDone(FakeTime time) =>
        Until(() => time.CountDeadlines(static (state, armedFor, _) =>
            state is ControlChannel && armedFor == ControlChannel.WriteTimeout) == 0,
            "control writes finished");

    /// <summary>
    /// <see cref="PeersAlive"/> then <see cref="WritesDone"/>: real I/O has caught up with fake time. Only for
    /// <see cref="Step"/>, which also works without a session; tests with a session order on
    /// <see cref="NetHarness.SettleAsync"/>.
    /// </summary>
    private static async Task Settled(FakeTime time)
    {
        await PeersAlive(time);
        await WritesDone(time);
    }

    /// <summary>
    /// Advances fake time in steps of at most <paramref name="step"/> (default 1 s). After each step it waits until
    /// real I/O caught up (<see cref="Settled"/>) and <paramref name="settled"/> holds, so I/O keeps up with fake time
    /// however slow the machine is: one big jump, or a fixed real pause per step, lets deadlines expire before the
    /// I/O that would have re-armed or finished them ran. Keep steps below the write timeout (2 s).
    /// </summary>
    public static async Task Step(FakeTime time, TimeSpan total, TimeSpan? step = null, Func<bool>? settled = null)
    {
        TimeSpan size = step ?? TimeSpan.FromSeconds(1);
        for (TimeSpan done = TimeSpan.Zero; done < total; done += size)
        {
            time.Advance(total - done < size ? total - done : size);
            await Settled(time);
            if (settled is not null)
            {
                await Until(settled, "I/O settled after a fake-time step");
            }
        }
    }

    /// <summary>Waits for <paramref name="task"/> at most <see cref="Limit"/>, and only while the test runs.</summary>
    public static Task<T> Bounded<T>(this Task<T> task) => task.WaitAsync(Limit, Token);

    /// <inheritdoc cref="Bounded{T}(Task{T})"/>
    public static Task Bounded(this Task task) => task.WaitAsync(Limit, Token);
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

    /// <summary>
    /// The one ordering barrier for a running session (reusable, independent of fake time): completes once every
    /// control frame either side sent before it was handled by the other side, including the pushes those frames
    /// caused. Events the phone raised for them are in their <see cref="EventLog{T}"/> by then.
    /// </summary>
    public Task SettleAsync() =>
        (Server.ActiveConnection ?? throw new InvalidOperationException("No session to settle.")).RoundTripAsync().Bounded();

    /// <summary>
    /// A client that is already paired: seeds both sides with a device key and connects with reconnect auth, then
    /// waits until the server applied its SurfaceInfo. Much cheaper than <see cref="PairAsync"/> (no J-PAKE); use it
    /// whenever the test isn't about pairing. <paramref name="subscribe"/> runs before the connect.
    /// </summary>
    public async Task<PenBridgeClient> ConnectPairedAsync(
        ISecretStore? store = null,
        DatagramFilter? filter = null,
        Func<PenBridgeClientOptions, PenBridgeClientOptions>? configure = null,
        Action<PenBridgeClient>? subscribe = null)
    {
        store ??= new InMemorySecretStore();
        var credentials = new ClientCredentialStore(store);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        Registry.Add(credentials.GetOrCreateDeviceId(), "Test Phone", key);
        credentials.SaveDeviceKey(Identity.Fingerprint, key);
        PenBridgeClient client = CreateClient(store, filter, configure);
        subscribe?.Invoke(client);
        await client.ConnectAsync("127.0.0.1", Port, Identity.Fingerprint.ToArray()).Bounded();
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

/// <summary>
/// A thread-safe record of events (subscribe with <see cref="Add"/>) that tests count from a <see cref="Mark"/>
/// instead of a baseline or a <c>Clear()</c>: a late event from before the mark can't be mistaken for a new one.
/// </summary>
internal sealed class EventLog<T>
{
    private readonly Lock _lock = new();
    private readonly List<T> _items = [];

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            _items.Add(item);
        }
    }

    /// <summary>The position of the next event: pass it to <see cref="Since"/> or <see cref="WaitFor"/>.</summary>
    public int Mark() => Count;

    /// <summary>Events recorded at or after <paramref name="mark"/>, in order.</summary>
    public T[] Since(int mark)
    {
        lock (_lock)
        {
            return [.. _items.Skip(mark)];
        }
    }

    public T[] All() => Since(0);

    /// <summary>Waits until at least <paramref name="count"/> events arrived since <paramref name="mark"/>; returns them.</summary>
    public Task<T[]> WaitFor(int mark, int count, string because) => WaitFor(mark, got => got.Length >= count, because);

    /// <summary>Waits until the events since <paramref name="mark"/> satisfy <paramref name="ok"/>; returns them.</summary>
    public Task<T[]> WaitFor(int mark, Func<T[], bool> ok, string because) => Wait.Until(() => Since(mark), ok, because);
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

    public async Task<byte[]> HelloAsync(Guid deviceId, ProtocolVersion? version = null, ProtocolCapabilities capabilities = ProtocolCapabilities.None)
    {
        byte[] id = deviceId.ToByteArray(bigEndian: true);
        await WriteAsync(new HelloMessage(version ?? ProtocolVersion.Current, id, "Raw", capabilities));
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
        _listener.Server.ReceiveBufferSize = 4096; // inherited by accepted sockets: a pause stalls the sender fast
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
