using AppKit;
using CoreGraphics;
using Foundation;
using PenBridge.Core.Input;

namespace PenBridge.Mac.Settings;

/// <summary>
/// "Set region…" (ADR-0031): dims one display in a borderless full-screen window and lets the user drag a rectangle.
/// Releasing the mouse commits it as a rect normalized to the display (top-left origin, y down); Esc, or the overlay
/// losing focus, cancels. With an aspect ratio the rectangle keeps it (and is shrunk to fit the display).
/// </summary>
/// <remarks>Main thread only. Event-driven; one instance per drag session, closed by <see cref="Close"/>.</remarks>
internal sealed class RegionOverlay : IDisposable
{
    /// <summary>Smallest accepted rectangle side, in points; a smaller drag (e.g. a click) is ignored.</summary>
    private const double MinSide = 24;

    private readonly OverlayWindow _window;
    private readonly OverlayView _view;
    private readonly Action<NormalizedRect> _committed;
    private readonly Action _closed;
    private bool _closedRaised;

    /// <param name="screen">The display to cover.</param>
    /// <param name="current">The display's current region, shown as an outline until the user drags.</param>
    /// <param name="aspect">Width / height to keep, or null for a free rectangle.</param>
    /// <param name="committed">Called with the new region (then the overlay closes).</param>
    /// <param name="closed">Called once when the overlay closes, committed or cancelled.</param>
    public RegionOverlay(NSScreen screen, NormalizedRect current, double? aspect, Action<NormalizedRect> committed, Action closed)
    {
        ArgumentNullException.ThrowIfNull(screen);
        _committed = committed;
        _closed = closed;
        CGRect frame = screen.Frame;
        _window = new OverlayWindow(this, frame)
        {
            Level = NSWindowLevel.ScreenSaver,
            IsOpaque = false,
            BackgroundColor = NSColor.Clear,
            HasShadow = false,
            CollectionBehavior = NSWindowCollectionBehavior.CanJoinAllSpaces | NSWindowCollectionBehavior.FullScreenAuxiliary,
        };
        _window.SetFrame(frame, display: true);
        _view = new OverlayView(this, new CGRect(CGPoint.Empty, frame.Size), current, aspect is > 0 && double.IsFinite(aspect.Value) ? aspect : null);
        _window.ContentView = _view;
        _window.InitialFirstResponder = _view;
    }

    public void Show()
    {
        NSApplication.SharedApplication.Activate();
        _window.MakeKeyAndOrderFront(null);
        _window.MakeFirstResponder(_view);
    }

    public void Close()
    {
        if (_closedRaised)
        {
            return;
        }

        _closedRaised = true;
        _window.OrderOut(null);
        _window.Close();
        _closed();
    }

    public void Dispose()
    {
        Close();
        _window.ContentView = new NSView();
        _view.Dispose();
        _window.Dispose();
    }

    private void Commit(CGRect rect, CGSize size)
    {
        // View coordinates have a bottom-left origin; the stored region is top-left, y down.
        double x = Math.Clamp(rect.X / size.Width, 0, 1);
        double w = Math.Clamp(rect.Width / size.Width, 0, 1 - x);
        double top = (size.Height - rect.GetMaxY()) / size.Height;
        double y = Math.Clamp(top, 0, 1);
        double h = Math.Clamp(rect.Height / size.Height, 0, 1 - y);
        if (w > 0 && h > 0)
        {
            _committed(new NormalizedRect(x, y, w, h));
        }

        Close();
    }

    private sealed class OverlayWindow : NSWindow
    {
        private readonly RegionOverlay _owner;

        public OverlayWindow(RegionOverlay owner, CGRect frame)
            : base(frame, NSWindowStyle.Borderless, NSBackingStore.Buffered, false)
        {
            _owner = owner;
        }

        public override bool CanBecomeKeyWindow => true;

        public override bool CanBecomeMainWindow => false;

        [Export("cancelOperation:")]
        public void CancelOperation(NSObject? sender) => _owner.Close();

        // Switching away (Cmd-Tab, another Space) must not leave a dimmed display behind.
        public override void ResignKeyWindow()
        {
            base.ResignKeyWindow();
            _owner.Close();
        }
    }

    private sealed class FirstMouseButton : NSButton
    {
        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;
    }

    private sealed class OverlayView : NSView
    {
        private const ushort EscapeKeyCode = 53;
        private const double HintHeightFraction = 0.12;
        private readonly RegionOverlay _owner;
        private readonly NormalizedRect _current;
        private readonly double? _aspect;
        private CGPoint _anchor;
        private CGRect? _selection;

        public OverlayView(RegionOverlay owner, CGRect frame, NormalizedRect current, double? aspect)
            : base(frame)
        {
            _owner = owner;
            _current = current;
            _aspect = aspect;

            // A visible way out that works even if the app could not be activated (no key window, so no Esc): its
            // first click is accepted on the inactive overlay.
            var cancel = new FirstMouseButton { Title = "Cancel", BezelStyle = NSBezelStyle.Push };
            cancel.Activated += (_, _) => _owner.Close();
            cancel.SizeToFit();
            CGSize size = cancel.Frame.Size;
            cancel.Frame = new CGRect((frame.Width - size.Width) / 2, (frame.Height * HintHeightFraction) - size.Height - 16, size.Width, size.Height);
            cancel.AutoresizingMask = NSViewResizingMask.MinXMargin | NSViewResizingMask.MaxXMargin;
            AddSubview(cancel);
        }

        public override bool AcceptsFirstResponder() => true;

        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

        public override void ResetCursorRects() => AddCursorRect(Bounds, NSCursor.CrosshairCursor);

        public override void KeyDown(NSEvent theEvent)
        {
            if (theEvent.KeyCode == EscapeKeyCode)
            {
                _owner.Close();
                return;
            }

            base.KeyDown(theEvent);
        }

        public override void MouseDown(NSEvent theEvent)
        {
            _anchor = Clamp(ConvertPointFromView(theEvent.LocationInWindow, null));
            _selection = new CGRect(_anchor, CGSize.Empty);
            NeedsDisplay = true;
        }

        public override void MouseDragged(NSEvent theEvent)
        {
            _selection = Selection(Clamp(ConvertPointFromView(theEvent.LocationInWindow, null)));
            NeedsDisplay = true;
        }

        public override void MouseUp(NSEvent theEvent)
        {
            CGRect rect = Selection(Clamp(ConvertPointFromView(theEvent.LocationInWindow, null)));
            if (rect.Width < MinSide || rect.Height < MinSide)
            {
                _selection = null;
                NeedsDisplay = true;
                return;
            }

            _owner.Commit(rect, Bounds.Size);
        }

        public override void DrawRect(CGRect dirtyRect)
        {
            CGRect bounds = Bounds;

            // The whole display is dimmed except the rectangle being dragged; before the drag the current region is
            // only outlined (dashed), so a full-display region still shows the dimming.
            using (NSBezierPath dim = NSBezierPath.FromRect(bounds))
            {
                if (_selection is { } hole)
                {
                    using NSBezierPath holePath = NSBezierPath.FromRect(hole);
                    dim.AppendPath(holePath);
                    dim.WindingRule = NSWindingRule.EvenOdd;
                }

                NSColor.Black.ColorWithAlphaComponent(0.5f).SetFill();
                dim.Fill();
            }

            CGRect highlight = _selection ?? CurrentRegionRect().Inset(2, 2);
            if (highlight.Width > 0 && highlight.Height > 0)
            {
                using NSBezierPath outline = NSBezierPath.FromRect(highlight);
                outline.LineWidth = 2;
                if (_selection is null)
                {
                    outline.SetLineDash([6, 4], 0);
                }

                NSColor.White.SetStroke();
                outline.Stroke();
            }

            DrawHint(bounds);
        }

        private void DrawHint(CGRect bounds)
        {
            string text = _selection is { } s
                ? Math.Round(s.Width).ToString(System.Globalization.CultureInfo.CurrentCulture) + " × " + Math.Round(s.Height).ToString(System.Globalization.CultureInfo.CurrentCulture) + " pt"
                : "Drag a rectangle to set the region" + (_aspect is null ? string.Empty : " (aspect locked)") + ". Esc or Cancel to keep the current one.";
            var attributes = new NSStringAttributes
            {
                Font = NSFont.SystemFontOfSize(18, NSFontWeight.Medium),
                ForegroundColor = NSColor.White,
            };
            using var label = new NSAttributedString(text, attributes);
            CGSize size = label.Size;
            var origin = new CGPoint((bounds.Width - size.Width) / 2, bounds.Height * HintHeightFraction);
            CGRect pill = new CGRect(origin, size).Inset(-14, -8);
            using (NSBezierPath background = NSBezierPath.FromRoundedRect(pill, 10, 10))
            {
                NSColor.Black.ColorWithAlphaComponent(0.6f).SetFill();
                background.Fill();
            }

            label.DrawAtPoint(origin);
        }

        private CGRect CurrentRegionRect()
        {
            CGSize size = Bounds.Size;
            double h = _current.Height * size.Height;
            return new CGRect(
                _current.X * size.Width,
                size.Height - (_current.Y * size.Height) - h,
                _current.Width * size.Width,
                h);
        }

        private CGPoint Clamp(CGPoint p) =>
            new(Math.Clamp(p.X, 0, Bounds.Width), Math.Clamp(p.Y, 0, Bounds.Height));

        // The rectangle from the anchor to p; with an aspect ratio, the larger side wins and the result is shrunk to
        // stay on the display, keeping the anchor corner.
        private CGRect Selection(CGPoint p)
        {
            double dx = p.X - _anchor.X;
            double dy = p.Y - _anchor.Y;
            double w = Math.Abs(dx);
            double h = Math.Abs(dy);
            if (_aspect is { } ratio)
            {
                w = Math.Max(w, h * ratio);
                h = w / ratio;
                double roomX = dx >= 0 ? Bounds.Width - _anchor.X : _anchor.X;
                double roomY = dy >= 0 ? Bounds.Height - _anchor.Y : _anchor.Y;
                double scale = Math.Min(1, Math.Min(w > 0 ? roomX / w : 1, h > 0 ? roomY / h : 1));
                w *= scale;
                h *= scale;
            }

            double x = dx >= 0 ? _anchor.X : _anchor.X - w;
            double y = dy >= 0 ? _anchor.Y : _anchor.Y - h;
            return new CGRect(x, y, w, h);
        }
    }
}
