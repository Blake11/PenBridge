using Android.Views;
using PenBridge.Android.Pad;
using PenBridge.Android.Services;

namespace PenBridge.Android.Connect;

/// <summary>
/// Hosts the pad (<see cref="PadController"/>) over the <see cref="ClientHost"/>. Back, Disconnect and Change Mac
/// end the session and return to the Mac list; the orientation picked in the pad menu becomes the saved default.
/// </summary>
internal sealed class PadScreen : Screen
{
    private ClientHostPadOutput? _output;
    private PadController? _controller;

    public PadScreen(MainActivity activity)
        : base(activity)
    {
    }

    public override bool IsFullScreen => true;

    public override void Resume() => _controller?.Start();

    public override void Pause() => _controller?.Stop();

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
        _output = new ClientHostPadOutput(Host);
        var options = new PadOptions
        {
            Orientation = ToPad(Host.Settings.Orientation),
            FingerMode = Host.Settings.FingerMode,
            FingerFallbackPressure = Host.Settings.FingerPressure,
            FingerFallbackArmDelay = TimeSpan.FromMilliseconds(Host.Settings.FingerArmDelayMs),
            PalmSizeThreshold = Host.Settings.PalmSizeThreshold,
            ShowTouchSize = Host.Settings.ShowTouchSize,
        };
        _controller = new PadController(Activity, _output, options);
        _controller.DisconnectRequested += OnDisconnectRequested;
        _controller.ChangeMacRequested += OnChangeMacRequested;
        _controller.OrientationModeChanged += OnOrientationModeChanged;
        return _controller.View;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _controller is not null)
        {
            _controller.DisconnectRequested -= OnDisconnectRequested;
            _controller.ChangeMacRequested -= OnChangeMacRequested;
            _controller.OrientationModeChanged -= OnOrientationModeChanged;
            _controller.Dispose();
            _controller = null;
            _output?.Dispose();
            _output = null;
        }

        base.Dispose(disposing);
    }

    private void OnDisconnectRequested(object? sender, EventArgs e) => Leave(forgetLastUsed: false);

    /// <summary>"Change Mac": also drop the last-used mark so the next launch shows the list instead of reconnecting.</summary>
    private void OnChangeMacRequested(object? sender, EventArgs e) => Leave(forgetLastUsed: true);

    private void OnOrientationModeChanged(object? sender, PadOrientationMode mode) => Host.Settings.Orientation = FromPad(mode);

    private void Leave(bool forgetLastUsed)
    {
        // Stop first: releases any held pen/touch state on the Mac before the session closes.
        _controller?.Stop();
        Host.Disconnect(forgetLastUsed);
        Activity.Screens.PopToRoot();
    }
}
