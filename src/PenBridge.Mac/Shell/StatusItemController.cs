using System.Globalization;
using AppKit;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Net;
using PenBridge.Core.Security;
using PenBridge.Mac.Input;
using PenBridge.Mac.Pairing;
using PenBridge.Mac.Services;
using ServiceManagement;

namespace PenBridge.Mac.Shell;

/// <summary>
/// The menu-bar item: an icon that follows the server state (idle / connected / paused / warning) and the PenBridge menu.
/// </summary>
/// <remarks>
/// <para>Main thread only. Nothing polls: the icon updates on <see cref="ServerHost.StateChanged"/> and
/// <see cref="CGEventTabletInjector.AccessibilityChanged"/>; the menu is rebuilt from fresh snapshots in
/// <c>menuWillOpen</c> (stats, Accessibility, login item and displays are pulled then), and while it is open it is
/// rebuilt again on a state change.</para>
/// <para>Device names come from phones (untrusted) and are shown through <see cref="PeerText.Sanitize"/>.</para>
/// </remarks>
public sealed partial class StatusItemController : IDisposable
{
    private const string IdleSymbol = "pencil.tip";
    private const string ConnectedSymbol = "pencil.tip.crop.circle.fill";
    private const string PausedSymbol = "pause.circle";
    private const string WarningSymbol = "exclamationmark.triangle";
    private const string ResumedFlashSymbol = "play.circle";
    private static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(600);

    private readonly ServerHost _host;
    private readonly CGEventTabletInjector _injector;
    private readonly DisplayTargetResolver _resolver;
    private readonly SettingsStore _settings;
    private readonly PairingWindowController _pairing;
    private readonly GlobalHotkey _hotkey;
    private readonly ILogger _log;
    private readonly NSStatusItem _item;
    private readonly NSMenu _menu;
    private readonly MenuDelegate _menuDelegate;
    private readonly Dictionary<string, NSImage?> _images = new(StringComparer.Ordinal);
    private bool _starting;
    private bool _menuOpen;
    private int _flashGeneration;
    private bool _flashing;
    private bool _disposed;

    public StatusItemController(
        ServerHost host,
        CGEventTabletInjector injector,
        DisplayTargetResolver resolver,
        SettingsStore settings,
        PairingWindowController pairing,
        GlobalHotkey hotkey,
        ILogger<StatusItemController> log)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(injector);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(hotkey);
        ArgumentNullException.ThrowIfNull(log);
        _host = host;
        _injector = injector;
        _resolver = resolver;
        _settings = settings;
        _pairing = pairing;
        _hotkey = hotkey;
        _log = log;

        _item = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Variable);
        _menuDelegate = new MenuDelegate(this);
        _menu = new NSMenu("PenBridge") { AutoEnablesItems = false, Delegate = _menuDelegate };
        _item.Menu = _menu;

        _host.StateChanged += OnStateChanged;
        _injector.AccessibilityChanged += OnAccessibilityChanged;
        _resolver.TargetChanged += OnDisplaysChanged;
        _resolver.DisplaysChanged += OnDisplaysChanged;

        BuildMenu();
        UpdateIcon();
    }

    /// <summary>True while the server's first start (Keychain read, bind) is in progress: shows "Starting…".</summary>
    public bool IsStarting
    {
        get => _starting;
        set
        {
            if (_starting != value)
            {
                _starting = value;
                Refresh();
            }
        }
    }

    /// <summary>Briefly shows the new pause state on the icon (feedback for the global hotkey, when the menu is closed).</summary>
    public void FlashPauseState()
    {
        int generation = ++_flashGeneration;
        _flashing = true;
        SetIcon(_host.State.Paused ? PausedSymbol : ResumedFlashSymbol, highlighted: true);
        DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, FlashDuration), () =>
        {
            if (generation == _flashGeneration && !_disposed)
            {
                _flashing = false;
                UpdateIcon();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.StateChanged -= OnStateChanged;
        _injector.AccessibilityChanged -= OnAccessibilityChanged;
        _resolver.TargetChanged -= OnDisplaysChanged;
        _resolver.DisplaysChanged -= OnDisplaysChanged;
        _item.Menu = null;
        NSStatusBar.SystemStatusBar.RemoveStatusItem(_item);
        _menu.Dispose();
        _menuDelegate.Dispose();
        _item.Dispose();
        foreach (NSImage? image in _images.Values)
        {
            image?.Dispose();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        if (_menuOpen)
        {
            BuildMenu();
        }
    }

    // The injector sees grant changes first (the system broadcast); keep the host's state in step so both agree.
    private void OnAccessibilityChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            _host.RefreshAccessibility();
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        UpdateIcon();
        if (_menuOpen)
        {
            BuildMenu();
        }
    }

    private void OnMenuWillOpen()
    {
        _menuOpen = true;
        _injector.RefreshAccessibility();
        _host.RefreshAccessibility();
        BuildMenu();
        UpdateIcon();
    }

    private void OnMenuDidClose() => _menuOpen = false;

    private bool AccessibilityMissing => _injector.AccessibilityMissing || _host.State.AccessibilityMissing;

    private void UpdateIcon()
    {
        if (_disposed || _flashing)
        {
            return;
        }

        ServerHostState state = _host.State;
        string symbol;
        if (AccessibilityMissing || state.Status == ServerHostStatus.Failed || !_resolver.HasDisplay)
        {
            symbol = WarningSymbol;
        }
        else if (state.Paused)
        {
            symbol = PausedSymbol;
        }
        else if (state.Status == ServerHostStatus.Connected)
        {
            symbol = ConnectedSymbol;
        }
        else
        {
            symbol = IdleSymbol;
        }

        SetIcon(symbol, highlighted: false);
        _item.Button.ToolTip = "PenBridge: " + StatusText(state);
    }

    private void SetIcon(string symbol, bool highlighted)
    {
        NSStatusBarButton button = _item.Button;
        NSImage? image = Image(symbol);
        if (image is not null)
        {
            button.Image = image;
            button.Title = string.Empty;
        }
        else
        {
            button.Image = null;
            button.Title = "PB";
        }

        button.Highlight(highlighted);
    }

    private NSImage? Image(string symbol)
    {
        if (!_images.TryGetValue(symbol, out NSImage? image))
        {
            image = NSImage.GetSystemSymbol(symbol, "PenBridge");
            if (image is not null)
            {
                image.Template = true;
            }

            _images[symbol] = image;
        }

        return image;
    }

    private string StatusText(ServerHostState state)
    {
        if (_starting && state.Status == ServerHostStatus.Stopped)
        {
            return "Starting…";
        }

        return state.Status switch
        {
            ServerHostStatus.Listening when state.Addresses.Count > 0 => "Listening on " + state.Addresses[0] + ":" + state.Port.ToString(CultureInfo.InvariantCulture),
            ServerHostStatus.Listening => "Listening on port " + state.Port.ToString(CultureInfo.InvariantCulture) + " (no network)",
            ServerHostStatus.Connected => "Connected: " + (state.ConnectedDeviceName ?? "device"),
            ServerHostStatus.Sleeping => "Sleeping",
            ServerHostStatus.Failed => "Not running: " + (state.Error ?? "see the logs"),
            _ => "Stopped",
        };
    }

    private static string StatsText(PenBridgeServerStats stats)
    {
        string rtt = stats.RoundTripTime is { } t ? "RTT " + Math.Round(t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms" : "RTT –";
        string rate = Math.Round(stats.PacketsPerSecond).ToString(CultureInfo.InvariantCulture) + " packets/s";
        string transport = stats.Transport switch
        {
            InputTransport.Udp => "UDP",
            InputTransport.Tls => "TLS fallback",
            _ => "no input yet",
        };
        return rtt + " · " + rate + " · " + transport;
    }

    private void BuildMenu()
    {
        if (_disposed)
        {
            return;
        }

        ServerHostState state = _host.State;
        bool running = state.Status is ServerHostStatus.Listening or ServerHostStatus.Connected;
        _menu.RemoveAllItems();

        // 1. Status, 2. stats.
        _menu.AddItem(Disabled(StatusText(state)));
        if (state.Status == ServerHostStatus.Connected)
        {
            _menu.AddItem(Disabled(StatsText(_host.GetStats())));
        }

        if (!_resolver.HasDisplay)
        {
            _menu.AddItem(Disabled("No display available: input is paused"));
        }

        // 3. Accessibility.
        if (AccessibilityMissing)
        {
            _menu.AddItem(NSMenuItem.SeparatorItem);
            _menu.AddItem(Action("⚠️ Grant Accessibility…", GrantAccessibility));
        }

        _menu.AddItem(NSMenuItem.SeparatorItem);

        // 4. Pause input.
        NSMenuItem pause = Action(_hotkey.IsRegistered ? "Pause input" : "Pause input (" + GlobalHotkey.DisplayText + " unavailable)", TogglePause);
        pause.State = state.Paused ? NSCellStateValue.On : NSCellStateValue.Off;
        if (_hotkey.IsRegistered)
        {
            pause.KeyEquivalent = "p";
            pause.KeyEquivalentModifierMask = NSEventModifierMask.ControlKeyMask | NSEventModifierMask.AlternateKeyMask | NSEventModifierMask.CommandKeyMask;
        }

        _menu.AddItem(pause);
        _menu.AddItem(NSMenuItem.SeparatorItem);

        // 5. Pair new device…, 6. Paired devices ▸.
        NSMenuItem pair = Action("Pair new device…", () => _pairing.Show());
        pair.Enabled = running;
        _menu.AddItem(pair);
        _menu.AddItem(PairedDevicesItem(state));

        // 7. Display ▸, 8. Allow takeover, 9. Run at startup.
        _menu.AddItem(DisplayItem());
        NSMenuItem takeover = Action("Allow takeover", () => _host.SetAllowTakeover(!_host.State.AllowTakeover));
        takeover.State = state.AllowTakeover ? NSCellStateValue.On : NSCellStateValue.Off;
        takeover.ToolTip = "A newly connecting paired device replaces the active one instead of being refused.";
        _menu.AddItem(takeover);
        _menu.AddItem(RunAtStartupItem());
        _menu.AddItem(NSMenuItem.SeparatorItem);

        // 10. Open logs, 11. Quit.
        _menu.AddItem(Action("Open logs", OpenLogs));
        _menu.AddItem(NSMenuItem.SeparatorItem);
        _menu.AddItem(new NSMenuItem("Quit PenBridge", "q", (_, _) => NSApplication.SharedApplication.Terminate(null)));
    }

    private NSMenuItem PairedDevicesItem(ServerHostState state)
    {
        var item = new NSMenuItem("Paired devices");
        var submenu = new NSMenu { AutoEnablesItems = false };
        if (state.PairedDevices.Count == 0)
        {
            submenu.AddItem(Disabled(_starting && state.Status == ServerHostStatus.Stopped ? "Loading…" : "None"));
        }

        foreach (PairedDevice device in state.PairedDevices)
        {
            string name = PeerText.Sanitize(device.Name);
            bool connected = device.DeviceId == state.ConnectedDeviceId;
            var entry = new NSMenuItem(name + "  (" + (connected ? "connected" : "last seen " + LastSeenText(device.LastSeen)) + ")");
            var actions = new NSMenu { AutoEnablesItems = false };
            Guid id = device.DeviceId;
            actions.AddItem(Action("Forget…", () => ConfirmForget(id, name)));
            entry.Submenu = actions;
            submenu.AddItem(entry);
        }

        item.Submenu = submenu;
        return item;
    }

    private NSMenuItem DisplayItem()
    {
        var item = new NSMenuItem("Display");
        var submenu = new NSMenu { AutoEnablesItems = false };
        IReadOnlyList<DisplayInfo> displays = _resolver.Displays;
        if (displays.Count == 0)
        {
            submenu.AddItem(Disabled("No displays"));
        }

        foreach (DisplayInfo display in displays)
        {
            string title = display.Name + (display.IsMain ? " (main)" : string.Empty);
            string uuid = display.Uuid;
            NSMenuItem entry = Action(title, () => ChooseDisplay(uuid));
            entry.State = _resolver.HasDisplay && display.Uuid == _resolver.Target.Uuid ? NSCellStateValue.On : NSCellStateValue.Off;
            submenu.AddItem(entry);
        }

        if (_resolver.IsUsingFallback)
        {
            submenu.AddItem(NSMenuItem.SeparatorItem);
            submenu.AddItem(Disabled("The chosen display is not connected; using the main display"));
        }

        item.Submenu = submenu;
        return item;
    }

    private static NSMenuItem RunAtStartupItem()
    {
        SMAppServiceStatus status = LoginItem.Status;
        NSMenuItem item = Action("Run at startup", ToggleRunAtStartup);
        item.State = status switch
        {
            SMAppServiceStatus.Enabled => NSCellStateValue.On,
            SMAppServiceStatus.RequiresApproval => NSCellStateValue.Mixed,
            _ => NSCellStateValue.Off,
        };
        if (status == SMAppServiceStatus.RequiresApproval)
        {
            item.ToolTip = "Waiting for approval in System Settings ▸ General ▸ Login Items. Click to open it.";
        }

        return item;
    }

    private void TogglePause() => _host.SetPaused(!_host.State.Paused);

    private void GrantAccessibility()
    {
        // Only the Settings pane: the system prompt is shown once, at first launch (AppDelegate), never alongside it.
        using var url = new NSUrl(AccessibilityStatus.SettingsUrl);
        NSWorkspace.SharedWorkspace.OpenUrl(url);
    }

    private void ChooseDisplay(string uuid)
    {
        _resolver.SetTarget(uuid);
        _settings.TargetDisplayUuid = _resolver.PreferredUuid;
    }

    private static void ToggleRunAtStartup()
    {
        SMAppServiceStatus status = LoginItem.Status;
        if (status == SMAppServiceStatus.RequiresApproval)
        {
            LoginItem.OpenSystemSettings();
            return;
        }

        bool enable = status != SMAppServiceStatus.Enabled;
        if (!LoginItem.TrySetEnabled(enable, out string? error))
        {
            ShowError(enable ? "PenBridge could not be added to the login items." : "PenBridge could not be removed from the login items.", error);
            return;
        }

        if (enable && LoginItem.Status == SMAppServiceStatus.RequiresApproval)
        {
            LoginItem.OpenSystemSettings();
        }
    }

    private void OpenLogs()
    {
        if (!LogViewer.Open(out string? error))
        {
            Log.OpenLogsFailed(_log, error ?? string.Empty);
            ShowError("The log window could not be opened.", error);
        }
    }

    private void ConfirmForget(Guid deviceId, string name)
    {
        using var alert = new NSAlert
        {
            AlertStyle = NSAlertStyle.Warning,
            MessageText = "Forget “" + name + "”?",
            InformativeText = "It is disconnected and can't connect again until you pair it anew.",
        };
        alert.AddButton("Forget");
        alert.AddButton("Cancel");
        alert.Buttons[0].HasDestructiveAction = true;
        NSApplication.SharedApplication.Activate();
        if (alert.RunModal() != (nint)(long)NSAlertButtonReturn.First)
        {
            return;
        }

        _ = ForgetAsync(deviceId);
    }

    private async Task ForgetAsync(Guid deviceId)
    {
        try
        {
            if (!await _host.ForgetDeviceAsync(deviceId).ConfigureAwait(true))
            {
                Log.ForgetNotFound(_log, deviceId);
            }
        }
#pragma warning disable CA1031 // A failed Keychain write is reported to the user, never thrown into the run loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.ForgetFailed(_log, deviceId, ex);
            string detail = ex.Message;

            // Not inline: this continuation may run inside a main-queue block, and a modal loop nested in one stalls
            // the serial main queue (ServerHost events) until the alert closes. A run-loop perform doesn't.
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() => ShowError("The device could not be forgotten.", detail));
        }
    }

    private static void ShowError(string message, string? detail)
    {
        using var alert = new NSAlert { AlertStyle = NSAlertStyle.Warning, MessageText = message, InformativeText = detail ?? string.Empty };
        NSApplication.SharedApplication.Activate();
        alert.RunModal();
    }

    private static string LastSeenText(DateTimeOffset lastSeen)
    {
        TimeSpan ago = DateTimeOffset.UtcNow - lastSeen;
        if (ago < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (ago < TimeSpan.FromHours(1))
        {
            return ((int)ago.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min ago";
        }

        DateTime local = lastSeen.ToLocalTime().DateTime;
        return local.Date == DateTime.Today
            ? "today " + local.ToString("t", CultureInfo.CurrentCulture)
            : local.ToString("d", CultureInfo.CurrentCulture);
    }

    private static NSMenuItem Disabled(string title) => new(title) { Enabled = false };

    private static NSMenuItem Action(string title, Action action) => new(title, (_, _) => action());

    private sealed class MenuDelegate(StatusItemController owner) : NSMenuDelegate
    {
        public override void MenuWillOpen(NSMenu menu) => owner.OnMenuWillOpen();

        public override void MenuDidClose(NSMenu menu) => owner.OnMenuDidClose();

        public override void MenuWillHighlightItem(NSMenu menu, NSMenuItem item)
        {
        }
    }

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Warning, "Open logs failed: {Error}")]
        public static partial void OpenLogsFailed(ILogger logger, string error);

        [LoggerMessage(2, LogLevel.Information, "Forget: device {DeviceId} was not paired")]
        public static partial void ForgetNotFound(ILogger logger, Guid deviceId);

        [LoggerMessage(3, LogLevel.Error, "Forgetting device {DeviceId} failed")]
        public static partial void ForgetFailed(ILogger logger, Guid deviceId, Exception exception);
    }
}
