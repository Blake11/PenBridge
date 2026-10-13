using System.Diagnostics;
using AppKit;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PenBridge.Core.Input;

namespace PenBridge.Mac.Input;

/// <summary>
/// Injects pen input into macOS as CGEvent tablet events (ADR-0003): proximity events announce a pen or eraser, then
/// mouse events with the tablet-point subtype carry position, pressure, tilt and rotation. The side button is a
/// right-click (ADR-0016). Gesture events are accepted and ignored until Phase 4.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> <see cref="OnPenEvent"/>/<see cref="OnGestureEvent"/> are called by
/// <c>PenBridgeServer</c> on thread-pool threads (UDP loop, TLS handler, watchdog timer), serialized by the server's
/// input lock. <see cref="ReleaseAll"/>, <see cref="IsPaused"/> and the Accessibility members may be called from any
/// thread (typically the main thread). An internal lock serializes posting, so the posted button state is never
/// torn; the lock is uncontended on the hot path. CGEventPost is thread-safe and never touches AppKit.
/// <see cref="AccessibilityChanged"/> is always raised on the main thread.</para>
/// <para><b>Allocation.</b> Per event, the only allocations are the CGEvent wrappers, which are disposed (released)
/// right after posting. The CGEventSource is created once. The planner works on a stack buffer.</para>
/// <para><b>Accessibility.</b> Without the Accessibility grant, CGEventPost silently drops events, so the injector
/// drops them itself, reports <see cref="AccessibilityMissing"/> and logs once. The grant is re-checked on every
/// proximity enter, when the system broadcasts an Accessibility change, and on <see cref="RefreshAccessibility"/>
/// (for example when the menu opens) — never by polling.</para>
/// <para><b>Safety.</b> Posted state (proximity, left, right) is tracked against what was actually posted, so leave,
/// pause, disconnect (the server's synthesized release) and <see cref="ReleaseAll"/> post every outstanding up and the
/// proximity leave; nothing stays stuck.</para>
/// </remarks>
public sealed class CGEventTabletInjector : IInputSink, IDisposable
{
    /// <summary>The System Settings pane where the user grants Accessibility.</summary>
    public const string AccessibilitySettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    /// <summary>Samples older than this (after the learned latency) are re-anchored to now; see <see cref="TabletMath.MapTimestamp"/>.</summary>
    private const long MaxTimestampLagNs = 100_000_000;

    /// <summary>Points a second tap may move and still count as a double-click.</summary>
    private const double ClickSlop = 6;

    /// <summary>The TCC database updates shortly after the Accessibility broadcast; check again after this delay.</summary>
    private static readonly TimeSpan AccessibilityRecheckDelay = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly CGEventSource _source;
    private readonly long _doubleClickNs;
    private readonly NSObject _accessibilityObserver;
    private PostedState _state;
    private ClockAnchor _clock;
    private ClickState _leftClicks;
    private ClickState _rightClicks;
    private long _leftClickCount;
    private long _rightClickCount;
    private long _mouseEventNumber;
    private DisplayPoint _lastPoint;
    private PenSample _lastSample;
    private bool _paused;
    private bool _disposed;
    private volatile bool _trusted;
    private int _trustState = -1; // -1 unknown, 0 missing, 1 trusted; guards the log-once and the change event

    /// <param name="logger">Diagnostics (state changes only, never per sample).</param>
    public CGEventTabletInjector(ILogger<CGEventTabletInjector>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        // HID system state: the events behave like hardware input (cursor and button state of the HID system).
        _source = new CGEventSource(CGEventSourceStateID.HidSystem);
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

    /// <summary>True when the Accessibility grant is missing: pen events are dropped. Show "Grant Accessibility…".</summary>
    public bool AccessibilityMissing => !_trusted;

    /// <summary>
    /// When true, everything posted is released and pen events are dropped (the session stays connected). Resuming
    /// re-enters proximity on the next event; a stroke in progress continues as hover until the next pen-down.
    /// </summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_paused == value)
                {
                    return;
                }

                _paused = value;
                if (value)
                {
                    ReleaseLocked();
                }
            }
        }
    }

    /// <summary>Re-checks <c>AXIsProcessTrusted()</c>, updates the state (raising the event on change) and returns it.</summary>
    public bool RefreshAccessibility()
    {
        bool trusted = NativeMethods.AXIsProcessTrusted() != 0;
        int next = trusted ? 1 : 0;
        _trusted = trusted;
        int previous = Interlocked.Exchange(ref _trustState, next);
        if (previous != next)
        {
            if (trusted)
            {
                if (previous == 0)
                {
                    InputLog.AccessibilityGranted(_logger);
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

    /// <summary>
    /// Posts left-up, right-up and proximity-leave for anything still posted (pause, disconnect, quit). Idempotent and
    /// safe from any thread.
    /// </summary>
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
        if (!trusted)
        {
            return;
        }

        lock (_gate)
        {
            if (_paused || _disposed)
            {
                return;
            }

            Span<TabletStep> steps = stackalloc TabletStep[TabletEventPlanner.MaxSteps];
            int count = TabletEventPlanner.Plan(ref _state, e.Kind, e.Button, e.Sample.Tool, steps);
            if (count == 0)
            {
                return;
            }

            long nowNs = TabletMath.StopwatchToNanoseconds(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
            long timestampNs = TabletMath.MapTimestamp(ref _clock, e.TimestampUs, nowNs, MaxTimestampLagNs);
            for (int i = 0; i < count; i++)
            {
                PostStep(steps[i], e.Position, e.Sample, timestampNs);
            }

            _lastPoint = e.Position;
            _lastSample = e.Sample;
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

        long nowNs = TabletMath.StopwatchToNanoseconds(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        long timestampNs = Math.Max(nowNs, _clock.LastNs);
        _clock.LastNs = timestampNs;
        for (int i = 0; i < count; i++)
        {
            PostStep(steps[i], _lastPoint, _lastSample, timestampNs);
        }
    }

    private void PostStep(in TabletStep step, DisplayPoint at, in PenSample sample, long timestampNs)
    {
        switch (step.Action)
        {
            case TabletAction.ProximityEnter:
                PostProximity(enter: true, step.Tool, at, timestampNs);
                break;
            case TabletAction.ProximityLeave:
                PostProximity(enter: false, step.Tool, at, timestampNs);
                break;
            case TabletAction.MouseMoved:
                PostMouse(CGEventType.MouseMoved, CGMouseButton.Left, clickState: 0, step, at, sample, timestampNs);
                break;
            case TabletAction.LeftDown:
                _mouseEventNumber++;
                _leftClickCount = TabletMath.NextClickCount(ref _leftClicks, timestampNs, at, _doubleClickNs, ClickSlop);
                PostMouse(CGEventType.LeftMouseDown, CGMouseButton.Left, _leftClickCount, step, at, sample, timestampNs);
                break;
            case TabletAction.LeftDragged:
                PostMouse(CGEventType.LeftMouseDragged, CGMouseButton.Left, _leftClickCount, step, at, sample, timestampNs);
                break;
            case TabletAction.LeftUp:
                PostMouse(CGEventType.LeftMouseUp, CGMouseButton.Left, _leftClickCount, step, at, sample, timestampNs);
                break;
            case TabletAction.RightDown:
                _mouseEventNumber++;
                _rightClickCount = TabletMath.NextClickCount(ref _rightClicks, timestampNs, at, _doubleClickNs, ClickSlop);
                PostMouse(CGEventType.RightMouseDown, CGMouseButton.Right, _rightClickCount, step, at, sample, timestampNs);
                break;
            case TabletAction.RightDragged:
                PostMouse(CGEventType.RightMouseDragged, CGMouseButton.Right, _rightClickCount, step, at, sample, timestampNs);
                break;
            case TabletAction.RightUp:
                PostMouse(CGEventType.RightMouseUp, CGMouseButton.Right, _rightClickCount, step, at, sample, timestampNs);
                break;
        }
    }

    /// <summary>Posts <c>kCGEventTabletProximity</c> with the pointer identity and capabilities.</summary>
    private void PostProximity(bool enter, PenTool tool, DisplayPoint at, long timestampNs)
    {
        using var ev = new CGEvent(_source);
        ev.EventType = CGEventType.TabletProximity;
        ev.Location = new CGPoint(at.X, at.Y);
        ev.SetValueField(CGEventField.TabletProximityEventEnterProximity, enter ? 1L : 0L);
        ev.SetValueField(CGEventField.TabletProximityEventPointerType, TabletMath.PointerType(tool));
        ev.SetValueField(CGEventField.TabletProximityEventDeviceId, TabletConstants.DeviceId);
        ev.SetValueField(CGEventField.TabletProximityEventPointerId, TabletConstants.PointerId);
        ev.SetValueField(CGEventField.TabletProximityEventSystemTabletId, TabletConstants.TabletId);
        ev.SetValueField(CGEventField.TabletProximityEventTabletId, TabletConstants.TabletId);
        ev.SetValueField(CGEventField.TabletProximityEventCapabilityMask, TabletConstants.CapabilityMask);
        ev.SetValueField(CGEventField.TabletProximityEventVendorPointerType, TabletConstants.VendorPointerType);
        ev.SetValueField(CGEventField.TabletProximityEventVendorUniqueId, TabletConstants.VendorUniqueId);
        ev.Timestamp = (ulong)timestampNs;
        CGEvent.Post(ev, CGEventTapLocation.HID);
        if (enter)
        {
            _lastPoint = at; // the first motion after entering reports no relative jump
        }
    }

    /// <summary>Posts a mouse event with the tablet-point subtype and the pen's pressure, tilt and rotation.</summary>
    private void PostMouse(
        CGEventType type, CGMouseButton button, long clickState, in TabletStep step, DisplayPoint at, in PenSample sample, long timestampNs)
    {
        double pressure = step.Contact ? TabletMath.Pressure(sample.Pressure) : 0;
        long buttons = (step.Contact ? TabletConstants.ButtonTip : 0) | (step.Side ? TabletConstants.ButtonLowerSide : 0);

        using var ev = new CGEvent(_source, type, new CGPoint(at.X, at.Y), button);
        ev.MouseEventSubtype = (long)CGEventMouseSubtype.TabletPoint;
        ev.MouseEventNumber = _mouseEventNumber;
        ev.MouseEventClickState = clickState;
        ev.MouseEventPressure = pressure;
        ev.MouseEventDeltaX = (long)Math.Round(at.X - _lastPoint.X);
        ev.MouseEventDeltaY = (long)Math.Round(at.Y - _lastPoint.Y);
        ev.SetValueField(CGEventField.TabletEventPointX, (long)sample.X);
        ev.SetValueField(CGEventField.TabletEventPointY, (long)sample.Y);
        ev.SetValueField(CGEventField.TabletEventPointButtons, buttons);
        ev.SetValueField(CGEventField.TabletEventPointPressure, pressure);
        ev.SetValueField(CGEventField.TabletEventTiltX, TabletMath.Tilt(sample.TiltX));
        ev.SetValueField(CGEventField.TabletEventTiltY, TabletMath.Tilt(sample.TiltY));
        ev.SetValueField(CGEventField.TabletEventRotation, TabletMath.RotationDegrees(sample.Rotation));
        ev.SetValueField(CGEventField.TabletEventDeviceId, TabletConstants.DeviceId);
        ev.Timestamp = (ulong)timestampNs;
        CGEvent.Post(ev, CGEventTapLocation.HID);
    }
}
