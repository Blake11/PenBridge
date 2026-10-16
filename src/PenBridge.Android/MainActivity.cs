using Android.Content.PM;
using Android.OS;
using Android.Widget;
using AndroidX.Activity;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using PenBridge.Android.Connect;
using PenBridge.Android.Services;

namespace PenBridge.Android;

/// <summary>
/// The single activity: Mac list → Pair (PIN / QR) → Pad, plus Connect manually and Settings, as a stack of
/// <see cref="Screen"/>s. Drives <see cref="ClientHost"/> from the lifecycle (OnStart/OnStop) and opens the pad
/// whenever a session is up. On a fresh launch it gives auto-connect to the last-used Mac a ~3 s window; after that
/// the user picks from the list. Handles rotation itself so the pad is never recreated mid-session; UiMode too, since
/// the theme is dark-only with fixed colours and a night-mode or dock change needs no resource reload.
/// </summary>
[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    Theme = "@style/PenBridgeTheme",
    LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode)]
public sealed class MainActivity : ComponentActivity
{
    /// <summary>How long a fresh launch waits for the last-used Mac to be discovered and connected.</summary>
    private const long StartupWindowMs = 3000;

    private Handler? _handler;
    private Java.Lang.Runnable? _endStartupRunnable;
    private MacListScreen? _home;
    private BackCallback? _back;
    private ActivityResultLauncher? _cameraPermission;

    internal ClientHost Host { get; private set; } = null!;

    internal ScreenStack Screens { get; private set; } = null!;

    /// <summary>Opens the QR scanner, asking for the camera permission first if needed (only ever from this tap).</summary>
    internal void OpenQrScanner()
    {
        if (CheckSelfPermission(global::Android.Manifest.Permission.Camera) == Permission.Granted)
        {
            Screens.Push(new QrScanScreen(this));
        }
        else
        {
            _cameraPermission?.Launch(new Java.Lang.String(global::Android.Manifest.Permission.Camera));
        }
    }

    /// <summary>Closes the startup auto-connect window: from now on only the user starts connections.</summary>
    internal void EndStartupWindow()
    {
        if (_endStartupRunnable is not null)
        {
            _handler?.RemoveCallbacks(_endStartupRunnable);
        }

        Host.AutoConnectEnabled = false;
        if (_home is { LookingFor: not null })
        {
            _home.LookingFor = null;
            _home.Refresh();
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Host = AppServices.GetHost(this);
        _handler = new Handler(Looper.MainLooper!);
        _endStartupRunnable = new Java.Lang.Runnable(EndStartupWindow);

        var root = new FrameLayout(this);
        SetContentView(root);
        Screens = new ScreenStack(root);
        Screens.Changed += OnScreensChanged;
        _home = new MacListScreen(this);
        Screens.Push(_home);
        if (Host.CurrentMac is not null)
        {
            // Recreated mid-session: the old activity's OnStop already closed the session (state Idle) but kept the
            // target, which this activity's OnStart reconnects to.
            Screens.Push(new PadScreen(this));
        }

        _back = new BackCallback(this);
        OnBackPressedDispatcher.AddCallback(this, _back);
        OnScreensChanged(this, EventArgs.Empty);
        _cameraPermission = RegisterForActivityResult(new ActivityResultContracts.RequestPermission(), new PermissionResult(this));

        Host.StateChanged += OnHostStateChanged;
        Host.DiscoveredChanged += OnDiscoveredChanged;

        if (savedInstanceState is null && Host.CurrentMac is null && Host.Settings.LastMac is { } last)
        {
            Host.AutoConnectEnabled = true;
            _home.LookingFor = last.Name;
            _handler.PostDelayed(_endStartupRunnable, StartupWindowMs);
        }
        else
        {
            Host.AutoConnectEnabled = false;
        }
    }

    protected override void OnStart()
    {
        base.OnStart();
        Host.OnStart();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Screens.Resume();
    }

    protected override void OnPause()
    {
        Screens.Pause();
        base.OnPause();
    }

    protected override void OnStop()
    {
        Host.OnStop();
        base.OnStop();
    }

    protected override void OnDestroy()
    {
        Host.StateChanged -= OnHostStateChanged;
        Host.DiscoveredChanged -= OnDiscoveredChanged;
        if (_endStartupRunnable is not null)
        {
            _handler?.RemoveCallbacks(_endStartupRunnable);
        }

        Screens.Changed -= OnScreensChanged;
        Screens.Clear();
        _back?.Remove();
        _cameraPermission?.Unregister();
        base.OnDestroy();
    }

    private void OnHostStateChanged(object? sender, ClientHostStateChangedEventArgs e)
    {
        ClientHostState state = e.State;
        // The pad may sit under Settings (opened from its menu): a reconnect must not replace that with a new pad.
        if (state.IsConnected && !Screens.HasSessionPad)
        {
            EndStartupWindow();
            Screens.ReplaceAboveRoot(new PadScreen(this));
        }
        else if (state.Status == ClientHostStatus.Error && Screens.HasSessionPad)
        {
            Screens.PopToRoot(); // the session ended for good (takeover, unpaired, …): the list shows why
        }
        else if (state.Status == ClientHostStatus.Error)
        {
            EndStartupWindow();
        }

        Screens.Top?.OnHostStateChanged(state);
    }

    private void OnDiscoveredChanged(object? sender, EventArgs e) => Screens.Top?.OnDiscoveredChanged();

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        // At the root the system handles Back (leaves the app); above it, Back pops or the screen handles it.
        if (_back is not null)
        {
            _back.Enabled = Screens.Count > 1;
        }
    }

    private void HandleBack()
    {
        if (Screens.Top is { } top && !top.OnBack())
        {
            Screens.Pop();
        }
    }

    private void OnCameraPermission(bool granted)
    {
        if (granted)
        {
            Screens.Push(new QrScanScreen(this));
            return;
        }

        string text = ShouldShowRequestPermissionRationale(global::Android.Manifest.Permission.Camera)
            ? "Camera access is needed to scan the QR code. You can enter the PIN instead."
            : "Camera access is off for PenBridge. Allow it in system settings to scan QR codes, or enter the PIN instead.";
        Toast.MakeText(this, text, ToastLength.Long)?.Show();
    }

    private sealed class BackCallback(MainActivity activity) : OnBackPressedCallback(false)
    {
        public override void HandleOnBackPressed() => activity.HandleBack();
    }

    private sealed class PermissionResult(MainActivity activity) : Java.Lang.Object, IActivityResultCallback
    {
        public void OnActivityResult(Java.Lang.Object? result) =>
            activity.OnCameraPermission(result is Java.Lang.Boolean granted && granted.BooleanValue());
    }
}
