using AppKit;
using CoreGraphics;
using Foundation;
using PenBridge.Core.Settings;
using PenBridge.Mac.Input;
using PenBridge.Mac.Services;

namespace PenBridge.Mac.Settings;

/// <summary>
/// Owns the one region overlay (ADR-0031, ADR-0035): Settings → Set region… and the menu's Display ▸ Region… both open
/// it here, so at most one exists. It covers the display the region applies to (the resolved target) and stores the
/// dragged rect as that display's region.
/// </summary>
/// <remarks>Main thread only. A display reconfiguration or a target change cancels an open overlay (its screen may be
/// gone). Nothing is observed beyond the resolver's events.</remarks>
public sealed class RegionPicker : IDisposable
{
    private readonly SettingsStore _store;
    private readonly DisplayTargetResolver _resolver;
    private readonly Func<double?>? _phoneSurfaceAspect;
    private RegionOverlay? _overlay;
    private bool _disposed;

    /// <param name="store">The settings (the target, the regions, the aspect lock).</param>
    /// <param name="resolver">The resolved target display.</param>
    /// <param name="phoneSurfaceAspect">
    /// The connected phone surface's width / height in its current orientation, or null when unknown; the aspect lock
    /// uses it, else the display's ratio. Read when the overlay opens.
    /// </param>
    public RegionPicker(SettingsStore store, DisplayTargetResolver resolver, Func<double?>? phoneSurfaceAspect = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolver);
        _store = store;
        _resolver = resolver;
        _phoneSurfaceAspect = phoneSurfaceAspect;
        _resolver.DisplaysChanged += OnDisplaysChanged;
        _resolver.TargetChanged += OnDisplaysChanged;
    }

    /// <summary>Raised (main thread) when the overlay opens or closes; read <see cref="IsOpen"/>.</summary>
    public event EventHandler? OpenChanged;

    /// <summary>True while the overlay is showing.</summary>
    public bool IsOpen => _overlay is not null;

    /// <summary>True when a region can be picked now: a single target display with a screen, and no overlay open.</summary>
    public bool CanOpen => !IsOpen && RegionDisplay() is { } display && ScreenFor(display) is not null;

    /// <summary>
    /// The display the region applies to, as applied (SettingsSync uses the resolved target): the chosen one, main for
    /// "" or while the chosen one is disconnected (the stored choice is kept), null for All displays or no display.
    /// </summary>
    public DisplayInfo? RegionDisplay() =>
        _store.TargetDisplay == MacSettings.AllDisplays || _resolver.IsAllDisplays || !_resolver.HasDisplay ? null : _resolver.Target;

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
        if (_disposed || display is null || screen is null || _overlay is not null)
        {
            return false;
        }

        double? aspect = null;
        if (_store.RegionAspectLock)
        {
            CGRect frame = screen.Frame;
            aspect = _phoneSurfaceAspect?.Invoke() ?? (frame.Width / frame.Height);
        }

        string uuid = display.Uuid;
        RegionOverlay? overlay = null;
        overlay = new RegionOverlay(
            screen,
            _store.GetRegion(uuid),
            aspect,
            region => _store.SetRegion(uuid, region),
            () => OnOverlayClosed(overlay!, closed));
        _overlay = overlay;
        OpenChanged?.Invoke(this, EventArgs.Empty);
        overlay.Show();
        return true;
    }

    /// <summary>Cancels an open overlay.</summary>
    public void Close() => _overlay?.Close();

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
    }

    // A reconfiguration (display unplugged, resolution change) or a new target invalidates an open overlay's screen.
    private void OnDisplaysChanged(object? sender, EventArgs e) => Close();

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
