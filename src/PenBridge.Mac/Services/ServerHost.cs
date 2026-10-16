using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using AppKit;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using Network;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using PenBridge.Core.Settings;
using PenBridge.Mac.Input;

namespace PenBridge.Mac.Services;

/// <summary>What the server is doing (drives the menu-bar icon and status line).</summary>
public enum ServerHostStatus
{
    /// <summary>Not started, or stopped for quit.</summary>
    Stopped,

    /// <summary>Listening; no device connected.</summary>
    Listening,

    /// <summary>A paired device has the active session.</summary>
    Connected,

    /// <summary>Stopped because the Mac is going to sleep; restarts on wake.</summary>
    Sleeping,

    /// <summary>Starting failed (e.g. ports 47820–47829 all taken); retried on wake or a network change.</summary>
    Failed,
}

/// <summary>An immutable snapshot of <see cref="ServerHost"/> for the shell. A new instance follows every change.</summary>
/// <param name="Status">Lifecycle / connection state.</param>
/// <param name="Port">Bound TCP/UDP port, 0 when not listening.</param>
/// <param name="Addresses">This Mac's LAN IPv4 addresses (Ethernet/Wi-Fi first), for "Listening on …" and the pairing window.</param>
/// <param name="ConnectedDeviceId">The active device, or <see cref="Guid.Empty"/>.</param>
/// <param name="ConnectedDeviceName">The active device's name, sanitized for display, or null.</param>
/// <param name="Paused">Input paused (session stays connected, nothing is injected).</param>
/// <param name="AllowTakeover">"Allow takeover" (ADR-0010).</param>
/// <param name="AccessibilityMissing">The Accessibility grant is missing, so injected input would be dropped.</param>
/// <param name="IsPairingWindowOpen">"Pair new device…" is accepting a PIN/QR attempt.</param>
/// <param name="PairedDevices">Paired devices (no keys), ordered by pairing time.</param>
/// <param name="Error">Why the last start failed, or null.</param>
public sealed record ServerHostState(
    ServerHostStatus Status,
    int Port,
    IReadOnlyList<string> Addresses,
    Guid ConnectedDeviceId,
    string? ConnectedDeviceName,
    bool Paused,
    bool AllowTakeover,
    bool AccessibilityMissing,
    bool IsPairingWindowOpen,
    IReadOnlyList<PairedDevice> PairedDevices,
    string? Error);

/// <summary>What the pairing window shows. Never log <see cref="Pin"/> or <see cref="Qr"/>.</summary>
/// <param name="Pin">The 6-digit PIN.</param>
/// <param name="Qr">The QR payload (<see cref="PairingQrPayload.Format"/> is the QR text), or null if the window closed meanwhile.</param>
/// <param name="Addresses">Host IPs for manual entry (ADR-0008).</param>
/// <param name="Port">The bound port.</param>
/// <param name="ExpiresAt">When the window closes by itself.</param>
/// <param name="LockedUntil">After 3 failures: attempts are refused until then (show the wait).</param>
/// <param name="AttemptsLeft">Attempts left with this PIN.</param>
public sealed record PairingInfo(
    string Pin,
    PairingQrPayload? Qr,
    IReadOnlyList<string> Addresses,
    int Port,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LockedUntil,
    int AttemptsLeft);

/// <summary>
/// Owns the <see cref="PenBridgeServer"/> lifecycle and its macOS wiring: Bonjour advert, sleep/wake
/// (<see cref="NSWorkspace"/> notifications), network changes (<see cref="NWPathMonitor"/>), pause, and an
/// observable <see cref="State"/> for the menu-bar shell.
/// </summary>
/// <remarks>
/// <para><b>Threading:</b> every public member must be called on the main thread, and every event is raised on it.
/// Server events arrive on thread-pool threads and are marshalled to the main queue. Nothing polls: state changes come
/// from server events, notifications and the path monitor; <see cref="GetStats"/> is a snapshot to pull on menu open.</para>
/// <para><b>Lifecycle:</b> start/stop operations are serialized; a <see cref="PenBridgeServer"/> can't be restarted,
/// so each start (launch, wake, retry) creates a new one with the same identity, registry and settings.</para>
/// <para><b>Quit:</b> call <see cref="DisposeAsync"/> from <c>ApplicationShouldTerminate</c> (return
/// <c>TerminateLater</c>, then <c>ReplyToApplicationShouldTerminate(true)</c> once it completes) so the phone gets
/// Bye(Shutdown) and the advert is withdrawn.</para>
/// </remarks>
public sealed partial class ServerHost : IAsyncDisposable
{
    private readonly PausableInputSink _sink;
    private readonly SettingsStore _settings;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly ISecretStore _secrets;
    private readonly LatencyLog? _latency;
    private ServerIdentity? _identity;
    private PairedDeviceRegistry? _registry;
    private readonly BonjourAdvertiser _bonjour;
    private readonly List<NSObject> _observers = [];
    private ComputerNameWatcher? _nameWatcher;
    private NWPathMonitor? _pathMonitor;
    private PenBridgeServer? _server;
    private Task _lifecycle = Task.CompletedTask;
    private bool _wantRunning;
    private bool _disposed;
    private ServerHostStatus _status = ServerHostStatus.Stopped;
    private SessionInfo? _session;
    private string? _error;
    private bool _accessibilityMissing;
    private IReadOnlyList<string> _addresses = [];
    private MappingTarget _target = MappingTarget.ForDisplay(new DisplayRect(0, 0, 1, 1));
    private InputSettings _inputSettings = InputSettings.Default;
    private IReadOnlyList<SettingEntry>? _settingsSnapshot;
    private IReadOnlyList<DisplayListEntry>? _displayList;

    /// <param name="injector">The CGEvent injector; it only ever receives input through the pause gate.</param>
    /// <param name="secrets">Holds the server identity and the paired-device registry (<see cref="KeychainSecretStore"/>).</param>
    /// <param name="settings">Initial paused / allow-takeover values; updated when they change here.</param>
    /// <param name="loggers">For the server and the host (<see cref="OsLogLoggerFactory"/>).</param>
    /// <remarks>Cheap: the identity and registry are read from <paramref name="secrets"/> off the main thread by the first <see cref="StartAsync"/>.</remarks>
    public ServerHost(IReleasableInputSink injector, ISecretStore secrets, SettingsStore settings, ILoggerFactory loggers, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(injector);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(loggers);
        _sink = new PausableInputSink(injector);
        _secrets = secrets;
        _settings = settings;
        _loggers = loggers;
        _log = loggers.CreateLogger<ServerHost>();
        _time = timeProvider ?? TimeProvider.System;
        _latency = LatencyLog.CreateIfEnabled(loggers, _time);
        _bonjour = new BonjourAdvertiser(loggers.CreateLogger<BonjourAdvertiser>());
        _sink.SetPaused(settings.Paused);
        _accessibilityMissing = !AccessibilityStatus.IsTrusted();
        State = BuildState();
    }

    /// <summary>Raised on the main thread after <see cref="State"/> changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised on the main thread when the pairing window opens, closes, or its PIN/token changes (refresh <see cref="GetPairingInfo"/>).</summary>
    public event EventHandler? PairingChanged;

    /// <summary>Raised on the main thread after a device paired (the pairing window closes itself on this).</summary>
    public event EventHandler<DevicePairedEventArgs>? DevicePaired;

    /// <summary>
    /// Raised on the main thread when the connected phone asks to change Mac settings (ADR-0030). The handler
    /// validates, applies and persists, then replies with <see cref="PublishSettings"/> (the snapshot is the answer).
    /// </summary>
    public event EventHandler<SettingsUpdateRequestedEventArgs>? SettingsUpdateRequested;

    /// <summary>The current snapshot.</summary>
    public ServerHostState State { get; private set; }

    /// <summary>The server certificate fingerprint (unpadded base64url) phones pin; null until the first start loaded it.</summary>
    public string? FingerprintBase64Url => _identity?.FingerprintBase64Url;

    /// <summary>Starts listening and advertising, and begins watching sleep/wake and network changes.</summary>
    public Task StartAsync()
    {
        EnsureMainThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _wantRunning = true;
        StartWatching();
        return Enqueue(StartServerAsync);
    }

    /// <summary>Stops the server (Bye(Shutdown) to the phone) and withdraws the advert. Sleep/wake no longer restarts it.</summary>
    public Task StopAsync()
    {
        EnsureMainThread();
        _wantRunning = false;
        return Enqueue(() => StopServerAsync(ServerHostStatus.Stopped));
    }

    /// <summary>Pauses input (the injector releases everything first) or resumes it; the session stays connected.</summary>
    public void SetPaused(bool paused)
    {
        EnsureMainThread();
        if (_sink.IsPaused == paused)
        {
            return;
        }

        _sink.SetPaused(paused);
        _settings.Paused = paused;
        Log.PausedChanged(_log, paused);
        PublishState();
    }

    /// <summary>"Allow takeover": a new device closes the active session instead of getting Busy.</summary>
    public void SetAllowTakeover(bool allow)
    {
        EnsureMainThread();
        _settings.AllowTakeover = allow;
        if (_server is not null)
        {
            _server.AllowTakeover = allow;
        }

        PublishState();
    }

    /// <summary>What the phone maps to (one display and region, or all displays; ADR-0031); kept across restarts.</summary>
    public void SetTarget(MappingTarget target)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _server?.SetTarget(target);
    }

    /// <summary>The input settings (stylus mode, side button, pressure curve, gestures; ADR-0032/0033); kept across restarts.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Settings out of range (<see cref="InputSettings.Validate"/>).</exception>
    public void SetInputSettings(InputSettings settings)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _inputSettings = settings;
        _server?.SetInputSettings(settings);
    }

    /// <summary>
    /// The Mac settings snapshot for the phone (ADR-0030): pushed to the connected phone now and to every later
    /// session after its SessionStart (also across server restarts). Coalesced in Core, latest wins.
    /// </summary>
    public void PublishSettings(IReadOnlyList<SettingEntry> snapshot)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(snapshot);
        _settingsSnapshot = snapshot;
        if (_server is not null)
        {
            _ = _server.SendSettingsSnapshotAsync(snapshot);
        }
    }

    /// <summary>The display list for the phone (ADR-0030), like <see cref="PublishSettings"/>; it goes out before a snapshot published with it.</summary>
    public void PublishDisplays(IReadOnlyList<DisplayListEntry> displays)
    {
        EnsureMainThread();
        ArgumentNullException.ThrowIfNull(displays);
        _displayList = displays;
        if (_server is not null)
        {
            _ = _server.SendDisplayListAsync(displays);
        }
    }

    /// <summary>Re-checks <see cref="AccessibilityStatus.IsTrusted"/> (call on menu open; also done on connect).</summary>
    public void RefreshAccessibility()
    {
        EnsureMainThread();
        bool missing = !AccessibilityStatus.IsTrusted();
        if (missing != _accessibilityMissing)
        {
            _accessibilityMissing = missing;
            if (missing)
            {
                Log.AccessibilityMissing(_log);
            }

            PublishState();
        }
    }

    /// <summary>Connection stats snapshot (RTT, packets/s, transport); pull it when the menu opens.</summary>
    public PenBridgeServerStats GetStats()
    {
        EnsureMainThread();
        return _server?.GetStats() ?? default;
    }

    /// <summary>
    /// Forgets a paired device; its active session, if any, is closed with Bye(Unpaired). The Keychain write runs off
    /// the main thread; the task completes (and <see cref="StateChanged"/> fires) back on it.
    /// </summary>
    public async Task<bool> ForgetDeviceAsync(Guid deviceId)
    {
        EnsureMainThread();
        PenBridgeServer? server = _server;
        PairedDeviceRegistry? registry = _registry;
        bool removed = await Task.Run(() => server?.ForgetDevice(deviceId) ?? registry?.Remove(deviceId) ?? false).ConfigureAwait(true);
        EnsureMainThread();
        if (removed)
        {
            Log.DeviceForgotten(_log, deviceId);
            PublishState();
        }

        return removed;
    }

    /// <summary>Opens the 2-minute pairing window with a fresh PIN and QR token. False when the server isn't running.</summary>
    public bool OpenPairingWindow()
    {
        EnsureMainThread();
        if (_server is null)
        {
            return false;
        }

        _server.OpenPairingWindow();
        return true;
    }

    public void ClosePairingWindow()
    {
        EnsureMainThread();
        _server?.ClosePairingWindow();
    }

    /// <summary>The PIN, QR payload and addresses to show, or null when the pairing window is closed.</summary>
    public PairingInfo? GetPairingInfo()
    {
        EnsureMainThread();
        PenBridgeServer? server = _server;
        string? pin = server?.CurrentPin;
        if (server is null || pin is null || server.Port == 0)
        {
            return null;
        }

        string? host = _addresses.Count > 0 ? _addresses[0] : null;
        return new PairingInfo(
            pin,
            server.CreateQrPayload(host),
            _addresses,
            server.Port,
            server.PairingWindowExpiresAt,
            server.PairingLockedUntil,
            server.PairingAttemptsLeft);
    }

    public async ValueTask DisposeAsync()
    {
        EnsureMainThread();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wantRunning = false;
        await Enqueue(() => StopServerAsync(ServerHostStatus.Stopped)).ConfigureAwait(true);
        StopWatching();
        _bonjour.Dispose();
        _identity?.Dispose();
        _latency?.Dispose();
    }

    private void StartWatching()
    {
        if (_observers.Count > 0)
        {
            return;
        }

        _observers.Add(NSWorkspace.Notifications.ObserveWillSleep(OnWillSleep));
        _observers.Add(NSWorkspace.Notifications.ObserveDidWake(OnDidWake));
        _nameWatcher = new ComputerNameWatcher();
        _nameWatcher.Changed += OnComputerNameChanged;

        _addresses = LocalAddresses();
        var monitor = new NWPathMonitor();
        monitor.SetQueue(DispatchQueue.MainQueue);
        monitor.SnapshotHandler = OnPathChanged;
        monitor.Start();
        _pathMonitor = monitor;
    }

    private void StopWatching()
    {
        foreach (NSObject observer in _observers)
        {
            observer.Dispose();
        }

        _observers.Clear();
        if (_nameWatcher is not null)
        {
            _nameWatcher.Changed -= OnComputerNameChanged;
            _nameWatcher.Dispose();
            _nameWatcher = null;
        }

        _pathMonitor?.Cancel();
        _pathMonitor?.Dispose();
        _pathMonitor = null;
    }

    private void OnWillSleep(object? sender, NSNotificationEventArgs e)
    {
        if (!_wantRunning)
        {
            // Stopped by the user: stay Stopped, nothing to tear down.
            return;
        }

        Log.WillSleep(_log);
        RunInBackground(Enqueue(() => StopServerAsync(ServerHostStatus.Sleeping)), "sleep");
    }

    // The new name reaches the Bonjour instance and TXT at once; HelloAck uses it from the next server start.
    private void OnComputerNameChanged(object? sender, EventArgs e)
    {
        if (_server is not null && _identity is not null)
        {
            _bonjour.Publish(ComputerName.Get(), _server.Port, _identity.Fingerprint);
        }
    }

    private void OnDidWake(object? sender, NSNotificationEventArgs e)
    {
        Log.DidWake(_log);
        if (_wantRunning)
        {
            _addresses = LocalAddresses();
            RunInBackground(Enqueue(StartServerAsync), "wake");
        }
    }

    private void OnPathChanged(NWPath path)
    {
        IReadOnlyList<string> addresses = path.Status == NWPathStatus.Satisfied ? LocalAddresses() : [];
        if (addresses.SequenceEqual(_addresses, StringComparer.Ordinal))
        {
            return;
        }

        _addresses = addresses;
        Log.NetworkChanged(_log, path.Status.ToString(), string.Join(", ", addresses));
        if (_server is not null && _identity is not null)
        {
            // mDNSResponder follows interfaces itself, but a fresh registration announces on the new network at once.
            _bonjour.Publish(ComputerName.Get(), _server.Port, _identity.Fingerprint, force: true);
        }
        else if (_wantRunning && _status == ServerHostStatus.Failed)
        {
            RunInBackground(Enqueue(StartServerAsync), "retry");
        }

        PublishState();
    }

    private Task Enqueue(Func<Task> operation)
    {
        Task next = RunAfterAsync(_lifecycle, operation);
        _lifecycle = next;
        return next;
    }

    /// <summary>Observes a fire-and-forget lifecycle operation: a fault is logged and shown as Failed at once.</summary>
    private void RunInBackground(Task task, string operation)
    {
        task.ContinueWith(
            t => DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                Log.LifecycleFailed(_log, operation, t.Exception!.GetBaseException());
                if (_server is null && !_disposed)
                {
                    _status = ServerHostStatus.Failed;
                    _error = "The server could not " + (operation == "sleep" ? "stop" : "start") + "; see the logs.";
                    PublishState();
                }
            }),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task RunAfterAsync(Task previous, Func<Task> operation)
    {
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Already observed: by the awaiting caller, or by RunInBackground for sleep/wake/retry. Run the next one.
        }

        EnsureMainThread();
        await operation().ConfigureAwait(true);
    }

    private async Task StartServerAsync()
    {
        if (_server is not null || !_wantRunning)
        {
            return;
        }

        if (_identity is null || _registry is null)
        {
            try
            {
                // Keychain reads and the PKCS#12 import block (seconds on the first access after a rebuild while
                // securityd checks the signature), so keep them off the main thread.
                (_identity, _registry) = await Task.Run(() =>
                {
                    ServerIdentity identity = ServerIdentity.LoadOrCreate(_secrets, _time);
                    return (identity, new PairedDeviceRegistry(_secrets, _time));
                }).ConfigureAwait(true);
                EnsureMainThread();
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
            {
                _status = ServerHostStatus.Failed;
                _error = "Could not read the PenBridge identity or paired devices from the Keychain.";
                Log.SecretsFailed(_log, ex);
                PublishState();
                return;
            }

            if (_disposed || !_wantRunning)
            {
                return;
            }
        }

        string name = ComputerName.Get();
        var server = new PenBridgeServer(new PenBridgeServerOptions
        {
            Identity = _identity,
            Registry = _registry,
            InputSink = _sink,
            ServerName = name,
            AllowTakeover = _settings.AllowTakeover,
            FingerTouches = true, // the injector posts PointerMove/Click/PointerDrag (ADR-0026)
            RemoteSettings = true, // ADR-0030: SettingsSync answers SettingsUpdateRequested
            ActiveArea = true, // ADR-0031
            InputSettings = _inputSettings,
            TimeProvider = _time,
            LoggerFactory = _loggers,
            LatencyRecorder = _latency?.Recorder,
            // Read live at each two-finger tap: Smart Zoom off right-clicks on the lift (ADR-0029).
            TrackerOptions = new PenStateTrackerOptions
            {
                Gestures = new GestureRecognizerOptions { SmartZoomEnabled = SmartZoom.Delegate },
            },
        });
        server.SetTarget(_target);
        if (_displayList is not null)
        {
            _ = server.SendDisplayListAsync(_displayList); // held for the first session; no session yet
        }

        if (_settingsSnapshot is not null)
        {
            _ = server.SendSettingsSnapshotAsync(_settingsSnapshot);
        }

        server.SessionStarted += OnSessionStarted;
        server.SessionEnded += OnSessionEnded;
        server.DevicePaired += OnDevicePaired;
        server.PairingWindowChanged += OnPairingWindowChanged;
        server.SettingsUpdateRequested += OnSettingsUpdateRequested;
        try
        {
            server.Start();
        }
        catch (SocketException ex)
        {
            Unsubscribe(server);
            _status = ServerHostStatus.Failed;
            _error = "No free port in 47820–47829 (" + ex.SocketErrorCode + ").";
            Log.StartFailed(_log, ex);
            PublishState();
            await server.DisposeAsync().ConfigureAwait(true);
            return;
        }

        _server = server;
        _session = null;
        _error = null;
        _status = ServerHostStatus.Listening;
        _bonjour.Publish(name, server.Port, _identity.Fingerprint);
        InputLog.SmartZoom(_log, SmartZoom.IsEnabled());
        Log.Started(_log, server.Port, _identity.FingerprintBase64Url);
        PublishState();
    }

    private async Task StopServerAsync(ServerHostStatus status)
    {
        PenBridgeServer? server = _server;
        _bonjour.Stop();
        if (server is not null)
        {
            _server = null;
            Unsubscribe(server);
            bool hadPairingWindow = server.IsPairingWindowOpen;
            await server.DisposeAsync().ConfigureAwait(true);
            EnsureMainThread();
            Log.Stopped(_log, status.ToString());
            if (hadPairingWindow)
            {
                Raise(PairingChanged, nameof(PairingChanged));
            }
        }

        _status = status;
        _session = null;
        PublishState();
    }

    private void Unsubscribe(PenBridgeServer server)
    {
        server.SessionStarted -= OnSessionStarted;
        server.SessionEnded -= OnSessionEnded;
        server.DevicePaired -= OnDevicePaired;
        server.PairingWindowChanged -= OnPairingWindowChanged;
        server.SettingsUpdateRequested -= OnSettingsUpdateRequested;
    }

    // Server events: thread pool → main queue; events from a server that was replaced meanwhile are ignored.
    private void OnSessionStarted(object? sender, SessionStartedEventArgs e) => OnMain(sender, () =>
    {
        // A coast belongs to the previous session's fingers. On a takeover Core releases the old session's input and
        // raises its SessionEnded before this SessionStarted (OnMain keeps that order), so the old session's stop has
        // already run; stopping here too is a harmless belt-and-braces measure.
        _sink.StopMomentum();
        _session = e.Session;
        _status = ServerHostStatus.Connected;
        _accessibilityMissing = !AccessibilityStatus.IsTrusted();
        Log.SessionStarted(_log, PeerText.Sanitize(e.Session.DeviceName), e.Session.DeviceId);
        if (_accessibilityMissing)
        {
            Log.AccessibilityMissing(_log);
        }

        PublishState();
    });

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e) => OnMain(sender, () =>
    {
        if (_session?.DeviceId == e.Session.DeviceId)
        {
            _session = null;
            _status = ServerHostStatus.Listening;
            _sink.StopMomentum(); // a coast outlives the fingers; on a takeover Core ends this session before the new one starts, so it runs then too
        }

        Log.SessionEnded(_log, PeerText.Sanitize(e.Session.DeviceName), e.Reason.ToString());
        _latency?.DumpInBackground("session end");
        PublishState();
    });

    private void OnDevicePaired(object? sender, DevicePairedEventArgs e) => OnMain(sender, () =>
    {
        Log.DevicePaired(_log, PeerText.Sanitize(e.Device.Name), e.Device.DeviceId);
        PublishState();
        Raise(DevicePaired, e, nameof(DevicePaired));
    });

    private void OnPairingWindowChanged(object? sender, EventArgs e) => OnMain(sender, () =>
    {
        PublishState();
        Raise(PairingChanged, nameof(PairingChanged));
    });

    // Raised on the session's control-read task; handled on the main thread (store, resolver, window). An update from
    // a session that has ended meanwhile is still applied: it was authenticated, and the Mac settings are global.
    private void OnSettingsUpdateRequested(object? sender, SettingsUpdateRequestedEventArgs e) => OnMain(sender, () =>
        Raise(SettingsUpdateRequested, e, nameof(SettingsUpdateRequested)));

    private void OnMain(object? sender, Action action) => DispatchQueue.MainQueue.DispatchAsync(() =>
    {
        if (!_disposed && ReferenceEquals(sender, _server))
        {
            try
            {
                action();
            }
#pragma warning disable CA1031 // A main-queue block must not throw into the dispatcher.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Log.HandlerFailed(_log, "server event", ex);
            }
        }
    });

    private void PublishState()
    {
        State = BuildState();
        Raise(StateChanged, nameof(StateChanged));
    }

    // Handlers run on main-queue blocks; one that throws must not take the app down or skip the rest of the update.
    private void Raise(EventHandler? handler, string name)
    {
        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.HandlerFailed(_log, name, ex);
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T args, string name)
    {
        try
        {
            handler?.Invoke(this, args);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.HandlerFailed(_log, name, ex);
        }
    }

    private ServerHostState BuildState() => new(
        _status,
        _server?.Port ?? 0,
        _addresses,
        _session?.DeviceId ?? Guid.Empty,
        _session is null ? null : PeerText.Sanitize(_session.DeviceName),
        _sink.IsPaused,
        _settings.AllowTakeover,
        _accessibilityMissing,
        _server?.IsPairingWindowOpen ?? false,
        _registry?.List() ?? [],
        _error);

    /// <summary>Up, non-loopback, non-link-local IPv4 addresses; Ethernet/Wi-Fi (en*) before VPN/bridge interfaces.</summary>
    private static List<string> LocalAddresses()
    {
        var preferred = new List<string>();
        var other = new List<string>();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                List<string> target = nic.Name.StartsWith("en", StringComparison.Ordinal) ? preferred : other;
                foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                {
                    IPAddress address = info.Address;
                    byte[] bytes = address.GetAddressBytes();
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !(bytes[0] == 169 && bytes[1] == 254))
                    {
                        target.Add(address.ToString());
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        preferred.AddRange(other);
        return preferred;
    }

    private static void EnsureMainThread()
    {
        if (!NSThread.IsMain)
        {
            throw new InvalidOperationException("ServerHost must be used on the main thread.");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Information, "Server listening on port {Port} (fingerprint {Fingerprint})")]
        public static partial void Started(ILogger logger, int port, string fingerprint);

        [LoggerMessage(2, LogLevel.Error, "Server failed to start")]
        public static partial void StartFailed(ILogger logger, Exception exception);

        [LoggerMessage(3, LogLevel.Information, "Server stopped ({Reason})")]
        public static partial void Stopped(ILogger logger, string reason);

        [LoggerMessage(4, LogLevel.Information, "System will sleep; stopping the server")]
        public static partial void WillSleep(ILogger logger);

        [LoggerMessage(5, LogLevel.Information, "System woke; restarting the server")]
        public static partial void DidWake(ILogger logger);

        [LoggerMessage(6, LogLevel.Information, "Network changed ({Status}); addresses: {Addresses}")]
        public static partial void NetworkChanged(ILogger logger, string status, string addresses);

        [LoggerMessage(7, LogLevel.Information, "Session started: \"{Name}\" ({DeviceId})")]
        public static partial void SessionStarted(ILogger logger, string name, Guid deviceId);

        [LoggerMessage(8, LogLevel.Information, "Session ended: \"{Name}\" ({Reason})")]
        public static partial void SessionEnded(ILogger logger, string name, string reason);

        [LoggerMessage(9, LogLevel.Information, "Device paired: \"{Name}\" ({DeviceId})")]
        public static partial void DevicePaired(ILogger logger, string name, Guid deviceId);

        [LoggerMessage(10, LogLevel.Information, "Device forgotten: {DeviceId}")]
        public static partial void DeviceForgotten(ILogger logger, Guid deviceId);

        [LoggerMessage(11, LogLevel.Information, "Input paused: {Paused}")]
        public static partial void PausedChanged(ILogger logger, bool paused);

        [LoggerMessage(14, LogLevel.Error, "Background {Operation} operation failed")]
        public static partial void LifecycleFailed(ILogger logger, string operation, Exception exception);

        [LoggerMessage(15, LogLevel.Error, "{Handler} handler threw")]
        public static partial void HandlerFailed(ILogger logger, string handler, Exception exception);

        [LoggerMessage(13, LogLevel.Error, "Reading the server identity or paired devices failed")]
        public static partial void SecretsFailed(ILogger logger, Exception exception);

        [LoggerMessage(12, LogLevel.Warning, "Accessibility permission missing: injected input is dropped until it is granted")]
        public static partial void AccessibilityMissing(ILogger logger);
    }
}
