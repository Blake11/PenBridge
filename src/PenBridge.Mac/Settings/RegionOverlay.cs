using AppKit;
using CoreGraphics;
using Foundation;
using PenBridge.Core.Input;
using PenBridge.Core.Settings;

namespace PenBridge.Mac.Settings;

/// <summary>
/// The region editor (ADR-0031, ADR-0035 amended by #243), like macOS's screenshot/record selection: a borderless
/// full-screen window over one display that opens with the current region selected and tints the rest. The selection
/// moves by its inside, resizes by 8 handles, and a drag outside it draws a new one; a size readout shows while
/// dragging. A floating bar offers Full display, the aspect lock, Cancel and Done. Done (or Return) commits the
/// selection as a rect normalized to the display (top-left origin, y down) together with the bar's aspect lock; Esc,
/// Cancel or the overlay losing focus cancels, and nothing (region or lock) applies before Done. The geometry is Core's <see cref="RegionEditGeometry"/>.
/// </summary>
/// <remarks>Main thread only. Event-driven (no timers); one instance per edit session, closed by <see cref="Close"/>.</remarks>
internal sealed class RegionOverlay : IDisposable
{
    private readonly OverlayWindow _window;
    private readonly OverlayView _view;
    private readonly Action<NormalizedRect, bool> _committed;
    private readonly Action _closed;
    private bool _closedRaised;

    /// <param name="screen">The display to cover.</param>
    /// <param name="current">The display's current region, selected when the editor opens.</param>
    /// <param name="aspect">Width / height the aspect lock keeps (the phone surface's, else the display's).</param>
    /// <param name="aspectLock">Whether the aspect lock starts on (then the selection is fitted to the ratio).</param>
    /// <param name="committed">
    /// Called on Done with the new region and the bar's aspect-lock state (then the overlay closes). The toggle is local
    /// to the editor until then.
    /// </param>
    /// <param name="closed">Called once when the overlay closes, committed or cancelled.</param>
    public RegionOverlay(
        NSScreen screen,
        NormalizedRect current,
        double aspect,
        bool aspectLock,
        Action<NormalizedRect, bool> committed,
        Action closed)
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

            // A non-opaque window passes clicks through its fully transparent pixels unless this is set explicitly:
            // the clear selection would let a move-drag fall through to the app below.
            IgnoresMouseEvents = false,
            CollectionBehavior = NSWindowCollectionBehavior.CanJoinAllSpaces | NSWindowCollectionBehavior.FullScreenAuxiliary,
        };

        // The tint is dark in either system appearance: keep the bar's controls dark too, else in light mode they draw
        // light-on-dark and nearly vanish.
        _window.Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua);
        _window.SetFrame(frame, display: true);
        double ratio = aspect > 0 && double.IsFinite(aspect) ? aspect : frame.Width / frame.Height;
        _view = new OverlayView(this, new CGRect(CGPoint.Empty, frame.Size), current, ratio, aspectLock);
        _window.ContentView = _view;
        _window.InitialFirstResponder = _view;
    }

    public void Show()
    {
        // Opened from the menu bar the accessory app isn't active, and the cooperative Activate() is ignored while
        // another app holds focus; without activation the overlay never becomes key (no Esc, no Return).
#pragma warning disable CA1422 // Deprecated, but the cooperative form does not take focus from the frontmost app.
        NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
#pragma warning restore CA1422
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
        using (var blank = new NSView())
        {
            _window.ContentView = blank; // detach the view before its wrapper goes
        }

        _view.Dispose();
        _window.Dispose();
    }

    /// <summary>
    /// Commits a valid selection (as the store accepts it: both sides at least <see cref="RegionEditGeometry.MinSide"/>
    /// and <see cref="MacSettings.MinRegionSize"/> of the display) and closes. An invalid one keeps the editor open.
    /// </summary>
    /// <returns>False if the selection was refused.</returns>
    private bool Commit(ViewRect selection, CGSize size, bool aspectLock)
    {
        if (_closedRaised)
        {
            return true;
        }

        NormalizedRect region = RegionEditGeometry.ToRegion(selection, size.Width, size.Height);
        if (!RegionEditGeometry.IsBigEnough(selection, size.Width, size.Height)
            || !(region.Width >= MacSettings.MinRegionSize && region.Height >= MacSettings.MinRegionSize))
        {
            return false;
        }

        _committed(region, aspectLock);
        Close();
        return true;
    }

    private static CGRect ToCG(ViewRect r) => new(r.X, r.Y, r.Width, r.Height);

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

        // Switching away (Cmd-Tab, another Space) must not leave a tinted display behind.
        public override void ResignKeyWindow()
        {
            base.ResignKeyWindow();
            _owner.Close();
        }
    }

    // The overlay may not be active (no key window): a control's first click must still work.
    private sealed class FirstMouseButton : NSButton
    {
        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;
    }

    /// <summary>The floating bar: a dark rounded panel holding the controls; clicks on its background do nothing.</summary>
    private sealed class BarView : NSView
    {
        public BarView(CGRect frame)
            : base(frame)
        {
        }

        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

        public override void MouseDown(NSEvent theEvent)
        {
            // Swallowed, so a click between the buttons doesn't start a new selection underneath.
        }

        public override void DrawRect(CGRect dirtyRect)
        {
            using NSBezierPath background = NSBezierPath.FromRoundedRect(Bounds, 10, 10);
            NSColor.FromWhite(0.12f, 0.92f).SetFill();
            background.Fill();
        }
    }

    private sealed class OverlayView : NSView
    {
        private const ushort EscapeKeyCode = 53;
        private const ushort ReturnKeyCode = 36;
        private const ushort EnterKeyCode = 76;
        private const double HandleTolerance = 8;
        private const double HandleRadius = 5;
        private const double BarGap = 14;

        private readonly RegionOverlay _owner;
        private readonly double _ratio;
        private readonly BarView _bar;
        private readonly NSButton _lock;
        private readonly NSCursor?[] _resizeCursors = new NSCursor?[10];
        private ViewRect _selection;
        private ViewRect _dragStartSelection;
        private CGPoint _dragStart;
        private RegionHandle _dragHandle;
        private DragKind _drag;
        private NSTrackingArea? _tracking;

        public OverlayView(RegionOverlay owner, CGRect frame, NormalizedRect current, double ratio, bool aspectLock)
            : base(frame)
        {
            _owner = owner;
            _ratio = ratio;
            ViewRect hole = RegionTintRule.HoleInView(current, frame.Width, frame.Height);
            _selection = hole.Width > 0 && hole.Height > 0 ? hole : new ViewRect(0, 0, frame.Width, frame.Height);
            if (aspectLock)
            {
                // A region stored with the lock off (or another phone) may not have the ratio: show what Done would keep.
                _selection = RegionEditGeometry.ApplyAspect(_selection, ratio, frame.Width, frame.Height);
            }

            NSButton full = Button("Full display", () => SetSelection(new ViewRect(0, 0, Bounds.Width, Bounds.Height)));
            full.ToolTip = "Select the whole display.";
            _lock = Button("Keep phone aspect", OnLockToggled);
            _lock.SetButtonType(NSButtonType.Switch);
            _lock.State = aspectLock ? NSCellStateValue.On : NSCellStateValue.Off;
            _lock.ToolTip = "Keep the selection at the phone's aspect ratio.";
            NSButton cancel = Button("Cancel", () => _owner.Close());
            NSButton done = Button("Done", Done);
            done.KeyEquivalent = "\r";

            NSStackView stack = NSStackView.FromViews([full, _lock, Spacer(), cancel, done]);
            stack.Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
            stack.Spacing = 10;
            stack.EdgeInsets = new NSEdgeInsets(8, 12, 8, 12);
            CGSize size = stack.FittingSize;
            _bar = new BarView(new CGRect(CGPoint.Empty, size));
            stack.Frame = _bar.Bounds;
            stack.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;
            _bar.AddSubview(stack);
            AddSubview(_bar);
            PlaceBar();
        }

        private enum DragKind
        {
            None,
            Move,
            Resize,
            Draw,
        }

        private double? Aspect => _lock.State == NSCellStateValue.On ? _ratio : null;

        public override bool AcceptsFirstResponder() => true;

        public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

        public override void UpdateTrackingAreas()
        {
            base.UpdateTrackingAreas();
            if (_tracking is null)
            {
                // Mouse-moved and cursor-update events only while the pointer moves over the overlay: no polling.
                _tracking = new NSTrackingArea(
                    CGRect.Empty,
                    NSTrackingAreaOptions.MouseMoved | NSTrackingAreaOptions.CursorUpdate | NSTrackingAreaOptions.ActiveAlways | NSTrackingAreaOptions.InVisibleRect,
                    this,
                    null);
                AddTrackingArea(_tracking);
            }
        }

        public override void CursorUpdate(NSEvent theEvent) => UpdateCursor(Point(theEvent));

        public override void MouseMoved(NSEvent theEvent) => UpdateCursor(Point(theEvent));

        public override void KeyDown(NSEvent theEvent)
        {
            switch (theEvent.KeyCode)
            {
                case EscapeKeyCode:
                    _owner.Close();
                    return;
                case ReturnKeyCode or EnterKeyCode:
                    Done();
                    return;
                default:
                    base.KeyDown(theEvent);
                    return;
            }
        }

        public override void MouseDown(NSEvent theEvent)
        {
            CGPoint p = Point(theEvent);
            _dragStart = p;
            _dragStartSelection = _selection;
            _dragHandle = RegionEditGeometry.HitTest(_selection, p.X, p.Y, HandleTolerance);
            _drag = _dragHandle switch
            {
                RegionHandle.None => DragKind.Draw,
                RegionHandle.Inside => DragKind.Move,
                _ => DragKind.Resize,
            };
            if (_drag == DragKind.Move)
            {
                NSCursor.ClosedHandCursor.Set();
            }
            else if (_drag == DragKind.Draw)
            {
                // Nothing selected until the drag has a size; a plain click keeps the old selection (see MouseUp).
                _selection = new ViewRect(p.X, p.Y, 0, 0);
                NeedsDisplay = true;
            }
        }

        public override void MouseDragged(NSEvent theEvent)
        {
            CGPoint p = Point(theEvent);
            double w = Bounds.Width;
            double h = Bounds.Height;
            _selection = _drag switch
            {
                DragKind.Move => RegionEditGeometry.Move(_dragStartSelection, p.X - _dragStart.X, p.Y - _dragStart.Y, w, h),
                DragKind.Resize => RegionEditGeometry.Resize(_dragStartSelection, _dragHandle, p.X - _dragStart.X, p.Y - _dragStart.Y, w, h, Aspect),
                DragKind.Draw => RegionEditGeometry.Draw(_dragStart.X, _dragStart.Y, p.X, p.Y, w, h, Aspect),
                _ => _selection,
            };
            _bar.Hidden = _drag == DragKind.Draw; // a new selection has no bar until it's drawn
            PlaceBar();
            NeedsDisplay = true;
        }

        public override void MouseUp(NSEvent theEvent)
        {
            if (_drag == DragKind.Draw && !RegionEditGeometry.IsBigEnough(_selection, Bounds.Width, Bounds.Height))
            {
                _selection = _dragStartSelection; // a click or a tiny drag
            }

            _drag = DragKind.None;
            _bar.Hidden = false;
            PlaceBar();
            NeedsDisplay = true;
            UpdateCursor(Point(theEvent));
        }

        public override void DrawRect(CGRect dirtyRect)
        {
            CGRect bounds = Bounds;
            CGRect selection = ToCG(_selection);

            // Everything outside the selection is tinted (a full-display selection leaves no tint, like macOS).
            using (NSBezierPath dim = NSBezierPath.FromRect(bounds))
            {
                if (selection.Width > 0 && selection.Height > 0)
                {
                    using NSBezierPath holePath = NSBezierPath.FromRect(selection);
                    dim.AppendPath(holePath);
                    dim.WindingRule = NSWindingRule.EvenOdd;
                }

                NSColor.Black.ColorWithAlphaComponent(0.45f).SetFill();
                dim.Fill();
            }

            if (selection.Width > 0 && selection.Height > 0)
            {
                // Imperceptible, but not fully transparent, so the window server also routes clicks inside the
                // selection to the overlay (belt and braces with IgnoresMouseEvents = false).
                NSColor.Black.ColorWithAlphaComponent(0.01f).SetFill();
                NSGraphics.RectFill(selection);
            }

            if (selection.Width <= 0 || selection.Height <= 0)
            {
                return;
            }

            // Kept inside the display so a full-display selection still shows its frame and handles.
            CGRect frame = selection.Inset(1, 1);
            using (NSBezierPath outline = NSBezierPath.FromRect(frame))
            {
                outline.LineWidth = 1.5f;
                NSColor.White.SetStroke();
                outline.Stroke();
            }

            if (_drag != DragKind.Draw)
            {
                DrawHandles(selection);
            }

            if (_drag != DragKind.None)
            {
                DrawReadout(selection);
            }
        }

        private void DrawHandles(CGRect r)
        {
            double r0 = HandleRadius + 1;
            double left = Math.Max(r.X, r0);
            double right = Math.Min(r.GetMaxX(), Bounds.Width - r0);
            double bottom = Math.Max(r.Y, r0);
            double top = Math.Min(r.GetMaxY(), Bounds.Height - r0);
            double midX = (left + right) / 2;
            double midY = (bottom + top) / 2;
            Span<(double X, double Y)> points =
            [
                (left, bottom), (midX, bottom), (right, bottom), (right, midY),
                (right, top), (midX, top), (left, top), (left, midY),
            ];
            foreach ((double x, double y) in points)
            {
                using NSBezierPath dot = NSBezierPath.FromOvalInRect(new CGRect(x - HandleRadius, y - HandleRadius, HandleRadius * 2, HandleRadius * 2));
                dot.LineWidth = 1;
                NSColor.White.SetFill();
                dot.Fill();
                NSColor.FromWhite(0, 0.5f).SetStroke();
                dot.Stroke();
            }
        }

        private static void DrawReadout(CGRect selection)
        {
            System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
            string text = Math.Round(selection.Width).ToString(culture) + " × " + Math.Round(selection.Height).ToString(culture) + " pt";
            var attributes = new NSStringAttributes
            {
                Font = NSFont.MonospacedDigitSystemFontOfSize(14, NSFontWeight.Medium),
                ForegroundColor = NSColor.White,
            };
            using var label = new NSAttributedString(text, attributes);
            CGSize size = label.Size;
            var origin = new CGPoint(selection.GetMidX() - (size.Width / 2), selection.GetMidY() - (size.Height / 2));
            CGRect pill = new CGRect(origin, size).Inset(-10, -5);
            using (NSBezierPath background = NSBezierPath.FromRoundedRect(pill, 7, 7))
            {
                NSColor.Black.ColorWithAlphaComponent(0.65f).SetFill();
                background.Fill();
            }

            label.DrawAtPoint(origin);
        }

        private static FirstMouseButton Button(string title, Action activated)
        {
            var button = new FirstMouseButton { Title = title, BezelStyle = NSBezelStyle.Push };
            button.Activated += (_, _) => activated();
            return button;
        }

        private static NSView Spacer()
        {
            var spacer = new NSView(new CGRect(0, 0, 8, 1));
            spacer.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
            return spacer;
        }

        private CGPoint Point(NSEvent e) => ConvertPointFromView(e.LocationInWindow, null);

        private void OnLockToggled()
        {
            // Local to the editor: the setting is stored with the region on Done.
            if (_lock.State == NSCellStateValue.On)
            {
                SetSelection(RegionEditGeometry.ApplyAspect(_selection, _ratio, Bounds.Width, Bounds.Height));
            }
        }

        private void Done()
        {
            if (_drag != DragKind.None || !_owner.Commit(_selection, Bounds.Size, _lock.State == NSCellStateValue.On))
            {
                AppKitFramework.NSBeep(); // stays open: the selection isn't a valid region (or a drag is still going)
            }
        }

        private void SetSelection(ViewRect selection)
        {
            _selection = selection;
            PlaceBar();
            NeedsDisplay = true;
        }

        private void PlaceBar()
        {
            CGSize size = _bar.Frame.Size;
            ViewRect r = RegionEditGeometry.PlaceBar(_selection, size.Width, size.Height, Bounds.Width, Bounds.Height, BarGap);
            _bar.SetFrameOrigin(new CGPoint(Math.Round(r.X), Math.Round(r.Y)));
        }

        private void UpdateCursor(CGPoint p)
        {
            if (_drag == DragKind.Move)
            {
                NSCursor.ClosedHandCursor.Set();
                return;
            }

            if (!_bar.Hidden && _bar.Frame.Contains(p))
            {
                NSCursor.ArrowCursor.Set();
                return;
            }

            RegionHandle handle = _drag == DragKind.Resize ? _dragHandle : RegionEditGeometry.HitTest(_selection, p.X, p.Y, HandleTolerance);
            (handle switch
            {
                RegionHandle.None => NSCursor.CrosshairCursor,
                RegionHandle.Inside => NSCursor.OpenHandCursor,
                _ => ResizeCursor(handle),
            }).Set();
        }

        private NSCursor ResizeCursor(RegionHandle handle)
        {
            if (_resizeCursors[(int)handle] is { } cached)
            {
                return cached;
            }

            NSCursor cursor;
            if (OperatingSystem.IsMacOSVersionAtLeast(15))
            {
                NSCursorFrameResizePosition position = handle switch
                {
                    RegionHandle.Left => NSCursorFrameResizePosition.Left,
                    RegionHandle.Right => NSCursorFrameResizePosition.Right,
                    RegionHandle.Top => NSCursorFrameResizePosition.Top,
                    RegionHandle.Bottom => NSCursorFrameResizePosition.Bottom,
                    RegionHandle.TopLeft => NSCursorFrameResizePosition.TopLeft,
                    RegionHandle.TopRight => NSCursorFrameResizePosition.TopRight,
                    RegionHandle.BottomLeft => NSCursorFrameResizePosition.BottomLeft,
                    _ => NSCursorFrameResizePosition.BottomRight,
                };
                cursor = NSCursor.GetFrameResizeCursor(position, NSCursorFrameResizeDirections.All);
            }
            else
            {
                cursor = handle switch
                {
                    RegionHandle.Left or RegionHandle.Right => NSCursor.ResizeLeftRightCursor,
                    RegionHandle.Top or RegionHandle.Bottom => NSCursor.ResizeUpDownCursor,
                    _ => NSCursor.CrosshairCursor, // macOS 14 has no public diagonal resize cursor
                };
            }

            _resizeCursors[(int)handle] = cursor;
            return cursor;
        }
    }
}
