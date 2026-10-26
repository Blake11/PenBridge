using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using PenBridge.Android.Mirror;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;

namespace PenBridge.Android.Pad;

/// <summary>
/// The drawing pad screen: input surface, ink echo, status dot and menu, immersive mode and orientation handling.
/// Host it with <c>activity.SetContentView(controller.View)</c>; call <see cref="Start"/> when the pad is shown (and
/// from <c>OnResume</c> while it is), <see cref="Stop"/> when it is left (and from <c>OnPause</c>). System Back is the
/// host's: it leaves the pad. UI thread only.
/// </summary>
/// <remarks>
/// The host activity must handle configuration changes for orientation/screen size itself (no recreation), so a
/// rotation only resizes the surface and sends a new SurfaceInfo through <see cref="IPadOutput.UpdateSurface"/>.
/// </remarks>
public sealed class PadController : IDisposable
{
    private const int MenuLatency = 1;
    private const int MenuTransport = 2;
    private const int MenuLandscape = 3;
    private const int MenuPortrait = 4;
    private const int MenuAuto = 5;
    private const int MenuDisconnect = 6;
    private const int MenuChangeMac = 7;
    private const int MenuSettings = 8;
    private const int MenuMirror = 10;
#if DEBUG
    private const int MenuDebugArea = 9;
    private const int MenuDebugMirrorBase = 100;
#endif
    private const int OrientationGroup = 1;

    private readonly Activity _activity;
    private readonly IPadOutput _output;
    private readonly PadSendQueue _queue;
    private readonly FrameLayout _root;
    private readonly SurfaceLookView _look;
    private readonly DrawSurfaceView _surface;
    private readonly InkEchoView? _ink;
    private readonly MirrorView _mirror;
    private readonly StatusDotView _dot;
    private readonly Java.Lang.Runnable _stateRunnable;

    /// <summary>Owned main-looper handler: removing from it always works, attached view or not (#78).</summary>
    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly Lock _postLock = new();
    private PadOrientationMode _orientation;
    private PadOrientationMode? _pendingOrientation;
    private bool _fingerFallbackEnabled;
    private FingerFallbackMode _fingerMode;
    private ushort _fingerFallbackPressure;
    private TimeSpan _armDelay;
    private float _palmSizeThreshold;
    private TouchSizeReadoutView? _readout;
    private NormalizedRect _activeArea = NormalizedRect.Full;
#if DEBUG
    private bool _debugArea;
    private readonly MirrorDebugMenu _mirrorDebug;
#endif
    private ScreenOrientation _hostOrientation;
    private bool _started;
    private volatile bool _disposed;

    public PadController(Activity activity, IPadOutput output, PadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new PadOptions();
        _activity = activity;
        _output = output;
        _orientation = options.Orientation;
        _fingerFallbackEnabled = options.FingerFallbackEnabled;
        _fingerMode = options.FingerMode;
        _fingerFallbackPressure = options.FingerFallbackPressure;
        _armDelay = options.FingerFallbackArmDelay;
        _palmSizeThreshold = options.PalmSizeThreshold;

        _queue = new PadSendQueue(output);
        _look = new SurfaceLookView(activity);
        _ink = options.InkEcho ? new InkEchoView(activity) : null;
        _surface = new DrawSurfaceView(activity, _queue, _ink, CreateFilterOptions())
        {
            ActiveChanged = OnActiveChanged,
            SurfaceSizeChanged = OnSurfaceSizeChanged,
            WindowFocusRegained = OnWindowFocusRegained,
        };
        _mirror = new MirrorView(activity) { ShownChanged = _look.SetMirroring };
#if DEBUG
        _mirrorDebug = new MirrorDebugMenu(activity, _mirror, MenuDebugMirrorBase, DebugActivePixels);
#endif
        _dot = new StatusDotView(activity, _surface);
        _dot.Tapped += OnDotActivated;

        // No splitting: every pointer of a gesture goes to the view its first pointer landed on, so a second finger
        // on the status dot during a stroke stays on the surface.
        _root = new FrameLayout(activity) { MotionEventSplittingEnabled = false };
        _root.SetBackgroundColor(global::Android.Graphics.Color.Black);
        var match = ViewGroup.LayoutParams.MatchParent;

        // Mirror video (ADR-0038) at the bottom: shown through the active area, which the look leaves transparent while
        // mirroring. Then background, margins and markers under the (transparent) input surface; neither takes input.
        _root.AddView(_mirror.VideoLayer, new FrameLayout.LayoutParams(match, match));
        _root.AddView(_look, new FrameLayout.LayoutParams(match, match));
        _root.AddView(_surface, new FrameLayout.LayoutParams(match, match));
        if (_ink is not null)
        {
            // Above the surface for drawing only: it never consumes touches or hover, so input reaches the surface.
            _root.AddView(_ink, new FrameLayout.LayoutParams(match, match));
        }

        // Mirror state overlay (Paused / Unavailable): drawing only, like the ink layer.
        _root.AddView(_mirror.Overlay, new FrameLayout.LayoutParams(match, match));
        if (options.ShowTouchSize)
        {
            AttachReadout();
        }

        int dotSize = Dp(40);
        _root.AddView(_dot, new FrameLayout.LayoutParams(dotSize, dotSize, GravityFlags.Top | GravityFlags.End));
        SetSurfaceLook(options.Look);

        _stateRunnable = new Java.Lang.Runnable(RefreshConnectionState);
        _output.ConnectionStateChanged += OnConnectionStateChanged;
        _output.ActiveAreaChanged += OnOutputActiveAreaChanged;
        RefreshConnectionState();
        SetActiveArea(_output.ActiveArea);
    }

    /// <summary>The pad's root view.</summary>
    public View View => _root;

    /// <summary>The surface last reported via <see cref="IPadOutput.UpdateSurface"/>, or null before the first layout.</summary>
    public SurfaceInfoMessage? Surface { get; private set; }

    /// <summary>Screen mirroring: video under the pad and its state overlay (<see cref="MirrorSession"/> wires it to the session). UI thread.</summary>
    internal MirrorView Mirror => _mirror;

    /// <summary>The pad menu's "Mirror Mac screen" toggle, or null (no session, e.g. the debug preview pad). UI thread.</summary>
    internal IMirrorToggle? MirrorToggle { get; set; }

    /// <summary>Current orientation setting (a pending change applies when input is idle).</summary>
    public PadOrientationMode OrientationMode => _pendingOrientation ?? _orientation;

    /// <summary>Synthetic pressure for finger drawing; applied between strokes.</summary>
    public ushort FingerFallbackPressure
    {
        get => _fingerFallbackPressure;
        set
        {
            _fingerFallbackPressure = value;
            _surface.SetFilterOptions(CreateFilterOptions());
        }
    }

    /// <summary>
    /// The active area in the current surface orientation (ADR-0031): the corner ticks, dimmed margins and centre cross
    /// follow it. The whole surface until the Mac sends one.
    /// </summary>
    public NormalizedRect ActiveArea => _activeArea;

    /// <summary>The user chose Settings in the pad menu (show the Settings screen; call <see cref="ApplyOptions"/> on return).</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>The user chose Disconnect in the pad menu.</summary>
    public event EventHandler? DisconnectRequested;

    /// <summary>The user chose Change Mac in the pad menu (show the Mac list).</summary>
    public event EventHandler? ChangeMacRequested;

    /// <summary>The user picked an orientation in the pad menu (persist it in settings).</summary>
    public event EventHandler<PadOrientationMode>? OrientationModeChanged;

    /// <summary>Pad became visible / activity resumed: immersive, orientation, sender thread.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _hostOrientation = _activity.RequestedOrientation;
        _queue.Start();
        EnterImmersive();
        ApplyOrientation(locked: false);
        RefreshConnectionState();
        _surface.RequestFocus();
    }

    /// <summary>Pad hidden / activity paused: releases all input on the Mac, stops sending, restores the window.</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _surface.ReleaseAll();
        _queue.Stop();
#if DEBUG
        _mirrorDebug.StopPlayer();
#endif
        _mirror.Hide(); // the display mode and surface frame rate never outlive the pad
        ExitImmersive();
        _activity.RequestedOrientation = _hostOrientation;
    }

    /// <summary>Changes the orientation setting; applied now if no contact or touch is active, else after the stroke.</summary>
    public void SetOrientationMode(PadOrientationMode mode)
    {
        _pendingOrientation = mode;
        if (!_surface.IsActive)
        {
            ApplyPendingOrientation();
        }
    }

    /// <summary>
    /// Sets the active area (normalized to the current surface orientation); redraws the markers. UI thread. The pad
    /// follows <see cref="IPadOutput.ActiveArea"/> by itself.
    /// </summary>
    public void SetActiveArea(NormalizedRect area)
    {
        _activeArea = area;
#if DEBUG
        if (_debugArea)
        {
            return;
        }
#endif
        ShowArea(area);
    }

    /// <summary>Applies the area to everything drawn on it: the look (markers, margins) and the mirror video.</summary>
    private void ShowArea(NormalizedRect area)
    {
        _look.SetActiveArea(area);
        _mirror.SetActiveArea(_look.ActiveArea);
    }

    /// <summary>Applies the surface look: background, marker opacity, centre marker, and matching ink and dot colours.</summary>
    public void SetSurfaceLook(SurfaceLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        _look.SetLook(look);
        _ink?.SetColor(look.InkColor);
        _dot.SetLightBackground(look.IsLight);
    }

    /// <summary>
    /// Re-applies settings changed while the pad was hidden (e.g. from the Settings screen): look, finger and palm
    /// options (swapped in when input is idle), the touch-size readout and the orientation.
    /// </summary>
    public void ApplyOptions(PadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SetSurfaceLook(options.Look);
        bool readoutChanged = options.ShowTouchSize != (_readout is not null) || options.PalmSizeThreshold != _palmSizeThreshold;
        _fingerMode = options.FingerMode;
        _fingerFallbackPressure = options.FingerFallbackPressure;
        _armDelay = options.FingerFallbackArmDelay;
        _palmSizeThreshold = options.PalmSizeThreshold;
        _surface.SetFilterOptions(CreateFilterOptions());
        if (readoutChanged)
        {
            DetachReadout();
            if (options.ShowTouchSize)
            {
                AttachReadout();
            }
        }

        if (options.Orientation != OrientationMode)
        {
            SetOrientationMode(options.Orientation);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _output.ConnectionStateChanged -= OnConnectionStateChanged;
        _output.ActiveAreaChanged -= OnOutputActiveAreaChanged;
        lock (_postLock)
        {
            _disposed = true; // a concurrent ConnectionStateChanged can no longer post the runnable
        }

        _dot.Tapped -= OnDotActivated;
        _handler.RemoveCallbacks(_stateRunnable); // nothing can post it any more (see _postLock)
#if DEBUG
        _mirrorDebug.Dispose();
#endif
        _mirror.Dispose();
        _queue.Dispose();
        _stateRunnable.Dispose();
        _handler.Dispose();
    }

    private InputFilterOptions CreateFilterOptions() => new()
    {
        FingerFallbackEnabled = _fingerFallbackEnabled,
        FingerMode = _fingerMode,
        FingerFallbackPressure = _fingerFallbackPressure,
        FingerFallbackArmDelay = _armDelay,
        PalmSizeThreshold = _palmSizeThreshold,
    };

    /// <summary>Creates and adds the touch-size readout: drawing only, like the ink layer, it never takes input.</summary>
    private void AttachReadout()
    {
        _readout = new TouchSizeReadoutView(_activity, _palmSizeThreshold);
        _surface.TouchSizeReadout = _readout;
        _root.AddView(_readout, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom | GravityFlags.Start));
    }

    private void DetachReadout()
    {
        if (_readout is null)
        {
            return;
        }

        _surface.TouchSizeReadout = null;
        _root.RemoveView(_readout);
        _readout.Dispose();
        _readout = null;
    }

    private int Dp(float dp) => (int)((dp * (_activity.Resources?.DisplayMetrics?.Density ?? 1f)) + 0.5f);

    private void OnConnectionStateChanged(object? sender, EventArgs e)
    {
        lock (_postLock)
        {
            if (!_disposed)
            {
                _handler.Post(_stateRunnable);
            }
        }
    }

    /// <summary>UI thread (the output's contract). A rotation's reset to the whole surface comes through here too.</summary>
    private void OnOutputActiveAreaChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            SetActiveArea(_output.ActiveArea);
        }
    }

    private void RefreshConnectionState()
    {
        if (_disposed)
        {
            return;
        }

        PadConnectionState state = _output.ConnectionState;
        _dot.SetState(state);
        _surface.KeepScreenOn = state.Kind != PadConnectionKind.Disconnected;
        // ADR-0026: relative one-finger cursor only while a session negotiated it. The surface defers the filter swap
        // until input is idle, so a flip never splits a touch between the two modes.
        _surface.FingerTouches = state.Kind == PadConnectionKind.Connected && _output.FingerTouches;
    }

    private void OnActiveChanged(bool active)
    {
        if (active)
        {
            // Lock the current rotation in every mode: sensorLandscape/-Portrait may otherwise flip 180° mid-stroke.
            ApplyOrientation(locked: true);
        }
        else if (!ApplyPendingOrientation())
        {
            ApplyOrientation(locked: false);
        }
    }

    private bool ApplyPendingOrientation()
    {
        if (_pendingOrientation is not { } mode)
        {
            return false;
        }

        _pendingOrientation = null;
        _orientation = mode;
        ApplyOrientation(locked: false);
        return true;
    }

    private void ApplyOrientation(bool locked)
    {
        if (!_started)
        {
            return;
        }

        ScreenOrientation requested = locked ? ScreenOrientation.Locked : _orientation switch
        {
            PadOrientationMode.Portrait => ScreenOrientation.SensorPortrait,
            PadOrientationMode.Auto => ScreenOrientation.FullSensor,
            _ => ScreenOrientation.SensorLandscape,
        };

        if (_activity.RequestedOrientation != requested)
        {
            _activity.RequestedOrientation = requested;
        }
    }

    private void OnSurfaceSizeChanged(int width, int height)
    {
        var metrics = _activity.Resources?.DisplayMetrics;
        float fallbackDpi = metrics is null ? 160f : (float)metrics.DensityDpi;
        float xDpi = metrics is { Xdpi: > 0 and < 10_000 } ? metrics.Xdpi : fallbackDpi;
        float yDpi = metrics is { Ydpi: > 0 and < 10_000 } ? metrics.Ydpi : fallbackDpi;
        var surface = new SurfaceInfoMessage(
            (ushort)Math.Min(width, ushort.MaxValue),
            (ushort)Math.Min(height, ushort.MaxValue),
            xDpi,
            yDpi,
            width >= height ? PenBridge.Core.Protocol.SurfaceOrientation.Landscape : PenBridge.Core.Protocol.SurfaceOrientation.Portrait,
            _fingerFallbackEnabled ? SurfaceCapabilities.FingerFallback : SurfaceCapabilities.None);
        Surface = surface;
        _output.UpdateSurface(surface);
#if DEBUG
        if (_debugArea)
        {
            ShowArea(DebugSampleArea(width, height));
        }
#endif
    }

#if DEBUG

    /// <summary>
    /// Debug-only preview of a non-full active area (screenshots before the Mac sends one): the largest centred 4:3
    /// rectangle, as a 4:3 display would give per ADR-0031. Not 16:10, which fills the tablet AVD in landscape.
    /// </summary>
    private static NormalizedRect DebugSampleArea(int width, int height)
    {
        const double Aspect = 4.0 / 3.0;
        if (width <= 0 || height <= 0)
        {
            return NormalizedRect.Full;
        }

        double surface = (double)width / height;
        if (surface > Aspect)
        {
            double w = Aspect / surface;
            return new NormalizedRect((1 - w) / 2, 0, w, 1);
        }

        double h = surface / Aspect;
        return new NormalizedRect(0, (1 - h) / 2, 1, h);
    }

    private void ToggleDebugArea()
    {
        _debugArea = !_debugArea;
        ShowArea(_debugArea ? DebugSampleArea(_surface.Width, _surface.Height) : _activeArea);
    }

    private (int Width, int Height) DebugActivePixels()
    {
        (int l, int t, int r, int b) = MirrorGeometry.ActivePixels(_look.ActiveArea, _surface.Width, _surface.Height);
        return (r - l, b - t);
    }
#endif

    private void OnWindowFocusRegained()
    {
        if (_started)
        {
            EnterImmersive();
        }
    }

    private void EnterImmersive()
    {
        Window? window = _activity.Window;
        if (window is null)
        {
            return;
        }

        WindowManagerLayoutParams? attributes = window.Attributes;
        if (attributes is not null && attributes.LayoutInDisplayCutoutMode != LayoutInDisplayCutoutMode.ShortEdges)
        {
            attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
            window.Attributes = attributes;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(30) && !OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            window.SetDecorFitsSystemWindows(false); // API 35+ is always edge-to-edge for our target SDK
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (window.InsetsController is { } insets)
            {
                insets.Hide(WindowInsets.Type.SystemBars());
                insets.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
        }
        else if (window.DecorView is { } decor)
        {
#pragma warning disable CS0618, CA1422 // API 29 has no WindowInsetsController; SystemUiVisibility is its immersive API.
            decor.SystemUiVisibility = (StatusBarVisibility)(SystemUiFlags.ImmersiveSticky | SystemUiFlags.Fullscreen
                | SystemUiFlags.HideNavigation | SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation
                | SystemUiFlags.LayoutFullscreen);
#pragma warning restore CS0618, CA1422
        }
    }

    private void ExitImmersive()
    {
        Window? window = _activity.Window;
        if (window is null)
        {
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            window.InsetsController?.Show(WindowInsets.Type.SystemBars());
        }
        else if (window.DecorView is { } decor)
        {
#pragma warning disable CS0618, CA1422 // see EnterImmersive
            decor.SystemUiVisibility = (StatusBarVisibility)SystemUiFlags.Visible;
#pragma warning restore CS0618, CA1422
        }
    }

    private void OnDotActivated(object? sender, EventArgs e)
    {
        var popup = new PopupMenu(_activity, _dot, GravityFlags.End);
        if (popup.Menu is not { } menu)
        {
            popup.Dispose();
            return;
        }

        PadConnectionState state = _output.ConnectionState;

        string latency = state is { Kind: PadConnectionKind.Connected, RttMs: { } rtt } ? $"Latency: {rtt} ms" : "Latency: —";
        string transport = state.Kind switch
        {
            PadConnectionKind.Connected => state.Transport switch
            {
                InputTransport.Udp => "Transport: UDP",
                InputTransport.Tls => "Transport: TLS (fallback)",
                _ => "Transport: connected",
            },
            PadConnectionKind.Connecting => "Connecting…",
            PadConnectionKind.Reconnecting => "Reconnecting…",
            _ => "Disconnected",
        };
        menu.Add(0, MenuLatency, 0, latency)?.SetEnabled(false);
        menu.Add(0, MenuTransport, 1, transport)?.SetEnabled(false);

        PadOrientationMode current = OrientationMode;
        ISubMenu? orientation = menu.AddSubMenu(0, 0, 2, "Orientation: " + Describe(current));
        if (orientation is not null)
        {
            orientation.Add(OrientationGroup, MenuLandscape, 0, "Landscape")?.SetChecked(current == PadOrientationMode.Landscape);
            orientation.Add(OrientationGroup, MenuPortrait, 1, "Portrait")?.SetChecked(current == PadOrientationMode.Portrait);
            orientation.Add(OrientationGroup, MenuAuto, 2, "Auto-rotate")?.SetChecked(current == PadOrientationMode.Auto);
            orientation.SetGroupCheckable(OrientationGroup, true, true);
        }

        if (MirrorToggle is { IsAvailable: true } mirror)
        {
            IMenuItem? item = menu.Add(0, MenuMirror, 3, "Mirror Mac screen");
            item?.SetCheckable(true);
            item?.SetChecked(mirror.IsEnabled);
        }

        menu.Add(0, MenuSettings, 3, "Settings…");
        menu.Add(0, MenuDisconnect, 4, "Disconnect");
        menu.Add(0, MenuChangeMac, 5, "Change Mac");
#if DEBUG
        IMenuItem? debugArea = menu.Add(0, MenuDebugArea, 6, "Debug: sample active area");
        debugArea?.SetCheckable(true);
        debugArea?.SetChecked(_debugArea);
        _mirrorDebug.AddTo(menu, 7);
#endif
        popup.MenuItemClick += OnMenuItemClick;
        popup.DismissEvent += (_, _) => popup.MenuItemClick -= OnMenuItemClick;
        popup.Show();
    }

    private void OnMenuItemClick(object? sender, PopupMenu.MenuItemClickEventArgs e)
    {
        switch (e.Item?.ItemId ?? 0)
        {
            case MenuLandscape:
                PickOrientation(PadOrientationMode.Landscape);
                break;
            case MenuPortrait:
                PickOrientation(PadOrientationMode.Portrait);
                break;
            case MenuAuto:
                PickOrientation(PadOrientationMode.Auto);
                break;
            case MenuSettings:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuMirror:
                if (MirrorToggle is { } toggle)
                {
                    toggle.SetEnabled(!toggle.IsEnabled);
                }

                break;
#if DEBUG
            case MenuDebugArea:
                ToggleDebugArea();
                break;
            case var id when _mirrorDebug.Handle(id):
                break;
#endif
            case MenuDisconnect:
                DisconnectRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuChangeMac:
                ChangeMacRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                e.Handled = false;
                return;
        }

        e.Handled = true;
    }

    private void PickOrientation(PadOrientationMode mode)
    {
        SetOrientationMode(mode);
        OrientationModeChanged?.Invoke(this, mode);
    }

    private static string Describe(PadOrientationMode mode) => mode switch
    {
        PadOrientationMode.Portrait => "Portrait",
        PadOrientationMode.Auto => "Auto",
        _ => "Landscape",
    };
}
