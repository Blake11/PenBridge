using System.Diagnostics;
using CoreGraphics;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Input;

namespace PenBridge.Mac.Input;

/// <summary>
/// Posts the two-finger gestures as trackpad-like CGEvents (ADR-0023): phased continuous scroll with momentum (plus the
/// companion gesture events AppKit's swipe-between-pages tracking reads), magnify and rotate gesture events, smart
/// zoom and the two-finger tap as a right-click. Every event is posted at the current cursor position, which never
/// moves. Field numbers and values were measured on macOS (see the mac-build-run skill, "mac-gesture-events").
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> Owned by <see cref="CGEventTabletInjector"/>: every method except the coast timer callback is
/// called with the owner's lock held, and the timer callback takes that same lock, so all state is guarded by it.
/// Lock order is unchanged (pause gate, then injector lock; the timer takes only the injector lock).</para>
/// <para><b>Idle.</b> The momentum timer exists only while coasting (created on the flick, disposed at the end or on
/// any stop), so nothing runs while idle.</para>
/// <para><b>Allocation.</b> None per event: raw CGEvent handles, created, filled, posted and released. A flick
/// allocates its timer once.</para>
/// <para><b>Accessibility.</b> Without the grant the posts are skipped and the state still advances, as for pen events.
/// The window server may then still hold a phase opened before the revoke (its end was skipped), so the injector
/// remembers which phases it actually posted open and <see cref="RepairAfterGrant"/> closes them when the grant
/// returns. A revoke is only noticed on the system broadcast, so posts in that short window count as delivered.</para>
/// <para>The relative cursor gestures (one-finger PointerMove, Click and PointerDrag of ADR-0026, three-finger Drag)
/// go to <see cref="CGEventPointerInjector"/>, the four-finger system gestures (SystemSwipe, SystemPinch) to
/// <see cref="CGEventSystemGestureInjector"/>.</para>
/// </remarks>
internal sealed class CGEventGestureInjector : IDisposable
{
    // CGEvent types (CGEventType values; 29 is the undocumented kCGSEventGesture).
    private const uint TypeRightMouseDown = 3;
    private const uint TypeRightMouseUp = 4;
    private const uint TypeGesture = 29;

    // Scroll-wheel fields: kCGScrollWheelEventIsContinuous, …ScrollPhase, …MomentumPhase, and the undocumented
    // "direction inverted from device" flag (NSEvent.isDirectionInvertedFromDevice).
    private const uint FieldScrollIsContinuous = 88;
    private const uint FieldScrollPhase = 99;
    private const uint FieldMomentumPhase = 123;
    private const uint FieldScrollInvertedFromDevice = 137;

    // Gesture fields (undocumented): the HID event type, the value slots and the phase. The value fields share
    // storage (113/114/118 are one slot, 119 the next), so set only the ones the type uses.
    private const uint FieldGestureHidType = 110;
    private const uint FieldGestureMagnification = 113;
    private const uint FieldGestureRotation = 114;
    private const uint FieldGestureScrollX = 118;
    private const uint FieldGestureScrollY = 119;
    private const uint FieldGesturePhase = 132;

    // Gesture HID types (IOHIDEventType): rotation, scroll (the swipe-tracking companion), zoom, smart zoom.
    private const long HidRotation = 5;
    private const long HidScroll = 6;
    private const long HidZoom = 8;
    private const long HidZoomToggle = 22;

    // Phases for the scroll-phase and gesture-phase fields (CGScrollPhase); momentum phases (CGMomentumScrollPhase).
    private const long PhaseBegan = 1;
    private const long PhaseChanged = 2;
    private const long PhaseEnded = 4;
    private const long PhaseCancelled = 8;
    private const long MomentumBegin = 1;
    private const long MomentumContinue = 2;
    private const long MomentumEnd = 3;

    private const uint ScrollUnitPixel = 0; // kCGScrollEventUnitPixel
    private const uint ButtonRight = (uint)CGMouseButton.Right;
    private const uint HidEventTap = (uint)CGEventTapLocation.HID;

    private readonly Lock _gate;
    private readonly IntPtr _source;
    private readonly Func<bool> _trusted;
    private readonly NaturalScrolling _naturalScrolling;
    private readonly ScrollMomentum _momentum;
    private readonly TimeSpan _tickInterval;
    private readonly TimerCallback _onCoastTick;
    private readonly CGEventSystemGestureInjector _system;
    private readonly CGEventPointerInjector _pointer;

    private bool _scrollOpen;
    private bool _natural = true;
    private double _remainderX;
    private double _remainderY;
    private bool _pinchActive;

    // Which of the magnify/rotate streams are open, and the held-back amounts (dominance decides the first stream).
    private readonly TransformStreams _transform = new();

    private Timer? _coastTimer;
    private long _coastGeneration;
    private long _releaseTicks;
    private bool _momentumBegun;
    private bool _disposed;

    // Phases the window server saw open: set when a Began actually posted, cleared when its end actually posted.
    private bool _postedScrollOpen;
    private bool _postedMomentumOpen;
    private bool _postedMagnifyOpen;
    private bool _postedRotateOpen;

    /// <param name="gate">The owner's lock; held on every call, taken by the coast timer.</param>
    /// <param name="source">The owner's CGEventSource handle (HID system state), alive for this object's lifetime.</param>
    /// <param name="trusted">Whether posting is allowed (Accessibility granted); read per post.</param>
    /// <param name="naturalScrolling">The cached natural-scrolling setting, read at each scroll start.</param>
    /// <param name="logger">Diagnostics (mode and failures only, never per event).</param>
    /// <param name="momentum">Momentum model options; null = Core defaults.</param>
    public CGEventGestureInjector(
        Lock gate,
        IntPtr source,
        Func<bool> trusted,
        NaturalScrolling naturalScrolling,
        ILogger logger,
        ScrollMomentumOptions? momentum = null)
    {
        _gate = gate;
        _source = source;
        _trusted = trusted;
        _naturalScrolling = naturalScrolling;
        _momentum = new ScrollMomentum(momentum);
        _tickInterval = _momentum.Options.TickInterval;
        _onCoastTick = OnCoastTick;
        _system = new CGEventSystemGestureInjector(source, trusted, logger);
        _pointer = new CGEventPointerInjector(source, trusted);
    }

    /// <summary>Handles one gesture event. Call with the owner's lock held.</summary>
    public void OnGesture(in GestureEvent e)
    {
        if (_disposed)
        {
            return;
        }

        // Any gesture event (a finger landing, a new scroll, a pinch, a tap) stops a coast at once.
        StopMomentum();

        switch (e.Kind)
        {
            case GestureKind.Scroll:
                OnScroll(e);
                break;
            case GestureKind.Pinch:
                OnPinch(e);
                break;
            case GestureKind.Tap:
                PostRightClick();
                break;
            case GestureKind.SmartZoom:
                PostGesture(HidZoomToggle, phase: 0, valueField: 0, value: 0);
                break;
            case GestureKind.TouchDown:
                break; // only stops the coast (done above)
            case GestureKind.PointerMove or GestureKind.Click or GestureKind.PointerDrag or GestureKind.Drag:
                _pointer.OnGesture(e);
                break;
            default:
                _system.OnGesture(e); // SystemSwipe, SystemPinch
                break;
        }
    }

    /// <summary>
    /// A pen event arrived: the pen takes over, so stop a coast and release a finger drag (one- or three-finger) before
    /// the pen's own events, which then start with no button held.
    /// </summary>
    public void OnPenActivity()
    {
        StopCoast();
        _pointer.CancelAll();
        _system.CancelAll();
    }

    /// <summary>
    /// Stops a coast (posting its end): a pen event arrived (the recognizer cancels open gestures itself) or the
    /// session ended. Cheap when not coasting.
    /// </summary>
    public void StopCoast()
    {
        if (_coastTimer is not null)
        {
            StopMomentum();
        }
    }

    /// <summary>
    /// Closes any open scroll/magnify/rotate phase with Cancelled and stops momentum (posting its end). Pause, a
    /// missing display, Dispose. Idempotent.
    /// </summary>
    public void CancelAll()
    {
        StopMomentum();
        _momentum.Reset();
        if (_scrollOpen)
        {
            _scrollOpen = false;
            CancelScroll();
        }

        ClosePinch(PhaseCancelled);
        _pointer.CancelAll();
        _system.CancelAll();
    }

    /// <summary>
    /// The Accessibility grant returned: drops the current gesture (its later events are ignored until a new Began)
    /// and closes every phase the window server may still hold open from before the revoke. Call with the owner's
    /// lock held, while trusted.
    /// </summary>
    public void RepairAfterGrant()
    {
        if (_disposed)
        {
            return;
        }

        StopMomentum();
        _momentum.Reset();
        _scrollOpen = false;
        _pinchActive = false;
        _transform.Reset();
        if (_postedScrollOpen)
        {
            CancelScroll();
        }

        if (_postedMomentumOpen)
        {
            PostScroll(0, 0, 0, 0, 0, MomentumEnd);
        }

        if (_postedMagnifyOpen)
        {
            PostGesture(HidZoom, PhaseCancelled, FieldGestureMagnification, 0);
        }

        if (_postedRotateOpen)
        {
            PostGesture(HidRotation, PhaseCancelled, FieldGestureRotation, 0);
        }

        _pointer.Repair();
    }

    /// <summary>Cancels everything and stops accepting events. Call with the owner's lock held.</summary>
    public void Dispose()
    {
        CancelAll();
        _disposed = true;
    }

    private void OnScroll(in GestureEvent e)
    {
        switch (e.Phase)
        {
            case GesturePhase.Began:
                if (_scrollOpen)
                {
                    CancelScroll(); // never leave a phase open
                }

                _natural = _naturalScrolling.Value;
                _remainderX = 0;
                _remainderY = 0;
                _scrollOpen = true;
                _momentum.Observe(e);
                PostScrollDelta(e.DeltaX, e.DeltaY, PhaseBegan);
                break;
            case GesturePhase.Changed:
                if (!_scrollOpen)
                {
                    return; // no Began seen (e.g. it was dropped): don't post a dangling Changed
                }

                _momentum.Observe(e);
                PostScrollDelta(e.DeltaX, e.DeltaY, PhaseChanged);
                break;
            case GesturePhase.Ended:
                if (!_scrollOpen)
                {
                    return;
                }

                _scrollOpen = false;
                PostScroll(0, 0, 0, 0, PhaseEnded, 0);
                if (_momentum.Observe(e))
                {
                    StartCoast();
                }

                break;
            default:
                _momentum.Reset();
                if (_scrollOpen)
                {
                    _scrollOpen = false;
                    CancelScroll();
                }

                break;
        }
    }

    private void PostScrollDelta(double fingerDx, double fingerDy, long phase)
    {
        GestureOutput.ToScrollWheel(fingerDx, fingerDy, _natural, out double wheelX, out double wheelY);
        long wholeX = GestureOutput.TakeWhole(wheelX, ref _remainderX);
        long wholeY = GestureOutput.TakeWhole(wheelY, ref _remainderY);
        PostScroll(wheelX, wheelY, wholeX, wholeY, phase, 0);
    }

    private void OnPinch(in GestureEvent e)
    {
        switch (e.Phase)
        {
            case GesturePhase.Began:
                ClosePinch(PhaseCancelled); // never leave a phase open
                _pinchActive = true;
                PostTransform(e);
                break;
            case GesturePhase.Changed:
                if (_pinchActive)
                {
                    PostTransform(e); // no Began seen (e.g. it was dropped): drop the gesture, as for scroll
                }

                break;
            case GesturePhase.Ended:
                ClosePinch(PhaseEnded);
                break;
            default:
                ClosePinch(PhaseCancelled);
                break;
        }
    }

    /// <summary>
    /// Posts the magnify and rotate parts of a transform step. Core's <see cref="TransformStreams"/> decides when each
    /// stream begins (Began, value 0) and what each posts: the dominant component opens first, the other only past a
    /// larger amount (Preview follows only the stream that began first); a pure zoom posts no rotate events and a pure
    /// turn no magnify events.
    /// </summary>
    private void PostTransform(in GestureEvent e)
    {
        TransformStreamStep step = _transform.Step(
            GestureOutput.ToMagnification(e.ScaleDelta), GestureOutput.ToCounterClockwiseDegrees(e.RotationDelta));
        if (step.RotateFirst)
        {
            PostRotateStep(step);
            PostMagnifyStep(step);
        }
        else
        {
            PostMagnifyStep(step);
            PostRotateStep(step);
        }
    }

    private void PostMagnifyStep(in TransformStreamStep step)
    {
        if (step.MagnifyBegins)
        {
            PostGesture(HidZoom, PhaseBegan, FieldGestureMagnification, 0);
        }

        if (step.PostMagnify)
        {
            PostGesture(HidZoom, PhaseChanged, FieldGestureMagnification, step.Magnification);
        }
    }

    private void PostRotateStep(in TransformStreamStep step)
    {
        if (step.RotateBegins)
        {
            PostGesture(HidRotation, PhaseBegan, FieldGestureRotation, 0);
        }

        if (step.PostRotate)
        {
            PostGesture(HidRotation, PhaseChanged, FieldGestureRotation, step.Degrees);
        }
    }

    private void ClosePinch(long phase)
    {
        _pinchActive = false;
        bool magnifyOpen = _transform.MagnifyOpen;
        bool rotateOpen = _transform.RotateOpen;
        _transform.Reset();
        if (magnifyOpen)
        {
            PostGesture(HidZoom, phase, FieldGestureMagnification, 0);
        }

        if (rotateOpen)
        {
            PostGesture(HidRotation, phase, FieldGestureRotation, 0);
        }
    }

    private void StartCoast()
    {
        _releaseTicks = Stopwatch.GetTimestamp();
        _momentumBegun = false;
        _coastGeneration++;
        _coastTimer = new Timer(_onCoastTick, _coastGeneration, _tickInterval, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Stops a coast at once: disposes the timer and posts the momentum end if any momentum was posted.</summary>
    private void StopMomentum()
    {
        Timer? timer = _coastTimer;
        if (timer is null)
        {
            return;
        }

        _coastTimer = null;
        _coastGeneration++; // a tick already queued sees a stale generation and does nothing
        timer.Dispose();
        _momentum.Reset();
        _momentumBegun = false;
        if (_postedMomentumOpen)
        {
            PostScroll(0, 0, 0, 0, 0, MomentumEnd); // only after a Begin that was actually posted
        }
    }

    private void OnCoastTick(object? state)
    {
        lock (_gate)
        {
            if (_disposed || _coastTimer is not { } timer || state is not long generation || generation != _coastGeneration)
            {
                return;
            }

            long elapsedUs = (long)((Stopwatch.GetTimestamp() - _releaseTicks) * 1e6 / Stopwatch.Frequency);
            if (_momentum.Step(elapsedUs, out double dx, out double dy))
            {
                GestureOutput.ToScrollWheel(dx, dy, _natural, out double wheelX, out double wheelY);
                if (!_momentumBegun)
                {
                    _remainderX = 0;
                    _remainderY = 0;
                }

                long wholeX = GestureOutput.TakeWhole(wheelX, ref _remainderX);
                long wholeY = GestureOutput.TakeWhole(wheelY, ref _remainderY);
                PostScroll(wheelX, wheelY, wholeX, wholeY, 0, _postedMomentumOpen ? MomentumContinue : MomentumBegin);
                _momentumBegun = true;
                timer.Change(_tickInterval, Timeout.InfiniteTimeSpan);
                return;
            }

            // The coast is over: end it (also when no step was posted, an End needs a Begin first).
            _coastTimer = null;
            _coastGeneration++;
            timer.Dispose();
            _momentumBegun = false;
            if (_postedMomentumOpen)
            {
                PostScroll(0, 0, 0, 0, 0, MomentumEnd); // only after a Begin that was actually posted
            }
        }
    }

    /// <summary>
    /// Cancels the open scroll phase: Cancelled, then an Ended without deltas. AppKit delivers a posted Cancelled
    /// scroll but keeps the gesture open, and on the next phased scroll event synthesizes an Ended that carries that
    /// event's deltas (the first delta of the next scroll lands twice). The trailing Ended closes it at once; a page
    /// swipe (<c>trackSwipeEvent</c>) cut by the Cancelled still aborts.
    /// </summary>
    private void CancelScroll()
    {
        PostScroll(0, 0, 0, 0, PhaseCancelled, 0);
        PostScroll(0, 0, 0, 0, PhaseEnded, 0);
    }

    /// <summary>
    /// Posts one continuous (pixel) scroll-wheel event at the cursor. With a scroll phase it is preceded by the
    /// companion gesture event (HID type scroll, same phase, the unrounded deltas) that AppKit's swipe tracking reads;
    /// momentum events have none (the fingers are up).
    /// </summary>
    private void PostScroll(double wheelX, double wheelY, long wholeX, long wholeY, long scrollPhase, long momentumPhase)
    {
        if (!_trusted())
        {
            return;
        }

        ulong now = NowNs();
        if (scrollPhase != 0)
        {
            IntPtr g = NativeMethods.CGEventCreate(_source);
            if (g != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.CGEventSetType(g, TypeGesture);
                    NativeMethods.CGEventSetIntegerValueField(g, FieldGestureHidType, HidScroll);
                    NativeMethods.CGEventSetIntegerValueField(g, FieldGesturePhase, scrollPhase);
                    NativeMethods.CGEventSetDoubleValueField(g, FieldGestureScrollX, wheelX);
                    NativeMethods.CGEventSetDoubleValueField(g, FieldGestureScrollY, wheelY);
                    NativeMethods.CGEventSetTimestamp(g, now);
                    NativeMethods.CGEventPost(HidEventTap, g);
                }
                finally
                {
                    NativeMethods.CFRelease(g);
                }
            }
        }

        IntPtr ev = NativeMethods.CGEventCreateScrollWheelEvent2(_source, ScrollUnitPixel, 2, Clamp(wholeY), Clamp(wholeX), 0);
        if (ev == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.CGEventSetIntegerValueField(ev, FieldScrollIsContinuous, 1);
            NativeMethods.CGEventSetIntegerValueField(ev, FieldScrollPhase, scrollPhase);
            NativeMethods.CGEventSetIntegerValueField(ev, FieldMomentumPhase, momentumPhase);
            NativeMethods.CGEventSetIntegerValueField(ev, FieldScrollInvertedFromDevice, _natural ? 1 : 0);
            NativeMethods.CGEventSetTimestamp(ev, now);
            NativeMethods.CGEventPost(HidEventTap, ev);
            TrackPosted(ref _postedScrollOpen, scrollPhase);
            if (momentumPhase == MomentumBegin)
            {
                _postedMomentumOpen = true;
            }
            else if (momentumPhase == MomentumEnd)
            {
                _postedMomentumOpen = false;
            }
        }
        finally
        {
            NativeMethods.CFRelease(ev);
        }
    }

    /// <summary>Follows a posted phase: Began opens, Ended/Cancelled close, anything else leaves it.</summary>
    private static void TrackPosted(ref bool open, long phase)
    {
        if (phase == PhaseBegan)
        {
            open = true;
        }
        else if (phase is PhaseEnded or PhaseCancelled)
        {
            open = false;
        }
    }

    /// <summary>Posts a gesture event (type 29) at the cursor; <paramref name="valueField"/> 0 = no value.</summary>
    private void PostGesture(long hidType, long phase, uint valueField, double value)
    {
        if (!_trusted())
        {
            return;
        }

        IntPtr ev = NativeMethods.CGEventCreate(_source);
        if (ev == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.CGEventSetType(ev, TypeGesture);
            NativeMethods.CGEventSetIntegerValueField(ev, FieldGestureHidType, hidType);
            if (phase != 0)
            {
                NativeMethods.CGEventSetIntegerValueField(ev, FieldGesturePhase, phase);
            }

            if (valueField != 0)
            {
                NativeMethods.CGEventSetDoubleValueField(ev, valueField, value);
            }

            NativeMethods.CGEventSetTimestamp(ev, NowNs());
            NativeMethods.CGEventPost(HidEventTap, ev);
            if (hidType == HidZoom)
            {
                TrackPosted(ref _postedMagnifyOpen, phase);
            }
            else if (hidType == HidRotation)
            {
                TrackPosted(ref _postedRotateOpen, phase);
            }
        }
        finally
        {
            NativeMethods.CFRelease(ev);
        }
    }

    /// <summary>Two-finger tap: a plain right-button click at the cursor (click state 1 on both).</summary>
    private void PostRightClick()
    {
        if (!_trusted())
        {
            return;
        }

        NativePoint at = CurrentCursor();
        PostMouse(TypeRightMouseDown, at);
        PostMouse(TypeRightMouseUp, at);
    }

    private void PostMouse(uint type, NativePoint at)
    {
        IntPtr ev = NativeMethods.CGEventCreateMouseEvent(_source, type, at, ButtonRight);
        if (ev == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.CGEventSetIntegerValueField(ev, (uint)CGEventField.MouseEventClickState, 1);
            NativeMethods.CGEventSetTimestamp(ev, NowNs());
            NativeMethods.CGEventPost(HidEventTap, ev);
        }
        finally
        {
            NativeMethods.CFRelease(ev);
        }
    }

    private static NativePoint CurrentCursor()
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

    private static int Clamp(long v) => (int)Math.Clamp(v, int.MinValue, int.MaxValue);
}
