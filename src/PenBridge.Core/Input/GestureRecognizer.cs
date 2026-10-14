namespace PenBridge.Core.Input;

/// <summary>Options for <see cref="GestureRecognizer"/>. Distances are fractions of the mapped content's shorter side.</summary>
public sealed record GestureRecognizerOptions
{
    /// <summary>Centroid travel that starts a scroll.</summary>
    public double ScrollThreshold { get; init; } = 0.04;

    /// <summary>Finger-spread change that starts a pinch/rotate transform.</summary>
    public double PinchThreshold { get; init; } = 0.04;

    /// <summary>
    /// Rotation arc (the turned angle in radians × half the finger spread, i.e. how far each finger travelled around
    /// the centroid) that starts a pinch/rotate transform.
    /// </summary>
    public double RotationThreshold { get; init; } = 0.04;

    /// <summary>
    /// Multiplier applied to scroll deltas (<see cref="GestureEvent.DeltaX"/>/<see cref="GestureEvent.DeltaY"/>).
    /// 1 means content moves 1:1 with the fingers in mapped display points. Must be positive and finite.
    /// </summary>
    public double ScrollGain { get; init; } = 1.0;

    /// <summary>A two-finger tap lasts at most this long, from the first finger landing to the last lifting.</summary>
    public TimeSpan TapMaxDuration { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How far each finger of a two-finger tap may move from where it landed.</summary>
    public double TapSlop { get; init; } = 0.02;
}

/// <summary>
/// Mac-side two-finger gesture recognizer over raw touch state (one call per received datagram).
/// </summary>
/// <remarks>
/// <para>
/// The first two contacts by landing order are tracked (later fingers are ignored). Until a threshold is crossed
/// nothing is emitted; then the gesture locks to Scroll or Pinch and stays that kind until a tracked finger lifts
/// (Ended). Pinch is a pinch/rotate transform: it wins when the spread change crosses
/// <see cref="GestureRecognizerOptions.PinchThreshold"/> or the rotation arc crosses
/// <see cref="GestureRecognizerOptions.RotationThreshold"/>, and that quantity is at least the centroid travel; then
/// its events carry scale and rotation together. Otherwise centroid travel beyond
/// <see cref="GestureRecognizerOptions.ScrollThreshold"/> is a scroll (exclusive: no scale or rotation).
/// </para>
/// <para>
/// Two-finger tap: when exactly two fingers touched (no third finger landed at any time, also not after one lifted),
/// both lifted within <see cref="GestureRecognizerOptions.TapMaxDuration"/> of the first landing, each moved at most
/// <see cref="GestureRecognizerOptions.TapSlop"/> from where it landed and no gesture began, one
/// <see cref="GestureKind.Tap"/> event is emitted when the last finger lifts. The first landing is the first frame
/// that carried a touch.
/// </para>
/// <para>
/// Touch down: when the contact count goes from 0 to at least 1 (and the pen does not block gestures), one
/// <see cref="GestureKind.TouchDown"/> event is emitted first, so the head can stop a scroll coast. After
/// <see cref="Cancel"/> the count restarts at 0, so fingers still down in the next frame count as landing again.
/// </para>
/// <para>
/// When the pen comes into range an active gesture is Cancelled, a pending tap is dropped, and nothing new starts
/// until all fingers have lifted. <see cref="Cancel"/> (watchdog, session end) cancels likewise. Frames are ignored
/// until a configured <see cref="Mapper"/> is set (apply SurfaceInfo first). Not thread-safe; allocation-free per call.
/// </para>
/// </remarks>
public sealed class GestureRecognizer
{
    /// <summary>Maximum contacts whose landing order is tracked.</summary>
    public const int MaxTrackedContacts = 10;

    private const double MinSpread = 1e-6;
    private const double DegreesPerRadian = 180.0 / Math.PI;

    private readonly IInputSink _sink;
    private readonly GestureRecognizerOptions _options;
    private readonly long _tapMaxUs;
    private readonly byte[] _order = new byte[MaxTrackedContacts];
    private readonly byte[] _scratch = new byte[MaxTrackedContacts];
    private int _orderCount;
    private State _state;
    private byte _idA;
    private byte _idB;
    private DisplayPoint _startCenter;
    private double _startSpread;
    private DisplayPoint _prevCenter;
    private double _prevSpread;
    private double _prevAngle;
    private double _rotationSinceStart;

    // Two-finger tap candidate (one touch sequence: first landing until all fingers lifted).
    private bool _tapPending;
    private bool _tapSuppressed;
    private bool _tapPaired;
    private long _tapStartUs;
    private int _tapFingers;
    private byte _tapId0;
    private byte _tapId1;
    private DisplayPoint _tapLand0;
    private DisplayPoint _tapLand1;

    public GestureRecognizer(IInputSink sink, GestureRecognizerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _options = options ?? new GestureRecognizerOptions();
        RequireNonNegative(_options.ScrollThreshold, nameof(GestureRecognizerOptions.ScrollThreshold));
        RequireNonNegative(_options.PinchThreshold, nameof(GestureRecognizerOptions.PinchThreshold));
        RequireNonNegative(_options.RotationThreshold, nameof(GestureRecognizerOptions.RotationThreshold));
        RequireNonNegative(_options.TapSlop, nameof(GestureRecognizerOptions.TapSlop));
        if (!(_options.ScrollGain > 0) || !double.IsFinite(_options.ScrollGain))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ScrollGain must be positive and finite.");
        }

        if (_options.TapMaxDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "TapMaxDuration must not be negative.");
        }

        _tapMaxUs = _options.TapMaxDuration.Ticks / TimeSpan.TicksPerMicrosecond;
    }

    private enum State : byte
    {
        Idle,
        Possible,
        Scrolling,
        Pinching,
        Blocked,
    }

    /// <summary>Maps touch positions to display points; thresholds scale with its content size. Unset by default.</summary>
    public CoordinateMapper Mapper { get; set; }

    /// <summary>Number of contacts in the last processed frame.</summary>
    public int ContactCount { get; private set; }

    /// <summary>True when a gesture (Scroll or Pinch) has Began and not yet Ended/Cancelled. Never true for a Tap.</summary>
    public bool IsActive => _state is State.Scrolling or State.Pinching;

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

        int previousCount = ContactCount;
        int count = UpdateLandingOrder(touches);
        ContactCount = count;

        if (penInRange)
        {
            if (IsActive)
            {
                Emit(GesturePhase.Cancelled, 0, 0, 1, 0, _prevCenter, synthetic: false, timestampUs);
            }

            _tapPending = false;
            _state = State.Blocked;
            return;
        }

        if (previousCount == 0 && count > 0 && _state != State.Blocked)
        {
            EmitTouchDown(touches, timestampUs);
        }

        TrackTap(touches, previousCount, count, timestampUs);
        RunStateMachine(touches, timestampUs);

        if (count == 0 && previousCount > 0)
        {
            if (_tapPending && _tapPaired && timestampUs - _tapStartUs <= _tapMaxUs)
            {
                var mid = new DisplayPoint((_tapLand0.X + _tapLand1.X) / 2, (_tapLand0.Y + _tapLand1.Y) / 2);
                _sink.OnGestureEvent(new GestureEvent(GestureKind.Tap, GesturePhase.Ended, 0, 0, 1, 0, mid, false, timestampUs));
            }

            _tapPending = false;
        }

        if (count == 0)
        {
            _tapSuppressed = false;
        }
    }

    /// <summary>
    /// Aborts any gesture as if all fingers lifted (watchdog / session end). Emits Cancelled if active. A pending tap is
    /// dropped; if fingers were down, the ones seen in later frames can't form a tap until a frame without contacts.
    /// </summary>
    public void Cancel(long timestampUs, bool synthetic = true)
    {
        if (IsActive)
        {
            Emit(GesturePhase.Cancelled, 0, 0, 1, 0, _prevCenter, synthetic, timestampUs);
        }

        _state = State.Idle;
        _tapPending = false;
        _tapSuppressed = ContactCount > 0; // fingers still down when frames resume are no new tap
        _orderCount = 0;
        ContactCount = 0;
    }

    private static void RequireNonNegative(double value, string name)
    {
        if (!(value >= 0) || !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, "Must be finite and not negative.");
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

    private void RunStateMachine(ReadOnlySpan<TouchSample> touches, long timestampUs)
    {
        switch (_state)
        {
            case State.Blocked:
                if (ContactCount == 0)
                {
                    _state = State.Idle;
                }

                return;

            case State.Idle:
                TryStart(touches);
                return;
        }

        if (!TryFind(touches, _idA, out TouchSample a) || !TryFind(touches, _idB, out TouchSample b))
        {
            if (IsActive)
            {
                Emit(GesturePhase.Ended, 0, 0, 1, 0, _prevCenter, synthetic: false, timestampUs);
            }

            _state = State.Idle;
            TryStart(touches);
            return;
        }

        Measure(a, b, out DisplayPoint center, out double spread, out double angle);
        double rotation = RotationSincePrevious(spread, angle);
        switch (_state)
        {
            case State.Possible:
                _rotationSinceStart += rotation;
                Decide(center, spread, timestampUs);
                break;

            case State.Scrolling:
            {
                double dx = (center.X - _prevCenter.X) * _options.ScrollGain;
                double dy = (center.Y - _prevCenter.Y) * _options.ScrollGain;
                if (dx != 0 || dy != 0)
                {
                    Emit(GesturePhase.Changed, dx, dy, 1, 0, center, synthetic: false, timestampUs);
                }

                break;
            }

            case State.Pinching:
            {
                double scale = spread / Math.Max(_prevSpread, MinSpread);
                if (scale != 1 || rotation != 0 || center != _prevCenter)
                {
                    Emit(GesturePhase.Changed, 0, 0, scale, rotation, center, synthetic: false, timestampUs);
                }

                break;
            }
        }

        _prevCenter = center;
        _prevSpread = spread;
        if (spread >= MinSpread)
        {
            _prevAngle = angle;
        }
    }

    /// <summary>Unwrapped angle change since the previous frame; 0 when either spread is too small to have an angle.</summary>
    private double RotationSincePrevious(double spread, double angle) =>
        spread >= MinSpread && _prevSpread >= MinSpread ? WrapDegrees(angle - _prevAngle) : 0;

    /// <summary>Follows one touch sequence (first landing until all lift) and drops the tap when a rule breaks.</summary>
    private void TrackTap(ReadOnlySpan<TouchSample> touches, int previousCount, int count, long timestampUs)
    {
        if (previousCount == 0 && count > 0 && !_tapSuppressed && _state != State.Blocked)
        {
            _tapPending = true;
            _tapPaired = false;
            _tapFingers = 0;
            _tapStartUs = timestampUs;
        }

        if (!_tapPending || count == 0)
        {
            return;
        }

        if (count > 2 || timestampUs - _tapStartUs > _tapMaxUs)
        {
            _tapPending = false;
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
            if (_tapFingers > 0 && t.PointerId == _tapId0)
            {
                _tapPending &= Distance(p, _tapLand0) <= slop;
            }
            else if (_tapFingers > 1 && t.PointerId == _tapId1)
            {
                _tapPending &= Distance(p, _tapLand1) <= slop;
            }
            else if (_tapFingers == 0)
            {
                _tapId0 = t.PointerId;
                _tapLand0 = p;
                _tapFingers = 1;
            }
            else if (_tapFingers == 1)
            {
                _tapId1 = t.PointerId;
                _tapLand1 = p;
                _tapFingers = 2;
            }
            else
            {
                _tapPending = false; // a third finger in this sequence
            }
        }

        if (count == 2)
        {
            _tapPaired = true;
        }
    }

    /// <summary>Keeps still-present contacts in landing order and appends new ones; returns the contact count.</summary>
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

        Array.Copy(_scratch, _order, kept);
        _orderCount = kept;
        return count;
    }

    private void TryStart(ReadOnlySpan<TouchSample> touches)
    {
        if (_orderCount < 2
            || !TryFind(touches, _order[0], out TouchSample first)
            || !TryFind(touches, _order[1], out TouchSample second))
        {
            return;
        }

        _idA = first.PointerId;
        _idB = second.PointerId;
        Measure(first, second, out _startCenter, out _startSpread, out _prevAngle);
        _prevCenter = _startCenter;
        _prevSpread = _startSpread;
        _rotationSinceStart = 0;
        _state = State.Possible;
    }

    private void Decide(DisplayPoint center, double spread, long timestampUs)
    {
        double reference = Mapper.ShortSide;
        double dx = center.X - _startCenter.X;
        double dy = center.Y - _startCenter.Y;
        double travel = Math.Sqrt((dx * dx) + (dy * dy));
        double spreadChange = Math.Abs(spread - _startSpread);
        double arc = Math.Abs(_rotationSinceStart) / DegreesPerRadian * (spread / 2);

        bool pinch = spreadChange >= _options.PinchThreshold * reference && spreadChange >= travel;
        bool rotate = arc >= _options.RotationThreshold * reference && arc >= travel;
        if (pinch || rotate)
        {
            _state = State.Pinching;
            _tapPending = false;
            double scale = spread / Math.Max(_startSpread, MinSpread);
            Emit(GesturePhase.Began, 0, 0, scale, _rotationSinceStart, center, synthetic: false, timestampUs);
        }
        else if (travel >= _options.ScrollThreshold * reference)
        {
            _state = State.Scrolling;
            _tapPending = false;
            double gain = _options.ScrollGain;
            Emit(GesturePhase.Began, dx * gain, dy * gain, 1, 0, center, synthetic: false, timestampUs);
        }
    }

    private void Measure(TouchSample a, TouchSample b, out DisplayPoint center, out double spread, out double angle)
    {
        DisplayPoint pa = Mapper.Map(a.X, a.Y);
        DisplayPoint pb = Mapper.Map(b.X, b.Y);
        center = new DisplayPoint((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
        double sx = pb.X - pa.X;
        double sy = pb.Y - pa.Y;
        spread = Math.Sqrt((sx * sx) + (sy * sy));
        angle = Math.Atan2(sy, sx) * DegreesPerRadian; // y down: increasing angle turns clockwise on screen
    }

    private void EmitTouchDown(ReadOnlySpan<TouchSample> touches, long timestampUs)
    {
        for (int i = 0; i < touches.Length; i++)
        {
            if (touches[i].Contact)
            {
                DisplayPoint at = Mapper.Map(touches[i].X, touches[i].Y);
                _sink.OnGestureEvent(new GestureEvent(GestureKind.TouchDown, GesturePhase.Began, 0, 0, 1, 0, at, false, timestampUs));
                return;
            }
        }
    }

    private void Emit(GesturePhase phase, double dx, double dy, double scale, double rotation, DisplayPoint center, bool synthetic, long timestampUs)
    {
        GestureKind kind = _state == State.Pinching ? GestureKind.Pinch : GestureKind.Scroll;
        _sink.OnGestureEvent(new GestureEvent(kind, phase, dx, dy, scale, rotation, center, synthetic, timestampUs));
    }
}
