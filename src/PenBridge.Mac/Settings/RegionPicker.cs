using AppKit;
using CoreGraphics;
using Foundation;
using PenBridge.Core.Input;
using PenBridge.Core.Settings;
using PenBridge.Mac.Input;
using PenBridge.Mac.Services;

namespace PenBridge.Mac.Settings;

/// <summary>
/// Owns the one picker overlay: the region editor (ADR-0031, ADR-0035 and its #243 amendment; Settings → Area: Region…
/// and the menu's Display ▸ Region…) and the window click picker (ADR-0036, Display ▸ Window…), so at most one of them exists. The region
/// editor covers the display the region applies to (the store's TargetDisplay as resolved; while a window is the target
/// that setting names the window's display, the resolver itself ignores <see cref="WindowTarget"/>) and on Done stores
/// the selection as that display's region, stores the aspect lock and ends a window target; the window picker hands the clicked window to <see cref="WindowTarget"/>.
/// </summary>
/// <remarks>Main thread only. A display reconfiguration or a target change cancels an open overlay (its screen may be
/// gone). Nothing is observed beyond the resolver's events.</remarks>
public sealed class RegionPicker : IDisposable
{
    private readonly SettingsStore _store;
    private readonly DisplayTargetResolver _resolver;
    private readonly WindowTarget _windowTarget;
    private readonly Func<double?>? _phoneSurfaceAspect;
    private RegionOverlay? _overlay;
    private WindowPickerOverlay? _windowPicker;
    private bool _disposed;

    /// <param name="store">The settings (the target, the regions, the aspect lock).</param>
    /// <param name="resolver">The resolved target display.</param>
    /// <param name="windowTarget">The window target the window picker sets; picking a region clears it.</param>
    /// <param name="phoneSurfaceAspect">
    /// The connected phone surface's width / height in its current orientation, or null when unknown; the aspect lock
    /// uses it, else the display's ratio. Read when the overlay opens.
    /// </param>
    public RegionPicker(SettingsStore store, DisplayTargetResolver resolver, WindowTarget windowTarget, Func<double?>? phoneSurfaceAspect = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(windowTarget);
        _store = store;
        _resolver = resolver;
        _windowTarget = windowTarget;
        _phoneSurfaceAspect = phoneSurfaceAspect;
        _resolver.DisplaysChanged += OnDisplaysChanged;
        _resolver.TargetChanged += OnDisplaysChanged;
    }

    /// <summary>Raised (main thread) when an overlay (region or window picker) opens or closes; read <see cref="IsOpen"/>.</summary>
    public event EventHandler? OpenChanged;

    /// <summary>True while the region overlay or the window picker is showing.</summary>
    public bool IsOpen => _overlay is not null || _windowPicker is not null;

    /// <summary>
    /// True when a window can be picked now: no overlay open, a display exists, and the Accessibility grant (needed
    /// to follow the window) is present.
    /// </summary>
    public bool CanPickWindow => !_disposed && !IsOpen && _resolver.HasDisplay && AccessibilityStatus.IsTrusted();

    /// <summary>True when a region can be picked now: a single target display with a screen, and no overlay open.</summary>
    public bool CanOpen => !IsOpen && RegionDisplay() is { } display && ScreenFor(display) is not null;

    /// <summary>
    /// The display the region applies to, as applied (SettingsSync uses the resolved target): the chosen one, main for
    /// "" or while the chosen one is disconnected (the stored choice is kept), null for All displays or no display.
    /// </summary>
    public DisplayInfo? RegionDisplay() =>
        _store.TargetDisplay == MacSettings.AllDisplays || _resolver.IsAllDisplays || !_resolver.HasDisplay ? null : _resolver.Target;

    /// <summary>
    /// Which of the three mutually exclusive areas (#247) the pen maps to: <see cref="MappingArea.Window"/> while a
    /// window is the target, <see cref="MappingArea.Region"/> while the region display has a region, else
    /// <see cref="MappingArea.FullDisplay"/> (also for All displays, which always uses the whole desktop).
    /// </summary>
    public MappingArea Area =>
        _windowTarget.IsActive ? MappingArea.Window
        : RegionDisplay() is { } display && !SettingsStore.IsFull(_store.GetRegion(display.Uuid)) ? MappingArea.Region
        : MappingArea.FullDisplay;

    /// <summary>
    /// Full display (#247): ends a window target and resets the region display's region to the whole display. Does
    /// nothing for All displays or no display.
    /// </summary>
    public void UseFullDisplay()
    {
        if (RegionDisplay() is { } display)
        {
            _windowTarget.Clear(); // a region choice ends a window target (ADR-0036)
            _store.SetRegion(display.Uuid, NormalizedRect.Full);
        }
    }

    /// <summary>The NSScreen of a display (by CGDirectDisplayID), or null if AppKit doesn't list it.</summary>
    public static NSScreen? ScreenFor(DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);
        using var screenNumberKey = new NSString("NSScreenNumber");
        foreach (NSScreen screen in NSScreen.Screens)
        {
            if (screen.DeviceDescription[screenNumberKey] is NSNumber number && number.UInt32Value == display.Id)
            {
                return screen;
            }
        }

        return null;
    }

    /// <summary>Opens the overlay on the region display; does nothing if one is open or no single display applies.</summary>
    /// <param name="closed">Called once after this overlay closed (committed or cancelled), outside its callbacks.</param>
    /// <returns>True if the overlay opened.</returns>
    public bool Open(Action? closed = null)
    {
        DisplayInfo? display = RegionDisplay();
        NSScreen? screen = display is null ? null : ScreenFor(display);
        if (_disposed || display is null || screen is null || IsOpen)
        {
            return false;
        }

        // The aspect lock keeps the phone surface's ratio, else the display's. The bar's toggle is local to the editor;
        // like the region, it is stored only on Done (ADR-0035 amendment: nothing applies until Done).
        CGRect frame = screen.Frame;
        double aspect = _phoneSurfaceAspect?.Invoke() ?? (frame.Width / frame.Height);
        string uuid = display.Uuid;
        RegionOverlay? overlay = null;
        overlay = new RegionOverlay(
            screen,
            _store.GetRegion(uuid),
            aspect,
            _store.RegionAspectLock,
            (region, locked) =>
            {
                // Done picks a region, which ends a window target (ADR-0036); Cancel keeps it.
                _windowTarget.Clear();
                _store.RegionAspectLock = locked;
                _store.SetRegion(uuid, region);
            },
            () => OnOverlayClosed(overlay!, closed));
        _overlay = overlay;
        OpenChanged?.Invoke(this, EventArgs.Empty);
        overlay.Show();
        return true;
    }

    /// <summary>
    /// Opens the window click picker over every display; does nothing if an overlay is open or no window can be
    /// picked. A click makes that window the target; Esc keeps the current target.
    /// </summary>
    /// <returns>True if the picker opened.</returns>
    public bool OpenWindowPicker()
    {
        if (!CanPickWindow)
        {
            return false;
        }

        WindowPickerOverlay? picker = null;
        WindowListEntry? picked = null;
        picker = new WindowPickerOverlay(
            WindowList.OnScreenAppWindows(Environment.ProcessId),
            window => picked = window,
            () => OnWindowPickerClosed(picker!, picked));
        _windowPicker = picker;
        OpenChanged?.Invoke(this, EventArgs.Empty);
        picker.Show();
        return true;
    }

    /// <summary>Cancels an open overlay or window picker.</summary>
    public void Close()
    {
        _overlay?.Close();
        _windowPicker?.Close();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _resolver.DisplaysChanged -= OnDisplaysChanged;
        _resolver.TargetChanged -= OnDisplaysChanged;
        RegionOverlay? overlay = _overlay;
        _overlay = null;
        overlay?.Dispose();
        WindowPickerOverlay? picker = _windowPicker;
        _windowPicker = null;
        picker?.Dispose();
    }

    // A reconfiguration (display unplugged, resolution change) or a new target invalidates an open overlay's screen.
    private void OnDisplaysChanged(object? sender, EventArgs e) => Close();

    // The pick is applied after the picker is gone, outside its own mouse callbacks.
    private void OnWindowPickerClosed(WindowPickerOverlay picker, WindowListEntry? picked)
    {
        if (!ReferenceEquals(_windowPicker, picker))
        {
            return; // disposed meanwhile
        }

        _windowPicker = null;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            picker.Dispose();
            if (_disposed)
            {
                return;
            }

            if (picked is not null)
            {
                if (_windowTarget.Track(picked))
                {
                    ActivateApp(picked.Pid);
                }
                else
                {
                    ShowCannotFollow(picked.OwnerName);
                }
            }

            OpenChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    // The click went to the picker, not the app: bring the picked window's app to the front, as a click on it would.
    // PenBridge is active after the picker, so it yields activation first (cooperative activation, macOS 14+).
    private static void ActivateApp(int pid)
    {
        using NSRunningApplication? app = NSRunningApplication.GetRunningApplication(pid);
        if (app is not null)
        {
            NSApplication.SharedApplication.YieldActivation(app);
            _ = app.Activate(NSApplicationActivationOptions.ActivateAllWindows);
        }
    }

    // Started from a run-loop perform, so the modal doesn't stall the main queue (skill mac-build-run).
    private static void ShowCannotFollow(string app)
    {
        using var alert = new NSAlert
        {
            MessageText = "PenBridge can't follow this window",
            InformativeText = (app.Length == 0 ? "The app" : app) + " doesn't report the window through Accessibility, so the pen keeps its current target.",
            AlertStyle = NSAlertStyle.Informational,
        };
        _ = alert.RunModal();
    }

    private void OnOverlayClosed(RegionOverlay overlay, Action? closed)
    {
        if (!ReferenceEquals(_overlay, overlay))
        {
            return; // disposed meanwhile
        }

        _overlay = null;

        // Not inline: this runs inside the overlay window's own callbacks (key, mouse, resign key).
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            overlay.Dispose();
            if (!_disposed)
            {
                OpenChanged?.Invoke(this, EventArgs.Empty);
                closed?.Invoke();
            }
        });
    }
}
