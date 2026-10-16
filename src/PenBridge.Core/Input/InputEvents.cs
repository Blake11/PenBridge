namespace PenBridge.Core.Input;

/// <summary>Edge events produced by <see cref="PenStateTracker"/>.</summary>
public enum PenEventKind : byte
{
    /// <summary>The pen entered hover range. Sample carries the entry position and tool.</summary>
    ProximityEnter = 1,
    /// <summary>The pen left hover range. Any contact and buttons were released before this event.</summary>
    ProximityLeave = 2,
    /// <summary>The pen touched the surface.</summary>
    Down = 3,
    /// <summary>The pen moved while hovering (in range, no contact).</summary>
    Move = 4,
    /// <summary>The pen moved, or its pressure/tilt/rotation changed, while in contact.</summary>
    Drag = 5,
    /// <summary>The pen lifted off the surface.</summary>
    Up = 6,
    /// <summary>A barrel button was pressed. <see cref="PenEvent.Button"/> holds the single button.</summary>
    ButtonDown = 7,
    /// <summary>A barrel button was released. <see cref="PenEvent.Button"/> holds the single button.</summary>
    ButtonUp = 8,
    /// <summary>The tool changed while in range (e.g. pen to eraser). Contact and buttons of the old tool were released first.</summary>
    ToolChanged = 9,
}

/// <summary>
/// One pen edge event. <see cref="Sample"/> is the normalized sample after the input settings (ADR-0032): its pressure
/// is curved (<see cref="PressureCurve"/>; a contact below the click threshold arrives as hover), and with
/// <see cref="SideButtonAction.Eraser"/> its tool is <see cref="PenTool.Eraser"/> while the side button is held (or was
/// held at contact-down), with the button itself removed. <see cref="Position"/> is where to post it in global display
/// points: in <see cref="StylusMode.Pen"/> the sample mapped by the tracker's <see cref="CoordinateMapper"/>; in
/// <see cref="StylusMode.Mouse"/> the tracker's relative cursor (anchored at the current cursor on entering range,
/// moved by the pen's movement, kept on the displays).
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Sample">The sample the event was derived from (for synthesized releases: the last known sample with contact/pressure/buttons cleared as appropriate).</param>
/// <param name="Position">Mapped position in global display points.</param>
/// <param name="Button">For ButtonDown/ButtonUp: the single button that changed; otherwise <see cref="PenButtons.None"/>.</param>
/// <param name="PreviousTool">For ToolChanged: the tool before the change; otherwise <see cref="PenTool.None"/>.</param>
/// <param name="Synthetic">True when the event was synthesized by the watchdog or a reset rather than received.</param>
/// <param name="TimestampUs">Monotonic µs since session start (the datagram <c>timeUs</c> unwrapped to 64 bits; history samples are spaced before it).</param>
/// <param name="SideButton">
/// The side-button action in effect (constant through a stroke); pass it to <see cref="TabletEventPlanner.Plan"/>.
/// </param>
public readonly record struct PenEvent(
    PenEventKind Kind,
    PenSample Sample,
    DisplayPoint Position,
    PenButtons Button,
    PenTool PreviousTool,
    bool Synthetic,
    long TimestampUs,
    SideButtonAction SideButton = SideButtonAction.RightClick);

/// <summary>
/// Gesture kinds produced by <see cref="GestureRecognizer"/> (ADR-0023, trackpad parity). The kind of a moving
/// gesture is decided by the most fingers seen down before a threshold was crossed: 2 = Scroll or Pinch, 3 = Drag,
/// 4 = SystemSwipe or SystemPinch, 5 or more = nothing. One finger (PointerMove, Click, PointerDrag; ADR-0026) only
/// with <see cref="GestureRecognizerOptions.OneFingerCursor"/>, i.e. when the session negotiated
/// <c>ProtocolCapabilities.FingerTouches</c>.
/// </summary>
public enum GestureKind : byte
{
    /// <summary>
    /// Two-finger pan (exclusive of Pinch): Began / Changed with relative, accelerated deltas, then Ended (momentum may
    /// follow) or Cancelled. Act at the current cursor. A scroll that starts straight enough is locked to that axis
    /// (<see cref="GestureRecognizerOptions.ScrollAxisLockRatio"/>): the other delta is 0 in all its events.
    /// </summary>
    Scroll = 1,

    /// <summary>
    /// Two-finger pinch/rotate transform: Began / Changed with <see cref="GestureEvent.ScaleDelta"/> and
    /// <see cref="GestureEvent.RotationDelta"/> together, then Ended or Cancelled. Starts from a spread change, a
    /// rotation, or both.
    /// </summary>
    Pinch = 2,

    /// <summary>
    /// Two-finger tap, a secondary click at the current cursor. One event with <see cref="GesturePhase.Ended"/>. With
    /// smart zoom off (<see cref="GestureRecognizerOptions.SmartZoomEnabled"/>) it is emitted on the lift; otherwise
    /// once it can no longer become a double tap: the double-tap window passed (up to
    /// <see cref="GestureRecognizerOptions.DoubleTapWindow"/> after the lift), or other input interrupts it (pen or
    /// cursor hover, a gesture, a third finger), in which case it comes just before that input's events. Only a cancel
    /// (watchdog, pause, session end) drops it.
    /// </summary>
    Tap = 3,

    /// <summary>
    /// A finger landed: a contact appeared that was not in the previous frame, while no gesture was active and gestures
    /// were not blocked (pen, cancel). One informational event per such frame with <see cref="GesturePhase.Began"/>, no deltas, at the first
    /// new contact's position. The Mac uses it to stop a scroll coast at once (a finger landing stops momentum).
    /// Never active; nothing ends it.
    /// </summary>
    TouchDown = 4,

    /// <summary>
    /// Two-finger double tap (smart zoom at the current cursor). One event with <see cref="GesturePhase.Ended"/>, on the
    /// second tap's lift; no Tap is emitted for either tap.
    /// </summary>
    SmartZoom = 5,

    /// <summary>
    /// Three-finger drag: a left-button drag that moves the cursor by <see cref="GestureEvent.DeltaX"/>/
    /// <see cref="GestureEvent.DeltaY"/> (relative, accelerated). Began / Changed, then Ended when a finger lifts
    /// (release the button) or Cancelled.
    /// </summary>
    Drag = 6,

    /// <summary>
    /// Four-finger swipe: <see cref="GestureEvent.Direction"/> (Up/Down/Left/Right, locked at Began) and cumulative
    /// <see cref="GestureEvent.Progress"/>. Began / Changed / Ended / Cancelled. Up = Mission Control, Down = App Exposé,
    /// Left/Right = switch Space.
    /// </summary>
    SystemSwipe = 7,

    /// <summary>
    /// Four-finger pinch: <see cref="GestureEvent.Direction"/> (In = Launchpad, Out = show desktop, locked at Began) and
    /// cumulative <see cref="GestureEvent.Progress"/>. Began / Changed / Ended / Cancelled.
    /// </summary>
    SystemPinch = 8,

    /// <summary>
    /// One finger moves the cursor (ADR-0026), no button. Began / Changed with <see cref="GestureEvent.DeltaX"/>/
    /// <see cref="GestureEvent.DeltaY"/>: the finger's movement since the previous event in display points, through
    /// <see cref="GestureRecognizerOptions.DragGain"/> and <see cref="GestureRecognizerOptions.DragAcceleration"/>.
    /// Began comes when the finger first moves beyond <see cref="GestureRecognizerOptions.PointerSlop"/> of where it
    /// landed (beyond <see cref="GestureRecognizerOptions.TapSlop"/> during the first
    /// <see cref="GestureRecognizerOptions.PointerSettleTime"/>, while a second finger may still land) and carries the whole movement since the landing (the cursor catches up; nothing is lost to the slop).
    /// Only when the phone's finger fallback is on (<see cref="GestureRecognizer.PhoneFingerFallback"/>). Post a mouse-moved event at the current cursor plus the delta, clamped to the displays, for each
    /// Began/Changed. Ended (the finger lifted, or a second finger landed) and Cancelled post nothing.
    /// </summary>
    PointerMove = 9,

    /// <summary>
    /// One-finger tap = left click at the current cursor (ADR-0026). One event with <see cref="GesturePhase.Ended"/>,
    /// emitted on the lift with no delay, and <see cref="GestureEvent.ClickCount"/> = 1, or one more than the previous
    /// Click when this tap landed within <see cref="GestureRecognizerOptions.ClickWindow"/> after it lifted and
    /// within <see cref="GestureRecognizerOptions.ClickSlop"/> of where it landed (2 = double click, 3 = triple).
    /// Post a left mouse-down and mouse-up at the current cursor with that click state.
    /// </summary>
    Click = 10,

    /// <summary>
    /// One-finger "tap and a half" = left drag from the current cursor (ADR-0026): a touch landing within
    /// <see cref="GestureRecognizerOptions.ClickWindow"/> and <see cref="GestureRecognizerOptions.ClickSlop"/> of
    /// a Click that then moves beyond the pointer slop (as for PointerMove), or stays down longer than
    /// <see cref="GestureRecognizerOptions.TapMaxDuration"/>. Began: press the left button at the current cursor
    /// (click state 1, never counted as a double click), then move by the deltas (0 when it began by holding still);
    /// Changed: drag by the deltas (same units and gain as PointerMove; Began catches up the movement since landing); Ended (lift, or a second finger landed) or
    /// Cancelled: release the button at the current cursor. Always released: the head must post the mouse-up for both.
    /// </summary>
    PointerDrag = 11,
}

/// <summary>Direction of a <see cref="GestureKind.SystemSwipe"/> (finger movement, y down) or <see cref="GestureKind.SystemPinch"/>.</summary>
public enum GestureDirection : byte
{
    None = 0,
    /// <summary>Fingers moved up the screen.</summary>
    Up = 1,
    /// <summary>Fingers moved down the screen.</summary>
    Down = 2,
    /// <summary>Fingers moved left.</summary>
    Left = 3,
    /// <summary>Fingers moved right.</summary>
    Right = 4,
    /// <summary>Fingers pinched together.</summary>
    In = 5,
    /// <summary>Fingers spread apart.</summary>
    Out = 6,
}

/// <summary>Gesture phase.</summary>
public enum GesturePhase : byte
{
    Began = 1,
    Changed = 2,
    /// <summary>The fingers lifted normally (Scroll: momentum may follow). Also the phase of the one-shot Tap and SmartZoom events.</summary>
    Ended = 3,
    /// <summary>The gesture was aborted (pen came into range, the watchdog fired, session end). No momentum should follow.</summary>
    Cancelled = 4,
}

/// <summary>
/// One gesture event. Deltas and the center are in global display points (via the recognizer's
/// <see cref="CoordinateMapper"/>, top-left origin, y down); a positive <see cref="DeltaY"/> means the fingers moved
/// down. Gestures act at the current cursor position (ADR-0023); <see cref="Center"/> is informational only.
/// </summary>
/// <param name="Kind">See <see cref="GestureKind"/>.</param>
/// <param name="Phase">Began / Changed / Ended / Cancelled (Tap, SmartZoom and Click: always Ended; TouchDown: always Began).</param>
/// <param name="DeltaX">
/// Scroll and Drag: centroid movement since the previous event (Began: since the gesture's fingers settled),
/// multiplied by the kind's gain and its acceleration at the current speed
/// (<see cref="GestureRecognizerOptions.ScrollGain"/>/<see cref="GestureRecognizerOptions.ScrollAcceleration"/>,
/// <see cref="GestureRecognizerOptions.DragGain"/>/<see cref="GestureRecognizerOptions.DragAcceleration"/>).
/// PointerMove and PointerDrag: the finger's movement since the previous event, with Drag's gain and acceleration.
/// 0 for every other kind and for Ended/Cancelled.
/// </param>
/// <param name="DeltaY">See <paramref name="DeltaX"/>.</param>
/// <param name="ScaleDelta">Pinch: multiplicative finger-spread change since the previous event (Began: since landing). 1 otherwise.</param>
/// <param name="RotationDelta">
/// Pinch: change in the angle of the line between the two fingers since the previous event (Began: since landing), in
/// degrees, unwrapped (never jumps at ±180°). Measured in display coordinates (y down), so a positive value is a
/// <b>clockwise</b> turn as seen on the screen (AppKit's rotation is counterclockwise-positive: negate it). 0 otherwise.
/// </param>
/// <param name="Direction">SystemSwipe/SystemPinch: the locked direction (also on Ended/Cancelled). None otherwise.</param>
/// <param name="Progress">
/// SystemSwipe: cumulative centroid travel along the locked axis toward <paramref name="Direction"/> since the fingers
/// settled; SystemPinch: cumulative change of the finger spread (twice the mean distance from the centroid) toward
/// <paramref name="Direction"/>. Both as a fraction of the mapped short side, so about 0..1, negative when the
/// fingers went back past the start. Ended/Cancelled carry the last value. 0 for other kinds.
/// </param>
/// <param name="Center">
/// Informational: the current centroid of the gesture's fingers (Tap/SmartZoom: the landing midpoint; TouchDown: the
/// new contact; Click: where the finger landed). Never a cursor position: act at the current cursor.
/// </param>
/// <param name="Synthetic">True when synthesized by the watchdog or a reset.</param>
/// <param name="TimestampUs">Monotonic µs since session start, same clock as <see cref="PenEvent.TimestampUs"/>.</param>
/// <param name="ClickCount">Click: the click state to post (1 single, 2 double, …). 0 for every other kind.</param>
public readonly record struct GestureEvent(
    GestureKind Kind,
    GesturePhase Phase,
    double DeltaX,
    double DeltaY,
    double ScaleDelta,
    double RotationDelta,
    GestureDirection Direction,
    double Progress,
    DisplayPoint Center,
    bool Synthetic,
    long TimestampUs,
    int ClickCount = 0);

/// <summary>
/// Receives Mac-side input events from <see cref="PenStateTracker"/> and <see cref="GestureRecognizer"/>.
/// Called synchronously on the thread that feeds the tracker; implementations must not block and should not allocate.
/// </summary>
public interface IInputSink
{
    void OnPenEvent(in PenEvent e);

    void OnGestureEvent(in GestureEvent e);
}

/// <summary>
/// Optionally implemented by an <see cref="IInputSink"/> (or a wrapper that forwards it) to report the host's current
/// cursor location. In <see cref="StylusMode.Mouse"/> the tracker reads it once each time the pen enters range (or the
/// mode switches on), to anchor the relative cursor there, so re-entering never jumps the cursor. Without it the
/// tracker keeps its own last cursor (initially the centre of the target).
/// </summary>
public interface ICursorLocator
{
    /// <summary>The cursor in global display points. Called under the input lock: must be quick and must not block.</summary>
    bool TryGetCursorLocation(out DisplayPoint location);
}
