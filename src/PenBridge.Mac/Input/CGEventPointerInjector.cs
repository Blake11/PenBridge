using System.Diagnostics;
using CoreGraphics;
using PenBridge.Core.Input;

namespace PenBridge.Mac.Input;

/// <summary>
/// Posts the relative cursor gestures as plain mouse events (no tablet subtype): the one-finger cursor (ADR-0026:
/// PointerMove = mouse-moved, Click = left click with the recognizer's click count, PointerDrag = left drag) and the
/// three-finger drag (ADR-0023). Moves start at the real current cursor and go by the gesture's deltas, kept on the
/// displays; clicks land at the current cursor.
/// </summary>
/// <remarks>
/// <para><b>Position.</b> Each moving gesture reads the cursor once at its Began (<c>CGEventGetLocation</c> of a new
/// event: global display points, top-left origin) and then tracks the position it posted, with sub-point precision,
/// so a post the window server hasn't applied yet can't lose a step. Every posted event carries that location.</para>
/// <para><b>Displays.</b> The active display rectangles are read at each Began (no allocation, up to
/// <see cref="MaxDisplays"/>). A point inside any display moves freely (across displays, as a mouse does); a point
/// in no display (beyond the outer edge, or in a gap between displays) is clamped to the display the cursor was last
/// on, so it slides along that display's edge and never jumps to another one.</para>
/// <para><b>Release.</b> The left button is always released: a drag's Ended/Cancelled, a new moving gesture or click,
/// <see cref="CancelAll"/> (pause, no display, pen activity, Dispose) and the Cancelled that Core emits at the
/// watchdog, a pen entering, disconnect and takeover. A left-up skipped while Accessibility was missing is posted by
/// <see cref="Repair"/> when the grant returns; a gesture still in progress then is dropped.</para>
/// <para><b>Threading.</b> Owned by <see cref="CGEventGestureInjector"/>; every call is under the injector lock.
/// <b>Allocation:</b> none per event (raw CGEvent handles).</para>
/// </remarks>
internal sealed class CGEventPointerInjector
{
    private const uint TypeLeftMouseDown = 1;
    private const uint TypeLeftMouseUp = 2;
    private const uint TypeMouseMoved = 5;
    private const uint TypeLeftMouseDragged = 6;
    private const uint ButtonLeft = (uint)CGMouseButton.Left;
    private const uint HidEventTap = (uint)CGEventTapLocation.HID;
    private const int MaxDisplays = 16;

    private readonly IntPtr _source;
    private readonly Func<bool> _trusted;
    private readonly uint[] _displayIds = new uint[MaxDisplays];
    private readonly NativeRect[] _displays = new NativeRect[MaxDisplays];

    private bool _moving; // a PointerMove is in progress (no button)
    private bool _dragging; // a Drag or PointerDrag is in progress (left button logically down)
    private bool _downPosted; // a left-down reached the window server and its left-up has not
    private double _x;
    private double _y;
    private double _remainderX;
    private double _remainderY;
    private int _displayCount;
    private int _display;

    public CGEventPointerInjector(IntPtr source, Func<bool> trusted)
    {
        _source = source;
        _trusted = trusted;
    }

    /// <summary>Handles a PointerMove, Click, PointerDrag or Drag event. Call with the injector lock held.</summary>
    public void OnGesture(in GestureEvent e)
    {
        switch (e.Kind)
        {
            case GestureKind.PointerMove:
                OnPointerMove(e);
                break;
            case GestureKind.Click:
                if (e.Phase == GesturePhase.Ended)
                {
                    Click(e.ClickCount);
                }

                break;
            case GestureKind.PointerDrag or GestureKind.Drag:
                OnDrag(e);
                break;
        }
    }

    /// <summary>Releases the left button (if a drag holds it) and ends a move. Idempotent.</summary>
    public void CancelAll()
    {
        _moving = false;
        EndDrag();
    }

    /// <summary>
    /// The Accessibility grant returned: posts the left-up for a left-down that was posted but whose up was skipped, and
    /// drops a gesture still in progress (its later events are ignored until a new Began).
    /// </summary>
    public void Repair()
    {
        _moving = false;
        _dragging = false;
        if (_downPosted && PostMouse(TypeLeftMouseUp, Cursor(), 1, 0, 0))
        {
            _downPosted = false;
        }
    }

    private void OnPointerMove(in GestureEvent e)
    {
        switch (e.Phase)
        {
            case GesturePhase.Began:
                EndDrag(); // never leave a press open
                Start();
                _moving = true;
                MoveBy(TypeMouseMoved, e.DeltaX, e.DeltaY);
                break;
            case GesturePhase.Changed:
                if (_moving)
                {
                    MoveBy(TypeMouseMoved, e.DeltaX, e.DeltaY);
                }

                break;
            default:
                _moving = false; // Ended/Cancelled post nothing
                break;
        }
    }

    private void OnDrag(in GestureEvent e)
    {
        switch (e.Phase)
        {
            case GesturePhase.Began:
                EndDrag(); // never leave a press open
                _moving = false;
                Start();
                _dragging = true;
                // Click state 1: a tap-and-a-half drag must never read as a double click (ADR-0022/0026).
                if (PostMouse(TypeLeftMouseDown, new NativePoint(_x, _y), 1, 0, 0))
                {
                    _downPosted = true;
                }

                MoveBy(TypeLeftMouseDragged, e.DeltaX, e.DeltaY);
                break;
            case GesturePhase.Changed:
                if (_dragging)
                {
                    MoveBy(TypeLeftMouseDragged, e.DeltaX, e.DeltaY);
                }

                break;
            default:
                EndDrag();
                break;
        }
    }

    /// <summary>Left down + up at the current cursor with the recognizer's click state (1 single, 2 double, …).</summary>
    private void Click(int clickCount)
    {
        EndDrag();
        _moving = false;
        if (!_trusted())
        {
            return;
        }

        NativePoint at = Cursor();
        long clickState = Math.Max(1, clickCount);
        if (PostMouse(TypeLeftMouseDown, at, clickState, 0, 0))
        {
            _ = PostMouse(TypeLeftMouseUp, at, clickState, 0, 0);
        }
    }

    /// <summary>Starts a moving gesture at the real cursor, with fresh remainders and display rectangles.</summary>
    private void Start()
    {
        NativePoint at = Cursor();
        _x = at.X;
        _y = at.Y;
        _remainderX = 0;
        _remainderY = 0;
        LoadDisplays();
        if (_display < 0 && _displayCount > 0)
        {
            _display = Nearest(_x, _y);
            ClampTo(_displays[_display], ref _x, ref _y);
        }
    }

    private void MoveBy(uint type, double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || (dx == 0 && dy == 0))
        {
            return;
        }

        double x = _x + dx;
        double y = _y + dy;
        ClampToDisplays(ref x, ref y);
        if (x == _x && y == _y)
        {
            return; // held at an edge: nothing moves
        }

        long deltaX = GestureOutput.TakeWhole(x - _x, ref _remainderX);
        long deltaY = GestureOutput.TakeWhole(y - _y, ref _remainderY);
        _x = x;
        _y = y;
        _ = PostMouse(type, new NativePoint(x, y), type == TypeMouseMoved ? 0 : 1, deltaX, deltaY);
    }

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (_downPosted && PostMouse(TypeLeftMouseUp, new NativePoint(_x, _y), 1, 0, 0))
        {
            _downPosted = false; // else Repair posts it when the grant returns
        }
    }

    /// <summary>Posts a left-button (or plain moved) mouse event unless Accessibility is missing; false when not posted.</summary>
    private bool PostMouse(uint type, NativePoint at, long clickState, long deltaX, long deltaY)
    {
        if (!_trusted())
        {
            return false;
        }

        IntPtr ev = NativeMethods.CGEventCreateMouseEvent(_source, type, at, ButtonLeft);
        if (ev == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            NativeMethods.CGEventSetIntegerValueField(ev, (uint)CGEventField.MouseEventClickState, clickState);
            NativeMethods.CGEventSetIntegerValueField(ev, (uint)CGEventField.MouseEventDeltaX, deltaX);
            NativeMethods.CGEventSetIntegerValueField(ev, (uint)CGEventField.MouseEventDeltaY, deltaY);
            NativeMethods.CGEventSetTimestamp(ev, NowNs());
            NativeMethods.CGEventPost(HidEventTap, ev);
        }
        finally
        {
            NativeMethods.CFRelease(ev);
        }

        return true;
    }

    private void LoadDisplays()
    {
        _displayCount = 0;
        _display = -1;
        if (NativeMethods.CGGetActiveDisplayList(MaxDisplays, _displayIds, out uint count) != 0)
        {
            return;
        }

        _displayCount = (int)Math.Min(count, MaxDisplays);
        for (int i = 0; i < _displayCount; i++)
        {
            _displays[i] = NativeMethods.CGDisplayBounds(_displayIds[i]);
            if (_display < 0 && _displays[i].Contains(_x, _y))
            {
                _display = i;
            }
        }
    }

    /// <summary>
    /// Keeps the point on a display: inside any display it moves freely; otherwise it is clamped to the display the
    /// cursor was last on.
    /// </summary>
    private void ClampToDisplays(ref double x, ref double y)
    {
        for (int i = 0; i < _displayCount; i++)
        {
            if (_displays[i].Contains(x, y))
            {
                _display = i;
                return;
            }
        }

        if (_display >= 0)
        {
            ClampTo(_displays[_display], ref x, ref y);
        }
    }

    /// <summary>The display nearest to the point (the cursor was on none, e.g. a display just went away).</summary>
    private int Nearest(double x, double y)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < _displayCount; i++)
        {
            double cx = x;
            double cy = y;
            ClampTo(_displays[i], ref cx, ref cy);
            double d = ((cx - x) * (cx - x)) + ((cy - y) * (cy - y));
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        return best;
    }

    /// <summary>Clamps to the rectangle's last whole point (the cursor never sits on the right/bottom edge line).</summary>
    private static void ClampTo(in NativeRect r, ref double x, ref double y)
    {
        x = Math.Clamp(x, r.X, r.X + r.Width - 1);
        y = Math.Clamp(y, r.Y, r.Y + r.Height - 1);
    }

    private static NativePoint Cursor()
    {
        IntPtr ev = NativeMethods.CGEventCreate(IntPtr.Zero);
        if (ev == IntPtr.Zero)
        {
            return default;
        }

        try
        {
            return NativeMethods.CGEventGetLocation(ev);
        }
        finally
        {
            NativeMethods.CFRelease(ev);
        }
    }

    private static ulong NowNs() =>
        (ulong)TabletEventMath.TicksToNanoseconds(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
}
