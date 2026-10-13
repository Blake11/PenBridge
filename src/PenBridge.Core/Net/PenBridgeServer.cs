using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Core.Net;

/// <summary>
/// The Mac-side network engine: TLS control server and encrypted UDP input receiver on one port (47820, falling back
/// through 47829), pairing, reconnect auth, one active session with optional takeover, and the input pipeline into
/// <see cref="PenBridgeServerOptions.InputSink"/>.
/// </summary>
/// <remarks>
/// <para>Threads: one accept loop, one UDP receive loop, and one async handler per TCP connection, all on the thread
/// pool. Timers (handshake/read/pairing deadlines, ping, input watchdog) are one-shot or periodic
/// <see cref="ITimer"/>s from the injected <see cref="TimeProvider"/>; nothing polls, so an idle server uses no CPU.</para>
/// <para>All tracker, recognizer and sink calls are serialized by one input lock. Events are raised on thread-pool
/// threads; handlers must be quick and marshal to the UI themselves.</para>
/// </remarks>
public sealed class PenBridgeServer : IAsyncDisposable
{
    private readonly PenBridgeServerOptions _options;
    private readonly ILogger _log;
    private readonly PinIssuer _pins;
    private readonly Lock _inputLock = new();
    private readonly Lock _gate = new();
    private readonly HashSet<ServerConnection> _connections = [];
    private readonly HashSet<uint> _usedSessionIds = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SslServerAuthenticationOptions _tlsOptions;
    private readonly string _serverName;
    private Socket? _tcp;
    private Socket? _udp;
    private Task? _acceptLoop;
    private Task? _udpLoop;
    private ServerConnection? _active;
    private SessionInput? _activeInput;
    private int _pending;
    private volatile bool _allowTakeover;
    private DisplayRect _display;
    private NormalizedRect _region;
    private SessionInput? _ppsInput;
    private long _ppsCount;
    private long _ppsTimestamp;
    private double _pps;
    private int _disposed;

    public PenBridgeServer(PenBridgeServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Identity);
        ArgumentNullException.ThrowIfNull(options.Registry);
        ArgumentNullException.ThrowIfNull(options.InputSink);
        ArgumentNullException.ThrowIfNull(options.BindAddress);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, IPEndPoint.MaxPort);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PortFallbackCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPendingConnections, 1);
        _ = new CoordinateMapper(1, 1, options.TargetDisplay, options.TargetRegion); // validates the target

        _options = options;
        _log = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger("PenBridge.Server");
        _pins = new PinIssuer(options.TimeProvider);
        _pins.SecretsChanged += OnSecretsChanged;
        _allowTakeover = options.AllowTakeover;
        _display = options.TargetDisplay;
        _region = options.TargetRegion;
        _serverName = WireString.Truncate(options.ServerName ?? string.Empty);
        _tlsOptions = new SslServerAuthenticationOptions
        {
            ServerCertificateContext = SslStreamCertificateContext.Create(options.Identity.Certificate, additionalCertificates: null, offline: true),
            ClientCertificateRequired = false,
#pragma warning disable CA5398 // ADR-0013: TLS 1.2+ is a deliberate floor, not a pinned version.
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
#pragma warning restore CA5398
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };
    }

    /// <summary>Raised when the pairing window opens or closes or the PIN/token is replaced (refresh the UI).</summary>
    public event EventHandler? PairingWindowChanged;

    /// <summary>Raised after a device paired successfully.</summary>
    public event EventHandler<DevicePairedEventArgs>? DevicePaired;

    /// <summary>Raised when a device's session starts (after SessionStart was sent).</summary>
    public event EventHandler<SessionStartedEventArgs>? SessionStarted;

    /// <summary>Raised when the active session ends (after its input was released).</summary>
    public event EventHandler<SessionEndedEventArgs>? SessionEnded;

    /// <summary>The bound TCP/UDP port (0 before <see cref="Start"/>).</summary>
    public int Port { get; private set; }

    /// <summary>The server certificate fingerprint phones pin.</summary>
    public ReadOnlySpan<byte> Fingerprint => _options.Identity.Fingerprint;

    public string FingerprintBase64Url => _options.Identity.FingerprintBase64Url;

    public string ServerName => _serverName;

    public IPairedDeviceRegistry Registry => _options.Registry;

    /// <summary>"Allow takeover" (ADR-0010): a new device closes the active session instead of getting Busy.</summary>
    public bool AllowTakeover
    {
        get => _allowTakeover;
        set => _allowTakeover = value;
    }

    /// <summary>The PIN to show, or null when the pairing window is closed.</summary>
    public string? CurrentPin => _pins.CurrentPin;

    public bool IsPairingWindowOpen => _pins.IsWindowOpen;

    public DateTimeOffset PairingWindowExpiresAt => _pins.ExpiresAt;

    public DateTimeOffset PairingLockedUntil => _pins.LockedUntil;

    public int PairingAttemptsLeft => _pins.AttemptsLeft;

    internal Lock InputLock => _inputLock;

    internal PenBridgeServerOptions Options => _options;

    internal SslServerAuthenticationOptions TlsOptions => _tlsOptions;

    internal PinIssuer Pins => _pins;

    internal ILogger Log => _log;

    internal int PendingConnections => Volatile.Read(ref _pending);

    internal int ConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>The active session's input path (tests).</summary>
    internal SessionInput? ActiveInput => Volatile.Read(ref _activeInput);

    /// <summary>Opens "Pair new device…": fresh PIN and QR token for 2 minutes.</summary>
    public void OpenPairingWindow() => _pins.OpenPairingWindow();

    public void ClosePairingWindow() => _pins.ClosePairingWindow();

    /// <summary>
    /// The QR payload for the open pairing window, or null when it is closed. <paramref name="host"/> defaults to
    /// <see cref="PenBridgeServerOptions.AdvertisedHost"/>, else the first non-loopback IPv4 address.
    /// </summary>
    public PairingQrPayload? CreateQrPayload(string? host = null)
    {
        if (Port == 0)
        {
            throw new InvalidOperationException("The server is not started.");
        }

        string? token = _pins.CurrentQrTokenBase64Url;
        if (token is null || !Base64UrlBytes.TryDecode32(token, out byte[] tokenBytes))
        {
            return null;
        }

        // The payload keeps a reference to tokenBytes (it is the QR's content), so it is not zeroed here.
        return new PairingQrPayload(
            host ?? _options.AdvertisedHost ?? DetectLanHost(),
            (ushort)Port,
            _options.Identity.Fingerprint.ToArray(),
            tokenBytes,
            ProtocolVersion.Current);
    }

    /// <summary>Binds TCP and UDP on the configured port (with fallback) and starts accepting.</summary>
    /// <exception cref="SocketException">No port in the range could be bound for both TCP and UDP.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_tcp is not null)
        {
            throw new InvalidOperationException("The server is already started.");
        }

        int basePort = _options.Port;
        int attempts = basePort == 0 ? 10 : _options.PortFallbackCount + 1;
        for (int i = 0; i < attempts && _tcp is null; i++)
        {
            int port = basePort == 0 ? 0 : basePort + i;
            if (port > IPEndPoint.MaxPort)
            {
                break;
            }

            if (!TryBind(port))
            {
                NetLog.PortUnavailable(_log, port);
            }
        }

        if (_tcp is null)
        {
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        }

        Port = ((IPEndPoint)_tcp.LocalEndPoint!).Port;
        NetLog.Listening(_log, Port);
        _acceptLoop = AcceptLoopAsync();
        _udpLoop = UdpLoopAsync();
    }

    /// <summary>Changes the display region the phone maps to (applies to the active session immediately).</summary>
    public void SetTarget(DisplayRect display, NormalizedRect region)
    {
        _ = new CoordinateMapper(1, 1, display, region);
        SessionInput? input;
        lock (_gate)
        {
            _display = display;
            _region = region;
            input = _activeInput;
        }

        input?.SetTarget(display, region);
    }

    /// <summary>A snapshot for the menu: active device, transport, RTT, packets/s and input counters.</summary>
    public PenBridgeServerStats GetStats()
    {
        ServerConnection? connection = Volatile.Read(ref _active);
        SessionInput? input = connection?.Input;
        if (connection is null || input is null)
        {
            return new PenBridgeServerStats(false, Guid.Empty, null, InputTransport.None, null, 0, default);
        }

        long accepted = input.Accepted;
        double pps;
        lock (_gate)
        {
            long now = _options.TimeProvider.GetTimestamp();
            if (!ReferenceEquals(_ppsInput, input))
            {
                _ppsInput = input;
                _ppsCount = accepted;
                _ppsTimestamp = now;
                _pps = 0;
            }
            else
            {
                TimeSpan elapsed = _options.TimeProvider.GetElapsedTime(_ppsTimestamp, now);
                if (elapsed >= TimeSpan.FromSeconds(1))
                {
                    _pps = (accepted - _ppsCount) / elapsed.TotalSeconds;
                    _ppsCount = accepted;
                    _ppsTimestamp = now;
                }
            }

            pps = _pps;
        }

        return new PenBridgeServerStats(
            true, connection.DeviceId, connection.DeviceName, input.Transport, connection.RoundTripTime, pps, input.Counters);
    }

    /// <summary>Forgets a paired device; its active session (if any) is closed with Bye(Unpaired).</summary>
    public bool ForgetDevice(Guid deviceId)
    {
        bool removed = _options.Registry.Remove(deviceId);
        ServerConnection? connection;
        lock (_gate)
        {
            connection = _active is { } a && a.DeviceId == deviceId ? a : null;
        }

        if (connection is not null)
        {
            _ = connection.CloseAsync(ByeReason.Unpaired, SessionEndReason.Unpaired);
        }

        return removed;
    }

    /// <summary>Closes the active session with Bye(Normal).</summary>
    public Task DisconnectSessionAsync()
    {
        ServerConnection? connection = Volatile.Read(ref _active);
        return connection is null ? Task.CompletedTask : connection.CloseAsync(ByeReason.Normal, SessionEndReason.LocalDisconnect);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _tcp?.Dispose();
        _udp?.Dispose();
        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        if (_udpLoop is not null)
        {
            await _udpLoop.ConfigureAwait(false);
        }

        ServerConnection[] connections;
        lock (_gate)
        {
            connections = [.. _connections];
        }

        var closing = new Task[connections.Length];
        for (int i = 0; i < connections.Length; i++)
        {
            closing[i] = connections[i].CloseAsync(ByeReason.Shutdown, SessionEndReason.Shutdown);
        }

        await Task.WhenAll(closing).ConfigureAwait(false);
        _pins.SecretsChanged -= OnSecretsChanged;
        _pins.ClosePairingWindow();
        _shutdown.Dispose();
    }

    internal bool TryActivate(ServerConnection connection, out ServerConnection? previous)
    {
        lock (_gate)
        {
            previous = _active;
            if (previous is not null && previous.DeviceId != connection.DeviceId && !_allowTakeover)
            {
                previous = null;
                return false;
            }

            _active = connection;
            Volatile.Write(ref _activeInput, null);
            return true;
        }
    }

    internal bool PublishInput(ServerConnection connection, SessionInput input)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, connection))
            {
                return false;
            }

            input.SetTarget(_display, _region);
            Volatile.Write(ref _activeInput, input);
            return true;
        }
    }

    internal void OnConnectionEnded(ServerConnection connection)
    {
        lock (_gate)
        {
            _connections.Remove(connection);
            if (ReferenceEquals(_active, connection))
            {
                _active = null;
                Volatile.Write(ref _activeInput, null);
            }
        }
    }

    /// <summary>A random non-zero session id never used before on this server instance (GCM nonce uniqueness).</summary>
    internal uint NextSessionId()
    {
        Span<byte> bytes = stackalloc byte[4];
        lock (_gate)
        {
            while (true)
            {
                RandomNumberGenerator.Fill(bytes);
                uint id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
                if (id != 0 && _usedSessionIds.Add(id))
                {
                    return id;
                }
            }
        }
    }

    internal void ReleasePending() => Interlocked.Decrement(ref _pending);

    internal void RaiseDevicePaired(PairedDevice device) => Raise(DevicePaired, new DevicePairedEventArgs(device));

    internal void RaiseSessionStarted(SessionInfo session) => Raise(SessionStarted, new SessionStartedEventArgs(session));

    internal void RaiseSessionEnded(SessionInfo session, SessionEndReason reason) => Raise(SessionEnded, new SessionEndedEventArgs(session, reason));

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, args);
        }
#pragma warning disable CA1031 // A faulty UI handler must not take down the network engine.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            NetLog.Unexpected(_log, null, ex);
        }
    }

    private void OnSecretsChanged(object? sender, EventArgs e)
    {
        try
        {
            PairingWindowChanged?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // A faulty UI handler must not break the pairing state machine.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            NetLog.Unexpected(_log, null, ex);
        }
    }

    private bool TryBind(int port)
    {
        Socket? tcp = null;
        Socket? udp = null;
        try
        {
            tcp = CreateSocket(SocketType.Stream, ProtocolType.Tcp);
            tcp.Bind(new IPEndPoint(_options.BindAddress, port));
            tcp.Listen(16);
            int bound = ((IPEndPoint)tcp.LocalEndPoint!).Port;
            udp = CreateSocket(SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(_options.BindAddress, bound));
            _tcp = tcp;
            _udp = udp;
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            tcp?.Dispose();
            udp?.Dispose();
            return false;
        }
    }

    private Socket CreateSocket(SocketType type, ProtocolType protocol)
    {
        IPAddress address = _options.BindAddress;
        var socket = new Socket(address.AddressFamily, type, protocol);
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.Equals(IPAddress.IPv6Any))
        {
            socket.DualMode = true;
        }

        return socket;
    }

    private async Task AcceptLoopAsync()
    {
        CancellationToken ct = _shutdown.Token;
        Socket listener = _tcp!;
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                // Transient (e.g. out of descriptors): back off briefly instead of spinning.
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            if (Interlocked.Increment(ref _pending) > _options.MaxPendingConnections)
            {
                Interlocked.Decrement(ref _pending);
                NetLog.TooManyPending(_log, SafeRemote(socket));
                socket.Dispose();
                continue;
            }

            var connection = new ServerConnection(this, socket);
            lock (_gate)
            {
                _connections.Add(connection);
            }

            connection.Start();
        }
    }

    private async Task UdpLoopAsync()
    {
        CancellationToken ct = _shutdown.Token;
        Socket udp = _udp!;

        // One byte more than the maximum so oversized datagrams are seen as such and dropped.
        byte[] buffer = GC.AllocateArray<byte>(InputDatagram.MaxSize + 1, pinned: true);
        var address = new SocketAddress(udp.AddressFamily);
        while (!ct.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await udp.ReceiveFromAsync(buffer, SocketFlags.None, address, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                NetLog.UdpReceiveError(_log, ex.SocketErrorCode);
                continue;
            }

            Volatile.Read(ref _activeInput)?.ProcessDatagram(buffer.AsSpan(0, received), address);
        }
    }

    private static EndPoint? SafeRemote(Socket socket)
    {
        try
        {
            return socket.RemoteEndPoint;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static string DetectLanHost()
    {
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                {
                    IPAddress a = info.Address;
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        return a.ToString();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
        }

        return "localhost";
    }
}
