using Android.Content;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using PenBridge.Core.Input;

namespace PenBridge.Android.Mirror;

/// <summary>
/// The phone side of screen mirroring (ADR-0038, spec "Heads → Android"): a video <see cref="SurfaceView"/> on the
/// active area under the pad's input surface, the <see cref="MirrorDecoder"/> feeding it, and the state overlay. The
/// host adds <see cref="VideoLayer"/> as the bottom child of the pad and <see cref="Overlay"/> above the input
/// surface; neither takes input.
/// </summary>
/// <remarks>
/// <para><see cref="Show"/>, <see cref="Hide"/>, <see cref="SetActiveArea"/> and <see cref="SetState"/>: UI thread.
/// <see cref="Feed"/>: any thread (the network receive thread), never blocks. The callbacks come from the decoder
/// thread.</para>
/// <para>The video surface exists only while shown (<see cref="ViewStates.Gone"/> otherwise), so mirroring off costs
/// nothing. Above 60 fps the window asks for the display's matching refresh rate and gives it back on
/// <see cref="Hide"/>.</para>
/// </remarks>
internal sealed class MirrorView : IDisposable
{
    private const float DefaultFps = 60f;

    private readonly Activity _activity;
    private readonly MirrorVideoLayer _layer;
    private readonly MirrorStateOverlayView _overlay;
    private readonly MirrorDecoder _decoder;
    private readonly SurfaceCallback _surfaceCallback;
    private MirrorVideoConfig? _config;
    private int _modeId; // the preferredDisplayModeId we set, 0 = none
    private bool _disposed;

    public MirrorView(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        _activity = activity;
        _decoder = new MirrorDecoder(activity.ApplicationContext ?? activity);
        _layer = new MirrorVideoLayer(activity);
        _overlay = new MirrorStateOverlayView(activity);
        _surfaceCallback = new SurfaceCallback(this);
        _layer.Video.Holder?.AddCallback(_surfaceCallback);
    }

    /// <summary>Bottom layer of the pad: holds the video surface, positioned on the active area.</summary>
    public View VideoLayer => _layer;

    /// <summary>Drawing-only layer for <see cref="SetState"/>; goes above the input surface.</summary>
    public View Overlay => _overlay;

    /// <summary>True between <see cref="Show"/> and <see cref="Hide"/>.</summary>
    public bool IsShown => _config is not null;

    /// <summary>The current stream configuration, or null when hidden.</summary>
    public MirrorVideoConfig? Config => _config;

    /// <summary>Raised on the UI thread when the video is shown or hidden (the pad hides its markers while shown).</summary>
    public Action<bool>? ShownChanged { get; set; }

    /// <summary>A frame reached the screen: (frameNumber, renderedAtUs since <see cref="ClockOrigin"/>). Decoder thread.</summary>
    public Action<uint, long>? FrameRendered
    {
        get => _decoder.FrameRendered;
        set => _decoder.FrameRendered = value;
    }

    /// <summary>The decoder waits for a keyframe (gap or restart). Decoder or <see cref="Feed"/> thread.</summary>
    public Action? KeyframeNeeded
    {
        get => _decoder.KeyframeNeeded;
        set => _decoder.KeyframeNeeded = value;
    }

    /// <summary>The decoder can't run (reason text). Decoder thread.</summary>
    public Action<string>? Error
    {
        get => _decoder.Error;
        set => _decoder.Error = value;
    }

    /// <summary>Stopwatch timestamp that <see cref="FrameRendered"/> times count from (the Ping clock origin).</summary>
    public long ClockOrigin
    {
        get => _decoder.ClockOrigin;
        set => _decoder.ClockOrigin = value;
    }

    /// <summary>Decode latency and render latency windows (µs), for logs and the debug player.</summary>
    public MirrorDecoder Decoder => _decoder;

    /// <summary>Shows the video for <paramref name="config"/>, or applies a new config while shown. UI thread.</summary>
    public void Show(MirrorVideoConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool wasShown = _config is not null;
        _config = config;
        _decoder.Configure(config);
        ApplyFrameRate(config.Fps);
        if (!wasShown)
        {
            _layer.Video.Visibility = ViewStates.Visible; // creates the surface; the decoder starts once it exists
            ShownChanged?.Invoke(true);
        }
    }

    /// <summary>Queues one access unit; false if it was dropped (see <see cref="MirrorDecoder.Feed"/>). Any thread.</summary>
    public bool Feed(ReadOnlySpan<byte> accessUnit, bool keyframe, uint frameNumber) =>
        _decoder.Feed(accessUnit, keyframe, frameNumber);

    /// <summary>Stops the video and frees the surface; the pad shows its markers again. UI thread.</summary>
    public void Hide()
    {
        if (_config is null)
        {
            return;
        }

        _config = null;
        _decoder.Stop();
        ApplyFrameRate(0);
        _layer.Video.Visibility = ViewStates.Gone;
        ShownChanged?.Invoke(false);
    }

    /// <summary>The active area (normalized to the pad surface); the video surface follows it. UI thread.</summary>
    public void SetActiveArea(NormalizedRect area)
    {
        _layer.SetActiveArea(area);
        _overlay.SetActiveArea(area);
    }

    /// <summary>Shows a state over the active area (Paused / Unavailable + reason), or clears it. UI thread.</summary>
    public void SetState(MirrorOverlayState state, string? reason = null) => _overlay.SetState(state, reason);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        _disposed = true;
        _layer.Video.Holder?.RemoveCallback(_surfaceCallback);
        _decoder.Dispose();
        _surfaceCallback.Dispose();
    }

    /// <summary>
    /// The video surface declares its frame rate (API 30+), and above 60 fps the window prefers the display mode at the
    /// current resolution that fits it (<see cref="HighRefreshModeId"/>), re-picked on every fps change (90 → 120 picks
    /// another mode). 0 releases both.
    /// </summary>
    private void ApplyFrameRate(int fps)
    {
        if (_layer.Video.Holder?.Surface is { IsValid: true } surface)
        {
            SetSurfaceRate(surface, fps);
        }

        int modeId = fps > DefaultFps ? HighRefreshModeId(fps) : 0;
        if (modeId == _modeId || _activity.Window is not { } window || window.Attributes is not { } attrs)
        {
            return;
        }

        _modeId = modeId;
        attrs.PreferredDisplayModeId = modeId;
        window.Attributes = attrs;
        global::Android.Util.Log.Info("PenBridge", $"Mirror: preferred display mode {(modeId == 0 ? "released" : modeId.ToString(System.Globalization.CultureInfo.InvariantCulture))} for {fps} fps");
    }

    private static void SetSurfaceRate(Surface surface, int fps)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            return;
        }

        try
        {
            surface.SetFrameRate(fps > 0 ? fps : 0f, fps > 0 ? (int)SurfaceFrameRateCompatibility.FixedSource : (int)SurfaceFrameRateCompatibility.Default);
        }
        catch (Java.Lang.Exception)
        {
            // A released surface: the next surfaceCreated applies the rate again.
        }
    }

    /// <summary>The mode at the current resolution with the lowest refresh ≥ <paramref name="fps"/> (else the highest); 0 = none above 60 Hz.</summary>
    private int HighRefreshModeId(int fps)
    {
        var dm = _activity.GetSystemService(Context.DisplayService) as DisplayManager;
        Display? display = dm?.GetDisplay(Display.DefaultDisplay);
        Display.Mode? current = display?.GetMode();
        if (display is null || current is null)
        {
            return 0;
        }

        Display.Mode? atLeast = null; // lowest refresh that reaches fps
        Display.Mode? highest = null;
        foreach (Display.Mode m in display.GetSupportedModes() ?? [])
        {
            if (m.PhysicalWidth != current.PhysicalWidth || m.PhysicalHeight != current.PhysicalHeight)
            {
                continue;
            }

            if (highest is null || m.RefreshRate > highest.RefreshRate)
            {
                highest = m;
            }

            if (m.RefreshRate >= fps - 0.5f && (atLeast is null || m.RefreshRate < atLeast.RefreshRate))
            {
                atLeast = m;
            }
        }

        Display.Mode? pick = atLeast ?? highest;
        return pick is not null && pick.RefreshRate > DefaultFps + 0.5f ? pick.ModeId : 0;
    }

    private void OnSurfaceCreated(ISurfaceHolder holder)
    {
        if (holder.Surface is not { } surface)
        {
            return;
        }

        if (_config is { } config)
        {
            SetSurfaceRate(surface, config.Fps);
        }

        _decoder.SetSurface(surface);
    }

    private sealed class SurfaceCallback(MirrorView owner) : Java.Lang.Object, ISurfaceHolderCallback
    {
        public void SurfaceCreated(ISurfaceHolder holder) => owner.OnSurfaceCreated(holder);

        public void SurfaceChanged(ISurfaceHolder holder, [GeneratedEnum] Format format, int width, int height)
        {
            // Same surface, new size: the decoder scales to fit, nothing to do.
        }

        public void SurfaceDestroyed(ISurfaceHolder holder) => owner._decoder.ClearSurface();
    }
}

/// <summary>
/// Full-pad layer that places its one child, the video <see cref="SurfaceView"/>, exactly on the active area's pixel
/// rect (<see cref="MirrorGeometry"/>, the same rounding as the dimmed margins). Takes no input.
/// </summary>
internal sealed class MirrorVideoLayer : FrameLayout
{
    private NormalizedRect _area = NormalizedRect.Full;

    public MirrorVideoLayer(Context context)
        : base(context)
    {
        Video = new SurfaceView(context) { Visibility = ViewStates.Gone };
        Clickable = false;
        Focusable = false;
        ImportantForAccessibility = ImportantForAccessibility.NoHideDescendants;
        AddView(Video, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
    }

    public SurfaceView Video { get; }

    public void SetActiveArea(NormalizedRect area)
    {
        if (area == _area)
        {
            return;
        }

        _area = area;
        RequestLayout();
    }

    public override bool OnInterceptTouchEvent(MotionEvent? ev) => false;

    public override bool OnTouchEvent(MotionEvent? e) => false;

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int w = MeasureSpec.GetSize(widthMeasureSpec);
        int h = MeasureSpec.GetSize(heightMeasureSpec);
        SetMeasuredDimension(w, h);
        (int l, int t, int r, int b) = MirrorGeometry.ActivePixels(_area, w, h);
        Video.Measure(
            MeasureSpec.MakeMeasureSpec(Math.Max(0, r - l), MeasureSpecMode.Exactly),
            MeasureSpec.MakeMeasureSpec(Math.Max(0, b - t), MeasureSpecMode.Exactly));
    }

    protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
    {
        (int l, int t, int r, int b) = MirrorGeometry.ActivePixels(_area, right - left, bottom - top);
        Video.Layout(l, t, r, b);
    }
}
