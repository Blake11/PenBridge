using Android.Views;
using PenBridge.Android.Mirror;
using PenBridge.Android.Pad;
using PenBridge.Android.Services;

namespace PenBridge.Android.Connect;

/// <summary>
/// Hosts the pad (<see cref="PadController"/>) over the <see cref="ClientHost"/>. Back, Disconnect and Change Mac
/// end the session and return to the Mac list; the orientation picked in the pad menu becomes the saved default.
/// "Settings…" in the pad menu opens <see cref="SettingsScreen"/> on top (the session stays up); changes apply when
/// the pad comes back.
/// </summary>
internal sealed class PadScreen : Screen
{
    private readonly bool _preview;
    private IPadOutput? _output;
    private PadController? _controller;
    private MirrorSession? _mirror;

    public PadScreen(MainActivity activity)
        : this(activity, preview: false)
    {
    }

    /// <param name="activity">The host activity.</param>
    /// <param name="preview">
    /// Debug builds: a pad without a session (sends nothing; leaving only closes it), to check the surface look.
    /// </param>
    internal PadScreen(MainActivity activity, bool preview)
        : base(activity)
    {
        _preview = preview;
    }

    public override bool IsFullScreen => true;

    /// <summary>True for a pad over the host's session; false for the debug preview pad.</summary>
    public bool IsSession => !_preview;

    public override void Resume()
    {
        if (_controller is null)
        {
            return;
        }

        _controller.ApplyOptions(LoadOptions());
        _controller.Start();
        _mirror?.SetForeground(true);
    }

    public override void Pause()
    {
        _mirror?.SetForeground(false); // MirrorStop before the pad hides the video
        _controller?.Stop();
    }

    public override bool OnBack()
    {
        Leave(forgetLastUsed: false);
        return true;
    }

    internal static PadOrientationMode ToPad(OrientationMode mode) => mode switch
    {
        OrientationMode.LockPortrait => PadOrientationMode.Portrait,
        OrientationMode.Auto => PadOrientationMode.Auto,
        _ => PadOrientationMode.Landscape,
    };

    internal static OrientationMode FromPad(PadOrientationMode mode) => mode switch
    {
        PadOrientationMode.Portrait => OrientationMode.LockPortrait,
        PadOrientationMode.Auto => OrientationMode.Auto,
        _ => OrientationMode.LockLandscape,
    };

    protected override View CreateView()
    {
#if DEBUG
        _output = _preview ? new PreviewPadOutput() : new ClientHostPadOutput(Host);
#else
        _output = new ClientHostPadOutput(Host);
#endif
        _controller = new PadController(Activity, _output, LoadOptions());
        _controller.SettingsRequested += OnSettingsRequested;
        _controller.DisconnectRequested += OnDisconnectRequested;
        _controller.ChangeMacRequested += OnChangeMacRequested;
        _controller.OrientationModeChanged += OnOrientationModeChanged;
        if (!_preview)
        {
            _mirror = new MirrorSession(Activity, Host, _controller.Mirror);
            _controller.MirrorToggle = _mirror;
        }

        return _controller.View;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _controller is not null)
        {
            _controller.SettingsRequested -= OnSettingsRequested;
            _controller.DisconnectRequested -= OnDisconnectRequested;
            _controller.ChangeMacRequested -= OnChangeMacRequested;
            _controller.OrientationModeChanged -= OnOrientationModeChanged;
            _controller.MirrorToggle = null;
            _mirror?.Dispose();
            _mirror = null;
            _controller.Dispose();
            _controller = null;
            (_output as IDisposable)?.Dispose();
            _output = null;
        }

        base.Dispose(disposing);
    }

    private PadOptions LoadOptions()
    {
        SettingsStore settings = Host.Settings;
        return new PadOptions
        {
            Orientation = ToPad(settings.Orientation),
            FingerMode = settings.FingerMode,
            FingerFallbackPressure = settings.FingerPressure,
            FingerFallbackArmDelay = TimeSpan.FromMilliseconds(settings.FingerArmDelayMs),
            PalmSizeThreshold = settings.PalmSizeThreshold,
            ShowTouchSize = settings.ShowTouchSize,
            Look = settings.SurfaceLook,
        };
    }

    private void OnSettingsRequested(object? sender, EventArgs e) => Activity.Screens.Push(new SettingsScreen(Activity));

    private void OnDisconnectRequested(object? sender, EventArgs e) => Leave(forgetLastUsed: false);

    /// <summary>"Change Mac": also drop the last-used mark so the next launch shows the list instead of reconnecting.</summary>
    private void OnChangeMacRequested(object? sender, EventArgs e) => Leave(forgetLastUsed: true);

    private void OnOrientationModeChanged(object? sender, PadOrientationMode mode) => Host.Settings.Orientation = FromPad(mode);

    private void Leave(bool forgetLastUsed)
    {
        // Stop first: releases any held pen/touch state on the Mac before the session closes.
        _mirror?.SetForeground(false);
        _controller?.Stop();
        if (_preview)
        {
            Activity.Screens.Pop();
            return;
        }

        Host.Disconnect(forgetLastUsed);
        Activity.Screens.PopToRoot();
    }
}
