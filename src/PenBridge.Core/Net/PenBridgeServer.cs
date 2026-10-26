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
using PenBridge.Core.Mirror;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Core.Net;

/// <summary>
/// The Mac-side network engine: TLS control server and encrypted UDP input receiver on one port (47820, falling back
/// through 47829), pairing, reconnect auth, one active session with optional takeover, and the input pipeline into
/// <see cref="PenBridgeServerOptions.InputSink"/>.
/// </summary>
/// <remarks>
/// <para>Threads: one dedicated input thread (blocking UDP receive → decrypt → session input → sink, started through
/// <see cref="PenBridgeServerOptions.MirrorThreadStarted"/> with <see cref="MirrorThreadRole.InputReceive"/> so the head
/// can give it the highest priority; it owns the UDP socket, see <see cref="VideoSockets"/>), plus one accept loop and
/// one async handler per TCP connection on the thread pool. Timers (handshake/read/pairing deadlines, ping, input watchdog) are one-shot or periodic
/// <see cref="ITimer"/>s from the injected <see cref="TimeProvider"/>; nothing polls, so an idle server uses no CPU.</para>
/// <para>All tracker, recognizer and sink calls are serialized by one input lock. Events are raised on thread-pool
/// threads; handlers must be quick and marshal to the UI themselves.</para>
/// <para>Takeover ordering (ADR-0010, #107): the displaced session's input release (synthetic pen Up/ButtonUps/Leave,
/// gesture Cancelled) reaches the sink, under the input lock, before the new session's input is published; after that
/// no sink call comes from the old session (late frames, datagrams and timers are dropped). The old session's
/// <see cref="SessionEnded"/> is raised before the new one's <see cref="SessionStarted"/>.</para>
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
    private Thread? _udpThread;
    private volatile bool _udpStopping;
    private readonly TaskCompletionSource _udpDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _inputThreadAllocated;
    private long _inputThreadDatagrams;
    private ServerConnection? _active;
    private SessionInput? _activeInput;
    private SessionInput? _inputOwner; // the last published input; guarded by _inputLock
    private readonly Dictionary<IPAddress, int> _pendingByAddress = [];
    private int _pending;
    private volatile bool _allowTakeover;
    private SettingsSnapshotMessage? _settingsSnapshot; // guarded by _gate
    private DisplayListMessage? _displayList; // guarded by _gate
    private readonly Lock _applyLock = new(); // orders target/settings applications to the input; see ApplyToInput
    private PhoneSurfaceSize? _phoneSurface; // guarded by _gate
    private MappingTarget _target; // guarded by _gate
    private InputSettings _inputSettings; // guarded by _gate
    private SessionInput? _ppsInput;
    private long _ppsCount;
    private long _ppsTimestamp;
    private double _pps;
    private int _disposed;
    private int _qosWarned;

    // Screen mirroring inputs from the head (server-wide), guarded by _gate.
    private MirrorState _mirrorState = MirrorState.Starting;
    private MirrorReason _mirrorReason;
    private MirrorCodecs _mirrorEncoders;
    private bool _mirrorEncodersSet;
    private MirrorSource? _mirrorSource;
    private long _mirrorMaxPixelRate;
    private MirrorSettingsState _mirrorSettings = MirrorSettingsState.Default;

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
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPendingConnectionsPerAddress, 1);
        _ = new CoordinateMapper(1, 1, options.TargetDisplay, options.TargetRegion); // validates the target
        ArgumentNullException.ThrowIfNull(options.InputSettings);
        options.InputSettings.Validate();

        _options = options;
        _log = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger("PenBridge.Server");
        _pins = new PinIssuer(options.TimeProvider);
        _pins.SecretsChanged += OnSecretsChanged;
        _allowTakeover = options.AllowTakeover;
        _target = MappingTarget.ForDisplay(options.TargetDisplay, options.TargetRegion);
        _inputSettings = options.InputSettings;
        _serverName = WireString.Truncate(options.ServerName ?? string.Empty);
        MirrorEvents = new MirrorEvents(this);
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

    /// <summary>
    /// Raised when the phone sends a SettingsUpdate (needs <see cref="PenBridgeServerOptions.RemoteSettings"/>), on the
    /// session's control-read task, in arrival order, never under the input lock. Validate and apply the entries, then
    /// reply with <see cref="SendSettingsSnapshotAsync"/> (the snapshot is the acknowledgement, ADR-0030). Handlers must
    /// be quick and marshal to the UI themselves. At most 20 updates per second (burst 20) per session reach it; the
    /// excess is dropped (and logged), so a reply isn't guaranteed for every update the phone sent.
    /// </summary>
    public event EventHandler<SettingsUpdateRequestedEventArgs>? SettingsUpdateRequested;

    /// <summary>
    /// Raised after <see cref="PhoneSurface"/> changed (a SurfaceInfo arrived, or the session ended or was taken over).
    /// Raised outside the server's locks from whichever thread caused it; handlers must be quick and only queue (read
    /// <see cref="PhoneSurface"/> for the current value, which may be newer than the one that triggered the call).
    /// </summary>
    public event EventHandler? PhoneSurfaceChanged;

    /// <summary>
    /// Raised when the phone asks for video (every MirrorStart; needs <see cref="PenBridgeServerOptions.ScreenMirror"/>):
    /// start capturing the current target. Answer with <see cref="SetMirrorState"/> if the head can't stream (setting
    /// off, paused, no Screen Recording permission, capture failed); Streaming means it can. Video starts with the
    /// next <see cref="MirrorConfigChanged"/>. Mirroring events are raised in order on a thread-pool thread, never under
    /// a server lock; handlers must be quick and may call back into the server.
    /// </summary>
    public event EventHandler<MirrorRequest>? MirrorRequested;

    /// <summary>
    /// Raised when the phone no longer wants video (MirrorStop, Bye, takeover, session end, TLS fallback): stop
    /// capturing and encoding. <see cref="SendVideoFrame"/> drops frames from now on.
    /// </summary>
    public event EventHandler? MirrorStopped;

    /// <summary>
    /// Raised with every new epoch: (re)configure the encoder to this codec, size, fps and bitrate, and make its next
    /// access unit a keyframe (a resize gives one anyway; an in-place rate change needs a keyframe request). Core
    /// discards access units until that keyframe, which becomes the epoch's frame 1. Every accepted MirrorStart starts
    /// a new epoch, also a repeated one with an unchanged plan, and so does a return to Streaming (after Paused,
    /// Unavailable or a TLS fallback): never keep sending into an old configuration.
    /// </summary>
    public event EventHandler<MirrorStreamConfig>? MirrorConfigChanged;

    /// <summary>The phone lost video: encode the next frame as a keyframe (at most one per 250 ms reaches the head).</summary>
    public event EventHandler? KeyframeRequested;

    /// <summary>
    /// The head's mirroring availability (server-wide, kept across sessions): <see cref="MirrorState.Streaming"/> when
    /// it can capture and encode now, or <see cref="MirrorState.Off"/>, <see cref="MirrorState.Paused"/> or
    /// <see cref="MirrorState.Unavailable"/> with a reason (SettingOff, PenBridgePaused, PermissionDenied, CaptureFailed),
    /// or <see cref="MirrorState.Starting"/> (the default: not decided yet). Core composes what the phone sees: it adds
    /// TlsFallback, NoCommonCodec, SettingOff (MirrorEnabled false in the settings snapshot) and Throttled (the input
    /// guard) itself, and while Streaming sends the stream configuration. Thread-safe.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An unknown state, Throttled (Core's own), or a reason with a state that carries none.</exception>
    public void SetMirrorState(MirrorState state, MirrorReason reason = MirrorReason.None)
    {
        if (!MirrorWire.IsKnown(state) || state == MirrorState.Throttled || !MirrorWire.IsKnown(reason)
            || (reason != MirrorReason.None && !MirrorWire.HasReason(state)))
        {
            throw new ArgumentOutOfRangeException(nameof(state), "A known state other than Throttled; a reason only with Off, Paused or Unavailable.");
        }

        lock (_gate)
        {
            _mirrorState = state;
            _mirrorReason = reason;
        }

        ReevaluateMirror();
    }

    /// <summary>
    /// The Mac's hardware encoders (planner input; HEVC is preferred). Drop HEVC here when its session failed to
    /// start: the stream falls back to H.264 with a new epoch. Until the first call the stream waits (Starting). Thread-safe.
    /// </summary>
    public void SetMirrorEncoders(MirrorCodecs codecs)
    {
        lock (_gate)
        {
            _mirrorEncoders = codecs;
            _mirrorEncodersSet = true;
        }

        ReevaluateMirror();
    }

    /// <summary>
    /// The current target's capture source: its size in pixels and its display's refresh rate (All displays: the
    /// highest; a variable-rate display: its maximum), plus optionally the encoder's measured pixel-rate cap
    /// (fps × width × height per second; 0 = the default <see cref="MirrorStreamPlanner.DefaultMaxPixelRate"/>). Call
    /// it for every target change and refresh change; until the first call the stream waits (Starting). Thread-safe.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A size or rate below 1, or a negative pixel rate.</exception>
    public void SetMirrorSource(int widthPx, int heightPx, int refreshHz, long maxPixelRate = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(widthPx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(heightPx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(refreshHz, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPixelRate);
        lock (_gate)
        {
            _mirrorSource = new MirrorSource(widthPx, heightPx, refreshHz);
            _mirrorMaxPixelRate = maxPixelRate;
        }

        ReevaluateMirror();
    }

    /// <summary>
    /// Sends one encoded access unit (Annex-B; a keyframe starts with its parameter sets) to the phone. Thread-safe and
    /// non-blocking: the bytes are copied and fragmented, sealed and paced on Core's video send thread, below input
    /// priority. Dropped when no stream runs, before the epoch's first keyframe, and when the frame before it is still
    /// waiting to go out. <paramref name="captureTimeUs"/>: the capture time in host-monotonic microseconds on the
    /// <see cref="PenBridgeServerOptions.TimeProvider"/>'s timestamp scale (for <see cref="TimeProvider.System"/>,
    /// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> in µs; on the Mac that is <c>CLOCK_UPTIME_RAW</c>).
    /// Allocation-free once warm.
    /// </summary>
    public void SendVideoFrame(ReadOnlySpan<byte> accessUnit, bool keyframe, long captureTimeUs) =>
        Volatile.Read(ref _active)?.Mirror?.SendVideoFrame(accessUnit, keyframe, captureTimeUs);

    /// <summary>The mirroring state the active session's phone sees (Off without a session or a request).</summary>
    public (MirrorState State, MirrorReason Reason) MirrorStatus =>
        Volatile.Read(ref _active)?.Mirror?.State ?? (MirrorState.Off, MirrorReason.None);

    internal MirrorEvents MirrorEvents { get; }

    /// <summary>The head's mirroring inputs (planner and state).</summary>
    internal MirrorHostState MirrorHost
    {
        get
        {
            lock (_gate)
            {
                return new MirrorHostState(_mirrorState, _mirrorReason, _mirrorEncoders, _mirrorEncodersSet, _mirrorSource, _mirrorMaxPixelRate, _mirrorSettings);
            }
        }
    }

    internal void RaiseMirrorEvent(MirrorEvents.Kind kind, MirrorRequest request, MirrorStreamConfig config)
    {
        switch (kind)
        {
            case MirrorEvents.Kind.Requested:
                Raise(MirrorRequested, request);
                break;
            case MirrorEvents.Kind.Stopped:
                Raise(MirrorStopped);
                break;
            case MirrorEvents.Kind.ConfigChanged:
                Raise(MirrorConfigChanged, config);
                break;
            default:
                Raise(KeyframeRequested);
                break;
        }
    }

    private void ReevaluateMirror() => Volatile.Read(ref _active)?.Mirror?.Reevaluate();

    /// <summary>
    /// The active session's phone surface in pixels (from its latest SurfaceInfo), or null before the first
    /// SurfaceInfo and when no session is active. Thread-safe.
    /// </summary>
    public PhoneSurfaceSize? PhoneSurface
    {
        get
        {
            lock (_gate)
            {
                return _phoneSurface;
            }
        }
    }

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

    internal int PendingConnections
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>Ports that failed to bind during <see cref="Start"/> (tests).</summary>
    internal List<int> FailedPorts { get; } = [];

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

        foreach (int port in _options.PortCandidates ?? PortRange(_options.Port, _options.PortFallbackCount))
        {
            if (TryBind(port))
            {
                break;
            }

            FailedPorts.Add(port);
            NetLog.PortUnavailable(_log, port);
        }

        if (_tcp is null)
        {
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        }

        Port = ((IPEndPoint)_tcp.LocalEndPoint!).Port;
        NetLog.Listening(_log, Port);
        _acceptLoop = AcceptLoopAsync();
        VideoSockets.PrepareForReceiveThread(_udp!);
        _udpThread = MirrorThreads.Start("PenBridge input receive", MirrorThreadRole.InputReceive, _options.MirrorThreadStarted, _log, UdpReceiveLoop);
    }

    /// <summary>The input thread's <see cref="GC.GetAllocatedBytesForCurrentThread"/> after its last datagram (tests).</summary>
    internal long InputThreadAllocatedBytes => Interlocked.Read(ref _inputThreadAllocated);

    /// <summary>Datagrams the input thread handled, counted after <see cref="InputThreadAllocatedBytes"/> (tests).</summary>
    internal long InputThreadDatagrams => Interlocked.Read(ref _inputThreadDatagrams);

    /// <summary>The input socket's receive timeout, the stop backstop (tests).</summary>
    internal int InputSocketReceiveTimeoutMs => _udp?.ReceiveTimeout ?? -1;

    /// <summary>Completes once the input thread left and disposed the UDP socket (tests).</summary>
    internal Task InputThreadExited => _udpDone.Task;

    /// <summary>Ports tried for <paramref name="basePort"/> (ADR-0011): it and the next <paramref name="fallbackCount"/>; port 0 retries ephemeral binds.</summary>
    internal static IReadOnlyList<int> PortRange(int basePort, int fallbackCount)
    {
        var ports = new List<int>(fallbackCount + 1);
        for (int i = 0; i <= fallbackCount; i++)
        {
            int port = basePort == 0 ? 0 : basePort + i;
            if (port > IPEndPoint.MaxPort)
            {
                break;
            }

            ports.Add(port);
        }

        return ports;
    }

    /// <summary>Changes the display region the phone maps to (applies to the active session immediately).</summary>
    public void SetTarget(DisplayRect display, NormalizedRect region) => SetTarget(MappingTarget.ForDisplay(display, region));

    /// <summary>
    /// Changes the mapping target (one display and region, or all displays; ADR-0031) for the active session and every
    /// later one. When the active area changes and the session negotiated <see cref="ProtocolCapabilities.ActiveArea"/>,
    /// the phone gets the new area. Thread-safe.
    /// </summary>
    public void SetTarget(MappingTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            _target = target;
        }

        ApplyToInput(target: true, settings: false);
    }

    /// <summary>
    /// Changes the input settings (ADR-0031..0033: stylus mode, side button, pressure curve, gestures) for the active
    /// session and every later one; starts from <see cref="PenBridgeServerOptions.InputSettings"/>. Pen settings take
    /// effect between strokes. A stylus mode change that changes the active area sends it to the phone (with
    /// <see cref="ProtocolCapabilities.ActiveArea"/>). Thread-safe.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Settings out of range (<see cref="InputSettings.Validate"/>).</exception>
    public void SetInputSettings(InputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        lock (_gate)
        {
            _inputSettings = settings;
        }

        ApplyToInput(target: false, settings: true);
    }

    /// <summary>The current input settings.</summary>
    public InputSettings InputSettings
    {
        get
        {
            lock (_gate)
            {
                return _inputSettings;
            }
        }
    }

    /// <summary>
    /// Applies the latest target and input settings to the active input. Runs outside <c>_gate</c> and the input lock
    /// (SessionInput takes the input lock and raises ActiveAreaChanged after it); <c>_applyLock</c> serializes the
    /// applications and each reads the latest values under it, so the last one to run applies the latest state.
    /// Lock order: <c>_applyLock</c>, then <c>_gate</c> or the input lock (never held while taking <c>_applyLock</c>).
    /// </summary>
    private void ApplyToInput(bool target, bool settings)
    {
        lock (_applyLock)
        {
            SessionInput? input;
            MappingTarget latestTarget;
            InputSettings latestSettings;
            lock (_gate)
            {
                input = _activeInput;
                latestTarget = _target;
                latestSettings = _inputSettings;
            }

            if (input is null)
            {
                return;
            }

            if (target)
            {
                input.SetTarget(latestTarget);
            }

            if (settings)
            {
                input.ApplySettings(latestSettings);
            }
        }
    }

    /// <summary>
    /// Sets the Mac settings snapshot (ADR-0030) and sends it to the active session if it negotiated
    /// <see cref="ProtocolCapabilities.RemoteSettings"/>; otherwise only stores it. Every new session gets the latest
    /// one right after SessionStart, so call this at startup, after every change and in reply to every
    /// <see cref="SettingsUpdateRequested"/>. Only queues the send: calls in quick succession coalesce and the phone
    /// gets the latest snapshot. The task completes when it went out (or the session failed); it is already complete
    /// when there's no session, or the session isn't sending yet (between its activation and SessionStart): it then
    /// gets the latest snapshot right after SessionStart. Never throws for network failures (a failed write closes the
    /// session).
    /// </summary>
    /// <exception cref="ArgumentException">More than <see cref="SettingsWire.MaxEntries"/> entries.</exception>
    public Task SendSettingsSnapshotAsync(IReadOnlyList<SettingEntry> entries)
    {
        var message = new SettingsSnapshotMessage(entries);
        MirrorSettingsState mirror = MirrorSettingsState.From(message.Entries);
        ServerConnection? target;
        bool mirrorChanged;
        lock (_gate)
        {
            _settingsSnapshot = message;
            target = _active;
            mirrorChanged = mirror != _mirrorSettings;
            _mirrorSettings = mirror;
        }

        if (mirrorChanged)
        {
            target?.Mirror?.Reevaluate();
        }

        return target is null ? Task.CompletedTask : target.RequestPush(ServerConnection.PushSnapshot);
    }

    /// <summary>
    /// Sets the targetable displays (ADR-0031; "All displays" is implied) and sends them to the active session like
    /// <see cref="SendSettingsSnapshotAsync"/> (coalesced, latest wins). The list always goes out before a snapshot
    /// queued with it, and new sessions get it before their first snapshot. As there, the task is already complete when
    /// there's no session or it isn't sending yet (between its activation and SessionStart).
    /// </summary>
    /// <exception cref="ArgumentException">More than <see cref="SettingsWire.MaxDisplays"/> displays.</exception>
    public Task SendDisplayListAsync(IReadOnlyList<DisplayListEntry> displays)
    {
        var message = new DisplayListMessage(displays);
        ServerConnection? target;
        lock (_gate)
        {
            _displayList = message;
            target = _active;
        }

        return target is null ? Task.CompletedTask : target.RequestPush(ServerConnection.PushDisplays);
    }

    /// <summary>The latest settings snapshot and display list (null until set).</summary>
    internal (SettingsSnapshotMessage? Snapshot, DisplayListMessage? Displays) SettingsState
    {
        get
        {
            lock (_gate)
            {
                return (_settingsSnapshot, _displayList);
            }
        }
    }

    /// <summary>
    /// Queues the active area for the active session if it negotiated <see cref="ProtocolCapabilities.ActiveArea"/>
    /// (coalesced, latest wins, unchanged areas aren't resent). Only takes a short lock and queues, so it may be called
    /// from input callbacks.
    /// </summary>
    internal void SendActiveArea(NormalizedRect area) => Volatile.Read(ref _active)?.SendActiveArea(area);

    /// <summary>The active session's connection (for tests).</summary>
    internal ServerConnection? ActiveConnection => Volatile.Read(ref _active);

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
        bool removed;
        ServerConnection? connection;
        lock (_gate)
        {
            // Under the gate: TryActivate re-checks the registry under it, so a connection that verified its HMAC
            // just before this call can't activate afterwards.
            removed = _options.Registry.Remove(deviceId);
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
        if (_udpThread is null)
        {
            _udp?.Dispose();
        }
        else
        {
            // The input thread owns the socket: flag it, wake its blocking receive, and wait until it disposed it (the
            // receive timeout ends it within a second if the wake datagram is lost).
            _udpStopping = true;
            VideoSockets.Wake(_udp!);
            await _udpDone.Task.ConfigureAwait(false);
        }

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
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

    internal ActivationResult TryActivate(ServerConnection connection, out ServerConnection? previous)
    {
        Span<byte> key = stackalloc byte[KeySchedule.KeyLength];
        bool surfaceCleared;
        ActivationResult result;
        lock (_gate)
        {
            previous = null;
            bool paired = _options.Registry.TryGetDeviceKey(connection.DeviceId, key);
            CryptographicOperations.ZeroMemory(key);
            if (!paired)
            {
                return ActivationResult.Unpaired;
            }

            ServerConnection? current = _active;
            if (current is not null && current.DeviceId != connection.DeviceId && !_allowTakeover)
            {
                return ActivationResult.Busy;
            }

            previous = current;
            _active = connection;
            Volatile.Write(ref _activeInput, null);
            surfaceCleared = _phoneSurface is not null;
            _phoneSurface = null;
            result = ActivationResult.Activated;
        }

        if (surfaceCleared)
        {
            Raise(PhoneSurfaceChanged);
        }

        return result;
    }

    /// <summary>Records the surface a SurfaceInfo declared, if <paramref name="connection"/> is still the active one.</summary>
    internal void SetPhoneSurface(ServerConnection connection, int widthPx, int heightPx)
    {
        var size = new PhoneSurfaceSize(widthPx, heightPx);
        lock (_gate)
        {
            if (!ReferenceEquals(_active, connection) || _phoneSurface == size)
            {
                return;
            }

            _phoneSurface = size;
        }

        Raise(PhoneSurfaceChanged);
    }

    private void Raise(EventHandler? handler)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // A faulty UI handler must not take down the network engine.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// Makes <paramref name="input"/> the one that receives input, if <paramref name="connection"/> is still active.
    /// False when it was taken over (or closed) meanwhile; the caller then shuts the input down unused.
    /// </summary>
    /// <remarks>
    /// <para>Input handoff (#107): under the input lock, the previously published input is shut down (its release —
    /// synthetic pen Up/ButtonUps/ProximityLeave and a gesture Cancelled — reaches the sink) <i>before</i> the new
    /// input becomes reachable by the UDP loop or the session loop. Normally the old connection's close already did
    /// this (the taking-over connection awaits it), and the shutdown here is a no-op; this makes the order a local
    /// invariant instead of a consequence of that await. After the handoff the old input is closed, so a late frame,
    /// datagram or timer of the old session is dropped and never reaches the sink.</para>
    /// <para>Lock order: input lock, then <c>_gate</c>. Nothing takes them the other way round.</para>
    /// </remarks>
    internal bool PublishInput(ServerConnection connection, SessionInput input)
    {
        SessionInput? previous;
        Exception? fault = null;
        bool published;
        lock (_inputLock)
        {
            lock (_gate)
            {
                // Checked before releasing: a connection that is no longer active must not release the input of the
                // one that took over from it.
                if (!ReferenceEquals(_active, connection))
                {
                    return false;
                }

                previous = ReferenceEquals(_inputOwner, input) ? null : _inputOwner;
            }

            if (previous is not null)
            {
                fault = previous.ShutdownCore();
            }

            lock (_gate)
            {
                // Taken over while releasing: the newer connection publishes (and releases nothing more of ours).
                published = ReferenceEquals(_active, connection);
                if (published)
                {
                    _inputOwner = input;
                    Volatile.Write(ref _activeInput, input);
                }
            }
        }

        // Outside the locks: the fault callback closes the old connection. The target and settings are applied
        // before SessionStart goes out, so no input of this session is processed without them.
        previous?.ReportFault(fault);
        if (published)
        {
            ApplyToInput(target: true, settings: true);
        }

        return published;
    }

    internal void OnConnectionEnded(ServerConnection connection)
    {
        lock (_inputLock)
        {
            // The ending connection already shut its input down; don't retain it until the next session publishes.
            if (connection.Input is { } input && ReferenceEquals(_inputOwner, input))
            {
                _inputOwner = null;
            }
        }

        bool surfaceCleared = false;
        lock (_gate)
        {
            _connections.Remove(connection);
            if (ReferenceEquals(_active, connection))
            {
                _active = null;
                Volatile.Write(ref _activeInput, null);
                surfaceCleared = _phoneSurface is not null;
                _phoneSurface = null;
            }
        }

        if (surfaceCleared)
        {
            Raise(PhoneSurfaceChanged);
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

    /// <summary>Releases the pre-auth slot taken by <see cref="TryAdmit"/> (exactly once per admitted connection).</summary>
    internal void ReleasePending(IPAddress address)
    {
        address = PendingKey(address);
        lock (_gate)
        {
            _pending--;
            if (_pendingByAddress.TryGetValue(address, out int count))
            {
                if (count <= 1)
                {
                    _pendingByAddress.Remove(address);
                }
                else
                {
                    _pendingByAddress[address] = count - 1;
                }
            }
        }
    }

    /// <summary>Takes a pre-auth slot for <paramref name="address"/>'s key (IPv4 or IPv6 /64): false when the global or per-key cap is reached.</summary>
    internal bool TryAdmit(IPAddress address)
    {
        address = PendingKey(address);
        lock (_gate)
        {
            _pendingByAddress.TryGetValue(address, out int count);
            if (_pending >= _options.MaxPendingConnections || count >= _options.MaxPendingConnectionsPerAddress)
            {
                return false;
            }

            _pending++;
            _pendingByAddress[address] = count + 1;
            return true;
        }
    }

    internal static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// The key a peer's pending connections are counted under: the IPv4 address, or the IPv6 /64 prefix (one host
    /// usually owns a whole /64, so per-address counting would be trivial to evade).
    /// </summary>
    internal static IPAddress PendingKey(IPAddress address)
    {
        address = Normalize(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes);
    }

    internal void RaiseDevicePaired(PairedDevice device) => Raise(DevicePaired, new DevicePairedEventArgs(device));

    internal void RaiseSessionStarted(SessionInfo session) => Raise(SessionStarted, new SessionStartedEventArgs(session));

    internal void RaiseSessionEnded(SessionInfo session, SessionEndReason reason) => Raise(SessionEnded, new SessionEndedEventArgs(session, reason));

    internal void RaiseSettingsUpdateRequested(SessionInfo session, IReadOnlyList<SettingEntry> entries) =>
        Raise(SettingsUpdateRequested, new SettingsUpdateRequestedEventArgs(session, entries));

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
            tcp.Listen(64);
            int bound = ((IPEndPoint)tcp.LocalEndPoint!).Port;
            udp = CreateSocket(SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(_options.BindAddress, bound));
            SocketQos.Configure(udp, tos: null, _options.ConfigureInputSocket, _log, ref _qosWarned);
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
                // Only Dispose cancels the token or closes the listener.
                break;
            }
#pragma warning disable CA1031 // The accept loop must survive anything but shutdown.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                NetLog.Unexpected(_log, null, ex);

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

            Admit(socket);
        }
    }

    /// <summary>Starts a handler for an accepted socket, or closes it. Never throws.</summary>
    private void Admit(Socket socket)
    {
        // A peer that reset right after connecting has no endpoint; drop it.
        if (SafeRemote(socket) is not IPEndPoint remote)
        {
            socket.Dispose();
            return;
        }

        IPAddress address = PendingKey(remote.Address);
        if (!TryAdmit(address))
        {
            NetLog.TooManyPending(_log, remote);
            socket.Dispose();
            return;
        }

        try
        {
            var connection = new ServerConnection(this, socket, remote, address);
            lock (_gate)
            {
                _connections.Add(connection);
            }

            connection.Start();
        }
#pragma warning disable CA1031 // e.g. NoDelay or NetworkStream on a socket the peer already reset.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            NetLog.AcceptFailed(_log, remote, ex.GetType().Name);
            socket.Dispose();
            ReleasePending(address);
        }
    }

    /// <summary>
    /// The input thread's body: blocking receive on the UDP socket it owns, then the active session's datagram path
    /// (pinning, stale/budget checks, decrypt, tracker, sink) on this same thread. Allocation-free per datagram.
    /// </summary>
    private void UdpReceiveLoop()
    {
        Socket udp = _udp!;
        try
        {
            // One byte more than the maximum so oversized datagrams are seen as such and dropped.
            byte[] buffer = GC.AllocateArray<byte>(InputDatagram.MaxSize + 1, pinned: true);
            var address = new SocketAddress(udp.AddressFamily);
            while (!_udpStopping)
            {
                int received;
                try
                {
                    received = udp.ReceiveFrom(buffer, SocketFlags.None, address);
                }
                catch (SocketException ex)
                {
                    // The receive timeout (the stop backstop) is routine; anything else is worth a warning.
                    if (!_udpStopping && ex.SocketErrorCode is not (SocketError.TimedOut or SocketError.WouldBlock))
                    {
                        NetLog.UdpReceiveError(_log, ex.SocketErrorCode);
                    }

                    continue;
                }

                if (_udpStopping)
                {
                    break; // the wake datagram (or one that raced it): shutting down
                }

                try
                {
                    Volatile.Read(ref _activeInput)?.ProcessDatagram(buffer.AsSpan(0, received), address);
                }
#pragma warning disable CA1031 // One bad datagram (or a bug) must not stop input for every later session.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    NetLog.Unexpected(_log, null, ex);
                }

                Interlocked.Exchange(ref _inputThreadAllocated, GC.GetAllocatedBytesForCurrentThread());
                Interlocked.Increment(ref _inputThreadDatagrams);
            }
        }
        catch (ObjectDisposedException)
        {
            // Not expected (only this thread disposes the socket); leave quietly.
        }
        finally
        {
            udp.Dispose(); // this thread owns the socket (see VideoSockets)
            _udpDone.TrySetResult();
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
