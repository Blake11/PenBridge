using System.Diagnostics;
using AppKit;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Input;

namespace PenBridge.Mac.Input;

/// <summary>
/// Posts the three- and four-finger gestures (ADR-0023): three-finger drag as a left-button drag that moves the cursor
/// relatively, and the four-finger system gestures as their system shortcuts/actions, fired once per gesture when the
/// progress crosses <see cref="CommitProgress"/>.
/// </summary>
/// <remarks>
/// <para><b>Mapping (trackpad semantics, default shortcuts):</b> swipe up → ⌃↑ Mission Control; swipe down → ⌃↓ App
/// Exposé; swipe left (fingers move left) → ⌃→, the Space to the right (content follows the fingers), swipe right →
/// ⌃←; spread → F11 show desktop; pinch in → the app launcher (Apps, or Launchpad on older macOS) through
/// <see cref="NSWorkspace"/> (bundle id resolved once, on the main thread; no shell). A user who changed those
/// shortcuts in System Settings gets whatever they are bound to. Each keystroke carries its flags explicitly and is
/// followed by FlagsChanged events (the modifiers' own keycodes) back to the previous modifier state, so no modifier stays set.</para>
/// <para><b>Interactive mode</b> (Dock gesture events that follow the fingers, like a trackpad) is off: on this macOS
/// the posted dock-swipe events (type 30, HID type 23, motion/progress/velocity/phase fields 123/124/129/130/132) had
/// no effect (spike P4-B0, a Mac without a trackpad). <see cref="InteractiveDockGestures"/> keeps the switch.</para>
/// <para><b>Threading.</b> Owned by <see cref="CGEventGestureInjector"/>; every call is under the injector lock. The
/// launcher opens on the main queue. <b>Allocation:</b> none per event (raw CGEvent handles); firing the launcher
/// allocates once per gesture.</para>
/// <para><b>Release.</b> The drag's button is always released: Ended, Cancelled, pause/no display
/// (<see cref="CancelAll"/>), a pen event and disconnect (Core cancels open gestures at session end). A left-up skipped
/// while Accessibility was missing is posted by <see cref="Repair"/> when the grant returns; a drag still in progress
/// then is dropped.</para>
/// </remarks>
internal sealed class CGEventSystemGestureInjector
{
    /// <summary>
    /// TODO(#108): interactive four-finger gestures (Dock events that follow the fingers). Off: the spike found no
    /// posted dock-swipe variant that moved Mission Control or a Space here. Re-test on a Mac with a trackpad and
    /// several Spaces, capturing a real four-finger swipe with a listen-only event tap first.
    /// </summary>
    internal const bool InteractiveDockGestures = false;

    /// <summary>Progress (fraction of the mapped short side, Core's SystemSwipe/SystemPinch Progress) that fires the action.</summary>
    internal const double CommitProgress = 0.15;

    private const uint TypeLeftMouseDown = 1;
    private const uint TypeLeftMouseUp = 2;
    private const uint TypeLeftMouseDragged = 6;
    private const uint ButtonLeft = (uint)CGMouseButton.Left;
    private const uint HidEventTap = (uint)CGEventTapLocation.HID;
    private const int MaxDisplays = 16;

    // Virtual key codes (kVK_*) and flags (kCGEventFlagMaskControl, kCGEventFlagMaskSecondaryFn).
    private const ushort KeyLeftArrow = 123;
    private const ushort KeyRightArrow = 124;
    private const ushort KeyDownArrow = 125;
    private const ushort KeyUpArrow = 126;
    private const ushort KeyF11 = 103;
    private const ulong FlagControl = 0x40000;
    private const ulong FlagSecondaryFn = 0x800000;
    private const uint TypeFlagsChanged = 12;
    private const int StateHidSystem = 1; // kCGEventSourceStateHIDSystemState
    private const uint FieldKeycode = 9; // kCGKeyboardEventKeycode
    private const ushort KeyControl = 59; // kVK_Control
    private const ushort KeyFunction = 63; // kVK_Function

    /// <summary>The app launcher: Apps (macOS 26+), then Launchpad.</summary>
    private static readonly string[] LauncherBundleIds = ["com.apple.apps.launcher", "com.apple.launchpad.launcher"];

    private readonly IntPtr _source;
    private readonly Func<bool> _trusted;
    private readonly ILogger _logger;
    private readonly uint[] _displayIds = new uint[MaxDisplays];
    private readonly NativeRect[] _displays = new NativeRect[MaxDisplays];
    private readonly Action _openLauncherOnMain;
    private readonly Action<NSRunningApplication?, NSError?> _onLauncherOpened;

    private bool _dragging;
    private bool _downPosted; // a left-down reached the window server and its left-up has not
    private double _x;
    private double _y;
    private double _remainderX;
    private double _remainderY;
    private int _displayCount;
    private int _display;

    private bool _systemActive;
    private bool _fired;

    // Main thread only (see OpenLauncherOnMain).
    private NSUrl? _launcher;
    private bool _launcherResolved;

    public CGEventSystemGestureInjector(IntPtr source, Func<bool> trusted, ILogger logger)
    {
        _source = source;
        _trusted = trusted;
        _logger = logger;
        _openLauncherOnMain = OpenLauncherOnMain;
        _onLauncherOpened = OnLauncherOpened;
        InputLog.SystemGestureMode(_logger, InteractiveDockGestures
            ? "interactive Dock gesture events"
            : "discrete system shortcuts (interactive Dock gesture events are off)");
    }

    /// <summary>Handles a Drag, SystemSwipe or SystemPinch event. Call with the injector lock held.</summary>
    public void OnGesture(in GestureEvent e)
    {
        switch (e.Kind)
        {
            case GestureKind.Drag:
                OnDrag(e);
                break;
            case GestureKind.SystemSwipe or GestureKind.SystemPinch:
                OnSystem(e);
                break;
        }
    }

    /// <summary>Releases the drag button (pause, no display, pen, disconnect, Dispose) and forgets a system gesture. Idempotent.</summary>
    public void CancelAll()
    {
        EndDrag();
        _systemActive = false;
        _fired = false;
    }

    /// <summary>
    /// The Accessibility grant returned: posts the left-up for a left-down that was posted but whose up was skipped, and
    /// drops a drag still in progress (its later events are ignored until a new Began), as the two-finger gestures do.
    /// </summary>
    public void Repair()
    {
        _dragging = false;
        if (_downPosted && PostMouse(TypeLeftMouseUp, Cursor(), 0, 0))
        {
            _downPosted = false;
        }
    }

    private void OnDrag(in GestureEvent e)
    {
        switch (e.Phase)
        {
            case GesturePhase.Began:
                EndDrag(); // never leave a press open
                NativePoint at = Cursor();
                _x = at.X;
                _y = at.Y;
                _remainderX = 0;
                _remainderY = 0;
                LoadDisplays();
                _dragging = true;
                if (PostMouse(TypeLeftMouseDown, at, 0, 0))
                {
                    _downPosted = true;
                }

                MoveBy(e.DeltaX, e.DeltaY);
                break;
            case GesturePhase.Changed:
                if (_dragging)
                {
                    MoveBy(e.DeltaX, e.DeltaY);
                }

                break;
            default:
                EndDrag();
                break;
        }
    }

    private void MoveBy(double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || (dx == 0 && dy == 0))
        {
            return;
        }

        double x = _x + dx;
        double y = _y + dy;
        ClampToDisplays(ref x, ref y);
        long deltaX = GestureOutput.TakeWhole(x - _x, ref _remainderX);
        long deltaY = GestureOutput.TakeWhole(y - _y, ref _remainderY);
        _x = x;
        _y = y;
        _ = PostMouse(TypeLeftMouseDragged, new NativePoint(x, y), deltaX, deltaY);
    }

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (_downPosted && PostMouse(TypeLeftMouseUp, new NativePoint(_x, _y), 0, 0))
        {
            _downPosted = false; // else Repair posts it when the grant returns
        }
    }

    private void OnSystem(in GestureEvent e)
    {
        if (e.Phase == GesturePhase.Began)
        {
            _systemActive = true;
            _fired = false;
        }
        else if (!_systemActive)
        {
            return; // no Began seen
        }

        // Ended counts too: a fast flick can cross the threshold only in its last frame. Cancelled never fires.
        if (!_fired && e.Phase is GesturePhase.Began or GesturePhase.Changed or GesturePhase.Ended
            && e.Progress >= CommitProgress)
        {
            _fired = true;
            Fire(e.Kind, e.Direction);
        }

        if (e.Phase is GesturePhase.Ended or GesturePhase.Cancelled)
        {
            _systemActive = false;
            _fired = false;
        }
    }

    private void Fire(GestureKind kind, GestureDirection direction)
    {
        if (kind == GestureKind.SystemPinch)
        {
            if (direction == GestureDirection.Out)
            {
                PostKey(KeyF11, FlagSecondaryFn);
            }
            else if (direction == GestureDirection.In)
            {
                OpenLauncher();
            }

            return;
        }

        switch (direction)
        {
            case GestureDirection.Up:
                PostKey(KeyUpArrow, FlagControl | FlagSecondaryFn);
                break;
            case GestureDirection.Down:
                PostKey(KeyDownArrow, FlagControl | FlagSecondaryFn);
                break;
            case GestureDirection.Left:
                PostKey(KeyRightArrow, FlagControl | FlagSecondaryFn); // content follows the fingers: the Space to the right
                break;
            case GestureDirection.Right:
                PostKey(KeyLeftArrow, FlagControl | FlagSecondaryFn);
                break;
        }
    }

    /// <summary>
    /// Posts key down + up with the flags set explicitly on both, then FlagsChanged events back to the modifier state from
    /// before: posted key events update the HID system's modifier state, which every later event from a HID-state
    /// source inherits (measured: ⌃ and fn stayed set, so the next posted click would have been a ⌃-click).
    /// </summary>
    private void PostKey(ushort key, ulong flags)
    {
        if (!_trusted())
        {
            return;
        }

        ulong before = NativeMethods.CGEventSourceFlagsState(StateHidSystem);
        PostKeyEvent(key, down: true, flags);
        PostKeyEvent(key, down: false, flags);
        ReleaseModifiers(flags, before);
    }

    /// <summary>
    /// Releases the modifiers the keystroke added, one FlagsChanged per key as a keyboard does (fn, then ⌃, each with
    /// its own keycode), ending at <paramref name="before"/>.
    /// </summary>
    private void ReleaseModifiers(ulong flags, ulong before)
    {
        ulong pendingControl = (flags & FlagControl & ~before) != 0 ? FlagControl : 0;
        if ((flags & FlagSecondaryFn & ~before) != 0)
        {
            PostFlagsChanged(KeyFunction, before | pendingControl);
        }

        if (pendingControl != 0)
        {
            PostFlagsChanged(KeyControl, before);
        }
    }

    private void PostFlagsChanged(ushort keycode, ulong flags)
    {
        IntPtr ev = NativeMethods.CGEventCreate(_source);
        if (ev == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.CGEventSetType(ev, TypeFlagsChanged);
            NativeMethods.CGEventSetIntegerValueField(ev, FieldKeycode, keycode);
            NativeMethods.CGEventSetFlags(ev, flags);
            NativeMethods.CGEventSetTimestamp(ev, NowNs());
            NativeMethods.CGEventPost(HidEventTap, ev);
        }
        finally
        {
            NativeMethods.CFRelease(ev);
        }
    }

    private void PostKeyEvent(ushort key, bool down, ulong flags)
    {
        IntPtr ev = NativeMethods.CGEventCreateKeyboardEvent(_source, key, down ? (byte)1 : (byte)0);
        if (ev == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.CGEventSetFlags(ev, flags);
            NativeMethods.CGEventSetTimestamp(ev, NowNs());
            NativeMethods.CGEventPost(HidEventTap, ev);
        }
        finally
        {
            NativeMethods.CFRelease(ev);
        }
    }

    private void OpenLauncher()
    {
        if (!_trusted())
        {
            return; // like every other posted action: nothing while input injection is not allowed
        }

        // AppKit work (resolve once, open) happens on the main queue; cached delegates, no closure per gesture.
        DispatchQueue.MainQueue.DispatchAsync(_openLauncherOnMain);
    }

    /// <summary>Main thread: resolves the launcher once by bundle id, then opens it (opening it again toggles it).</summary>
    private void OpenLauncherOnMain()
    {
        if (!_launcherResolved)
        {
            _launcherResolved = true;
            foreach (string id in LauncherBundleIds)
            {
                _launcher = NSWorkspace.SharedWorkspace.UrlForApplication(id);
                if (_launcher is not null)
                {
                    break;
                }
            }

            if (_launcher is null)
            {
                InputLog.LauncherMissing(_logger, string.Join(", ", LauncherBundleIds));
            }
        }

        if (_launcher is { } url)
        {
            NSWorkspace.SharedWorkspace.OpenApplication(url, NSWorkspaceOpenConfiguration.Create(), _onLauncherOpened);
        }
    }

    private void OnLauncherOpened(NSRunningApplication? app, NSError? error)
    {
        _ = app;
        if (error is not null)
        {
            InputLog.LauncherFailed(_logger, error.LocalizedDescription);
        }
    }

    /// <summary>Posts a left-button mouse event (click state 1) unless Accessibility is missing; false when not posted.</summary>
    private bool PostMouse(uint type, NativePoint at, long deltaX, long deltaY)
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
            NativeMethods.CGEventSetIntegerValueField(ev, (uint)CGEventField.MouseEventClickState, 1);
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

    /// <summary>Caches the active display rectangles at drag start (no allocation; up to <see cref="MaxDisplays"/>).</summary>
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
    /// Keeps the point on a display: inside any display it moves freely (across displays, as a mouse does); otherwise
    /// it is clamped to the display the cursor was last on.
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

        if (_display < 0)
        {
            return;
        }

        NativeRect r = _displays[_display];
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
