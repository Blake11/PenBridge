using System.Diagnostics;
using System.Runtime.ExceptionServices;
using AppKit;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PenBridge.Core.Input;
using PenBridge.Mac.Services;

namespace PenBridge.Mac.Input;

/// <summary>
/// Injects pen input into macOS as CGEvent tablet events (ADR-0003): proximity events announce a pen or eraser, then
/// mouse events with the tablet-point subtype carry position, pressure, tilt and rotation. The side button is a
/// right-click while hovering (ADR-0016); held at contact-down it makes the stroke a right-button drag, latched until
/// the tip lifts (ADR-0022). Gesture events are accepted and ignored until Phase 4.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> <see cref="ServerHost"/> wraps the injector in a <see cref="PausableInputSink"/>, so
/// <see cref="OnPenEvent"/>/<see cref="OnGestureEvent"/> (thread-pool threads: UDP loop, TLS handler, watchdog timer)
/// and the pause path's <see cref="ReleaseAll"/> arrive serialized under that sink's lock. <see cref="IsTargetAvailable"/>
/// (main thread, from <see cref="DisplayTargetResolver.Bind"/>) and the Accessibility repair are not under that lock,
/// so an internal lock also serializes planning and posting; it is always taken last and never calls out, so the
/// lock order is fixed (pause gate, then injector) and it is uncontended on the hot path. CGEventPost is thread-safe
/// and never touches AppKit. <see cref="AccessibilityChanged"/> is always raised on the main thread.</para>
/// <para><b>Split.</b> The translation rules (<see cref="TabletEventPlanner"/>) and unit math
/// (<see cref="TabletEventMath"/>) are platform-free and tested in Core; this class only builds and posts CGEvents.</para>
/// <para><b>Timestamps.</b> CGEvent timestamps are nanoseconds on CLOCK_UPTIME_RAW (measured: an NSEvent created
/// with timestamp t seconds yields a CGEvent timestamp of t·10⁹, and t equals <c>clock_gettime_nsec_np(CLOCK_UPTIME_RAW)</c>;
/// they are not mach ticks). On macOS <see cref="Stopwatch"/> reads that clock at 1 GHz, so "now" is
/// <see cref="Stopwatch.GetTimestamp"/> converted with its frequency.</para>
/// <para><b>Allocation.</b> None per event: the hot path uses raw CGEvent handles (created, filled, posted, released
/// with <c>CFRelease</c>), the event source is created once and the planner works on a stack buffer.</para>
/// <para><b>Accessibility.</b> Without the grant CGEventPost silently drops events. The planner keeps running and
/// only the posts are skipped, so the state stays in step with the phone; <see cref="AccessibilityMissing"/> is
/// reported and logged once. When the grant returns, a repair posts LeftUp, RightUp and ProximityLeave so that the
/// window server's real button state (which may still hold a press from before the revoke) is released. The grant is
/// re-checked on every proximity enter, on the system's Accessibility broadcast and on
/// <see cref="RefreshAccessibility"/> (for example when the menu opens) — never by polling.</para>
/// <para><b>Pause</b> is <see cref="PausableInputSink"/>'s job (it calls <see cref="ReleaseAll"/>, then drops input
/// until a resync point); the injector has no pause state of its own.</para>
/// <para><b>Safety.</b> Leave, pause, a missing display, disconnect (the server's synthesized release) and
/// <see cref="ReleaseAll"/> post every outstanding up and the proximity leave. Each post stands alone: a failure
/// does not skip the remaining releases, and the failed step is reverted in the planner state so it is retried.</para>
/// </remarks>
public sealed class CGEventTabletInjector : IReleasableInputSink, IDisposable
{
    /// <summary>The System Settings pane where the user grants Accessibility.</summary>
    public const string AccessibilitySettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    /// <summary>
    /// Sign applied to tilt Y. Core's tiltY &gt; 0 leans the pen top toward +y (down on the surface, toward the user),
    /// which is what CGEvent expects, so +1. Verify in Krita: leaning the pen toward you must tilt the brush toward you.
    /// </summary>
    private const double TiltYSign = 1.0;

    /// <summary>Samples older than this (after the learned latency) are re-anchored to now; see <see cref="TabletEventMath.MapTimestamp"/>.</summary>
    private const long MaxTimestampLagNs = 100_000_000;

    /// <summary>Points a second pen tap may move and still count as a double-click (finger taps: see <see cref="TabletEventMath.ClickSlopFor"/>).</summary>
    private const double ClickSlop = 6;

    /// <summary><c>kCGHIDEventTap</c>: post where hardware events enter, so they update cursor and HID button state.</summary>
    private const uint HidEventTap = (uint)CGEventTapLocation.HID;

    private const uint ButtonLeft = (uint)CGMouseButton.Left;
    private const uint ButtonRight = (uint)CGMouseButton.Right;

    /// <summary>The TCC database updates shortly after the Accessibility broadcast; check again after this delay.</summary>
    private static readonly TimeSpan AccessibilityRecheckDelay = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly CGEventSource _source;
    private readonly IntPtr _sourceHandle;
    private readonly long _doubleClickNs;
    private readonly NSObject _accessibilityObserver;
    private TabletPostedState _state;
    private HostClockAnchor _clock;
    private ClickState _leftClicks;
    private ClickState _rightClicks;
    private long _leftClickCount;
    private long _rightClickCount;
    private long _mouseEventNumber;
    private DisplayPoint _lastPoint;
    private PenSample _lastSample;
    private bool _targetAvailable = true;
    private bool _postsDropped;
    private bool _disposed;
    private volatile bool _trusted;
    private int _trustState = -1; // -1 unknown, 0 missing, 1 trusted; guards the log-once and the change event

    /// <param name="logger">Diagnostics (state changes only, never per sample).</param>
    public CGEventTabletInjector(ILogger<CGEventTabletInjector>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        // HID system state: the events behave like hardware input (cursor and button state of the HID system).
        _source = new CGEventSource(CGEventSourceStateID.HidSystem);
        _sourceHandle = _source.Handle;
        _doubleClickNs = (long)(NSEvent.DoubleClickInterval * 1e9);

        // Broadcast by the system when the Accessibility (AX trust) list changes.
        _accessibilityObserver = NSDistributedNotificationCenter.DefaultCenter.AddObserver(
            new NSString("com.apple.accessibility.api"), _ => OnAccessibilityBroadcast());
        RefreshAccessibility();
    }

    /// <summary>
    /// Raised on the main thread when <see cref="IsAccessibilityTrusted"/> changes (including the first check if the
    /// grant is missing).
    /// </summary>
    public event EventHandler? AccessibilityChanged;

    /// <summary>True when this process may post input events (Accessibility granted).</summary>
    public bool IsAccessibilityTrusted => _trusted;

    /// <summary>True when the Accessibility grant is missing: pen events are not posted. Show "Grant Accessibility…".</summary>
    public bool AccessibilityMissing => !_trusted;

    /// <summary>
    /// False while no display is available to map to (set by <see cref="DisplayTargetResolver.Bind"/>): everything
    /// posted is released and pen events are dropped until a display exists again.
    /// </summary>
    public bool IsTargetAvailable
    {
        get
        {
            lock (_gate)
            {
                return _targetAvailable;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_targetAvailable != value)
                {
                    _targetAvailable = value;
                    if (!value)
                    {
                        ReleaseLocked();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Re-checks <c>AXIsProcessTrusted()</c>, updates the state (raising the event on change) and returns it. On the
    /// missing → granted transition it repairs the window server's button state (see remarks).
    /// </summary>
    public bool RefreshAccessibility()
    {
        bool trusted = NativeMethods.AXIsProcessTrusted() != 0;
        int next = trusted ? 1 : 0;
        _trusted = trusted;
        int previous = Interlocked.Exchange(ref _trustState, next);
        if (previous == next)
        {
            return trusted;
        }

        if (trusted)
        {
            if (previous == 0)
            {
                InputLog.AccessibilityGranted(_logger);
                lock (_gate)
                {
                    RepairLocked();
                }
            }
        }
        else
        {
            InputLog.AccessibilityMissing(_logger);
        }

        // The first check only raises when missing: "trusted" is the state everyone assumes.
        if (previous != -1 || !trusted)
        {
            DispatchQueue.MainQueue.DispatchAsync(() => AccessibilityChanged?.Invoke(this, EventArgs.Empty));
        }

        return trusted;
    }

    /// <summary>
    /// Shows the system "PenBridge would like to control this computer" prompt (if not yet granted) and returns the
    /// current state. Call on the main thread. For a direct link use <see cref="AccessibilitySettingsUrl"/>.
    /// </summary>
    public bool RequestAccessibility()
    {
        using var key = new NSString("AXTrustedCheckOptionPrompt"); // kAXTrustedCheckOptionPrompt
        using var options = NSDictionary.FromObjectAndKey(NSNumber.FromBoolean(true), key);
        _ = NativeMethods.AXIsProcessTrustedWithOptions(options.Handle);
        return RefreshAccessibility();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Posts left-up, right-up and proximity-leave for anything still posted (pause, quit). Idempotent and safe from
    /// any thread. The next event re-enters proximity first (the planner ensures it even without a resync).
    /// </remarks>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            ReleaseLocked();
        }
    }

    /// <inheritdoc />
    public void OnPenEvent(in PenEvent e)
    {
        bool trusted = e.Kind == PenEventKind.ProximityEnter ? RefreshAccessibility() : _trusted;
        lock (_gate)
        {
            if (!_targetAvailable || _disposed)
            {
                return;
            }

            Span<TabletStep> steps = stackalloc TabletStep[TabletEventPlanner.MaxSteps];
            int count = TabletEventPlanner.Plan(ref _state, e.Kind, e.Button, e.Sample.Tool, steps, e.Sample.Buttons);
            _lastPoint = e.Position;
            _lastSample = e.Sample;
            if (count == 0)
            {
                return;
            }

            if (!trusted)
            {
                // The system would drop the posts; keep the planner in step and repair when the grant returns.
                _postsDropped = true;
                return;
            }

            long nowNs = TabletEventMath.TicksToNanoseconds(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
            long timestampNs = TabletEventMath.MapTimestamp(ref _clock, e.TimestampUs, nowNs, MaxTimestampLagNs);
            PostAll(steps[..count], e.Position, e.Sample, timestampNs);
        }
    }

    /// <inheritdoc />
    /// <remarks>Two-finger gestures (scroll, pinch) are Phase 4; they are ignored here.</remarks>
    public void OnGestureEvent(in GestureEvent e)
    {
    }

    /// <summary>Releases everything posted, stops observing Accessibility changes and frees the event source.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ReleaseLocked();
            _disposed = true;
        }

        NSDistributedNotificationCenter.DefaultCenter.RemoveObserver(_accessibilityObserver);
        _accessibilityObserver.Dispose();
        _source.Dispose();
    }

    private void OnAccessibilityBroadcast()
    {
        RefreshAccessibility();
        DispatchQueue.MainQueue.DispatchAfter(
            new DispatchTime(DispatchTime.Now, AccessibilityRecheckDelay), () => RefreshAccessibility());
    }

    private void ReleaseLocked()
    {
        if (_disposed)
        {
            return;
        }

        Span<TabletStep> steps = stackalloc TabletStep[TabletEventPlanner.MaxSteps];
        int count = TabletEventPlanner.Release(ref _state, steps);
        if (count == 0)
        {
            return;
        }

        if (_trusted)
        {
            PostReleaseSteps(steps[..count]);
        }
        else
        {
            _postsDropped = true; // the window server may still hold a press; repaired when the grant returns
        }
    }

    /// <summary>After the grant returns: release whatever the window server may still hold.</summary>
    private void RepairLocked()
    {
        if (_disposed)
        {
            return;
        }

        Span<TabletStep> steps = stackalloc TabletStep[TabletEventPlanner.MaxSteps];
        int count = _postsDropped
            ? TabletEventPlanner.Repair(ref _state, steps)
            : TabletEventPlanner.Release(ref _state, steps);
        _postsDropped = false;
        if (count > 0)
        {
            PostReleaseSteps(steps[..count]);
        }
    }

    private void PostReleaseSteps(ReadOnlySpan<TabletStep> steps)
    {
        long nowNs = TabletEventMath.TicksToNanoseconds(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        PostAll(steps, _lastPoint, _lastSample, TabletEventMath.Now(ref _clock, nowNs));
    }

    /// <summary>
    /// Posts every step independently: a step that fails (NULL event or an exception) is reverted in the planner
    /// state and the rest are still posted; the first exception is rethrown afterwards.
    /// </summary>
    private void PostAll(ReadOnlySpan<TabletStep> steps, DisplayPoint at, in PenSample sample, long timestampNs)
    {
        Exception? failure = null;
        for (int i = 0; i < steps.Length; i++)
        {
            bool posted = false;
            try
            {
                posted = PostStep(steps[i], at, sample, timestampNs);
            }
#pragma warning disable CA1031 // One failed post must not skip the remaining releases; rethrown below.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure ??= ex;
            }

            if (!posted)
            {
                TabletEventPlanner.Revert(ref _state, steps[i]);
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    private bool PostStep(in TabletStep step, DisplayPoint at, in PenSample sample, long timestampNs)
    {
        switch (step.Action)
        {
            case TabletAction.ProximityEnter:
                return PostProximity(enter: true, step.Tool, at, timestampNs);
            case TabletAction.ProximityLeave:
                return PostProximity(enter: false, step.Tool, at, timestampNs);
            case TabletAction.MouseMoved:
                return PostMouse(CGEventType.MouseMoved, ButtonLeft, clickState: 0, step, at, sample, timestampNs);
            case TabletAction.LeftDown:
                _mouseEventNumber++;
                _leftClickCount = TabletEventMath.NextClickCount(
                    ref _leftClicks, timestampNs, at, _doubleClickNs, TabletEventMath.ClickSlopFor(step.Tool, ClickSlop));
                return PostMouse(CGEventType.LeftMouseDown, ButtonLeft, _leftClickCount, step, at, sample, timestampNs);
            case TabletAction.LeftDragged:
                return PostMouse(CGEventType.LeftMouseDragged, ButtonLeft, _leftClickCount, step, at, sample, timestampNs);
            case TabletAction.LeftUp:
                return PostMouse(CGEventType.LeftMouseUp, ButtonLeft, _leftClickCount, step, at, sample, timestampNs);
            case TabletAction.RightDown:
                _mouseEventNumber++;
                _rightClickCount = TabletEventMath.NextClickCount(
                    ref _rightClicks, timestampNs, at, _doubleClickNs, TabletEventMath.ClickSlopFor(step.Tool, ClickSlop));
                return PostMouse(CGEventType.RightMouseDown, ButtonRight, _rightClickCount, step, at, sample, timestampNs);
            case TabletAction.RightDragged:
                return PostMouse(CGEventType.RightMouseDragged, ButtonRight, _rightClickCount, step, at, sample, timestampNs);
            case TabletAction.RightUp:
                return PostMouse(CGEventType.RightMouseUp, ButtonRight, _rightClickCount, step, at, sample, timestampNs);
            default:
                return true;
        }
    }

    /// <summary>Posts <c>kCGEventTabletProximity</c> with the pointer identity and capabilities.</summary>
    private bool PostProximity(bool enter, PenTool tool, DisplayPoint at, long timestampNs)
    {
        IntPtr ev = NativeMethods.CGEventCreate(_sourceHandle);
        if (ev == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            NativeMethods.CGEventSetType(ev, (uint)CGEventType.TabletProximity);
            NativeMethods.CGEventSetLocation(ev, new NativePoint(at.X, at.Y));
            SetInt(ev, CGEventField.TabletProximityEventEnterProximity, enter ? 1 : 0);
            SetInt(ev, CGEventField.TabletProximityEventPointerType, TabletMath.PointerType(tool));
            SetInt(ev, CGEventField.TabletProximityEventDeviceId, TabletConstants.DeviceId);
            SetInt(ev, CGEventField.TabletProximityEventPointerId, TabletConstants.PointerId);
            SetInt(ev, CGEventField.TabletProximityEventSystemTabletId, TabletConstants.TabletId);
            SetInt(ev, CGEventField.TabletProximityEventTabletId, TabletConstants.TabletId);
            SetInt(ev, CGEventField.TabletProximityEventCapabilityMask, TabletConstants.CapabilityMask);
            SetInt(ev, CGEventField.TabletProximityEventVendorPointerType, TabletConstants.VendorPointerType);
            SetInt(ev, CGEventField.TabletProximityEventVendorUniqueId, TabletConstants.VendorUniqueId);
            NativeMethods.CGEventSetTimestamp(ev, (ulong)timestampNs);
            NativeMethods.CGEventPost(HidEventTap, ev);
        }
        finally
        {
            NativeMethods.CFRelease(ev);
        }

        if (enter)
        {
            _lastPoint = at; // the first motion after entering reports no relative jump
        }

        return true;
    }

    /// <summary>Posts a mouse event with the tablet-point subtype and the pen's pressure, tilt and rotation.</summary>
    private bool PostMouse(
        CGEventType type, uint button, long clickState, in TabletStep step, DisplayPoint at, in PenSample sample, long timestampNs)
    {
        IntPtr ev = NativeMethods.CGEventCreateMouseEvent(_sourceHandle, (uint)type, new NativePoint(at.X, at.Y), button);
        if (ev == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            double pressure = step.Contact ? TabletEventMath.Pressure(sample.Pressure) : 0;
            long buttons = (step.Contact ? TabletConstants.ButtonTip : 0) | (step.Side ? TabletConstants.ButtonLowerSide : 0);
            SetInt(ev, CGEventField.MouseEventSubtype, (long)CGEventMouseSubtype.TabletPoint);
            SetInt(ev, CGEventField.MouseEventNumber, _mouseEventNumber);
            SetInt(ev, CGEventField.MouseEventClickState, clickState);
            SetDouble(ev, CGEventField.MouseEventPressure, pressure);
            SetInt(ev, CGEventField.MouseEventDeltaX, (long)Math.Round(at.X - _lastPoint.X));
            SetInt(ev, CGEventField.MouseEventDeltaY, (long)Math.Round(at.Y - _lastPoint.Y));
            SetInt(ev, CGEventField.TabletEventPointX, sample.X);
            SetInt(ev, CGEventField.TabletEventPointY, sample.Y);
            SetInt(ev, CGEventField.TabletEventPointButtons, buttons);
            SetDouble(ev, CGEventField.TabletEventPointPressure, pressure);
            SetDouble(ev, CGEventField.TabletEventTiltX, TabletEventMath.Tilt(sample.TiltX));
            SetDouble(ev, CGEventField.TabletEventTiltY, TiltYSign * TabletEventMath.Tilt(sample.TiltY));
            SetDouble(ev, CGEventField.TabletEventRotation, TabletEventMath.RotationDegrees(sample.Rotation));
            SetInt(ev, CGEventField.TabletEventDeviceId, TabletConstants.DeviceId);
            NativeMethods.CGEventSetTimestamp(ev, (ulong)timestampNs);
            NativeMethods.CGEventPost(HidEventTap, ev);
        }
        finally
        {
            NativeMethods.CFRelease(ev);
        }

        return true;
    }

    private static void SetInt(IntPtr ev, CGEventField field, long value) =>
        NativeMethods.CGEventSetIntegerValueField(ev, (uint)field, value);

    private static void SetDouble(IntPtr ev, CGEventField field, double value) =>
        NativeMethods.CGEventSetDoubleValueField(ev, (uint)field, value);
}
