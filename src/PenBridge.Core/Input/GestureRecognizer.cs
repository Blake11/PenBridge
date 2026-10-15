namespace PenBridge.Core.Input;

/// <summary>Options for <see cref="GestureRecognizer"/>. Distances are fractions of the mapped content's shorter side.</summary>
public sealed record GestureRecognizerOptions
{
    /// <summary>Two fingers: centroid travel that starts a scroll.</summary>
    public double ScrollThreshold { get; init; } = 0.04;

    /// <summary>Two fingers: finger-spread change that starts a pinch/rotate transform.</summary>
    public double PinchThreshold { get; init; } = 0.04;

    /// <summary>
    /// Two fingers: rotation arc (the turned angle in radians × half the finger spread, i.e. how far each finger
    /// travelled around the centroid) that starts a pinch/rotate transform.
    /// </summary>
    public double RotationThreshold { get; init; } = 0.04;

    /// <summary>
    /// Multiplier applied to scroll deltas before acceleration. 1 means content moves 1:1 with the fingers in mapped
    /// display points at slow speeds. Must be positive and finite.
    /// </summary>
    public double ScrollGain { get; init; } = 1.0;

    /// <summary>Speed-dependent gain on scroll deltas (momentum then coasts on the accelerated deltas).</summary>
    public GestureAcceleration ScrollAcceleration { get; init; } = new();

    /// <summary>
    /// Axis lock for a two-finger scroll, decided by the movement that starts it (the one crossing
    /// <see cref="ScrollThreshold"/>): if the cross-axis movement is at most this ratio of the main-axis movement, the
    /// scroll carries only the main axis until it ends (the other delta is 0 in every event, so momentum coasts along
    /// it only); otherwise (diagonal) both axes stay free. 0.5 is about 27° off the axis. Fingers drift a little across
    /// a straight swipe, and a horizontal scroll with vertical drift scrolls a vertical-only view (Finder icon view,
    /// #123). 0 locks only an exactly straight start. Must be finite and not negative.
    /// </summary>
    public double ScrollAxisLockRatio { get; init; } = 0.5;

    /// <summary>Three fingers: centroid travel that starts a drag.</summary>
    public double DragThreshold { get; init; } = 0.02;

    /// <summary>Multiplier applied to three-finger drag deltas before acceleration. Must be positive and finite.</summary>
    public double DragGain { get; init; } = 1.0;

    /// <summary>Speed-dependent gain on three-finger drag deltas.</summary>
    public GestureAcceleration DragAcceleration { get; init; } = new();

    /// <summary>Four fingers: centroid travel that starts a system swipe.</summary>
    public double SwipeThreshold { get; init; } = 0.05;

    /// <summary>Four fingers: finger-spread change that starts a system pinch.</summary>
    public double SystemPinchThreshold { get; init; } = 0.05;

    /// <summary>
    /// A two-finger tap lasts at most this long, from the first finger landing to the last lifting. A relaxed tap
    /// lands and lifts its two fingers a few tens of ms apart, so it often takes over 200 ms; a press held for 300 ms
    /// is still well short of a long press (400-500 ms).
    /// </summary>
    public TimeSpan TapMaxDuration { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How far each finger of a two-finger tap may move from where it landed. A finger's reported position drifts as
    /// its contact patch rolls and shrinks on lift (1-2 mm on a phone; 3.5% of a ~70 mm short side is ~2.5 mm). Keep it
    /// below <see cref="ScrollThreshold"/>: movement that is too small to scroll can still be a tap, never both. The gap
    /// between them is deliberately small, so a nudge too short to scroll is a tap only if it also lifts within
    /// <see cref="TapMaxDuration"/>.
    /// </summary>
    public double TapSlop { get; init; } = 0.035;

    /// <summary>
    /// A two-finger tap is held this long after its lift: a second two-finger tap landing within it (and within
    /// <see cref="DoubleTapSlop"/>) makes a SmartZoom instead. Zero emits taps at once (no smart zoom). It delays every
    /// secondary click by this much (plus <see cref="TapDeadlineJitterAllowance"/>), so it trades a relaxed double
    /// tap's lift-to-landing gap (usually 200-280 ms) against right-click latency: 300 ms keeps the right-click near
    /// a third of a second after the lift while still catching a relaxed double tap, short of the macOS double-click
    /// interval (500 ms by default). The tap and double-tap slops, not a long window, are what make relaxed double
    /// taps register.
    /// </summary>
    public TimeSpan DoubleTapWindow { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How near the second tap's landing midpoint must be to the first's for a SmartZoom. Two fingers re-landing
    /// without looking rarely hit the same spot: 20% of a ~70 mm short side is ~14 mm, below Android's double-tap
    /// slop (100 dp, ~16 mm). Two separate secondary clicks that close together inside the window are rare.
    /// </summary>
    public double DoubleTapSlop { get; init; } = 0.2;

    /// <summary>
    /// Added to the double-tap window for the timer-driven deadline (<see cref="GestureRecognizer.PendingDeadlineUs"/>),
    /// which runs on host time estimated from the last frame: a second tap's landing datagram delayed by network
    /// jitter still arrives before the timer gives up on it. Frames themselves are judged on the frame clock, without it.
    /// </summary>
    public TimeSpan TapDeadlineJitterAllowance { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// One finger moves the cursor relatively, taps click and tap-and-a-half drags (ADR-0026): PointerMove, Click and
    /// PointerDrag. Off by default: a lone finger is no gesture. The server sets it per session from the negotiated
    /// <c>ProtocolCapabilities.FingerTouches</c> (only then does the phone send a lone finger as touches).
    /// </summary>
    public bool OneFingerCursor { get; init; }

    /// <summary>
    /// One finger: how far it may move from where it landed and still be a tap. Until it moves farther the cursor stays
    /// put, so a second finger landing (a two-finger gesture) never nudges it; once it does, PointerMove Began catches
    /// up the whole movement since the landing. 0.015 of the short side is about 1 mm on an S23 Ultra (≈70 mm short
    /// side), about 13 pt when the phone's landscape surface fills a 1920×1080 pt display (≈895 pt short side). Kept
    /// small so the cursor starts at once; a tap that drifts farther than this is a move, not a click.
    /// </summary>
    public double PointerSlop { get; init; } = 0.015;

    /// <summary>
    /// One finger: before this much time has passed since it landed, it starts moving the cursor only beyond
    /// <see cref="TapSlop"/>, because a second finger can still land and make it a two-finger tap or smart zoom (a
    /// gesture's slop must be at least that of every gesture it can turn into). After it,
    /// <see cref="PointerSlop"/> applies. Either way PointerMove Began catches up the whole movement. A relaxed second
    /// finger lands within ~100 ms (the phone's old finger arming delay).
    /// </summary>
    public TimeSpan PointerSettleTime { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// One finger: how soon after the previous Click's lift a touch must land to count it on (double click) or, if it
    /// moves, to drag (tap and a half). Separate from <see cref="DoubleTapWindow"/>: a Click is never held, so a longer
    /// window costs no latency, and 400 ms catches a relaxed double click within the macOS double-click interval
    /// (500 ms by default).
    /// </summary>
    public TimeSpan ClickWindow { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// One finger: how near a touch must land to the previous Click (within <see cref="ClickWindow"/>) to count it
    /// on (double click) or, if it moves, to drag (tap and a half). Narrower than <see cref="DoubleTapSlop"/> (widened
    /// for smart zoom), so moving the cursor soon after a click elsewhere stays a move; 5% like ADR-0022.
    /// </summary>
    public double ClickSlop { get; init; } = 0.05;
}

/// <summary>
/// Mac-side multi-finger gesture recognizer over raw touch state (one call per received datagram), with trackpad
/// semantics (ADR-0023).
/// </summary>
/// <remarks>
/// <para>
/// <b>Finger count.</b> While undecided, the gesture's fingers are all current contacts; whenever one lands or lifts
/// the measurement restarts from the new set, so staggered landings (2 → 3 → 4) settle on the final count and
/// fingers that lifted before a threshold was crossed don't count. When a threshold is crossed the kind locks: 2
/// fingers = Scroll or Pinch, 3 = Drag, 4 = SystemSwipe or SystemPinch; 5 or more = no gesture until all lift. Once
/// locked, extra fingers are ignored; when one of the gesture's fingers lifts it Ends, and nothing new starts until
/// all fingers have lifted.
/// </para>
/// <para>
/// <b>Two fingers.</b> Pinch is a pinch/rotate transform: it wins when the spread change crosses
/// <see cref="GestureRecognizerOptions.PinchThreshold"/> or the rotation arc crosses
/// <see cref="GestureRecognizerOptions.RotationThreshold"/>, and that quantity is at least the centroid travel.
/// Otherwise centroid travel beyond <see cref="GestureRecognizerOptions.ScrollThreshold"/> is a scroll (exclusive),
/// locked to one axis when it starts straight enough (<see cref="GestureRecognizerOptions.ScrollAxisLockRatio"/>).
/// <b>Four fingers</b> use the same rule: spread change ≥ travel and past its threshold is a SystemPinch, otherwise
/// travel past its threshold is a SystemSwipe along the dominant axis.
/// </para>
/// <para>
/// <b>Taps.</b> A two-finger tap: exactly two landings in the touch sequence (a third finger, or a finger re-landing
/// with a reused pointer id, is no tap), the last lift within <see cref="GestureRecognizerOptions.TapMaxDuration"/>
/// of the first landing, each finger within <see cref="GestureRecognizerOptions.TapSlop"/> of where it landed, and no
/// gesture began. It is held for <see cref="GestureRecognizerOptions.DoubleTapWindow"/>: a second tap landing in that
/// window within <see cref="GestureRecognizerOptions.DoubleTapSlop"/> emits SmartZoom on its lift. Otherwise Tap is
/// emitted when the window passes with no touch down (judged on the frame clock by the next frame, or by the timer
/// deadline <see cref="PendingDeadlineUs"/>/<see cref="CheckDeadline"/> with a jitter allowance), or as soon as it can no longer become a double tap, before the event that interrupts it: the pen (or a one-finger
/// cursor hover) comes into range, a gesture begins, a third finger lands, or the next touch sequence ends or
/// outlasts the tap duration without being that second tap. While fingers are down, tap timing uses only frame times
/// (frames arrive at least every 50 ms then, and the watchdog covers a lost link), so a delayed lift datagram can't
/// turn a double tap into two taps. Only <see cref="Cancel"/> drops a held tap.
/// </para>
/// <para>
/// <b>One finger</b> (only with <see cref="GestureRecognizerOptions.OneFingerCursor"/> and <see cref="PhoneFingerFallback"/>, ADR-0026): a touch sequence
/// whose first frame has exactly one contact is a pointer; a finger left over after a multi-finger gesture is never
/// one. While it stays within <see cref="GestureRecognizerOptions.PointerSlop"/> of where it landed (of
/// <see cref="GestureRecognizerOptions.TapSlop"/> during <see cref="GestureRecognizerOptions.PointerSettleTime"/>, so a
/// staggered two-finger tap stays a tap) nothing moves; leaving it, the cursor catches up the whole movement. When every
/// contact is replaced by new ones within one frame, the old touch sequence ends first.
/// Lifting within <see cref="GestureRecognizerOptions.TapMaxDuration"/> without leaving the slop emits a Click at once
/// (after a held two-finger Tap, which happened first). Leaving the slop begins a PointerMove, or a PointerDrag when the
/// touch landed within <see cref="GestureRecognizerOptions.ClickWindow"/> and
/// <see cref="GestureRecognizerOptions.ClickSlop"/> of a Click (tap and a half; holding still past the tap duration
/// begins it too); it Ends on the lift. Such a tap instead counts the Click on (ClickCount 2, 3, …). A second finger
/// ends the one-finger action (Ended; a PointerDrag releases its button) and the fingers start the multi-finger
/// decision as if they had just landed, so a staggered two-finger tap or scroll still works. Timing uses frame times
/// only (no timer: a Click is never held).
/// </para>
/// <para>
/// <b>Touch down.</b> In each frame where a contact appears that was not in the previous frame (gestures not blocked
/// and no gesture active), one TouchDown event is emitted first, so the head can stop a scroll coast. A finger
/// brushing during an active gesture emits none, so it can't reset the gesture's momentum samples.
/// </para>
/// <para>
/// When the pen comes into range an active gesture is Cancelled, and fingers already down are blocked until all have
/// lifted (fingers landing in a later pen-free frame are new). <see cref="PenStateTracker"/> does it through
/// <see cref="PenEntering"/> before the pen's first event, so the Cancelled (and a held Tap) precede it.
/// A touch present with <c>Contact=false</c> was withdrawn by the phone (palm, platform cancel; ADR-0024): an active
/// gesture is Cancelled, the touch sequence is no tap, and gestures stay blocked until every touch present then is gone
/// (fingers landing after that are new). Only contacts count as fingers.
/// <see cref="Cancel"/> (watchdog, session end) cancels likewise: fingers still down in later frames are blocked until
/// all lift. Frames are ignored until a configured <see cref="Mapper"/> is set (apply SurfaceInfo first). Not
/// thread-safe; allocation-free per call.
/// </para>
/// </remarks>
public sealed class GestureRecognizer
{
    /// <summary>Maximum contacts whose landing order is tracked.</summary>
    public const int MaxTrackedContacts = 10;

    /// <summary>Most fingers a gesture uses; more is no gesture.</summary>
    public const int MaxGestureFingers = 4;

    private const double MinSpread = 1e-6;
    private const double DegreesPerRadian = 180.0 / Math.PI;
    private const long MinSpeedIntervalUs = 1_000;
    private const long MaxSpeedIntervalUs = 100_000;

    private readonly IInputSink _sink;
    private readonly GestureRecognizerOptions _options;
    private readonly long _tapMaxUs;
    private readonly long _doubleTapWindowUs;
    private readonly long _clickWindowUs;
    private readonly long _jitterUs;
    private readonly long _pointerSettleUs;
    private readonly byte[] _order = new byte[MaxTrackedContacts];
    private readonly byte[] _scratch = new byte[MaxTrackedContacts];
    private readonly byte[] _ids = new byte[MaxGestureFingers];
    private readonly DisplayPoint[] _points = new DisplayPoint[MaxGestureFingers];
    private int _orderCount;

    // Withdrawn touches (ADR-0024): ids present when a non-contact touch was seen; blocked until each is gone.
    private readonly byte[] _withdrawnIds = new byte[MaxTrackedContacts];
    private readonly bool[] _withdrawnContact = new bool[MaxTrackedContacts]; // was in contact when last seen
    private int _withdrawnCount;
    private bool _blockUntilLift; // Blocked by the pen or Cancel: until a frame without contacts
    private int _landed;
    private byte _firstLandedId;
    private State _state;

    // The gesture's fingers and measurements.
    private int _n;
    private DisplayPoint _startCenter;
    private double _startSpread;
    private long _startUs;
    private DisplayPoint _prevCenter;
    private double _prevSpread;
    private double _prevAngle;
    private long _prevUs;
    private double _rotationSinceStart;
    private double _speed;
    private ScrollAxis _scrollAxis;
    private GestureDirection _direction;
    private double _progress;

    // Two-finger tap candidate for the current touch sequence (first landing until all fingers lifted).
    private bool _tapCandidate;
    private bool _tapPaired;
    private long _tapStartUs;
    private int _tapLandings;
    private int _tapSlots;
    private byte _tapId0;
    private byte _tapId1;
    private DisplayPoint _tapLand0;
    private DisplayPoint _tapLand1;

    // A completed tap held for the double-tap window.
    private bool _pendingTap;
    private long _pendingLiftUs;
    private DisplayPoint _pendingMid;

    // One finger (ADR-0026): the current lone finger, and the last Click for double-click counting.
    private byte _pointerId;
    private DisplayPoint _pointerLand;
    private long _pointerLandUs;
    private bool _pointerPress; // landed as the next touch of a Click: a tap counts on, moving or holding drags
    private bool _pointerMoved; // moved beyond the pointer slop since it landed
    private bool _clickValid;
    private long _clickLiftUs;
    private DisplayPoint _clickLand;
    private int _clickCount;

    public GestureRecognizer(IInputSink sink, GestureRecognizerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _options = options ?? new GestureRecognizerOptions();
        RequireNonNegative(_options.ScrollThreshold, nameof(GestureRecognizerOptions.ScrollThreshold));
        RequireNonNegative(_options.PinchThreshold, nameof(GestureRecognizerOptions.PinchThreshold));
        RequireNonNegative(_options.RotationThreshold, nameof(GestureRecognizerOptions.RotationThreshold));
        RequireNonNegative(_options.DragThreshold, nameof(GestureRecognizerOptions.DragThreshold));
        RequireNonNegative(_options.SwipeThreshold, nameof(GestureRecognizerOptions.SwipeThreshold));
        RequireNonNegative(_options.SystemPinchThreshold, nameof(GestureRecognizerOptions.SystemPinchThreshold));
        RequireNonNegative(_options.TapSlop, nameof(GestureRecognizerOptions.TapSlop));
        RequireNonNegative(_options.DoubleTapSlop, nameof(GestureRecognizerOptions.DoubleTapSlop));
        RequireNonNegative(_options.PointerSlop, nameof(GestureRecognizerOptions.PointerSlop));
        RequireNonNegative(_options.ClickSlop, nameof(GestureRecognizerOptions.ClickSlop));
        if (_options.PointerSettleTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PointerSettleTime must not be negative.");
        }

        _pointerSettleUs = _options.PointerSettleTime.Ticks / TimeSpan.TicksPerMicrosecond;
        RequireNonNegative(_options.ScrollAxisLockRatio, nameof(GestureRecognizerOptions.ScrollAxisLockRatio));
        RequirePositive(_options.ScrollGain, nameof(GestureRecognizerOptions.ScrollGain));
        RequirePositive(_options.DragGain, nameof(GestureRecognizerOptions.DragGain));
        ArgumentNullException.ThrowIfNull(_options.ScrollAcceleration, nameof(GestureRecognizerOptions.ScrollAcceleration));
        ArgumentNullException.ThrowIfNull(_options.DragAcceleration, nameof(GestureRecognizerOptions.DragAcceleration));
        _options.ScrollAcceleration.Validate(nameof(GestureRecognizerOptions.ScrollAcceleration));
        _options.DragAcceleration.Validate(nameof(GestureRecognizerOptions.DragAcceleration));
        if (_options.TapMaxDuration < TimeSpan.Zero || _options.DoubleTapWindow < TimeSpan.Zero || _options.ClickWindow < TimeSpan.Zero
            || _options.TapDeadlineJitterAllowance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "TapMaxDuration, DoubleTapWindow, ClickWindow and TapDeadlineJitterAllowance must not be negative.");
        }

        _jitterUs = _options.TapDeadlineJitterAllowance.Ticks / TimeSpan.TicksPerMicrosecond;

        _tapMaxUs = _options.TapMaxDuration.Ticks / TimeSpan.TicksPerMicrosecond;
        _doubleTapWindowUs = _options.DoubleTapWindow.Ticks / TimeSpan.TicksPerMicrosecond;
        _clickWindowUs = _options.ClickWindow.Ticks / TimeSpan.TicksPerMicrosecond;
    }

    /// <summary>The axis a scroll is locked to (<see cref="GestureRecognizerOptions.ScrollAxisLockRatio"/>).</summary>
    private enum ScrollAxis : byte
    {
        Free,
        Horizontal,
        Vertical,
    }

    private enum State : byte
    {
        /// <summary>Fewer than two contacts, nothing active.</summary>
        Idle,
        /// <summary>2-4 contacts, no threshold crossed yet.</summary>
        Possible,
        Scrolling,
        Pinching,
        Dragging,
        Swiping,
        SystemPinching,
        /// <summary>Five or more fingers: no gesture until all lift.</summary>
        Ignored,
        /// <summary>A gesture ended while fingers remain: nothing new until all lift.</summary>
        Finished,
        /// <summary>Pen in range, cancelled, or withdrawn touches: no gesture, tap or touch-down until they lift.</summary>
        Blocked,
        /// <summary>One finger down, not yet moved beyond the pointer slop: a Click on a quick lift (ADR-0026).</summary>
        Pointing,
        /// <summary>One finger moving the cursor (PointerMove).</summary>
        PointerMoving,
        /// <summary>One finger dragging with the left button down (PointerDrag).</summary>
        PointerDragging,
    }

    /// <summary>Maps touch positions to display points; thresholds scale with its content size. Unset by default.</summary>
    public CoordinateMapper Mapper { get; set; }

    /// <summary>
    /// The phone reports a lone finger at all (SurfaceInfo <c>SurfaceCapabilities.FingerFallback</c>, i.e. its finger
    /// fallback is on). With it off, a lone finger does nothing even with
    /// <see cref="GestureRecognizerOptions.OneFingerCursor"/>, as before ADR-0026. Checked when a touch sequence starts.
    /// True by default; the server sets it from every SurfaceInfo.
    /// </summary>
    public bool PhoneFingerFallback { get; set; } = true;

    /// <summary>Number of contacts in the last processed frame.</summary>
    public int ContactCount { get; private set; }

    /// <summary>True when a gesture with phases (not Tap/SmartZoom/TouchDown) has Began and not yet Ended/Cancelled.</summary>
    public bool IsActive => _state is State.Scrolling or State.Pinching or State.Dragging or State.Swiping or State.SystemPinching
        or State.PointerMoving or State.PointerDragging;

    /// <summary>
    /// Timer deadline (µs, the <see cref="Process"/> clock) for a held tap while no finger is down: the end of the
    /// double-tap window plus <see cref="GestureRecognizerOptions.TapDeadlineJitterAllowance"/>. Null when no tap is
    /// held or fingers are down (then frames decide). Call <see cref="CheckDeadline"/> then
    /// (<see cref="PenStateTracker"/> does it from its watchdog timer).
    /// </summary>
    public long? PendingDeadlineUs => _pendingTap && ContactCount == 0 ? _pendingLiftUs + _doubleTapWindowUs + _jitterUs : null;

    /// <summary>
    /// Timer path: emits a held Tap once <see cref="PendingDeadlineUs"/> has passed (no second tap landed in time).
    /// Does nothing while fingers are down. Safe to call at any time.
    /// </summary>
    public void CheckDeadline(long timestampUs)
    {
        if (PendingDeadlineUs is { } due && timestampUs >= due)
        {
            FlushPendingTap(timestampUs);
        }
    }

    /// <summary>Frame path: flushes a held tap whose window (no finger down) or tap duration (finger down) passed.</summary>
    private void CheckFrameDeadline(long timestampUs)
    {
        if (!_pendingTap)
        {
            return;
        }

        long due = ContactCount == 0 ? _pendingLiftUs + _doubleTapWindowUs : _tapStartUs + _tapMaxUs + 1;
        if (timestampUs >= due)
        {
            FlushPendingTap(timestampUs);
        }
    }

    /// <summary>Processes the complete current touch state.</summary>
    /// <param name="touches">All active touches from the datagram.</param>
    /// <param name="penInRange">True when the pen is in range or in contact in this frame.</param>
    /// <param name="timestampUs">Monotonic frame time in µs, copied into emitted events.</param>
    public void Process(ReadOnlySpan<TouchSample> touches, bool penInRange, long timestampUs)
    {
        if (!Mapper.IsConfigured)
        {
            return;
        }

        if (ReplacesAllContacts(touches))
        {
            // Every contact lifted and new ones landed within one frame (a coalesced snapshot or a lost datagram):
            // end the old touch sequence first, so the new one starts fresh (a lone finger is a pointer, taps count).
            Process(ReadOnlySpan<TouchSample>.Empty, penInRange, timestampUs);
        }

        CheckFrameDeadline(timestampUs);
        int previousCount = ContactCount;
        int count = UpdateLandingOrder(touches);
        ContactCount = count;
        TrackWithdrawn(touches, timestampUs);

        if (penInRange)
        {
            FlushPendingTap(timestampUs); // the tap happened; the pen only rules out a double tap
            if (IsActive)
            {
                Emit(GesturePhase.Cancelled, 0, 0, 1, 0, _prevCenter, synthetic: false, timestampUs);
            }

            _tapCandidate = false;
            Block(count > 0); // only fingers already down are blocked
            return;
        }

        if (_pendingTap && (count > 2 || (_tapCandidate && _tapLandings + _landed > 2)))
        {
            FlushPendingTap(timestampUs); // a third finger: no double tap, emit before its TouchDown
        }

        if (_landed > 0 && _state != State.Blocked && !IsActive)
        {
            EmitTouchDown(touches, timestampUs); // not during a gesture: a brushing finger must not reset its momentum samples
        }

        TrackTap(touches, previousCount, count, timestampUs);
        RunStateMachine(touches, count, previousCount, timestampUs);

        if (count == 0 && previousCount > 0)
        {
            OnSequenceEnded(timestampUs);
        }
    }

    /// <summary>
    /// Aborts any gesture as if all fingers lifted (watchdog / session end). Emits Cancelled if active. A held tap is
    /// dropped; if fingers were down, gestures (and taps, touch-downs) stay blocked until a frame without contacts.
    /// </summary>
    public void Cancel(long timestampUs, bool synthetic = true)
    {
        if (IsActive)
        {
            Emit(GesturePhase.Cancelled, 0, 0, 1, 0, _prevCenter, synthetic, timestampUs);
        }

        Block(ContactCount > 0);
        _tapCandidate = false;
        _pendingTap = false;
        _orderCount = 0;
        ContactCount = 0;
    }

    /// <summary>
    /// The pen (or a one-finger cursor hover) is about to come into range: emits a held Tap and cancels an active
    /// gesture now, so both come before the pen's events. A Tap right-clicks and a scroll ends at the current cursor,
    /// which the pen's events then move. Fingers already down are blocked until all lift, as in <see cref="Process"/>
    /// with the pen in range (which stays the rule for the rest of the frame). <see cref="PenStateTracker"/> calls it
    /// right before ProximityEnter.
    /// </summary>
    public void PenEntering(long timestampUs)
    {
        if (!Mapper.IsConfigured)
        {
            return;
        }

        FlushPendingTap(timestampUs);
        if (IsActive)
        {
            Emit(GesturePhase.Cancelled, 0, 0, 1, 0, _prevCenter, synthetic: false, timestampUs);
        }

        _tapCandidate = false;
        Block(ContactCount > 0);
    }

    /// <summary>Blocks gestures: until a frame without contacts if <paramref name="untilLift"/>, and while withdrawn touches remain.</summary>
    private void Block(bool untilLift)
    {
        _clickValid = false;
        _blockUntilLift = untilLift;
        _state = untilLift || _withdrawnCount > 0 ? State.Blocked : State.Idle;
    }

    /// <summary>
    /// Withdrawn touches (ADR-0024): a touch present with <c>Contact=false</c> was suppressed on the phone (palm,
    /// platform cancel), not lifted. It cancels the gesture (no Tap from this touch sequence, no momentum: Cancelled,
    /// not Ended) and blocks gestures until every touch present then, or landing while one is withdrawn, is gone. A tap
    /// held from an earlier sequence happened, so it is emitted first. Then fingers landing later are new, even if the
    /// withdrawn touches vanished without an empty frame or their id came back in contact (a new finger: the phone
    /// withdraws a touch until it lifts).
    /// </summary>
    private void TrackWithdrawn(ReadOnlySpan<TouchSample> touches, long timestampUs)
    {
        // An entry is gone when its id is absent, or when a withdrawn one is back in contact: the phone withdraws a
        // touch until it lifts, so that is a new finger reusing the id (Android restarts ids after a cancel).
        int kept = 0;
        for (int i = 0; i < _withdrawnCount; i++)
        {
            int at = IndexOf(touches, _withdrawnIds[i]);
            if (at >= 0 && (_withdrawnContact[i] || !touches[at].Contact))
            {
                _withdrawnContact[kept] = _withdrawnContact[i];
                _withdrawnIds[kept++] = _withdrawnIds[i];
            }
        }

        bool released = kept == 0 && _withdrawnCount > 0;
        _withdrawnCount = kept;

        bool withdrawn = false;
        for (int i = 0; i < touches.Length; i++)
        {
            withdrawn |= !touches[i].Contact;
        }

        if (!withdrawn)
        {
            if (released && _state == State.Blocked && !_blockUntilLift)
            {
                _state = State.Idle;
            }

            return;
        }

        FlushPendingTap(timestampUs);
        if (IsActive)
        {
            Emit(GesturePhase.Cancelled, 0, 0, 1, 0, _prevCenter, synthetic: false, timestampUs);
        }

        _tapCandidate = false;
        _clickValid = false;
        _state = State.Blocked;
        for (int i = 0; i < touches.Length; i++)
        {
            byte id = touches[i].PointerId;
            int at = Array.IndexOf(_withdrawnIds, id, 0, _withdrawnCount);
            if (at < 0 && _withdrawnCount < _withdrawnIds.Length)
            {
                at = _withdrawnCount++;
                _withdrawnIds[at] = id;
            }

            if (at >= 0)
            {
                _withdrawnContact[at] = touches[i].Contact;
            }
        }
    }

    /// <summary>
    /// Contacts were down, this frame has contacts, none of them was down before, and no touch is withdrawn (those keep
    /// their own rules).
    /// </summary>
    private bool ReplacesAllContacts(ReadOnlySpan<TouchSample> touches)
    {
        if (ContactCount == 0 || touches.IsEmpty)
        {
            return false;
        }

        for (int i = 0; i < touches.Length; i++)
        {
            if (!touches[i].Contact || Array.IndexOf(_order, touches[i].PointerId, 0, _orderCount) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOf(ReadOnlySpan<TouchSample> touches, byte id)
    {
        for (int i = 0; i < touches.Length; i++)
        {
            if (touches[i].PointerId == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static void RequireNonNegative(double value, string name)
    {
        if (!(value >= 0) || !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, "Must be finite and not negative.");
        }
    }

    private static void RequirePositive(double value, string name)
    {
        if (!(value > 0) || !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, "Must be positive and finite.");
        }
    }

    /// <summary>Normalizes an angle difference to (-180, 180] degrees.</summary>
    private static double WrapDegrees(double degrees)
    {
        degrees %= 360;
        if (degrees > 180)
        {
            degrees -= 360;
        }
        else if (degrees <= -180)
        {
            degrees += 360;
        }

        return degrees;
    }

    private static double Distance(DisplayPoint a, DisplayPoint b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static bool TryFind(ReadOnlySpan<TouchSample> touches, byte id, out TouchSample found)
    {
        for (int i = 0; i < touches.Length; i++)
        {
            if (touches[i].Contact && touches[i].PointerId == id)
            {
                found = touches[i];
                return true;
            }
        }

        found = default;
        return false;
    }

    private static GestureKind KindOf(State state) => state switch
    {
        State.Pinching => GestureKind.Pinch,
        State.Dragging => GestureKind.Drag,
        State.Swiping => GestureKind.SystemSwipe,
        State.SystemPinching => GestureKind.SystemPinch,
        State.PointerMoving => GestureKind.PointerMove,
        State.PointerDragging => GestureKind.PointerDrag,
        _ => GestureKind.Scroll,
    };

    private void RunStateMachine(ReadOnlySpan<TouchSample> touches, int count, int previousCount, long timestampUs)
    {
        if (_state is State.Pointing or State.PointerMoving or State.PointerDragging && !RunPointer(touches, count, timestampUs))
        {
            return;
        }

        switch (_state)
        {
            case State.Blocked:
            case State.Ignored:
            case State.Finished:
                if (count == 0)
                {
                    // Every contact lifted: the pen/Cancel block is over, even while withdrawn touches remain (they
                    // keep blocking on their own until they are gone).
                    _blockUntilLift = false;
                    if (_withdrawnCount == 0)
                    {
                        _state = State.Idle;
                    }
                }

                return;

            case State.Idle:
            case State.Possible:
                if (count == 1 && _state == State.Idle && previousCount == 0 && _options.OneFingerCursor && PhoneFingerFallback)
                {
                    StartPointer(touches, timestampUs); // only a touch sequence's first finger: never a leftover one
                }
                else if (count > MaxGestureFingers)
                {
                    _state = State.Ignored;
                }
                else if (count < 2)
                {
                    _state = State.Idle;
                }
                else if (_state == State.Idle || _landed > 0 || count != _n)
                {
                    Baseline(touches, count, timestampUs);
                }
                else if (Measure(touches, out DisplayPoint c, out double s, out double a))
                {
                    Decide(c, s, a, timestampUs);
                    Remember(c, s, a, timestampUs);
                }

                return;
        }

        if (!Measure(touches, out DisplayPoint center, out double spread, out double angle))
        {
            Emit(GesturePhase.Ended, 0, 0, 1, 0, _prevCenter, synthetic: false, timestampUs);
            _state = count > 0 ? State.Finished : State.Idle;
            return;
        }

        switch (_state)
        {
            case State.Scrolling:
            case State.Dragging:
            case State.PointerMoving:
            case State.PointerDragging:
            {
                double rx = center.X - _prevCenter.X;
                double ry = center.Y - _prevCenter.Y;
                LockAxis(ref rx, ref ry);
                if (rx != 0 || ry != 0)
                {
                    long dt = Math.Clamp(timestampUs - _prevUs, MinSpeedIntervalUs, MaxSpeedIntervalUs);
                    double speed = Math.Sqrt((rx * rx) + (ry * ry)) * 1e6 / dt;
                    _speed = (_speed + speed) / 2;
                    double gain = Gain(_speed);
                    Emit(GesturePhase.Changed, rx * gain, ry * gain, 1, 0, center, synthetic: false, timestampUs);
                }

                break;
            }

            case State.Pinching:
            {
                double scale = spread / Math.Max(_prevSpread, MinSpread);
                double rotation = RotationSincePrevious(spread, angle);
                if (scale != 1 || rotation != 0 || center != _prevCenter)
                {
                    Emit(GesturePhase.Changed, 0, 0, scale, rotation, center, synthetic: false, timestampUs);
                }

                break;
            }

            case State.Swiping:
            case State.SystemPinching:
            {
                double progress = SystemProgress(center, spread);
                if (progress != _progress || center != _prevCenter)
                {
                    _progress = progress;
                    Emit(GesturePhase.Changed, 0, 0, 1, 0, center, synthetic: false, timestampUs);
                }

                break;
            }
        }

        Remember(center, spread, angle, timestampUs);
    }

    /// <summary>Starts (or restarts) an undecided gesture on all current contacts, in landing order.</summary>
    private void Baseline(ReadOnlySpan<TouchSample> touches, int count, long timestampUs)
    {
        _clickValid = false; // two or more fingers: the next one-finger tap is a new single click
        _n = Math.Min(count, Math.Min(_orderCount, MaxGestureFingers));
        Array.Copy(_order, _ids, _n);
        if (!Measure(touches, out _startCenter, out _startSpread, out _prevAngle))
        {
            _state = State.Idle;
            return;
        }

        _prevCenter = _startCenter;
        _prevSpread = _startSpread;
        _startUs = timestampUs;
        _prevUs = timestampUs;
        _rotationSinceStart = 0;
        _state = State.Possible;
    }

    private void Remember(DisplayPoint center, double spread, double angle, long timestampUs)
    {
        if (_state == State.Possible)
        {
            _rotationSinceStart += RotationSincePrevious(spread, angle);
        }

        _prevCenter = center;
        _prevSpread = spread;
        if (spread >= MinSpread)
        {
            _prevAngle = angle;
        }

        _prevUs = timestampUs;
    }

    private void Decide(DisplayPoint center, double spread, double angle, long timestampUs)
    {
        double reference = Mapper.ShortSide;
        double dx = center.X - _startCenter.X;
        double dy = center.Y - _startCenter.Y;
        double travel = Math.Sqrt((dx * dx) + (dy * dy));
        double spreadChange = Math.Abs(spread - _startSpread);

        if (_n == 2)
        {
            double rotation = _rotationSinceStart + RotationSincePrevious(spread, angle);
            double arc = Math.Abs(rotation) / DegreesPerRadian * (spread / 2);
            bool pinch = spreadChange >= _options.PinchThreshold * reference && spreadChange >= travel;
            bool rotate = arc >= _options.RotationThreshold * reference && arc >= travel;
            if (pinch || rotate)
            {
                Lock(State.Pinching, timestampUs);
                double scale = spread / Math.Max(_startSpread, MinSpread);
                Emit(GesturePhase.Began, 0, 0, scale, rotation, center, synthetic: false, timestampUs);
            }
            else if (travel >= _options.ScrollThreshold * reference)
            {
                BeginRelative(State.Scrolling, dx, dy, travel, center, timestampUs);
            }
        }
        else if (_n == 3)
        {
            if (travel >= _options.DragThreshold * reference)
            {
                BeginRelative(State.Dragging, dx, dy, travel, center, timestampUs);
            }
        }
        else if (spreadChange >= _options.SystemPinchThreshold * reference && spreadChange >= travel)
        {
            Lock(State.SystemPinching, timestampUs);
            _direction = spread < _startSpread ? GestureDirection.In : GestureDirection.Out;
            _progress = SystemProgress(center, spread);
            Emit(GesturePhase.Began, 0, 0, 1, 0, center, synthetic: false, timestampUs);
        }
        else if (travel >= _options.SwipeThreshold * reference)
        {
            Lock(State.Swiping, timestampUs);
            _direction = Math.Abs(dx) >= Math.Abs(dy)
                ? (dx < 0 ? GestureDirection.Left : GestureDirection.Right)
                : (dy < 0 ? GestureDirection.Up : GestureDirection.Down);
            _progress = SystemProgress(center, spread);
            Emit(GesturePhase.Began, 0, 0, 1, 0, center, synthetic: false, timestampUs);
        }
    }

    private void BeginRelative(State state, double dx, double dy, double travel, DisplayPoint center, long timestampUs)
    {
        Lock(state, timestampUs);
        _scrollAxis = ScrollAxis.Free;
        if (state == State.Scrolling)
        {
            double ratio = _options.ScrollAxisLockRatio;
            _scrollAxis = Math.Abs(dy) <= ratio * Math.Abs(dx) ? ScrollAxis.Horizontal
                : Math.Abs(dx) <= ratio * Math.Abs(dy) ? ScrollAxis.Vertical
                : ScrollAxis.Free;
            LockAxis(ref dx, ref dy);
            travel = Math.Sqrt((dx * dx) + (dy * dy));
        }

        _speed = travel * 1e6 / Math.Max(timestampUs - _startUs, MinSpeedIntervalUs);
        double gain = Gain(_speed);
        Emit(GesturePhase.Began, dx * gain, dy * gain, 1, 0, center, synthetic: false, timestampUs);
    }

    /// <summary>Zeroes the cross-axis delta of an axis-locked scroll (only a Scrolling gesture is ever locked).</summary>
    private void LockAxis(ref double dx, ref double dy)
    {
        if (_state != State.Scrolling)
        {
            return;
        }

        if (_scrollAxis == ScrollAxis.Horizontal)
        {
            dy = 0;
        }
        else if (_scrollAxis == ScrollAxis.Vertical)
        {
            dx = 0;
        }
    }

    /// <summary>Locks the gesture kind: a gesture began, so this touch sequence is no tap and a held tap is emitted first.</summary>
    private void Lock(State state, long timestampUs)
    {
        FlushPendingTap(timestampUs);
        _state = state;
        _tapCandidate = false;
        _clickValid = false;
        _direction = GestureDirection.None;
        _progress = 0;
    }

    private double Gain(double speed) => _state is State.Dragging or State.PointerMoving or State.PointerDragging
        ? _options.DragGain * _options.DragAcceleration.GainAt(speed)
        : _options.ScrollGain * _options.ScrollAcceleration.GainAt(speed);

    /// <summary>Cumulative progress toward the locked direction, as a fraction of the short side.</summary>
    private double SystemProgress(DisplayPoint center, double spread)
    {
        double reference = Mapper.ShortSide;
        return _direction switch
        {
            GestureDirection.Up => (_startCenter.Y - center.Y) / reference,
            GestureDirection.Down => (center.Y - _startCenter.Y) / reference,
            GestureDirection.Left => (_startCenter.X - center.X) / reference,
            GestureDirection.Right => (center.X - _startCenter.X) / reference,
            GestureDirection.In => (_startSpread - spread) / reference,
            GestureDirection.Out => (spread - _startSpread) / reference,
            _ => 0,
        };
    }

    /// <summary>Unwrapped angle change since the previous frame; 0 when either spread is too small to have an angle.</summary>
    private double RotationSincePrevious(double spread, double angle) =>
        _n == 2 && spread >= MinSpread && _prevSpread >= MinSpread ? WrapDegrees(angle - _prevAngle) : 0;

    /// <summary>A touch sequence starts with one finger: it may become a Click, a PointerMove or a PointerDrag.</summary>
    private void StartPointer(ReadOnlySpan<TouchSample> touches, long timestampUs)
    {
        TouchSample t = default;
        for (int i = 0; i < touches.Length; i++)
        {
            if (touches[i].Contact)
            {
                t = touches[i];
                break;
            }
        }

        DisplayPoint p = Mapper.Map(t.X, t.Y);
        _pointerPress = _clickValid
            && timestampUs - _clickLiftUs <= _clickWindowUs
            && Distance(p, _clickLand) <= _options.ClickSlop * Mapper.ShortSide;
        _clickValid = _pointerPress; // a Click counts on only into the very next touch
        _pointerId = t.PointerId;
        _pointerLand = p;
        _pointerLandUs = timestampUs;
        _pointerMoved = false;
        _n = 1;
        _ids[0] = t.PointerId;
        _startCenter = p;
        _startUs = timestampUs;
        _prevCenter = p;
        _prevUs = timestampUs;
        _state = State.Pointing;
    }

    /// <summary>
    /// One-finger states. Returns true when the frame must also run through the multi-finger state machine: a second
    /// finger landed (the one-finger action ended first, so a drag is released), or the finger is moving or dragging
    /// (the shared relative-delta path emits Changed, or Ended on the lift).
    /// </summary>
    private bool RunPointer(ReadOnlySpan<TouchSample> touches, int count, long timestampUs)
    {
        bool found = TryFind(touches, _pointerId, out TouchSample t);
        if (count >= 2 || (count == 1 && !found))
        {
            if (_state == State.Pointing && count == 1)
            {
                // The finger lifted and another landed in the same frame: end the first (maybe a Click), start the second.
                PointerLifted(timestampUs);
                _state = State.Idle;
                StartPointer(touches, timestampUs);
                return false;
            }

            if (IsActive)
            {
                Emit(GesturePhase.Ended, 0, 0, 1, 0, _prevCenter, synthetic: false, timestampUs);
            }

            _clickValid = false;
            _state = count >= 2 ? State.Idle : State.Finished; // two or more: the multi-finger decision starts now
            return count >= 2;
        }

        if (_state != State.Pointing)
        {
            return true;
        }

        if (!found)
        {
            PointerLifted(timestampUs);
            _state = State.Idle;
            return false;
        }

        DisplayPoint p = Mapper.Map(t.X, t.Y);
        // While a second finger may still land, only movement beyond the two-finger tap slop starts the pointer, so a
        // staggered two-finger tap's first finger never becomes a move (which would end its tap candidate).
        double slop = timestampUs - _pointerLandUs < _pointerSettleUs
            ? Math.Max(_options.PointerSlop, _options.TapSlop)
            : _options.PointerSlop;
        bool moved = Distance(p, _pointerLand) > slop * Mapper.ShortSide;
        if (moved || (_pointerPress && timestampUs - _pointerLandUs > _tapMaxUs))
        {
            // Beyond the slop it moves (or, as the next touch of a Click, drags); holding still that long drags too.
            // Began catches up the whole movement since the landing, so no travel is lost to the slop.
            _pointerMoved = moved;
            double rx = moved ? p.X - _pointerLand.X : 0;
            double ry = moved ? p.Y - _pointerLand.Y : 0;
            Lock(_pointerPress ? State.PointerDragging : State.PointerMoving, timestampUs);
            _speed = Math.Sqrt((rx * rx) + (ry * ry)) * 1e6 / Math.Max(timestampUs - _pointerLandUs, MinSpeedIntervalUs);
            double gain = Gain(_speed);
            Emit(GesturePhase.Began, rx * gain, ry * gain, 1, 0, p, synthetic: false, timestampUs);
        }

        _prevCenter = p;
        _prevUs = timestampUs;
        return false;
    }

    /// <summary>The lone finger lifted while still Pointing: a quick lift is a Click at the current cursor.</summary>
    private void PointerLifted(long timestampUs)
    {
        if (_pointerMoved || timestampUs - _pointerLandUs > _tapMaxUs)
        {
            _clickValid = false;
            return;
        }

        FlushPendingTap(timestampUs); // a held two-finger tap happened first: its right-click comes before this click
        _clickCount = _pointerPress ? _clickCount + 1 : 1;
        _clickValid = true;
        _clickLiftUs = timestampUs;
        _clickLand = _pointerLand;
        _sink.OnGestureEvent(new GestureEvent(GestureKind.Click, GesturePhase.Ended, 0, 0, 1, 0, GestureDirection.None, 0, _pointerLand, false, timestampUs, _clickCount));
    }

    /// <summary>
    /// Measures the gesture's fingers: centroid, spread (2 fingers: their distance; more: twice the mean distance from
    /// the centroid) and, for 2 fingers, the angle from the first to the second (degrees, y down: increasing turns
    /// clockwise on screen). False when one of them is no longer down.
    /// </summary>
    private bool Measure(ReadOnlySpan<TouchSample> touches, out DisplayPoint center, out double spread, out double angle)
    {
        center = default;
        spread = 0;
        angle = 0;
        double sumX = 0;
        double sumY = 0;
        for (int i = 0; i < _n; i++)
        {
            if (!TryFind(touches, _ids[i], out TouchSample t))
            {
                return false;
            }

            DisplayPoint p = Mapper.Map(t.X, t.Y);
            _points[i] = p;
            sumX += p.X;
            sumY += p.Y;
        }

        center = new DisplayPoint(sumX / _n, sumY / _n);
        if (_n == 2)
        {
            double sx = _points[1].X - _points[0].X;
            double sy = _points[1].Y - _points[0].Y;
            spread = Math.Sqrt((sx * sx) + (sy * sy));
            angle = Math.Atan2(sy, sx) * DegreesPerRadian;
            return true;
        }

        double sum = 0;
        for (int i = 0; i < _n; i++)
        {
            sum += Distance(_points[i], center);
        }

        spread = 2 * sum / _n;
        return true;
    }

    /// <summary>Follows one touch sequence (first landing until all lift) and drops the tap candidate when a rule breaks.</summary>
    private void TrackTap(ReadOnlySpan<TouchSample> touches, int previousCount, int count, long timestampUs)
    {
        if (previousCount == 0 && count > 0 && _state != State.Blocked)
        {
            _tapCandidate = true;
            _tapPaired = false;
            _tapLandings = 0;
            _tapSlots = 0;
            _tapStartUs = timestampUs;
        }

        if (!_tapCandidate || count == 0)
        {
            return;
        }

        _tapLandings += _landed;
        if (count > 2 || _tapLandings > 2 || timestampUs - _tapStartUs > _tapMaxUs)
        {
            _tapCandidate = false;
            return;
        }

        double slop = _options.TapSlop * Mapper.ShortSide;
        for (int i = 0; i < touches.Length; i++)
        {
            TouchSample t = touches[i];
            if (!t.Contact)
            {
                continue;
            }

            DisplayPoint p = Mapper.Map(t.X, t.Y);
            if (_tapSlots > 0 && t.PointerId == _tapId0)
            {
                _tapCandidate &= Distance(p, _tapLand0) <= slop;
            }
            else if (_tapSlots > 1 && t.PointerId == _tapId1)
            {
                _tapCandidate &= Distance(p, _tapLand1) <= slop;
            }
            else if (_tapSlots == 0)
            {
                _tapId0 = t.PointerId;
                _tapLand0 = p;
                _tapSlots = 1;
            }
            else
            {
                _tapId1 = t.PointerId;
                _tapLand1 = p;
                _tapSlots = 2;
            }
        }

        if (count == 2)
        {
            _tapPaired = true;
        }
    }

    /// <summary>All fingers lifted: a tap is held (or completes a SmartZoom); anything else resolves a held tap.</summary>
    private void OnSequenceEnded(long timestampUs)
    {
        bool isTap = _tapCandidate && _tapPaired && timestampUs - _tapStartUs <= _tapMaxUs;
        _tapCandidate = false;
        if (!isTap)
        {
            FlushPendingTap(timestampUs);
            return;
        }

        var mid = new DisplayPoint((_tapLand0.X + _tapLand1.X) / 2, (_tapLand0.Y + _tapLand1.Y) / 2);
        if (_pendingTap && Distance(mid, _pendingMid) <= _options.DoubleTapSlop * Mapper.ShortSide)
        {
            _pendingTap = false;
            _sink.OnGestureEvent(new GestureEvent(GestureKind.SmartZoom, GesturePhase.Ended, 0, 0, 1, 0, GestureDirection.None, 0, mid, false, timestampUs));
            return;
        }

        FlushPendingTap(timestampUs);
        _pendingTap = true;
        _pendingLiftUs = timestampUs;
        _pendingMid = mid;
        if (_doubleTapWindowUs == 0)
        {
            FlushPendingTap(timestampUs);
        }
    }

    private void FlushPendingTap(long timestampUs)
    {
        if (!_pendingTap)
        {
            return;
        }

        _pendingTap = false;
        _sink.OnGestureEvent(new GestureEvent(GestureKind.Tap, GesturePhase.Ended, 0, 0, 1, 0, GestureDirection.None, 0, _pendingMid, false, timestampUs));
    }

    /// <summary>
    /// Keeps still-present contacts in landing order and appends new ones; returns the contact count. Sets
    /// <c>_landed</c> to the number of contacts that were not in the previous frame.
    /// </summary>
    private int UpdateLandingOrder(ReadOnlySpan<TouchSample> touches)
    {
        int kept = 0;
        for (int i = 0; i < _orderCount; i++)
        {
            if (TryFind(touches, _order[i], out _))
            {
                _scratch[kept++] = _order[i];
            }
        }

        int count = 0;
        int keptBefore = kept;
        for (int i = 0; i < touches.Length; i++)
        {
            TouchSample t = touches[i];
            if (!t.Contact)
            {
                continue;
            }

            count++;
            if (kept < MaxTrackedContacts && Array.IndexOf(_scratch, t.PointerId, 0, kept) < 0)
            {
                _scratch[kept++] = t.PointerId;
            }
        }

        _landed = kept - keptBefore;
        if (_landed > 0)
        {
            _firstLandedId = _scratch[keptBefore];
        }

        Array.Copy(_scratch, _order, kept);
        _orderCount = kept;
        return count;
    }

    private void EmitTouchDown(ReadOnlySpan<TouchSample> touches, long timestampUs)
    {
        TryFind(touches, _firstLandedId, out TouchSample first);
        DisplayPoint at = Mapper.Map(first.X, first.Y);
        _sink.OnGestureEvent(new GestureEvent(GestureKind.TouchDown, GesturePhase.Began, 0, 0, 1, 0, GestureDirection.None, 0, at, false, timestampUs));
    }

    private void Emit(GesturePhase phase, double dx, double dy, double scale, double rotation, DisplayPoint center, bool synthetic, long timestampUs)
    {
        GestureKind kind = KindOf(_state);
        bool system = kind is GestureKind.SystemSwipe or GestureKind.SystemPinch;
        _sink.OnGestureEvent(new GestureEvent(
            kind,
            phase,
            dx,
            dy,
            scale,
            rotation,
            system ? _direction : GestureDirection.None,
            system ? _progress : 0,
            center,
            synthetic,
            timestampUs));
    }
}
