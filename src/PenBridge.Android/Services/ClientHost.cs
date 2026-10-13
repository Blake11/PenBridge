using System.Net.Sockets;
using Android.Content;
using Android.OS;
using Android.Util;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Android.Services;

/// <summary>
/// The app's connection owner: wraps Core's <see cref="PenBridgeClient"/> with discovery, pairing, the last-used Mac,
/// reconnect-with-backoff and the Activity lifecycle, and publishes one observable <see cref="ClientHostState"/>.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> Everything except <see cref="SendState"/> must be called on the main (UI) thread and
/// throws otherwise. <see cref="StateChanged"/> and <see cref="DiscoveredChanged"/> are raised on the main thread.
/// Core's <c>Disconnected</c> event (thread pool) and NSD callbacks are posted to the main looper first; awaited
/// connects resume on the main thread through its SynchronizationContext. <see cref="SendState"/> may be called from
/// any thread (the sampler's), never blocks on the network and allocates nothing.</para>
/// <para><b>Session target.</b> After a session is established its Mac becomes the <i>target</i>: while the app is
/// started, a dropped session (lost link, timeout, Mac restart) is re-established with backoff 1, 2, 4 … 30 s
/// (<see cref="ReconnectPolicy"/>). <see cref="OnStop"/> closes the session but keeps the target; <see cref="OnStart"/>
/// reconnects to it. <see cref="Disconnect"/> (user action, e.g. leaving the pad) clears the target. A takeover by
/// another device, being unpaired, or a non-transient failure also clears it and suppresses auto-connect.</para>
/// <para><b>Auto-connect.</b> With no target, the first time after <see cref="OnStart"/> that discovery reports the
/// last-used Mac (still paired, compatible), the host connects to it once (<see cref="AutoConnectEnabled"/>).</para>
/// <para><b>Untrusted text.</b> Mac names are sanitized peer text; messages in the state are local strings only.</para>
/// </remarks>
public sealed class ClientHost : IAsyncDisposable
{
    private const string Tag = "PenBridge";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly Handler _main;
    private readonly Action _retryAction;
    private readonly Action _refreshAction;
    private readonly string _deviceName;
    private readonly ILoggerFactory? _loggerFactory;
    private PenBridgeClient? _client;
    private CancellationTokenSource? _attemptCts;
    private SurfaceInfoMessage _surface;
    private Target? _target;
    private ClientHostState _state = ClientHostState.Idle;
    private int _generation;
    private int _retryAttempt;
    private bool _retryPending;
    private bool _started;
    private bool _disposed;
    private bool _autoConnectSuppressed;
    private bool _autoConnectTried;

    /// <param name="context">Any context; the application context is used.</param>
    /// <param name="surface">Initial drawing surface (e.g. <see cref="CreateSurface(Context)"/>); update with <see cref="UpdateSurface"/>.</param>
    /// <param name="loggerFactory">Optional Core engine logging (never logs secrets or per-sample data).</param>
    public ClientHost(Context context, SurfaceInfoMessage surface, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = surface.PayloadSize; // throws for an invalid/default surface
        Context app = context.ApplicationContext ?? context;
        _main = new Handler(Looper.MainLooper!);
        _retryAction = OnRetryDue;
        _refreshAction = OnRefresh;
        _surface = surface;
        _loggerFactory = loggerFactory;
        _deviceName = GetDeviceName(app);
        SecretStore = new AndroidKeystoreSecretStore(app.FilesDir?.AbsolutePath ?? throw new InvalidOperationException("No files dir."));
        Credentials = new ClientCredentialStore(SecretStore);
        Settings = new SettingsStore(app);
        Discovery = new MacDiscovery(app);
        Discovery.Changed += OnDiscoveryChanged;
    }

    /// <summary>Raised on the main thread whenever <see cref="State"/> changes (value inequality).</summary>
    public event EventHandler<ClientHostStateChangedEventArgs>? StateChanged;

    /// <summary>Raised on the main thread when <see cref="DiscoveredMacs"/> or the browse status change.</summary>
    public event EventHandler? DiscoveredChanged;

    public ClientHostState State => _state;

    /// <summary>Device id and per-Mac keys (Keystore-backed).</summary>
    public ClientCredentialStore Credentials { get; }

    public AndroidKeystoreSecretStore SecretStore { get; }

    public SettingsStore Settings { get; }

    public MacDiscovery Discovery { get; }

    /// <summary>Resolved Macs on the network (main thread snapshot).</summary>
    public IReadOnlyList<DiscoveredServer> DiscoveredMacs => Discovery.Servers;

    /// <summary>Whether discovery of the last-used Mac connects automatically (default true).</summary>
    public bool AutoConnectEnabled { get; set; } = true;

    /// <summary>The Mac the current or wanted session is with, or null.</summary>
    public KnownMac? CurrentMac => _target is { } t ? new KnownMac(t.Fingerprint, t.Name, t.Host, t.Port, DateTimeOffset.UtcNow) : null;

    /// <summary>A surface description of the whole display in its current orientation (finger fallback enabled).</summary>
    public static SurfaceInfoMessage CreateSurface(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        DisplayMetrics metrics = context.Resources?.DisplayMetrics ?? throw new InvalidOperationException("No display metrics.");
        return CreateSurface(metrics.WidthPixels, metrics.HeightPixels, metrics.Xdpi, metrics.Ydpi);
    }

    /// <summary>A surface description for a view of <paramref name="widthPx"/> x <paramref name="heightPx"/>.</summary>
    public static SurfaceInfoMessage CreateSurface(int widthPx, int heightPx, float xDpi, float yDpi)
    {
        ushort w = (ushort)Math.Clamp(widthPx, 1, ushort.MaxValue);
        ushort h = (ushort)Math.Clamp(heightPx, 1, ushort.MaxValue);
        float dx = float.IsFinite(xDpi) && xDpi > 0 ? xDpi : 160f;
        float dy = float.IsFinite(yDpi) && yDpi > 0 ? yDpi : 160f;
        return new SurfaceInfoMessage(w, h, dx, dy, w >= h ? SurfaceOrientation.Landscape : SurfaceOrientation.Portrait, SurfaceCapabilities.FingerFallback);
    }

    /// <summary>True when this phone holds a device key for the Mac.</summary>
    public bool IsPaired(DiscoveredServer mac)
    {
        ArgumentNullException.ThrowIfNull(mac);
        return Credentials.IsPaired(mac.Fingerprint.Span);
    }

    // ---- Lifecycle (Activity.OnStart / OnStop) ----

    /// <summary>Call from <c>Activity.OnStart</c>: starts discovery and reconnects to the target, if any.</summary>
    public void OnStart()
    {
        EnsureUsable();
        if (_started)
        {
            return;
        }

        _started = true;
        _autoConnectTried = false;
        Discovery.Start();
        if (_target is not null)
        {
            _retryAttempt = 0;
            _ = ReconnectTargetAsync();
        }
        else
        {
            SetState(new ClientHostState(ClientHostStatus.Discovering));
            OnDiscoveryChanged(this, EventArgs.Empty);
        }
    }

    /// <summary>Call from <c>Activity.OnStop</c>: closes the session (keeping the target), stops discovery and retries.</summary>
    public void OnStop()
    {
        EnsureUsable();
        if (!_started)
        {
            return;
        }

        _started = false;
        Supersede();
        Discovery.Stop();
        SetState(ClientHostState.Idle);
    }

    // ---- Connect / pair (main thread; never throw for network or input errors: the outcome is in State) ----

    /// <summary>Connects to a discovered, already paired Mac. False (state Error NotPaired) when it isn't paired.</summary>
    public Task<bool> ConnectAsync(DiscoveredServer mac)
    {
        ArgumentNullException.ThrowIfNull(mac);
        byte[] fp = mac.Fingerprint.ToArray();
        return BeginUserAttempt(AttemptKind.Connect, mac.Host, mac.Port, mac.Name, (c, ct) => c.ConnectAsync(mac.Host, mac.Port, fp, ct));
    }

    /// <summary>Connects to a previously used Mac at its last known address (e.g. one entered manually).</summary>
    public Task<bool> ConnectAsync(KnownMac mac)
    {
        ArgumentNullException.ThrowIfNull(mac);
        return BeginUserAttempt(AttemptKind.Connect, mac.Host, mac.Port, mac.Name, (c, ct) => c.ConnectAsync(mac.Host, mac.Port, mac.Fingerprint, ct));
    }

    /// <summary>Pairs with a discovered Mac using the 6-digit PIN shown on it, then starts the session.</summary>
    public Task<bool> PairWithPinAsync(DiscoveredServer mac, string pin)
    {
        ArgumentNullException.ThrowIfNull(mac);
        return PairWithPinAsync(mac.Host, mac.Port, pin, mac.Name);
    }

    /// <summary>Pairs with host:port and a PIN (Advanced ▸ Connect manually, ADR-0008; emulator: host 10.0.2.2).</summary>
    public Task<bool> PairWithPinAsync(string host, int port, string pin) => PairWithPinAsync(host, port, pin, null);

    /// <summary>Pairs with scanned QR text (<c>penbridge://pair?…</c>); the Mac's certificate must match its fingerprint.</summary>
    public Task<bool> PairWithQrAsync(string qrText)
    {
        EnsureUsable();
        if (!PairingQrPayload.TryParse(qrText, out PairingQrPayload? payload))
        {
            SetError(ClientErrorKind.InvalidInput, "Not a PenBridge pairing code.");
            return Task.FromResult(false);
        }

        return BeginUserAttempt(AttemptKind.Pair, payload!.Host, payload.Port, null, (c, ct) => c.PairWithQrAsync(payload, ct));
    }

    /// <summary>
    /// User disconnect (leaving the pad, menu "Disconnect"/"Change Mac"): closes the session, clears the target and
    /// suppresses auto-connect until the next explicit connect. <paramref name="forgetLastUsed"/> also clears the
    /// last-used mark so the next start shows the Mac list.
    /// </summary>
    public void Disconnect(bool forgetLastUsed = false)
    {
        EnsureUsable();
        Supersede();
        _target = null;
        _autoConnectSuppressed = true;
        if (forgetLastUsed)
        {
            Settings.ClearLastMac();
        }

        SetState(_started ? new ClientHostState(ClientHostStatus.Discovering) : ClientHostState.Idle);
    }

    /// <summary>Forgets a Mac on this phone: deletes its device key and metadata; disconnects if it's the target.</summary>
    public void Forget(ReadOnlySpan<byte> fingerprint)
    {
        EnsureUsable();
        if (_target is { } t && fingerprint.SequenceEqual(t.Fingerprint))
        {
            Disconnect();
        }

        Credentials.Forget(fingerprint);
        Settings.Forget(fingerprint);
    }

    /// <summary>
    /// Sets the drawing surface (rotation, layout change). Sent at once when connected, and used for every later
    /// connection. Call between strokes only (ADR-0019). Main thread.
    /// </summary>
    public void UpdateSurface(SurfaceInfoMessage surface)
    {
        EnsureUsable();
        _ = surface.PayloadSize;
        _surface = surface;
        if (_client is { IsConnected: true } client)
        {
            _ = SendSurfaceAsync(client, surface);
        }
    }

    // ---- Hot path ----

    /// <summary>
    /// Sends the complete current input state; mirrors
    /// <see cref="PenBridgeClient.SendState(in PenSample?, ReadOnlySpan{TouchSample}, ReadOnlySpan{PenSample})"/>
    /// exactly (same signature and rules: full state each call, <paramref name="history"/> oldest first, newest 8 kept,
    /// keepalives handled by Core). Any thread; allocation-free; never blocks on the network. Returns false when no
    /// session is up (input is dropped, not queued). Throws <see cref="ArgumentException"/> for out-of-range samples or
    /// duplicate pointer ids, like Core.
    /// </summary>
    public bool SendState(in PenSample? pen, ReadOnlySpan<TouchSample> touches, ReadOnlySpan<PenSample> history)
    {
        PenBridgeClient? client = Volatile.Read(ref _client);
        return client is not null && client.SendState(pen, touches, history);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        EnsureMainThread();
        _disposed = true;
        _started = false;
        Discovery.Changed -= OnDiscoveryChanged;
        _main.RemoveCallbacks(_retryAction);
        _main.RemoveCallbacks(_refreshAction);
        _generation++;
        _attemptCts?.Cancel();
        PenBridgeClient? client = Interlocked.Exchange(ref _client, null);
        Discovery.Dispose();
        if (client is not null)
        {
            client.Disconnected -= OnClientDisconnected;
            await CloseClientAsync(client, graceful: true).ConfigureAwait(true);
        }

        _main.Dispose();
    }

    // ---- Attempts ----

    private Task<bool> PairWithPinAsync(string host, int port, string pin, string? name)
    {
        EnsureUsable();
        if (pin is null || pin.Length != PinIssuer.PinLength || !IsAsciiDigits(pin))
        {
            SetError(ClientErrorKind.InvalidInput, "The PIN must be 6 digits.");
            return Task.FromResult(false);
        }

        return BeginUserAttempt(AttemptKind.Pair, host, port, name, (c, ct) => c.PairWithPinAsync(host, port, pin, ct));
    }

    private Task<bool> BeginUserAttempt(AttemptKind kind, string host, int port, string? name, Func<PenBridgeClient, CancellationToken, Task> op)
    {
        EnsureUsable();
        if (!IsValidEndpoint(host, port))
        {
            SetError(ClientErrorKind.InvalidInput, "Enter a valid host and port.");
            return Task.FromResult(false);
        }

        _retryAttempt = 0;
        _autoConnectSuppressed = false;
        return AttemptAsync(kind, host, port, name, reconnecting: false, op);
    }

    private Task<bool> ReconnectTargetAsync()
    {
        Target t = _target!;
        return AttemptAsync(AttemptKind.Reconnect, t.Host, t.Port, t.Name, reconnecting: true, (c, ct) => c.ConnectAsync(t.Host, t.Port, t.Fingerprint, ct));
    }

    private async Task<bool> AttemptAsync(
        AttemptKind kind,
        string host,
        int port,
        string? name,
        bool reconnecting,
        Func<PenBridgeClient, CancellationToken, Task> op)
    {
        if (!_started)
        {
            SetError(ClientErrorKind.Unreachable, "PenBridge is not active.");
            return false;
        }

        Supersede();
        int generation = _generation;
        Log.Info(Tag, "Connect attempt: " + kind);
        using var cts = new CancellationTokenSource();
        _attemptCts = cts;
        PenBridgeClient client = CreateClient();
        Volatile.Write(ref _client, client);
        SetState(new ClientHostState(
            kind == AttemptKind.Pair ? ClientHostStatus.Pairing : ClientHostStatus.Connecting,
            MacName: name ?? _target?.Name,
            IsReconnecting: reconnecting,
            Error: reconnecting ? _state.Error : ClientErrorKind.None,
            Message: reconnecting ? _state.Message : null));

        Failure failure;
        try
        {
            await op(client, cts.Token).ConfigureAwait(true);
            if (generation != _generation)
            {
                return false;
            }

            if (client.IsConnected && client.ServerFingerprint is { } fp)
            {
                OnConnected(client, host, port, fp, name);
                return true;
            }

            failure = new Failure(ClientErrorKind.ConnectionLost, "The connection dropped.", 0, Retry: true);
        }
        catch (PenBridgeConnectException ex) when (generation == _generation)
        {
            failure = Map(ex);
        }
        catch (ArgumentException) when (generation == _generation)
        {
            failure = new Failure(ClientErrorKind.InvalidInput, "Invalid connection details.", 0, Retry: false);
        }
        catch (Exception ex) when (generation == _generation && ex is System.OperationCanceledException or IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            failure = new Failure(ClientErrorKind.Unreachable, "Could not connect to the Mac.", 0, Retry: true);
        }
        catch (Exception ex) when (generation != _generation && ex is not OutOfMemoryException)
        {
            return false; // superseded (OnStop, Disconnect, a newer attempt): that code already dropped the client
        }
        finally
        {
            if (ReferenceEquals(_attemptCts, cts))
            {
                _attemptCts = null;
            }
        }

        DropClient(graceful: false);
        Log.Info(Tag, "Connect attempt failed: " + failure.Kind);
        if (kind == AttemptKind.Reconnect && failure.Retry && _started && _target is not null)
        {
            ScheduleRetry(failure.Kind, failure.Message);
        }
        else
        {
            if (kind == AttemptKind.Reconnect)
            {
                _target = null;
                _autoConnectSuppressed = true;
            }

            SetState(new ClientHostState(ClientHostStatus.Error, MacName: name, Error: failure.Kind, Message: failure.Message, AttemptsLeft: failure.AttemptsLeft));
        }

        return false;
    }

    private void OnConnected(PenBridgeClient client, string host, int port, byte[] fingerprint, string? fallbackName)
    {
        string name = PeerText.Sanitize(client.ServerName);
        if (name.Length == 0)
        {
            name = fallbackName ?? host;
        }

        _target = new Target(host, port, fingerprint, name);
        Settings.RecordConnected(fingerprint, name, host, port);
        _retryAttempt = 0;
        _autoConnectSuppressed = false;
        Log.Info(Tag, "Connected");
        SetState(new ClientHostState(ClientHostStatus.Connected, name, client.Transport, RoundMs(client.RoundTripTime)));
        _main.RemoveCallbacks(_refreshAction);
        _main.PostDelayed(_refreshAction, (long)RefreshInterval.TotalMilliseconds);
    }

    private void HandleDisconnected(PenBridgeClient client, ClientDisconnectedEventArgs e)
    {
        if (_disposed || !ReferenceEquals(client, _client) || _state.Status != ClientHostStatus.Connected)
        {
            return; // superseded, or the attempt's continuation will notice !IsConnected
        }

        DropClient(graceful: false);
        _main.RemoveCallbacks(_refreshAction);
        Log.Info(Tag, "Session ended: " + e.Reason);
        (ClientErrorKind kind, string message) = Map(e.Reason);
        if (e.Reason == SessionEndReason.Unpaired && _target is { } t)
        {
            Settings.Forget(t.Fingerprint); // Core already deleted the device key
        }

        if (_started && _target is not null && ReconnectPolicy.ShouldReconnect(e.Reason))
        {
            _retryAttempt = 0;
            ScheduleRetry(kind, message);
            return;
        }

        string? macName = _target?.Name;
        _target = null;
        _autoConnectSuppressed = true;
        SetState(new ClientHostState(ClientHostStatus.Error, MacName: macName, Error: kind, Message: message));
    }

    private void ScheduleRetry(ClientErrorKind kind, string message)
    {
        TimeSpan delay = ReconnectPolicy.DelayForAttempt(_retryAttempt++);
        _retryPending = true;
        _main.RemoveCallbacks(_retryAction);
        _main.PostDelayed(_retryAction, (long)delay.TotalMilliseconds);
        SetState(new ClientHostState(ClientHostStatus.Connecting, _target?.Name, IsReconnecting: true, Error: kind, Message: message));
    }

    private void OnRetryDue()
    {
        _retryPending = false;
        if (_disposed || !_started || _target is null)
        {
            return;
        }

        _ = ReconnectTargetAsync();
    }

    private void OnRefresh()
    {
        if (_disposed || _state.Status != ClientHostStatus.Connected || Volatile.Read(ref _client) is not { IsConnected: true } client)
        {
            return;
        }

        SetState(_state with { Transport = client.Transport, RoundTripTime = RoundMs(client.RoundTripTime) });
        _main.PostDelayed(_refreshAction, (long)RefreshInterval.TotalMilliseconds);
    }

    private void OnDiscoveryChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        DiscoveredChanged?.Invoke(this, EventArgs.Empty);
        if (!_started)
        {
            return;
        }

        IReadOnlyList<DiscoveredServer> servers = Discovery.Servers;
        if (_target is { } t)
        {
            // The Mac moved (DHCP, network change): follow it. The advertised fingerprint is only a hint; the
            // connection still requires the pinned certificate.
            foreach (DiscoveredServer server in servers)
            {
                if (server.IsCompatible && server.HasFingerprint(t.Fingerprint) && (server.Host != t.Host || server.Port != t.Port))
                {
                    _target = t with { Host = server.Host, Port = server.Port };
                    if (_retryPending)
                    {
                        _main.RemoveCallbacks(_retryAction);
                        OnRetryDue();
                    }

                    break;
                }
            }

            return;
        }

        if (!AutoConnectEnabled || _autoConnectSuppressed || _autoConnectTried || _attemptCts is not null || _client is not null
            || Settings.LastMacFingerprint is not { } last)
        {
            return;
        }

        if (ReconnectPolicy.SelectAutoConnectTarget(servers, last, Credentials) is { } target)
        {
            _autoConnectTried = true;
            _ = ConnectAsync(target);
        }
    }

    // ---- Helpers ----

    /// <summary>Invalidates the running attempt, pending retry and current client (closing it gracefully).</summary>
    private void Supersede()
    {
        _generation++;
        _retryPending = false;
        _main.RemoveCallbacks(_retryAction);
        _main.RemoveCallbacks(_refreshAction);
        _attemptCts?.Cancel();
        _attemptCts = null;
        DropClient(graceful: true);
    }

    private void DropClient(bool graceful)
    {
        PenBridgeClient? old = Interlocked.Exchange(ref _client, null);
        if (old is not null)
        {
            old.Disconnected -= OnClientDisconnected;
            _ = CloseClientAsync(old, graceful);
        }
    }

    private static async Task CloseClientAsync(PenBridgeClient client, bool graceful)
    {
        try
        {
            if (graceful)
            {
                await client.DisconnectAsync().ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Fire-and-forget close: a failing Bye must not surface as an unobserved exception.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task SendSurfaceAsync(PenBridgeClient client, SurfaceInfoMessage surface)
    {
        try
        {
            await client.UpdateSurfaceAsync(surface).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The session is closing; the next connection sends the stored surface.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private PenBridgeClient CreateClient()
    {
        var client = new PenBridgeClient(new PenBridgeClientOptions
        {
            SecretStore = SecretStore,
            Surface = _surface,
            DeviceName = _deviceName,
            LoggerFactory = _loggerFactory,
        });
        client.Disconnected += OnClientDisconnected;
        return client;
    }

    /// <summary>Core raises this on a thread-pool thread; hop to the main looper.</summary>
    private void OnClientDisconnected(object? sender, ClientDisconnectedEventArgs e)
    {
        if (sender is PenBridgeClient client && !_disposed)
        {
            _main.Post(() => HandleDisconnected(client, e));
        }
    }

    private void SetState(ClientHostState state)
    {
        if (state == _state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, new ClientHostStateChangedEventArgs(state));
    }

    private void SetError(ClientErrorKind kind, string message) =>
        SetState(new ClientHostState(ClientHostStatus.Error, Error: kind, Message: message));

    private void EnsureUsable()
    {
        EnsureMainThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void EnsureMainThread()
    {
        if (Looper.MainLooper is not { IsCurrentThread: true })
        {
            throw new InvalidOperationException("ClientHost must be used on the main thread (except SendState).");
        }
    }

    private static Failure Map(PenBridgeConnectException ex) => ex.Failure switch
    {
        ConnectFailure.ConnectionFailed or ConnectFailure.ConnectionClosed => new(ClientErrorKind.Unreachable, ex.Message, 0, ReconnectPolicy.ShouldRetry(ex.Failure)),
        ConnectFailure.Timeout => new(ClientErrorKind.Timeout, ex.Message, 0, ReconnectPolicy.ShouldRetry(ex.Failure)),
        ConnectFailure.IncompatibleVersion => new(ClientErrorKind.IncompatibleVersion, ex.Message, 0, false),
        ConnectFailure.ServerIdentityMismatch => new(ClientErrorKind.IdentityMismatch, ex.Message, 0, false),
        ConnectFailure.NotPaired => new(ClientErrorKind.NotPaired, ex.Message, 0, false),
        ConnectFailure.PairingFailed when ex.AttemptsLeft <= 0 => new(ClientErrorKind.PairingLockedOut, "Too many wrong PINs. Use the new PIN on the Mac after a short wait.", 0, false),
        ConnectFailure.PairingFailed => new(ClientErrorKind.PairingFailed, ex.Message, ex.AttemptsLeft, false),
        ConnectFailure.PairingExpired => new(ClientErrorKind.PairingExpired, ex.Message, 0, false),
        ConnectFailure.PairingUnavailable => new(ClientErrorKind.PairingUnavailable, ex.Message, 0, false),
        ConnectFailure.AuthFailed => new(ClientErrorKind.AuthFailed, ex.Message, 0, false),
        ConnectFailure.Busy => new(ClientErrorKind.Busy, ex.Message, 0, ReconnectPolicy.ShouldRetry(ex.Failure)),
        _ => new(ClientErrorKind.ProtocolError, ex.Message, 0, ReconnectPolicy.ShouldRetry(ex.Failure)),
    };

    private static (ClientErrorKind Kind, string Message) Map(SessionEndReason reason) => reason switch
    {
        SessionEndReason.Timeout => (ClientErrorKind.Timeout, "The Mac stopped responding."),
        SessionEndReason.PeerClosed => (ClientErrorKind.ConnectionLost, "The Mac closed the connection."),
        SessionEndReason.Shutdown => (ClientErrorKind.ConnectionLost, "PenBridge on the Mac quit."),
        SessionEndReason.TakenOver => (ClientErrorKind.TakenOver, "Another device took over the session."),
        SessionEndReason.Unpaired => (ClientErrorKind.Unpaired, "The Mac forgot this device. Pair again."),
        SessionEndReason.ProtocolError or SessionEndReason.PeerError => (ClientErrorKind.ProtocolError, "The connection failed."),
        _ => (ClientErrorKind.ConnectionLost, "The connection dropped."),
    };

    private static TimeSpan? RoundMs(TimeSpan? rtt) =>
        rtt is { } value ? TimeSpan.FromMilliseconds(Math.Round(value.TotalMilliseconds)) : null;

    private static bool IsAsciiDigits(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidEndpoint(string? host, int port) =>
        !string.IsNullOrWhiteSpace(host)
        && host.Length <= PairingQrPayload.MaxHostLength
        && Uri.CheckHostName(host) != UriHostNameType.Unknown
        && port is >= 1 and <= ushort.MaxValue;

    private static string GetDeviceName(Context context)
    {
        string? name = null;
        if (context.ContentResolver is { } resolver)
        {
            name = global::Android.Provider.Settings.Global.GetString(resolver, "device_name");
        }

        return string.IsNullOrWhiteSpace(name) ? (Build.Model ?? "Android") : name;
    }

    private enum AttemptKind
    {
        Connect,
        Pair,
        Reconnect,
    }

    private readonly record struct Failure(ClientErrorKind Kind, string Message, int AttemptsLeft, bool Retry);

    private sealed record Target(string Host, int Port, byte[] Fingerprint, string Name);
}

/// <summary>Payload of <see cref="ClientHost.StateChanged"/>.</summary>
public sealed class ClientHostStateChangedEventArgs(ClientHostState state) : EventArgs
{
    public ClientHostState State { get; } = state;
}
