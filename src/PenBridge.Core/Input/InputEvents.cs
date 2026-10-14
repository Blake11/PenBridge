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
/// One pen edge event. <see cref="Sample"/> is the raw normalized sample; <see cref="Position"/> is the same point
/// mapped to global display points by the tracker's <see cref="CoordinateMapper"/>.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Sample">The sample the event was derived from (for synthesized releases: the last known sample with contact/pressure/buttons cleared as appropriate).</param>
/// <param name="Position">Mapped position in global display points.</param>
/// <param name="Button">For ButtonDown/ButtonUp: the single button that changed; otherwise <see cref="PenButtons.None"/>.</param>
/// <param name="PreviousTool">For ToolChanged: the tool before the change; otherwise <see cref="PenTool.None"/>.</param>
/// <param name="Synthetic">True when the event was synthesized by the watchdog or a reset rather than received.</param>
/// <param name="TimestampUs">Monotonic µs since session start (the datagram <c>timeUs</c> unwrapped to 64 bits; history samples are spaced before it).</param>
public readonly record struct PenEvent(
    PenEventKind Kind,
    PenSample Sample,
    DisplayPoint Position,
    PenButtons Button,
    PenTool PreviousTool,
    bool Synthetic,
    long TimestampUs);

/// <summary>Gesture kinds produced by <see cref="GestureRecognizer"/>.</summary>
public enum GestureKind : byte
{
    /// <summary>Two-finger pan: centroid deltas (Began/Changed), then Ended or Cancelled. Exclusive.</summary>
    Scroll = 1,

    /// <summary>
    /// Pinch/rotate transform: scale and rotation deltas together (Began/Changed), then Ended or Cancelled. Starts
    /// from a spread change, a rotation, or both (ADR-0023).
    /// </summary>
    Pinch = 2,

    /// <summary>
    /// Two-finger tap: a single event with <see cref="GesturePhase.Ended"/> at the fingers' landing midpoint, emitted
    /// when the last finger lifts. Never preceded by Began and never active.
    /// </summary>
    Tap = 3,
}

/// <summary>Gesture phase.</summary>
public enum GesturePhase : byte
{
    Began = 1,
    Changed = 2,
    /// <summary>The fingers lifted normally (Scroll: momentum may follow). Also the phase of the single Tap event.</summary>
    Ended = 3,
    /// <summary>The gesture was aborted (pen came into range, or the watchdog fired). No momentum should follow.</summary>
    Cancelled = 4,
}

/// <summary>
/// One gesture event. Deltas and the center are in global display points (via the recognizer's
/// <see cref="CoordinateMapper"/>, top-left origin, y down); a positive <see cref="DeltaY"/> means the fingers moved down.
/// </summary>
/// <param name="Kind">Scroll, Pinch (pinch/rotate transform) or Tap.</param>
/// <param name="Phase">Began / Changed / Ended / Cancelled (Tap: always Ended).</param>
/// <param name="DeltaX">
/// Scroll: centroid movement since the previous event (Began: since the fingers landed), multiplied by
/// <see cref="GestureRecognizerOptions.ScrollGain"/>. 0 for Pinch, Tap and Ended/Cancelled.
/// </param>
/// <param name="DeltaY">Scroll: see <paramref name="DeltaX"/>.</param>
/// <param name="ScaleDelta">Pinch: multiplicative finger-spread change since the previous event (Began: since landing). 1 otherwise.</param>
/// <param name="RotationDelta">
/// Pinch: change in the angle of the line between the two fingers since the previous event (Began: since landing), in
/// degrees, unwrapped (never jumps at ±180°). Measured in display coordinates (y down), so a positive value is a
/// <b>clockwise</b> turn as seen on the screen (AppKit's rotation is counterclockwise-positive: negate it). 0 otherwise.
/// </param>
/// <param name="Center">Current centroid of the two tracked fingers in display points (Tap: their landing midpoint).</param>
/// <param name="Synthetic">True when synthesized by the watchdog or a reset.</param>
/// <param name="TimestampUs">Monotonic µs since session start, same clock as <see cref="PenEvent.TimestampUs"/>.</param>
public readonly record struct GestureEvent(
    GestureKind Kind,
    GesturePhase Phase,
    double DeltaX,
    double DeltaY,
    double ScaleDelta,
    double RotationDelta,
    DisplayPoint Center,
    bool Synthetic,
    long TimestampUs);

/// <summary>
/// Receives Mac-side input events from <see cref="PenStateTracker"/> and <see cref="GestureRecognizer"/>.
/// Called synchronously on the thread that feeds the tracker; implementations must not block and should not allocate.
/// </summary>
public interface IInputSink
{
    void OnPenEvent(in PenEvent e);

    void OnGestureEvent(in GestureEvent e);
}
