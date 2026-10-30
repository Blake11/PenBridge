using AppKit;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Input;
using PenBridge.Core.Mirror;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;
using PenBridge.Mac.Input;
using PenBridge.Mac.Mirror;

namespace PenBridge.Mac.Services;

/// <summary>What the Mac's screen mirroring does now (menu and Settings window).</summary>
public enum MirrorHostStatus
{
    /// <summary>MirrorEnabled is off.</summary>
    Off,

    /// <summary>PenBridge is paused.</summary>
    Paused,

    /// <summary>Screen Recording is not granted.</summary>
    PermissionNeeded,

    /// <summary>Capture or encode failed; retried on the phone's next request or a target change.</summary>
    Failed,

    /// <summary>Ready; the phone isn't asking for video (not connected, in the background, or in TLS fallback).</summary>
    Waiting,

    /// <summary>Capturing and encoding for the phone.</summary>
    Streaming,
}

/// <summary>
/// Wires the Mac capture pipeline (<see cref="MirrorController"/>) to Core's server (ADR-0038, spec "Core API → Server"):
/// it reports the head's availability, encoders and capture source, starts, reconfigures and stops capture on the
/// server's mirroring events, and feeds the encoded access units to <see cref="PenBridgeServer.SendVideoFrame"/>.
/// </summary>
/// <remarks>
/// <para><b>Availability</b> (<see cref="PenBridgeServer.SetMirrorState"/>, main thread): MirrorEnabled off →
/// Off/SettingOff; paused → Paused/PenBridgePaused; no Screen Recording → Unavailable/PermissionDenied; a capture or
/// encoder failure → Unavailable/CaptureFailed (sticky until the phone asks again, the target changes or the setting is
/// toggled, so a failing capture is tried once per request, never in a loop); no target display → Starting; else
/// Streaming. Core composes the rest (TlsFallback, NoCommonCodec, Throttled).</para>
/// <para><b>Source</b>: the mapping target as a <see cref="MirrorTarget"/> (display, region, window rect or all displays),
/// its pixel size and refresh rate (<see cref="DisplayRefreshMonitor"/>) → <see cref="PenBridgeServer.SetMirrorSource"/>
/// on every change. The encoder pixel-rate cap stays Core's default: VideoToolbox doesn't report one, and measuring it
/// would mean seconds of GPU work.</para>
/// <para><b>Stream</b>: capture runs while the phone asks (MirrorRequested … MirrorStopped), the head is available and
/// Core gave a configuration (MirrorConfigChanged). One dedicated worker thread (user-initiated QoS, alive only while it has work) reconciles the controller with that
/// wanted state (latest wins), so the events never block. Each new epoch is applied (a new size or codec makes a new
/// encoder; rate changes apply in place) and then a keyframe is requested, so the epoch's first frame is a keyframe.
/// The input guard's rate for the current epoch (MirrorRateChanged) overrides the configuration's fps and bitrate and
/// is applied in place (encoder bitrate and expected frame rate, capture frame interval) with no keyframe and without
/// closing the frame gate.
/// Frames reach the server only while the applied configuration is the current one: an old-epoch keyframe still in the
/// encoder can't become the new epoch's frame 1. An HEVC encoder failure drops HEVC from the encoders (Core falls back
/// to H.264 with a new epoch) until a request at least 30 s later. A repeated MirrorStart (the phone recovering from a
/// decoder loss) never restarts capture: same plan → only the new epoch's keyframe, else an in-place reconfigure.</para>
/// <para><b>Idle</b>: nothing runs while mirroring is off, stopped or waiting; the worker exists only while it has work.
/// While streaming a static screen produces no frames (<see cref="MirrorController"/>).</para>
/// <para><b>Threads</b>: public members on the main thread; server events on Core's event thread; frames on a
/// VideoToolbox thread. Shared state is under one lock; nothing calls out while holding it.</para>
/// </remarks>
public sealed partial class MirrorHost : IMirrorFrameSink, IDisposable
{
    /// <summary>NSUserDefaults flag: the system Screen Recording prompt was shown once (the first enable without the grant).</summary>
    private const string ScreenRecordingPromptedKey = "ScreenRecordingPrompted";

    /// <summary>After an HEVC failure, a new request tries HEVC again only after this long (the phone may repeat requests every second).</summary>
    private const long HevcRetryMs = 30_000;

    private readonly SettingsStore _store;
    private readonly DisplayTargetResolver _resolver;
    private readonly DisplayRefreshMonitor _refresh;
    private readonly MirrorController _controller;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly Lock _pushLock = new();

    // Main thread.
    private MappingTarget? _mapping;
    private bool _paused;
    private MirrorFailure? _failure;
    private bool _permissionGranted;
    private (MirrorState State, MirrorReason Reason) _availability = (MirrorState.Starting, MirrorReason.None);
    private (int Width, int Height, int RefreshHz)? _source;
    private MirrorHostStatus _status = MirrorHostStatus.Off;
    private bool _disposed;
    private bool _detecting;
    private NSWindow? _anchor;

    // Under _lock (any thread).
    private PenBridgeServer? _server;
    private MirrorCodecs? _hardware;
    private MirrorCodecs _encoders;
    private bool _requested;
    private bool _awaitingConfig; // a MirrorStart is answered by a new epoch's configuration that hasn't arrived yet
    private bool _available;
    private MirrorStreamConfig? _config;
    private MirrorRateChange? _rate; // the guard's rate; applies only while its epoch is _config's
    private MirrorTarget? _target;
    private bool _dirty;
    private bool _working;
    private bool _stopped;
    private long _fallbackAt = long.MinValue / 2;

    // Reconcile worker only (one at a time).
    private MirrorTarget? _appliedTarget;
    private MirrorStreamSettings _appliedSettings;
    private int _appliedEpoch = -1;

    // Any thread.
    private int _send; // 1 while frames go to the server
    private int _capturing; // 1 while the controller runs
    private int _codec; // the running codec (for Failed)

    public MirrorHost(SettingsStore store, DisplayTargetResolver resolver, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(loggers);
        RequireMainThread();
        _store = store;
        _resolver = resolver;
        _log = loggers.CreateLogger<MirrorHost>();
        _refresh = new DisplayRefreshMonitor(loggers.CreateLogger<DisplayRefreshMonitor>());
        _controller = new MirrorController(this, loggers.CreateLogger<MirrorController>());
        _controller.Failed += OnControllerFailed;
        _permissionGranted = ScreenCapturePermission.IsGranted;
        _store.Changed += OnStoreChanged;
        _resolver.DisplaysChanged += OnDisplaysChanged;
        _refresh.Changed += OnDisplaysChanged;
        EnsureAnchorIfEnabled();
        UpdateAvailability(push: false);
    }

    /// <summary>Raised on the main thread when <see cref="Status"/> or <see cref="IsPermissionGranted"/> changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>What mirroring does now.</summary>
    public MirrorHostStatus Status => _status;

    /// <summary>Screen Recording is granted (as last checked; <see cref="RefreshPermission"/>).</summary>
    public bool IsPermissionGranted => _permissionGranted;

    /// <summary>
    /// Turns mirroring on or off (the menu item and the Settings switch). Turning it on without Screen Recording shows
    /// the system prompt the first time (<see cref="RequestPermission"/>).
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        RequireMainThread();
        _store.MirrorEnabled = enabled; // OnStoreChanged applies it
    }

    /// <summary>
    /// "Grant Screen Recording…": the system prompt if macOS hasn't shown it for PenBridge yet, else System Settings ▸
    /// Privacy &amp; Security ▸ Screen &amp; System Audio Recording (the same pattern as Accessibility).
    /// </summary>
    public void RequestPermission()
    {
        RequireMainThread();
        NSUserDefaults defaults = NSUserDefaults.StandardUserDefaults;
        if (!defaults.BoolForKey(ScreenRecordingPromptedKey))
        {
            defaults.SetBool(true, ScreenRecordingPromptedKey);
            Log.PermissionPrompt(_log);
            _ = ScreenCapturePermission.Request(); // registers PenBridge in the list and shows the prompt once
        }
        else
        {
            ScreenCapturePermission.OpenSettings();
        }

        RefreshPermission();
    }

    /// <summary>Re-checks Screen Recording (menu open, Settings window shown; there is no notification for it).</summary>
    public void RefreshPermission()
    {
        RequireMainThread();
        bool granted = ScreenCapturePermission.IsGranted;
        if (granted == _permissionGranted)
        {
            return;
        }

        _permissionGranted = granted;
        Log.PermissionChanged(_log, granted);
        if (_failure == MirrorFailure.PermissionDenied)
        {
            _failure = null;
        }

        UpdateAvailability(push: true);
        RaiseStatusChanged();
    }

    /// <summary>PenBridge paused or resumed (<see cref="ServerHost.SetPaused"/>): pausing stops capture.</summary>
    public void SetPaused(bool paused)
    {
        RequireMainThread();
        if (_paused == paused)
        {
            return;
        }

        _paused = paused;
        UpdateAvailability(push: true);
    }

    /// <summary>The pen's mapping target (<see cref="ServerHost.SetTarget"/>): what the stream shows.</summary>
    public void SetMapping(MappingTarget mapping)
    {
        RequireMainThread();
        ArgumentNullException.ThrowIfNull(mapping);
        _mapping = mapping;
        UpdateTarget(push: true);
    }

    /// <summary>
    /// Connects a new server before it starts (<see cref="ServerHost"/>; one at a time): its mirroring events, and the
    /// head's availability, encoders and source.
    /// </summary>
    internal void Attach(PenBridgeServer server)
    {
        RequireMainThread();
        ArgumentNullException.ThrowIfNull(server);
        MirrorCodecs? hardware;
        lock (_lock)
        {
            _server = server;
            _requested = false;
            _awaitingConfig = false;
            _config = null;
            hardware = _hardware;
            if (hardware is { } known)
            {
                _encoders = known;
            }
        }

        server.MirrorRequested += OnMirrorRequested;
        server.MirrorStopped += OnMirrorStopped;
        server.MirrorConfigChanged += OnMirrorConfigChanged;
        server.MirrorRateChanged += OnMirrorRateChanged;
        server.KeyframeRequested += OnKeyframeRequested;
        if (hardware is not null)
        {
            PushEncoders();
        }
        else
        {
            DetectEncodersIfEnabled();
        }

        _source = null; // pushed again to the new server
        UpdateTarget(push: true);
        server.SetMirrorState(_availability.State, _availability.Reason);
    }

    /// <summary>Disconnects the server before it is disposed (stop, sleep): capture stops.</summary>
    internal void Detach(PenBridgeServer server)
    {
        RequireMainThread();
        server.MirrorRequested -= OnMirrorRequested;
        server.MirrorStopped -= OnMirrorStopped;
        server.MirrorConfigChanged -= OnMirrorConfigChanged;
        server.MirrorRateChanged -= OnMirrorRateChanged;
        server.KeyframeRequested -= OnKeyframeRequested;
        lock (_lock)
        {
            if (!ReferenceEquals(_server, server))
            {
                return;
            }

            _server = null;
            _requested = false;
            _awaitingConfig = false;
            _config = null;
            Volatile.Write(ref _send, 0);
        }

        Kick();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        RequireMainThread();
        _disposed = true;
        _store.Changed -= OnStoreChanged;
        _resolver.DisplaysChanged -= OnDisplaysChanged;
        _refresh.Changed -= OnDisplaysChanged;
        _controller.Failed -= OnControllerFailed;
        lock (_lock)
        {
            _stopped = true;
            _server = null;
            Volatile.Write(ref _send, 0);
        }

        _controller.Dispose(); // stops capture (waits on the thread pool, at most 10 s)
        _refresh.Dispose();
        _anchor?.Dispose(); // never ordered in: no Close (it would release the window a second time)
        _anchor = null;
    }

    /// <inheritdoc />
    public void OnFrame(ReadOnlySpan<byte> accessUnit, bool keyframe, long captureTimeUs)
    {
        if (Volatile.Read(ref _send) != 0)
        {
            Volatile.Read(ref _server)?.SendVideoFrame(accessUnit, keyframe, captureTimeUs);
        }
    }

    // ---- Server events (Core's event thread, in order) ----

    private void OnMirrorRequested(object? sender, MirrorRequest request)
    {
        PenBridgeServer? server;
        bool restore = false;
        lock (_lock)
        {
            if (!ReferenceEquals(sender, _server))
            {
                return;
            }

            server = _server;
            _requested = true;
            _awaitingConfig = true; // _config is still the previous epoch's: the gate stays shut until Core's new one arrives

            // Core starts a new epoch for every MirrorStart and its configuration follows. A repeated request while
            // streaming (the phone recovering from a decoder loss) keeps the capture running: the worker reconfigures
            // only what changed and asks for the new epoch's keyframe. Until then no frame goes out.
            Volatile.Write(ref _send, 0);
            long now = Environment.TickCount64;
            if (_hardware is { } hardware && _encoders != hardware && now - _fallbackAt >= HevcRetryMs)
            {
                _encoders = hardware; // retry HEVC after an earlier fallback, at most every 30 s
                restore = true;
            }
        }

        Log.Requested(_log, request.Codecs.ToString(), request.MaxWidthPx, request.MaxHeightPx, request.MaxFps);
        if (restore)
        {
            PushEncoders();
        }

        Kick();
        OnMain(() =>
        {
            // A failed capture is tried again once per request; the permission may have been granted meanwhile.
            _failure = null;
            _permissionGranted = ScreenCapturePermission.IsGranted;
            UpdateAvailability(push: true);
            RaiseStatusChanged();
        });
    }

    private void OnMirrorStopped(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(sender, _server))
            {
                return;
            }

            _requested = false;
            _awaitingConfig = false;
            _config = null;
            Volatile.Write(ref _send, 0);
        }

        Log.PhoneStopped(_log);
        Kick();
    }

    private void OnMirrorConfigChanged(object? sender, MirrorStreamConfig config)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(sender, _server))
            {
                return;
            }

            _config = config;
            _rate = null; // a guard rate for this epoch follows right after, in order
            _awaitingConfig = false;
            Volatile.Write(ref _send, 0); // until this epoch's encoder settings are applied
        }

        Log.ConfigChanged(_log, config.Epoch, config.Codec.ToString(), config.WidthPx, config.HeightPx, config.Fps, config.BitrateKbps);
        Kick();
    }

    private void OnMirrorRateChanged(object? sender, MirrorRateChange rate)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(sender, _server))
            {
                return;
            }

            _rate = rate; // the frame gate stays open: same epoch, applied in place
        }

        Log.RateChanged(_log, rate.Epoch, rate.Fps, rate.BitrateKbps);
        Kick();
    }

    private void OnKeyframeRequested(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, Volatile.Read(ref _server)))
        {
            _controller.RequestKeyframe();
        }
    }

    private void OnControllerFailed(object? sender, MirrorFailedEventArgs e) =>
        OnFailure(e.Failure, e.Message, (MirrorCodec)Volatile.Read(ref _codec));

    // ---- Reconcile worker (thread pool) ----

    private void Kick()
    {
        lock (_lock)
        {
            _dirty = true;
            if (_working)
            {
                return;
            }

            _working = true;
        }

        // A dedicated thread at an explicit user-initiated QoS, not the pool: capture setup stays below Core's input
        // receive thread (user-interactive, Qos.OnCoreThreadStarted).
        var worker = new Thread(Reconcile) { IsBackground = true, Name = "PenBridge.MirrorReconcile" };
        worker.Start();
    }

    private void Reconcile()
    {
        _ = Qos.SetThread(Qos.UserInitiated);
        while (true)
        {
            bool run;
            MirrorStreamConfig config;
            MirrorRateChange? rate;
            MirrorTarget? target;
            lock (_lock)
            {
                if (!_dirty)
                {
                    _working = false;
                    return;
                }

                _dirty = false;
                config = _config.GetValueOrDefault();
                rate = _rate;
                target = _target;
                run = !_stopped && _server is not null && _requested && _available && _config is not null && target is not null;
            }

            MirrorCodec codec = config.Codec;
            try
            {
                if (run)
                {
                    Run(config, rate, target!);
                }
                else
                {
                    StopCapture();
                }
            }
            catch (MirrorException ex)
            {
                Interlocked.Exchange(ref _capturing, _controller.IsRunning ? 1 : 0);
                OnFailure(ex.Failure, ex.Message, codec);
            }
#pragma warning disable CA1031 // The worker must survive anything the pipeline throws (bad sizes, ObjC errors); it reports a failure.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Log.WorkerFailed(_log, ex);
                Interlocked.Exchange(ref _capturing, _controller.IsRunning ? 1 : 0);
                OnFailure(MirrorFailure.CaptureFailed, ex.Message, codec);
            }

            OnMain(UpdateStatus);
        }
    }

    private void Run(MirrorStreamConfig config, MirrorRateChange? rate, MirrorTarget target)
    {
        // The epoch runs at the guard's rate when Core sent one for it, else at the configuration's (planned) rate.
        (int fps, int bitrateKbps) = rate is { } r && r.Epoch == config.Epoch ? (r.Fps, r.BitrateKbps) : (config.Fps, config.BitrateKbps);
        var settings = new MirrorStreamSettings(config.WidthPx, config.HeightPx, fps, bitrateKbps, config.Codec);
        Volatile.Write(ref _codec, (int)settings.Codec);
        bool started = false;
        if (!_controller.IsRunning)
        {
            _controller.StartAsync(target, settings).GetAwaiter().GetResult();
            started = true;
        }
        else if (!target.SameAs(_appliedTarget) || settings != _appliedSettings)
        {
            _controller.ReconfigureAsync(target, settings).GetAwaiter().GetResult();
        }

        Interlocked.Exchange(ref _capturing, 1);
        bool newEpoch = started || _appliedEpoch != config.Epoch;
        _appliedTarget = target;
        _appliedSettings = settings;
        _appliedEpoch = config.Epoch;
        lock (_lock)
        {
            // Only while this configuration is still the current one; a newer epoch closed the gate and queued a pass.
            if (_config == config && !_awaitingConfig && _requested && _available && !_stopped)
            {
                Volatile.Write(ref _send, 1);
            }
        }

        if (newEpoch)
        {
            _controller.RequestKeyframe(); // the epoch's frame 1 (a new encoder starts with one anyway)
        }
    }

    private void StopCapture()
    {
        Volatile.Write(ref _send, 0);
        if (_controller.IsRunning)
        {
            _controller.StopAsync().GetAwaiter().GetResult();
        }

        Interlocked.Exchange(ref _capturing, 0);
        _appliedTarget = null;
        _appliedEpoch = -1;
    }

    private void OnFailure(MirrorFailure failure, string message, MirrorCodec codec)
    {
        Log.Failed(_log, failure.ToString(), codec.ToString(), message);
        bool fallback = false;
        lock (_lock)
        {
            _config = null; // Core starts a new epoch when the stream can run again
            Volatile.Write(ref _send, 0);
            if (failure == MirrorFailure.EncoderFailed && codec == MirrorCodec.Hevc
                && (_encoders & MirrorCodecs.Hevc) != 0 && (_encoders & MirrorCodecs.H264) != 0)
            {
                _encoders &= ~MirrorCodecs.Hevc;
                _fallbackAt = Environment.TickCount64;
                fallback = true;
            }
        }

        Kick();
        if (fallback)
        {
            Log.HevcFallback(_log);
            PushEncoders(); // Core replans with H.264 (new epoch)
            return;
        }

        OnMain(() =>
        {
            _failure = failure == MirrorFailure.PermissionDenied ? MirrorFailure.PermissionDenied : MirrorFailure.CaptureFailed;
            if (failure == MirrorFailure.PermissionDenied)
            {
                _permissionGranted = false;
            }

            UpdateAvailability(push: true);
            RaiseStatusChanged();
        });
    }

    // ScreenCaptureKit excludes PenBridge by application, and lists an application only while it owns a window. A menu-bar
    // app may have none (the status item isn't one), so the region tint or a window opened after the capture started
    // would show on the phone. This invisible 1-pt window, created in the window server but never ordered in, keeps
    // PenBridge in the shareable content list.
    private void EnsureAnchorIfEnabled()
    {
        if (_anchor is not null || !_store.MirrorEnabled)
        {
            return;
        }

        _anchor = new NSWindow(new CGRect(-32000, -32000, 1, 1), NSWindowStyle.Borderless, NSBackingStore.Buffered, deferCreation: false)
        {
            IsOpaque = false,
            BackgroundColor = NSColor.Clear,
            IgnoresMouseEvents = true,
            HasShadow = false,
            CollectionBehavior = NSWindowCollectionBehavior.Transient | NSWindowCollectionBehavior.IgnoresCycle,
        };
    }

    // A few VideoToolbox test sessions, once per process, and only once mirroring is on: until then VideoToolbox stays
    // unloaded (idle memory), and the phone doesn't ask for video. Core waits (Starting) until the encoders are known.
    private void DetectEncodersIfEnabled()
    {
        if (!_store.MirrorEnabled || _detecting)
        {
            return;
        }

        lock (_lock)
        {
            if (_hardware is not null)
            {
                return;
            }
        }

        _detecting = true;
        var detector = new Thread(DetectEncoders) { IsBackground = true, Name = "PenBridge.MirrorEncoderDetect" };
        detector.Start();
    }

    private void DetectEncoders()
    {
        _ = Qos.SetThread(Qos.Utility); // a few seconds of GPU test sessions, once; nobody waits on it interactively
        MirrorCodecs codecs;
        try
        {
            codecs = MirrorController.HardwareCodecs(_log);
        }
#pragma warning disable CA1031 // No encoder (or VideoToolbox missing) means no mirroring, never a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.WorkerFailed(_log, ex);
            codecs = MirrorCodecs.None;
        }

        lock (_lock)
        {
            _hardware = codecs;
            _encoders = codecs;
        }

        PushEncoders(); // None → Core answers NoCommonCodec
    }

    // Callers race (Core's event thread, the detection thread, the main thread): the encoder set is read under _lock
    // inside _pushLock, so the last call to run always sends the newest set and Core can't be left with a stale one.
    // Core's setter is quick and the events never take _pushLock, so no lock cycle.
    private void PushEncoders()
    {
        lock (_pushLock)
        {
            PenBridgeServer? server;
            MirrorCodecs encoders;
            lock (_lock)
            {
                server = _server;
                encoders = _encoders;
            }

            server?.SetMirrorEncoders(encoders);
        }
    }

    // ---- Main thread ----

    private void OnStoreChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key != SettingsStore.Keys.MirrorEnabled)
        {
            return; // quality and fps reach Core through the settings snapshot
        }

        bool enabled = _store.MirrorEnabled;
        Log.EnabledChanged(_log, enabled);
        EnsureAnchorIfEnabled();
        DetectEncodersIfEnabled();
        _failure = null;
        _permissionGranted = ScreenCapturePermission.IsGranted;
        if (enabled && !_permissionGranted && !NSUserDefaults.StandardUserDefaults.BoolForKey(ScreenRecordingPromptedKey))
        {
            // The first enable without the grant shows the system prompt; outside the store's change notification.
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                if (!_disposed && _store.MirrorEnabled && !ScreenCapturePermission.IsGranted)
                {
                    RequestPermission();
                }
            });
        }

        UpdateAvailability(push: true);
        RaiseStatusChanged();
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) => UpdateTarget(push: true);

    private void UpdateTarget(bool push)
    {
        if (_disposed)
        {
            return;
        }

        MirrorTarget? target = null;
        if (_mapping is { } mapping && _resolver.HasDisplay)
        {
            try
            {
                target = MirrorTarget.FromMapping(mapping, _resolver.Displays);
            }
            catch (ArgumentException)
            {
                target = null; // the rectangle left its display mid-reconfiguration
            }
        }

        (int, int, int)? source = null;
        if (target is not null)
        {
            (int width, int height) = target.SourcePixelSize();
            if (width >= 1 && height >= 1)
            {
                source = (width, height, _refresh.RefreshHz(target));
            }
        }

        bool targetChanged;
        PenBridgeServer? server;
        lock (_lock)
        {
            targetChanged = target is null ? _target is not null : !target.SameAs(_target);
            _target = target;
            server = _server;
        }

        if (push && source is { } s && source != _source)
        {
            server?.SetMirrorSource(s.Item1, s.Item2, s.Item3);
        }

        _source = source;
        if (targetChanged)
        {
            if (_failure == MirrorFailure.CaptureFailed)
            {
                _failure = null; // a new target (a display came back) is worth another try
            }

            Kick(); // a moved or resized rectangle updates the running capture in place
            UpdateAvailability(push: push);
        }
    }

    private void UpdateAvailability(bool push)
    {
        if (_disposed)
        {
            return;
        }

        (MirrorState State, MirrorReason Reason) next =
            !_store.MirrorEnabled ? (MirrorState.Off, MirrorReason.SettingOff)
            : _paused ? (MirrorState.Paused, MirrorReason.PenBridgePaused)
            : _failure == MirrorFailure.PermissionDenied || !_permissionGranted ? (MirrorState.Unavailable, MirrorReason.PermissionDenied)
            : _failure is not null ? (MirrorState.Unavailable, MirrorReason.CaptureFailed)
            : _source is null ? (MirrorState.Starting, MirrorReason.None)
            : (MirrorState.Streaming, MirrorReason.None);
        bool available = next.State == MirrorState.Streaming;
        bool changed = next != _availability;
        PenBridgeServer? server;
        lock (_lock)
        {
            if (_available != available)
            {
                // Either way, a configuration from the other period (an event in flight) must not survive into this one.
                _available = available;
                _config = null;
                Volatile.Write(ref _send, 0);
            }

            server = _server;
        }

        _availability = next;
        if (changed)
        {
            Log.Availability(_log, next.State.ToString(), next.Reason.ToString());
            if (push)
            {
                server?.SetMirrorState(next.State, next.Reason);
            }

            Kick();
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_disposed)
        {
            return;
        }

        MirrorHostStatus next = _availability.Reason switch
        {
            MirrorReason.SettingOff => MirrorHostStatus.Off,
            MirrorReason.PenBridgePaused => MirrorHostStatus.Paused,
            MirrorReason.PermissionDenied => MirrorHostStatus.PermissionNeeded,
            MirrorReason.CaptureFailed => MirrorHostStatus.Failed,
            _ => Volatile.Read(ref _capturing) != 0 ? MirrorHostStatus.Streaming : MirrorHostStatus.Waiting,
        };
        if (next != _status)
        {
            _status = next;
            RaiseStatusChanged();
        }
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // UI handlers run in main-queue blocks; one that throws must not break mirroring.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.WorkerFailed(_log, ex);
        }
    }

    private void OnMain(Action action) => DispatchQueue.MainQueue.DispatchAsync(() =>
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            action();
        }
#pragma warning disable CA1031 // A main-queue block must not throw into the dispatcher.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.WorkerFailed(_log, ex);
        }
    });

    private static void RequireMainThread()
    {
        if (!NSThread.IsMain)
        {
            throw new InvalidOperationException("MirrorHost must be used on the main thread.");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(930, LogLevel.Information, "Phone asks for video: decoders {Codecs}, up to {Width}x{Height} @ {Fps} Hz")]
        public static partial void Requested(ILogger logger, string codecs, int width, int height, int fps);

        [LoggerMessage(931, LogLevel.Information, "Phone stopped the video")]
        public static partial void PhoneStopped(ILogger logger);

        [LoggerMessage(932, LogLevel.Information, "Mirror epoch {Epoch}: {Codec} {Width}x{Height} @ {Fps} fps, {BitrateKbps} kbps")]
        public static partial void ConfigChanged(ILogger logger, byte epoch, string codec, int width, int height, int fps, int bitrateKbps);

        [LoggerMessage(933, LogLevel.Information, "Mirroring availability: {State} ({Reason})")]
        public static partial void Availability(ILogger logger, string state, string reason);

        [LoggerMessage(934, LogLevel.Warning, "Mirroring failed ({Failure}, {Codec}): {Message}")]
        public static partial void Failed(ILogger logger, string failure, string codec, string message);

        [LoggerMessage(935, LogLevel.Information, "HEVC encoder failed: falling back to H.264")]
        public static partial void HevcFallback(ILogger logger);

        [LoggerMessage(936, LogLevel.Error, "Mirroring worker failed")]
        public static partial void WorkerFailed(ILogger logger, Exception exception);

        [LoggerMessage(937, LogLevel.Information, "Screen mirroring enabled: {Enabled}")]
        public static partial void EnabledChanged(ILogger logger, bool enabled);

        [LoggerMessage(938, LogLevel.Information, "Screen Recording permission granted: {Granted}")]
        public static partial void PermissionChanged(ILogger logger, bool granted);

        [LoggerMessage(939, LogLevel.Information, "Asking for the Screen Recording permission")]
        public static partial void PermissionPrompt(ILogger logger);

        [LoggerMessage(940, LogLevel.Debug, "Mirror epoch {Epoch} rate: {Fps} fps, {BitrateKbps} kbps")]
        public static partial void RateChanged(ILogger logger, byte epoch, int fps, int bitrateKbps);
    }
}
