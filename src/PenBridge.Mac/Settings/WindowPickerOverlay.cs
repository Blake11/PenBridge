using AppKit;
using CoreGraphics;
using Foundation;
using PenBridge.Mac.Input;

namespace PenBridge.Mac.Settings;

/// <summary>
/// Display ▸ Window… (ADR-0036): a click picker over every display. It highlights the app window under the cursor, a
/// click picks it, and Esc, Cancel or switching to another app cancels.
/// </summary>
/// <remarks>
/// <para>One borderless, lightly dimmed window per screen above everything (the dim also keeps clicks from reaching
/// the apps below). The candidates are the window list's normal app windows at open (<see cref="WindowList"/>), front
/// to back, so hovering costs no window-server calls.</para>
/// <para>Main thread only; event-driven (a mouse-moved tracking area per screen). One instance per pick, closed by
/// <see cref="Close"/>.</para>
/// </remarks>
internal sealed class WindowPickerOverlay : IDisposable
{
    private const float DimAlpha = 0.15f;

    private readonly List<WindowListEntry> _candidates;
    private readonly Action<WindowListEntry> _picked;
    private readonly Action _closed;
    private readonly List<PickerWindow> _windows = [];
    private readonly double _primaryHeight;
    private NSObject? _resignObserver;
    private WindowListEntry? _hover;
    private bool _closedRaised;

    /// <param name="candidates">The pickable windows, front to back, in global points (top-left origin).</param>
    /// <param name="picked">Called with the clicked window (then the picker closes).</param>
    /// <param name="closed">Called once when the picker closes, picked or cancelled.</param>
    public WindowPickerOverlay(List<WindowListEntry> candidates, Action<WindowListEntry> picked, Action closed)
    {
        _candidates = candidates;
        _picked = picked;
        _closed = closed;
        NSScreen[] screens = NSScreen.Screens;

        // AppKit's global coordinates have a bottom-left origin on the primary screen (the first one, with the menu
        // bar); the window list's have a top-left one there.
        _primaryHeight = screens.Length > 0 ? screens[0].Frame.Height : 0;
        foreach (NSScreen screen in screens)
        {
            CGRect frame = screen.Frame;
            var window = new PickerWindow(this, frame)
            {
                Level = NSWindowLevel.ScreenSaver,
                IsOpaque = false,
                BackgroundColor = NSColor.Clear,
                HasShadow = false,
                CollectionBehavior = NSWindowCollectionBehavior.CanJoinAllSpaces | NSWindowCollectionBehavior.FullScreenAuxiliary,
                Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
            };
            window.SetFrame(frame, display: false);
            var view = new PickerView(this, frame);
            window.ContentView = view;
            window.InitialFirstResponder = view;
            _windows.Add(window);
        }
    }

    public void Show()
    {
        if (_windows.Count == 0)
        {
            Close();
            return;
        }

        // Opened from the menu bar the accessory app isn't active; without activation no window becomes key (no Esc).
#pragma warning disable CA1422 // Deprecated, but the cooperative form does not take focus from the frontmost app.
        NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
#pragma warning restore CA1422
        PickerWindow key = _windows[0];
        CGPoint mouse = NSEvent.CurrentMouseLocation;
        foreach (PickerWindow window in _windows)
        {
            window.OrderFrontRegardless();
            if (window.Frame.Contains(mouse))
            {
                key = window;
            }
        }

        key.MakeKeyAndOrderFront(null);
        key.MakeFirstResponder(key.ContentView);

        // Switching to another app (Cmd-Tab, a click that activates something) must not leave the displays dimmed.
        _resignObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSApplication.DidResignActiveNotification, _ => Close());
        UpdateHover();
    }

    public void Close()
    {
        if (_closedRaised)
        {
            return;
        }

        _closedRaised = true;
        RemoveResignObserver();
        foreach (PickerWindow window in _windows)
        {
            window.OrderOut(null);
            window.Close();
        }

        _closed();
    }

    public void Dispose()
    {
        Close();
        foreach (PickerWindow window in _windows)
        {
            NSView? view = window.ContentView;
            window.ContentView = new NSView();
            view?.Dispose();
            window.Dispose();
        }

        _windows.Clear();
    }

    private void RemoveResignObserver()
    {
        if (_resignObserver is not null)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(_resignObserver);
            _resignObserver.Dispose();
            _resignObserver = null;
        }
    }

    private void UpdateHover()
    {
        WindowListEntry? hover = CandidateAt(NSEvent.CurrentMouseLocation);
        if (hover != _hover)
        {
            _hover = hover;
            foreach (PickerWindow window in _windows)
            {
                window.ContentView!.NeedsDisplay = true;
            }
        }
    }

    private void Pick()
    {
        if (_closedRaised)
        {
            return;
        }

        if (CandidateAt(NSEvent.CurrentMouseLocation) is { } picked)
        {
            _picked(picked);
            Close();
        }
    }

    // The frontmost candidate under an AppKit global point.
    private WindowListEntry? CandidateAt(CGPoint appKitPoint)
    {
        double x = appKitPoint.X;
        double y = _primaryHeight - appKitPoint.Y;
        foreach (WindowListEntry c in _candidates)
        {
            if (x >= c.Bounds.X && y >= c.Bounds.Y && x < c.Bounds.X + c.Bounds.Width && y < c.Bounds.Y + c.Bounds.Height)
            {
                return c;
            }
        }

        return null;
    }

    // A candidate's frame in a screen window's view (bottom-left origin, y up), from global top-left points.
    private CGRect InView(WindowListEntry c, CGRect screenFrame) =>
        new(
            c.Bounds.X - screenFrame.X,
            _primaryHeight - c.Bounds.Y - c.Bounds.Height - screenFrame.Y,
            c.Bounds.Width,
            c.Bounds.Height);

    private sealed class PickerWindow : NSWindow
    {
        private readonly WindowPickerOverlay _owner;

        public PickerWindow(WindowPickerOverlay owner, CGRect frame)
            : base(frame, NSWindowStyle.Borderless, NSBackingStore.Buffered, false)
        {
            _owner = owner;
        }

        public override bool CanBecomeKeyWindow => true;

        public override bool CanBecomeMainWindow => false;

        [Export("cancelOperation:")]
        public void CancelOperation(NSObject? sender) => _owner.Close();
    }

    private sealed class FirstMouseButton : NSButton
    {
        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;
    }

    private sealed class PickerView : NSView
    {
        private const ushort EscapeKeyCode = 53;
        private const double HintHeightFraction = 0.12;
        private readonly WindowPickerOverlay _owner;
        private readonly CGRect _screenFrame;

        public PickerView(WindowPickerOverlay owner, CGRect screenFrame)
            : base(new CGRect(CGPoint.Empty, screenFrame.Size))
        {
            _owner = owner;
            _screenFrame = screenFrame;
            AddTrackingArea(new NSTrackingArea(
                Bounds,
                NSTrackingAreaOptions.MouseMoved | NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveAlways | NSTrackingAreaOptions.InVisibleRect,
                this,
                null));

            // A visible way out that works even without a key window (no Esc).
            var cancel = new FirstMouseButton { Title = "Cancel", BezelStyle = NSBezelStyle.Push };
            cancel.Activated += (_, _) => _owner.Close();
            cancel.SizeToFit();
            CGSize size = cancel.Frame.Size;
            cancel.Frame = new CGRect((Bounds.Width - size.Width) / 2, (Bounds.Height * HintHeightFraction) - size.Height - 16, size.Width, size.Height);
            AddSubview(cancel);
        }

        public override bool AcceptsFirstResponder() => true;

        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

        public override void MouseMoved(NSEvent theEvent) => _owner.UpdateHover();

        public override void MouseEntered(NSEvent theEvent) => _owner.UpdateHover();

        public override void MouseDown(NSEvent theEvent) => _owner.Pick();

        public override void RightMouseDown(NSEvent theEvent) => _owner.Close();

        public override void KeyDown(NSEvent theEvent)
        {
            if (theEvent.KeyCode == EscapeKeyCode)
            {
                _owner.Close();
                return;
            }

            base.KeyDown(theEvent);
        }

        public override void DrawRect(CGRect dirtyRect)
        {
            CGRect bounds = Bounds;
            NSColor.Black.ColorWithAlphaComponent(DimAlpha).SetFill();
            NSGraphics.RectFill(bounds, NSCompositingOperation.Copy);

            if (_owner._hover is { } hover)
            {
                CGRect rect = _owner.InView(hover, _screenFrame);
                if (rect.IntersectsWith(bounds))
                {
                    NSColor accent = NSColor.ControlAccent;
                    accent.ColorWithAlphaComponent(0.25f).SetFill();
                    NSGraphics.RectFill(rect, NSCompositingOperation.Copy);
                    using NSBezierPath outline = NSBezierPath.FromRect(rect.Inset(1.5f, 1.5f));
                    outline.LineWidth = 3;
                    accent.SetStroke();
                    outline.Stroke();
                    DrawLabel(hover.OwnerName, rect);
                }
            }

            DrawHint(bounds);
        }

        private static void DrawLabel(string text, CGRect window)
        {
            if (text.Length == 0)
            {
                return;
            }

            var attributes = new NSStringAttributes
            {
                Font = NSFont.SystemFontOfSize(16, NSFontWeight.Semibold),
                ForegroundColor = NSColor.White,
            };
            using var label = new NSAttributedString(text, attributes);
            CGSize size = label.Size;
            var origin = new CGPoint(window.GetMidX() - (size.Width / 2), window.GetMidY() - (size.Height / 2));
            Pill(new CGRect(origin, size));
            label.DrawAtPoint(origin);
        }

        private static void DrawHint(CGRect bounds)
        {
            var attributes = new NSStringAttributes
            {
                Font = NSFont.SystemFontOfSize(18, NSFontWeight.Medium),
                ForegroundColor = NSColor.White,
            };
            using var label = new NSAttributedString("Click a window to map the pen to it. Esc or Cancel to keep the current target.", attributes);
            CGSize size = label.Size;
            var origin = new CGPoint((bounds.Width - size.Width) / 2, bounds.Height * HintHeightFraction);
            Pill(new CGRect(origin, size));
            label.DrawAtPoint(origin);
        }

        private static void Pill(CGRect text)
        {
            CGRect pill = text.Inset(-14, -8);
            using NSBezierPath background = NSBezierPath.FromRoundedRect(pill, 10, 10);
            NSColor.Black.ColorWithAlphaComponent(0.6f).SetFill();
            background.Fill();
        }
    }
}
