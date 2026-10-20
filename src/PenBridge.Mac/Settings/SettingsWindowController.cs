using System.Globalization;
using AppKit;
using CoreGraphics;
using Foundation;
using PenBridge.Core.Input;
using PenBridge.Core.Settings;
using PenBridge.Mac.Input;
using PenBridge.Mac.Services;
using PenBridge.Mac.Shell;
using ServiceManagement;

namespace PenBridge.Mac.Settings;

/// <summary>
/// The Settings… (⌘,) window (ADR-0030–0033): Display, Pen, Gestures and General tabs over <see cref="SettingsStore"/>.
/// </summary>
/// <remarks>
/// <para>Main thread only. The window is built on <see cref="Show"/> and released when it closes; while closed nothing
/// is observed (idle cost zero). While open it follows <see cref="SettingsStore.Changed"/> (menu toggles, later the
/// phone) and display reconfiguration; nothing polls.</para>
/// <para>Controls write to the store only; <see cref="SettingsSync"/> applies them (server, resolver) and pushes the
/// snapshot to the phone, and phone edits come back through the store, so the window follows them live. Ranges come
/// from Core's key table (<see cref="MacSettings"/>). Sliders commit when released (or per arrow-key step), so a drag
/// is one change, not one per pixel.</para>
/// <para>Standard AppKit controls and semantic colours: follows light/dark; keyboard navigation via the window's
/// recalculated key-view loop (Tab, plus Full Keyboard Access for popups and checkboxes).</para>
/// </remarks>
public sealed class SettingsWindowController : IDisposable
{
    private const double SliderWidth = 220;
    private static readonly string[] SideButtonTitles = ["Right click", "Middle click", "Eraser while held", "Double click", "None"];
    private static readonly string[] SmartZoomTitles = ["Follow macOS", "On", "Off"];

    private readonly SettingsStore _store;
    private readonly DisplayTargetResolver _resolver;
    private readonly ServerHost _host;
    private readonly Func<double?>? _phoneSurfaceAspect;
    private readonly List<string> _targetValues = [];

    private SettingsWindow? _window;
    private WindowDelegate? _windowDelegate;
    private RegionOverlay? _overlay;
    private bool _refreshing;
    private bool _disposed;

    // Display
    private NSPopUpButton _target = null!;
    private NSTextField _regionText = null!;
    private NSButton _setRegion = null!;
    private NSButton _fullDisplay = null!;
    private NSButton _aspectLock = null!;

    // Pen
    private NSSegmentedControl _stylusMode = null!;
    private SliderRow _mouseSpeed = null!;
    private NSButton _mouseAcceleration = null!;
    private NSPopUpButton _sideButton = null!;
    private SliderRow _tipFeel = null!;
    private SliderRow _clickThreshold = null!;
    private SliderRow _fullPressureAt = null!;
    private PressureTestPad _pad = null!;
    private PressureCurveView _curve = null!;
    private NSTextField _padPressure = null!;

    // Gestures
    private NSPopUpButton _smartZoom = null!;
    private SliderRow _pointerSpeed = null!;
    private SliderRow _scrollSpeed = null!;
    private NSButton _zoom = null!;
    private NSButton _rotate = null!;
    private NSButton _threeFingerDrag = null!;
    private NSButton _fourFingerGestures = null!;

    // General
    private NSButton _runAtStartup = null!;
    private NSButton _allowTakeover = null!;

    /// <param name="store">The settings.</param>
    /// <param name="resolver">Displays for the target popup and the region overlay; driven like the menu does.</param>
    /// <param name="host">For "Allow takeover", which the host applies and persists.</param>
    /// <param name="phoneSurfaceAspect">
    /// The connected phone surface's width / height in its current orientation, or null when unknown; the region
    /// aspect lock uses it, else the display's ratio. Read when the overlay opens: an overlay that is already open
    /// keeps its aspect until the next "Set region".
    /// </param>
    public SettingsWindowController(SettingsStore store, DisplayTargetResolver resolver, ServerHost host, Func<double?>? phoneSurfaceAspect = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(host);
        _store = store;
        _resolver = resolver;
        _host = host;
        _phoneSurfaceAspect = phoneSurfaceAspect;
    }

    /// <summary>Opens the window (or brings it to the front), activating the accessory app.</summary>
    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        if (_window is null)
        {
            Build();
            _store.Changed += OnStoreChanged;
            _resolver.DisplaysChanged += OnDisplaysChanged;
            _resolver.TargetChanged += OnDisplaysChanged;
            Refresh();
            _window!.Center();
        }

        // An accessory app is never frontmost on its own, and the cooperative Activate() is ignored while another app
        // holds focus; force the activation, then order the window front so it is key even if activation lags.
#pragma warning disable CA1422 // Deprecated, but the cooperative form does not take focus from the frontmost app.
        NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
#pragma warning restore CA1422
        _window!.MakeKeyAndOrderFront(null);
        _window.OrderFrontRegardless();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window?.Close(); // WillClose tears down
    }

    // ---------------------------------------------------------------- building

    private void Build()
    {
        var tabs = new NSTabView { TranslatesAutoresizingMaskIntoConstraints = false };
        NSView[] pages = [BuildDisplayTab(), BuildPenTab(), BuildGesturesTab(), BuildGeneralTab()];
        string[] titles = ["Display", "Pen", "Gestures", "General"];
        for (int i = 0; i < pages.Length; i++)
        {
            tabs.Add(new NSTabViewItem { Label = titles[i], View = Page(pages[i]) });
        }

        // The tab view's chrome (tabs, border) around the page.
        tabs.Frame = new CGRect(0, 0, 1000, 1000);
        CGRect inner = tabs.ContentRect;
        double chromeW = 1000 - inner.Width;
        double chromeH = 1000 - inner.Height;
        double pageW = 0;
        double pageH = 0;
        foreach (NSView page in pages)
        {
            CGSize size = page.FittingSize;
            pageW = Math.Max(pageW, size.Width);
            pageH = Math.Max(pageH, size.Height);
        }

        const double PagePadding = 20;
        const double Margin = 12;
        var contentSize = new CGSize(
            pageW + (2 * PagePadding) + chromeW + (2 * Margin),
            pageH + (2 * PagePadding) + chromeH + (2 * Margin));

        _window = new SettingsWindow(new CGRect(CGPoint.Empty, contentSize))
        {
            Title = "PenBridge Settings",
            AutorecalculatesKeyViewLoop = true,
            TabbingMode = NSWindowTabbingMode.Disallowed,
        };
        _windowDelegate = new WindowDelegate(this);
        _window.Delegate = _windowDelegate;
        NSView content = _window.ContentView!;
        content.AddSubview(tabs);
        NSLayoutConstraint.ActivateConstraints(
        [
            tabs.TopAnchor.ConstraintEqualTo(content.TopAnchor, (nfloat)Margin),
            tabs.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, (nfloat)Margin),
            tabs.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, (nfloat)(-Margin)),
            tabs.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, (nfloat)(-Margin)),
        ]);
        _window.SetContentSize(contentSize);
    }

    private static NSView Page(NSView grid)
    {
        const double Padding = 20;
        var page = new NSView();
        page.AddSubview(grid);
        NSLayoutConstraint.ActivateConstraints(
        [
            grid.TopAnchor.ConstraintEqualTo(page.TopAnchor, (nfloat)Padding),
            grid.LeadingAnchor.ConstraintEqualTo(page.LeadingAnchor, (nfloat)Padding),
            grid.TrailingAnchor.ConstraintLessThanOrEqualTo(page.TrailingAnchor, (nfloat)(-Padding)),
            grid.BottomAnchor.ConstraintLessThanOrEqualTo(page.BottomAnchor, (nfloat)(-Padding)),
        ]);
        return page;
    }

    private NSGridView BuildDisplayTab()
    {
        _target = Popup(OnTargetChosen);
        _regionText = NSTextField.CreateLabel(string.Empty);
        _setRegion = NSButton.CreateButton("Set region…", OnSetRegion);
        _fullDisplay = NSButton.CreateButton("Full display", () => SetRegion(NormalizedRect.Full));
        _aspectLock = Checkbox("Keep the phone's aspect ratio", v => _store.RegionAspectLock = v);
        _aspectLock.ToolTip = "The dragged rectangle keeps the phone surface's aspect ratio (the display's while no phone is connected).";

        return Grid(
            [Label("Map the pen to:"), _target],
            [Label("Region:"), _regionText],
            [Empty(), Row(_setRegion, _fullDisplay)],
            [Empty(), _aspectLock],
            [Empty(), Note("The region applies to the chosen display; All displays always uses the whole desktop.")]);
    }

    private NSGridView BuildPenTab()
    {
        _stylusMode = NSSegmentedControl.FromLabels(["Pen", "Mouse"], NSSegmentSwitchTracking.SelectOne, () =>
        {
            if (!_refreshing)
            {
                _store.StylusMode = _stylusMode.SelectedSegment == 1 ? StylusMode.Mouse : StylusMode.Pen;
            }
        });
        _stylusMode.ToolTip = "Pen: the phone maps onto the display. Mouse: the hovering pen moves the cursor from where it is.";
        _mouseSpeed = SpeedSlider(MacSettingKey.MouseSpeed, v => Commit(() => _store.MouseSpeed = v));
        _mouseAcceleration = Checkbox("Acceleration", v => _store.MouseAcceleration = v);
        _sideButton = Popup(() => _store.SideButton = (SideButtonAction)(int)_sideButton.IndexOfSelectedItem, SideButtonTitles);

        _tipFeel = new SliderRow(MacSettingKey.TipFeel, v => Commit(() => _store.TipFeel = (float)v), OnCurveSliding, TipFeelText, false, "Soft", "Firm");
        _clickThreshold = new SliderRow(MacSettingKey.ClickThreshold, v => Commit(() => _store.ClickThreshold = (float)v), OnCurveSliding, Percent, true);
        _clickThreshold.Slider.ToolTip = "Below this pressure the pen hovers instead of touching (0: the phone decides).";
        _fullPressureAt = new SliderRow(MacSettingKey.FullPressureAt, v => Commit(() => _store.FullPressureAt = (float)v), OnCurveSliding, Percent, true);
        _fullPressureAt.Slider.ToolTip = "Pressing this hard already gives full pressure.";

        _pad = new PressureTestPad();
        _pad.PressureSampled += OnPadSampled;
        _curve = new PressureCurveView();
        _padPressure = NSTextField.CreateLabel("Pressure –");
        _padPressure.Font = NSFont.MonospacedDigitSystemFontOfSize(NSFont.SmallSystemFontSize, NSFontWeight.Regular)!;
        _padPressure.TextColor = NSColor.SecondaryLabel;
        NSButton clear = NSButton.CreateButton("Clear", () => _pad.Clear());
        clear.ControlSize = NSControlSize.Small;
        NSLayoutConstraint.ActivateConstraints(
        [
            _pad.WidthAnchor.ConstraintEqualTo(260),
            _pad.HeightAnchor.ConstraintEqualTo(120),
            _curve.WidthAnchor.ConstraintEqualTo(120),
            _curve.HeightAnchor.ConstraintEqualTo(120),
        ]);

        var padColumn = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 4,
        };
        padColumn.AddArrangedSubview(_pad);
        padColumn.AddArrangedSubview(Row(_padPressure, clear));
        var curveColumn = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 4,
        };
        curveColumn.AddArrangedSubview(_curve);
        curveColumn.AddArrangedSubview(Note("Curve"));
        NSStackView padRow = Row(padColumn, curveColumn);
        padRow.Alignment = NSLayoutAttribute.Top;

        NSTextField advanced = Label("Advanced");
        advanced.Font = NSFont.BoldSystemFontOfSize(NSFont.SystemFontSize)!;

        return Grid(
            [Label("Stylus mode:"), _stylusMode],
            [Label("Mouse speed:"), _mouseSpeed.View],
            [Empty(), _mouseAcceleration],
            [Label("Side button:"), _sideButton],
            [Label("Tip feel:"), _tipFeel.View],
            [advanced, Empty()],
            [Label("Click threshold:"), _clickThreshold.View],
            [Label("Full pressure at:"), _fullPressureAt.View],
            [Label("Test pad:"), padRow],
            [Empty(), Note("Draw here with the pen to try the settings: width follows the pressure an app receives.")]);
    }

    private NSGridView BuildGesturesTab()
    {
        _smartZoom = Popup(() => _store.SmartZoom = (SmartZoomMode)(int)_smartZoom.IndexOfSelectedItem, SmartZoomTitles);
        _smartZoom.ToolTip = "Two-finger double tap. Follow macOS uses the trackpad's Smart zoom setting.";
        _pointerSpeed = SpeedSlider(MacSettingKey.PointerSpeed, v => Commit(() => _store.PointerSpeed = v));
        _scrollSpeed = SpeedSlider(MacSettingKey.ScrollSpeed, v => Commit(() => _store.ScrollSpeed = v));
        _zoom = Checkbox("Zoom (pinch)", v => _store.Zoom = v);
        _rotate = Checkbox("Rotate", v => _store.Rotate = v);
        _threeFingerDrag = Checkbox("Three-finger drag", v => _store.ThreeFingerDrag = v);
        _fourFingerGestures = Checkbox("Four-finger gestures", v => _store.FourFingerGestures = v);

        return Grid(
            [Label("Smart zoom:"), _smartZoom],
            [Label("Pointer speed:"), _pointerSpeed.View],
            [Label("Scroll speed:"), _scrollSpeed.View],
            [Label("Gestures:"), _zoom],
            [Empty(), _rotate],
            [Empty(), _threeFingerDrag],
            [Empty(), _fourFingerGestures]);
    }

    private NSGridView BuildGeneralTab()
    {
        _runAtStartup = Checkbox("Run at startup", _ =>
        {
            StatusItemController.ToggleRunAtStartup();
            RefreshGeneralOnly();
        });
        _allowTakeover = Checkbox("Allow takeover", v => _host.SetAllowTakeover(v));
        _allowTakeover.ToolTip = "A newly connecting paired device replaces the active one instead of being refused.";

        return Grid(
            [Label("Startup:"), _runAtStartup],
            [Label("Connections:"), _allowTakeover],
            [Empty(), Note("A newly connecting paired device replaces the active one instead of being refused.")]);
    }

    // ---------------------------------------------------------------- refresh

    private void OnStoreChanged(object? sender, SettingChangedEventArgs e) => Refresh();

    // A reconfiguration (display unplugged, resolution change) invalidates an open overlay's screen: cancel it.
    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        _overlay?.Close();
        Refresh();
    }

    // The store may clamp a committed value (e.g. the pressure pair rule) without a change to raise; show what it kept.
    private void Commit(Action set)
    {
        set();
        Refresh();
    }

    private void Refresh()
    {
        if (_window is null)
        {
            return;
        }

        _refreshing = true;
        try
        {
            RefreshDisplay();

            _stylusMode.SelectedSegment = _store.StylusMode == StylusMode.Mouse ? 1 : 0;
            _mouseSpeed.SetValue(_store.MouseSpeed);
            SetCheck(_mouseAcceleration, _store.MouseAcceleration);
            _sideButton.SelectItem((nint)(int)_store.SideButton);
            _tipFeel.SetValue(_store.TipFeel);
            _clickThreshold.SetValue(_store.ClickThreshold);
            _fullPressureAt.SetValue(_store.FullPressureAt);
            _curve.SetCurve(_store.TipFeel, _store.ClickThreshold, _store.FullPressureAt);

            _smartZoom.SelectItem((nint)(int)_store.SmartZoom);
            _pointerSpeed.SetValue(_store.PointerSpeed);
            _scrollSpeed.SetValue(_store.ScrollSpeed);
            SetCheck(_zoom, _store.Zoom);
            SetCheck(_rotate, _store.Rotate);
            SetCheck(_threeFingerDrag, _store.ThreeFingerDrag);
            SetCheck(_fourFingerGestures, _store.FourFingerGestures);

            RefreshGeneral();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshDisplay()
    {
        string target = _store.TargetDisplay;
        _target.RemoveAllItems();
        _targetValues.Clear();
        AddTarget("Main display", MacSettings.MainDisplay);
        bool found = target.Length == 0 || target == MacSettings.AllDisplays;
        foreach (DisplayInfo d in _resolver.Displays)
        {
            AddTarget(d.Name + (d.IsMain ? " (main)" : string.Empty), d.Uuid);
            found |= string.Equals(d.Uuid, target, StringComparison.OrdinalIgnoreCase);
        }

        if (!found)
        {
            AddTarget("Disconnected display (using main)", target);
        }

        AddTarget("All displays", MacSettings.AllDisplays);
        for (int i = 0; i < _targetValues.Count; i++)
        {
            if (string.Equals(_targetValues[i], target, StringComparison.OrdinalIgnoreCase))
            {
                _target.SelectItem(i);
                break;
            }
        }

        DisplayInfo? display = RegionDisplay();
        bool all = target == MacSettings.AllDisplays;
        NormalizedRect region = display is null ? NormalizedRect.Full : _store.GetRegion(display.Uuid);
        _regionText.StringValue = all ? "Not used with All displays"
            : display is null ? "No display connected"
            : _resolver.IsUsingFallback ? "Not connected — using the main display: " + RegionText(region, display)
            : RegionText(region, display);
        _regionText.TextColor = display is null ? NSColor.SecondaryLabel : NSColor.Label;
        _setRegion.Enabled = display is not null && ScreenFor(display) is not null && _overlay is null;
        _fullDisplay.Enabled = display is not null && !SettingsStore.IsFull(region);
        _aspectLock.Enabled = !all;
        SetCheck(_aspectLock, _store.RegionAspectLock);

        void AddTarget(string title, string value)
        {
            _target.AddItem(title);
            _targetValues.Add(value);
        }
    }

    private void RefreshGeneral()
    {
        // Mixed ("waiting for approval") is set only from here: a click never cycles into it.
        SMAppServiceStatus status = LoginItem.Status;
        _runAtStartup.AllowsMixedState = status == SMAppServiceStatus.RequiresApproval;
        _runAtStartup.State = status switch
        {
            SMAppServiceStatus.Enabled => NSCellStateValue.On,
            SMAppServiceStatus.RequiresApproval => NSCellStateValue.Mixed,
            _ => NSCellStateValue.Off,
        };
        _runAtStartup.ToolTip = status == SMAppServiceStatus.RequiresApproval
            ? "Waiting for approval in System Settings ▸ General ▸ Login Items. Click to open it."
            : null;
        SetCheck(_allowTakeover, _host.State.AllowTakeover);
    }

    private static string RegionText(NormalizedRect r, DisplayInfo display)
    {
        if (SettingsStore.IsFull(r))
        {
            return "Full display";
        }

        CultureInfo c = CultureInfo.CurrentCulture;
        double w = Math.Round(r.Width * display.Bounds.Width);
        double h = Math.Round(r.Height * display.Bounds.Height);
        double x = Math.Round(r.X * display.Bounds.Width);
        double y = Math.Round(r.Y * display.Bounds.Height);
        return w.ToString(c) + " × " + h.ToString(c) + " pt at " + x.ToString(c) + ", " + y.ToString(c);
    }

    // ---------------------------------------------------------------- actions

    private void OnTargetChosen()
    {
        if (_refreshing)
        {
            return;
        }

        int index = (int)_target.IndexOfSelectedItem;
        if (index < 0 || index >= _targetValues.Count)
        {
            return;
        }

        _store.TargetDisplay = _targetValues[index]; // SettingsSync re-targets the resolver and the server
    }

    private void OnSetRegion()
    {
        DisplayInfo? display = RegionDisplay();
        NSScreen? screen = display is null ? null : ScreenFor(display);
        if (display is null || screen is null || _overlay is not null)
        {
            return;
        }

        double? aspect = null;
        if (_store.RegionAspectLock)
        {
            CGRect frame = screen.Frame;
            aspect = _phoneSurfaceAspect?.Invoke() ?? (frame.Width / frame.Height);
        }

        string uuid = display.Uuid;
        _overlay = new RegionOverlay(
            screen,
            _store.GetRegion(uuid),
            aspect,
            region => _store.SetRegion(uuid, region),
            OnOverlayClosed);
        _setRegion.Enabled = false;
        _overlay.Show();
    }

    private void OnOverlayClosed()
    {
        RegionOverlay? overlay = _overlay;
        _overlay = null;

        // Not inline: this runs inside the overlay window's own callbacks (key, mouse, resign key).
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            overlay?.Dispose();
            if (_window is not null)
            {
                Refresh();
                NSApplication.SharedApplication.Activate();
                _window.MakeKeyAndOrderFront(null);
            }
        });
    }

    private void SetRegion(NormalizedRect region)
    {
        DisplayInfo? display = RegionDisplay();
        if (display is not null)
        {
            _store.SetRegion(display.Uuid, region);
        }
    }

    private void OnCurveSliding() =>
        _curve.SetCurve(_tipFeel.Value, _clickThreshold.Value, _fullPressureAt.Value);

    private void OnPadSampled(object? sender, double pressure) =>
        _padPressure.StringValue = double.IsNaN(pressure)
            ? "Pressure –"
            : "Pressure " + pressure.ToString("0.00", CultureInfo.CurrentCulture);

    /// <summary>
    /// The display the region applies to, as applied (SettingsSync uses the resolved target): the chosen one, main for
    /// "" or while the chosen one is disconnected (the stored choice is kept), null for All displays or no display.
    /// </summary>
    private DisplayInfo? RegionDisplay() =>
        _store.TargetDisplay == MacSettings.AllDisplays || !_resolver.HasDisplay ? null : _resolver.Target;

    private static NSScreen? ScreenFor(DisplayInfo display)
    {
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

    private void OnWindowClosing()
    {
        _store.Changed -= OnStoreChanged;
        _resolver.DisplaysChanged -= OnDisplaysChanged;
        _resolver.TargetChanged -= OnDisplaysChanged;
        _overlay?.Close();
        _pad.PressureSampled -= OnPadSampled;

        SettingsWindow? window = _window;
        WindowDelegate? windowDelegate = _windowDelegate;
        _window = null;
        _windowDelegate = null;

        // Released after the close completes (we are inside windowWillClose).
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (window is not null)
            {
                window.Delegate = null!;
                window.Dispose();
            }

            windowDelegate?.Dispose();
        });
    }

    // Run at startup can change in System Settings while the window is open; pull it when the user comes back.
    private void OnWindowBecameKey() => RefreshGeneralOnly();

    private void RefreshGeneralOnly()
    {
        if (_window is not null)
        {
            _refreshing = true;
            try
            {
                RefreshGeneral();
            }
            finally
            {
                _refreshing = false;
            }
        }
    }

    // ---------------------------------------------------------------- control helpers

    private NSButton Checkbox(string title, Action<bool> changed)
    {
        NSButton box = null!;
        box = NSButton.CreateCheckbox(title, () =>
        {
            if (!_refreshing)
            {
                changed(box.State == NSCellStateValue.On);
            }
        });
        return box;
    }

    private NSPopUpButton Popup(Action changed, string[]? titles = null)
    {
        var popup = new NSPopUpButton(CGRect.Empty, false);
        if (titles is not null)
        {
            popup.AddItems(titles);
        }

        popup.Activated += (_, _) =>
        {
            if (!_refreshing)
            {
                changed();
            }
        };
        return popup;
    }

    private static SliderRow SpeedSlider(MacSettingKey key, Action<float> commit) =>
        new(key, v => commit((float)v), null, v => v.ToString("0.00", CultureInfo.CurrentCulture) + "×");

    private static string TipFeelText(double v) => (v == 0 ? 0 : v).ToString("+0.00;−0.00;0", CultureInfo.CurrentCulture);

    private static string Percent(double v) => Math.Round(v * 100).ToString(CultureInfo.CurrentCulture) + " %";

    private static void SetCheck(NSButton button, bool on) => button.State = on ? NSCellStateValue.On : NSCellStateValue.Off;

    private static NSTextField Label(string text) => NSTextField.CreateLabel(text);

    private static NSTextField Note(string text)
    {
        NSTextField note = NSTextField.CreateWrappingLabel(text);
        note.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
        note.TextColor = NSColor.SecondaryLabel;
        note.PreferredMaxLayoutWidth = 380;
        return note;
    }

    private static NSView Empty() => NSGridCell.EmptyContentView;

    private static NSStackView Row(params NSView[] views)
    {
        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 8,
        };
        foreach (NSView view in views)
        {
            row.AddArrangedSubview(view);
        }

        return row;
    }

    private static NSGridView Grid(params NSView[][] rows)
    {
        NSGridView grid = NSGridView.Create(rows);
        grid.TranslatesAutoresizingMaskIntoConstraints = false;
        grid.RowSpacing = 10;
        grid.ColumnSpacing = 8;
        grid.RowAlignment = NSGridRowAlignment.FirstBaseline;
        grid.GetColumn(0).X = NSGridCellPlacement.Trailing;
        return grid;
    }

    /// <summary>
    /// A slider, an editable value field and a stepper over one <see cref="SliderSpec"/>. The slider runs 0..1 along the
    /// spec's track; dragging snaps at the default. The slider commits on release or per keyboard step, the field on Return
    /// or focus-out, the stepper and the field's ↑/↓ keys per step. Values always go through the spec.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The views belong to the window, which disposes them; the delegate is released with the row.")]
    private sealed class SliderRow
    {
        private readonly SliderSpec _spec;
        private readonly Func<double, string> _format;
        private readonly bool _percent;
        private readonly Action<double> _commit;
        private readonly Action? _sliding;
        private readonly NSTextField _field;
        private readonly NSStepper _stepper;
        private readonly FieldDelegate _fieldDelegate;
        private double _value;

        public SliderRow(MacSettingKey key, Action<double> commit, Action? sliding, Func<double, string> format, bool percent = false, string? minLabel = null, string? maxLabel = null)
        {
            if (!SettingSliders.TryGet(key, out _spec))
            {
                throw new ArgumentException("No slider spec for " + key, nameof(key));
            }

            _format = format;
            _percent = percent;
            _commit = commit;
            _sliding = sliding;
            _value = _spec.Default;

            _field = new NSTextField
            {
                Editable = true,
                Selectable = true,
                Bezeled = true,
                BezelStyle = NSTextFieldBezelStyle.Square,
                DrawsBackground = true,
                Alignment = NSTextAlignment.Right,
                Font = NSFont.MonospacedDigitSystemFontOfSize(NSFont.SystemFontSize, NSFontWeight.Regular)!,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            _field.WidthAnchor.ConstraintEqualTo(64).Active = true;
            _fieldDelegate = new FieldDelegate(this);
            _field.Delegate = _fieldDelegate;
            _field.Activated += (_, _) => CommitText();

            // The stepper only reports a direction: it rests at 1 inside 0..2 and is put back after each click.
            _stepper = new NSStepper
            {
                MinValue = 0,
                MaxValue = 2,
                Increment = 1,
                DoubleValue = 1,
                ValueWraps = false,
                Autorepeat = true,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            _stepper.Activated += (_, _) =>
            {
                double d = _stepper.DoubleValue - 1;
                _stepper.DoubleValue = 1;
                if (d != 0)
                {
                    Step(d > 0);
                }
            };

            Slider = NSSlider.FromValue(_spec.ToPosition(_spec.Default), 0, 1, () =>
            {
                NSEventType? type = NSApplication.SharedApplication.CurrentEvent?.Type;
                bool mouse = type is NSEventType.LeftMouseDragged or NSEventType.LeftMouseDown or NSEventType.LeftMouseUp;
                if (!mouse && type == NSEventType.KeyDown)
                {
                    // An arrow key moves the thumb by a sliver of the track, which the default snap would swallow: step in value units.
                    _value = Slider!.DoubleValue >= _spec.ToPosition(_value) ? _spec.StepUp(_value) : _spec.StepDown(_value);
                }
                else
                {
                    _value = _spec.FromPosition(Slider!.DoubleValue);
                }

                Slider.DoubleValue = _spec.ToPosition(_value); // the thumb sticks to the snapped value
                _field.StringValue = _format(_value);
                _sliding?.Invoke();
                if (type is not NSEventType.LeftMouseDragged and not NSEventType.LeftMouseDown)
                {
                    _commit(_value);
                }
            });
            Slider.Continuous = true;
            Slider.WidthAnchor.ConstraintEqualTo((nfloat)SliderWidth).Active = true;

            NSStackView controls = Row(Slider, _field, _stepper);
            controls.SetCustomSpacing(4, _field);
            if (minLabel is null && maxLabel is null)
            {
                View = controls;
                return;
            }

            // End labels sit under the track, so the track, field and stepper line up with every other slider row.
            var ends = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
            ends.WidthAnchor.ConstraintEqualTo((nfloat)SliderWidth).Active = true;
            if (minLabel is not null)
            {
                NSTextField min = SmallLabel(minLabel);
                ends.AddSubview(min);
                min.LeadingAnchor.ConstraintEqualTo(ends.LeadingAnchor).Active = true;
                min.TopAnchor.ConstraintEqualTo(ends.TopAnchor).Active = true;
                min.BottomAnchor.ConstraintEqualTo(ends.BottomAnchor).Active = true;
            }

            if (maxLabel is not null)
            {
                NSTextField max = SmallLabel(maxLabel);
                ends.AddSubview(max);
                max.TrailingAnchor.ConstraintEqualTo(ends.TrailingAnchor).Active = true;
                max.TopAnchor.ConstraintEqualTo(ends.TopAnchor).Active = true;
                max.BottomAnchor.ConstraintEqualTo(ends.BottomAnchor).Active = true;
            }

            View = new NSStackView
            {
                Orientation = NSUserInterfaceLayoutOrientation.Vertical,
                Alignment = NSLayoutAttribute.Leading,
                Spacing = 2,
            };
            View.AddArrangedSubview(controls);
            View.AddArrangedSubview(ends);
        }

        public NSSlider Slider { get; }

        public NSStackView View { get; }

        /// <summary>The current value in the spec's units (what a drag or edit would commit).</summary>
        public double Value => _value;

        public void SetValue(double value)
        {
            _value = _spec.Coerce(value);
            Slider.DoubleValue = _spec.ToPosition(value);
            if (_field.CurrentEditor is null) // do not overwrite text the user is typing
            {
                _field.StringValue = _format(_value);
            }
        }

        private void Step(bool up)
        {
            _value = up ? _spec.StepUp(_value) : _spec.StepDown(_value);
            Slider.DoubleValue = _spec.ToPosition(_value);
            _field.StringValue = _format(_value);
            _sliding?.Invoke();
            _commit(_value);
        }

        private void CommitText()
        {
            if (TryParse(_field.StringValue, out double typed))
            {
                double coerced = _spec.Coerce(typed);
                if (coerced != _value)
                {
                    _value = coerced;
                    Slider.DoubleValue = _spec.ToPosition(_value);
                    _sliding?.Invoke();
                    _commit(_value); // the store may clamp; the window's Refresh then shows what it kept
                }
            }

            _field.StringValue = _format(_value); // also reverts invalid text
        }

        private void Revert() => _field.StringValue = _format(_value);

        private bool TryParse(string text, out double value)
        {
            Span<char> buffer = stackalloc char[Math.Min(text.Length, 32)];
            int n = 0;
            foreach (char c in text)
            {
                if (n == buffer.Length)
                {
                    value = 0;
                    return false;
                }

                if (c is '×' or 'x' or 'X' or '%' || char.IsWhiteSpace(c))
                {
                    continue;
                }

                buffer[n++] = c == '−' ? '-' : c;
            }

            ReadOnlySpan<char> number = buffer[..n];
            Span<char> dotted = stackalloc char[n];
            number.Replace(dotted, ',', '.');
            if ((double.TryParse(number, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                    || double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                    || double.TryParse(dotted, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                && double.IsFinite(value))
            {
                if (_percent)
                {
                    value /= 100;
                }

                return true;
            }

            value = 0;
            return false;
        }

        private static NSTextField SmallLabel(string text)
        {
            NSTextField label = NSTextField.CreateLabel(text);
            label.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
            label.TextColor = NSColor.SecondaryLabel;
            label.TranslatesAutoresizingMaskIntoConstraints = false;
            return label;
        }

        private sealed class FieldDelegate(SliderRow owner) : NSTextFieldDelegate
        {
            public override void EditingEnded(NSNotification notification) => owner.CommitText();

            public override bool DoCommandBySelector(NSControl control, NSTextView textView, ObjCRuntime.Selector commandSelector)
            {
                switch (commandSelector.Name)
                {
                    case "moveUp:":
                        owner.CommitText();
                        owner.Step(true);
                        return true;
                    case "moveDown:":
                        owner.CommitText();
                        owner.Step(false);
                        return true;
                    case "cancelOperation:":
                        owner.Revert(); // Esc leaves the field instead of closing the window
                        control.Window?.MakeFirstResponder(null);
                        return true;
                    default:
                        return false;
                }
            }
        }
    }

    /// <summary>⌘W and Esc close it (an accessory app has no main menu to provide them).</summary>
    private sealed class SettingsWindow : NSWindow
    {
        public SettingsWindow(CGRect contentRect)
            : base(contentRect, NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable, NSBackingStore.Buffered, false)
        {
        }

        public override bool PerformKeyEquivalent(NSEvent theEvent)
        {
            NSEventModifierMask modifiers = theEvent.ModifierFlags & NSEventModifierMask.DeviceIndependentModifierFlagsMask;
            if (modifiers == NSEventModifierMask.CommandKeyMask && theEvent.CharactersIgnoringModifiers == "w")
            {
                PerformClose(this);
                return true;
            }

            return base.PerformKeyEquivalent(theEvent);
        }

        [Export("cancelOperation:")]
        public void CancelOperation(NSObject? sender) => PerformClose(this);
    }

    private sealed class WindowDelegate(SettingsWindowController owner) : NSWindowDelegate
    {
        public override void WillClose(NSNotification notification) => owner.OnWindowClosing();

        public override void DidBecomeKey(NSNotification notification) => owner.OnWindowBecameKey();
    }
}
