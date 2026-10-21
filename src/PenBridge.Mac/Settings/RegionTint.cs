using AppKit;
using CoreGraphics;
using PenBridge.Core.Input;
using PenBridge.Mac.Input;
using PenBridge.Mac.Services;

namespace PenBridge.Mac.Settings;

/// <summary>
/// The tint outside the active region (ADR-0035): one borderless, click-through window over the target display that
/// dims everything but the region at about 25 % black.
/// </summary>
/// <remarks>
/// <para>Shown only while a phone session is active, input isn't paused, the target is one display whose region isn't
/// the full display, the RegionTint setting is on and the region overlay is closed; other displays are never covered.</para>
/// <para>Main thread only and event-driven: it re-evaluates on <see cref="SettingsStore.Changed"/>, the resolver's
/// target and display events, <see cref="ServerHost.StateChanged"/> and <see cref="RegionPicker.OpenChanged"/>, and
/// redraws only when the display frame or the region changed. Nothing polls; hidden, it costs nothing.</para>
/// <para>The region is normalized to the display with a top-left origin, y down (the store's and the mapper's
/// convention); the view flips it into AppKit's bottom-left coordinates, in points, so Retina scaling is AppKit's.</para>
/// </remarks>
public sealed class RegionTint : IDisposable
{
    private const float DimAlpha = 0.25f;

    private readonly SettingsStore _store;
    private readonly DisplayTargetResolver _resolver;
    private readonly ServerHost _host;
    private readonly RegionPicker _picker;
    private TintWindow? _window;
    private TintView? _view;
    private CGRect _frame;
    private NormalizedRect _region;
    private bool _visible;
    private bool _disposed;

    public RegionTint(SettingsStore store, DisplayTargetResolver resolver, ServerHost host, RegionPicker picker)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(picker);
        _store = store;
        _resolver = resolver;
        _host = host;
        _picker = picker;
        _store.Changed += OnStoreChanged;
        _resolver.TargetChanged += OnChanged;
        _resolver.DisplaysChanged += OnChanged;
        _host.StateChanged += OnChanged;
        _picker.OpenChanged += OnChanged;
        Update();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Changed -= OnStoreChanged;
        _resolver.TargetChanged -= OnChanged;
        _resolver.DisplaysChanged -= OnChanged;
        _host.StateChanged -= OnChanged;
        _picker.OpenChanged -= OnChanged;
        if (_window is not null)
        {
            _window.OrderOut(null);
            _window.ContentView = new NSView();
            _view?.Dispose();
            _window.Dispose();
            _window = null;
            _view = null;
        }
    }

    private void OnStoreChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key is SettingsStore.Keys.TargetDisplay or SettingsStore.Keys.Region or SettingsStore.Keys.RegionTint)
        {
            Update();
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        if (_disposed)
        {
            return;
        }

        ServerHostState state = _host.State;
        DisplayInfo? display = state.Status == ServerHostStatus.Connected && !state.Paused && _store.RegionTint && !_picker.IsOpen
            ? _picker.RegionDisplay()
            : null;
        NormalizedRect region = display is null ? NormalizedRect.Full : _store.GetRegion(display.Uuid);
        NSScreen? screen = display is null || SettingsStore.IsFull(region) ? null : RegionPicker.ScreenFor(display);
        if (screen is null)
        {
            Hide();
            return;
        }

        Show(screen.Frame, region);
    }

    private void Show(CGRect frame, NormalizedRect region)
    {
        if (_window is null)
        {
            _window = new TintWindow(frame);
            _view = new TintView(new CGRect(CGPoint.Empty, frame.Size));
            _window.ContentView = _view;
        }

        bool moved = frame != _frame;
        if (moved)
        {
            _frame = frame;
            _window.SetFrame(frame, display: false);
        }

        if (moved || region != _region || !_visible)
        {
            _region = region;
            _view!.Region = region;
            _view.NeedsDisplay = true;
        }

        if (!_visible)
        {
            _visible = true;
            _window.OrderFrontRegardless();
        }
    }

    private void Hide()
    {
        if (_visible)
        {
            _visible = false;
            _window!.OrderOut(null);
        }
    }

    private sealed class TintWindow : NSWindow
    {
        public TintWindow(CGRect frame)
            : base(frame, NSWindowStyle.Borderless, NSBackingStore.Buffered, false)
        {
            // Above normal and floating windows (and the menu bar, which the pen can't reach outside the region
            // either); below the region overlay (ScreenSaver), which hides the tint anyway.
            Level = NSWindowLevel.Status;
            IsOpaque = false;
            BackgroundColor = NSColor.Clear;
            HasShadow = false;
            IgnoresMouseEvents = true;
            SharingType = NSWindowSharingType.None;
            CollectionBehavior = NSWindowCollectionBehavior.CanJoinAllSpaces
                | NSWindowCollectionBehavior.FullScreenAuxiliary
                | NSWindowCollectionBehavior.Stationary
                | NSWindowCollectionBehavior.IgnoresCycle;
        }

        public override bool CanBecomeKeyWindow => false;

        public override bool CanBecomeMainWindow => false;
    }

    private sealed class TintView(CGRect frame) : NSView(frame)
    {
        public NormalizedRect Region { get; set; } = NormalizedRect.Full;

        public override bool IsOpaque => false;

        public override void DrawRect(CGRect dirtyRect)
        {
            CGRect bounds = Bounds;
            NSColor.Clear.SetFill();
            NSGraphics.RectFill(bounds, NSCompositingOperation.Copy);

            // Stored top-left, y down; the view is bottom-left, y up.
            double h = Region.Height * bounds.Height;
            var hole = new CGRect(
                Region.X * bounds.Width,
                bounds.Height - (Region.Y * bounds.Height) - h,
                Region.Width * bounds.Width,
                h);
            using NSBezierPath dim = NSBezierPath.FromRect(bounds);
            using NSBezierPath holePath = NSBezierPath.FromRect(hole);
            dim.AppendPath(holePath);
            dim.WindingRule = NSWindingRule.EvenOdd;
            NSColor.Black.ColorWithAlphaComponent(DimAlpha).SetFill();
            dim.Fill();
        }
    }
}
