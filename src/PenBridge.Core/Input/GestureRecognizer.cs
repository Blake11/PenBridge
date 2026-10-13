namespace PenBridge.Core.Input;

/// <summary>Options for <see cref="GestureRecognizer"/>.</summary>
public sealed record GestureRecognizerOptions
{
    /// <summary>Centroid travel, as a fraction of the mapped surface's shorter side, that starts a scroll.</summary>
    public double ScrollThreshold { get; init; } = 0.04;

    /// <summary>Finger-spread change, as a fraction of the mapped surface's shorter side, that starts a pinch.</summary>
    public double PinchThreshold { get; init; } = 0.04;
}

/// <summary>
/// Mac-side two-finger gesture recognizer over raw touch state (one call per received datagram).
/// </summary>
/// <remarks>
/// The first two contacts to land are tracked. Until a threshold is crossed nothing is emitted; then the gesture
/// locks to Scroll or Pinch (pinch wins when the spread change is at least the centroid travel) and stays that kind
/// until a tracked finger lifts (Ended). When the pen comes into range an active gesture is Cancelled and no new
/// gesture starts until all fingers have lifted. Not thread-safe; allocation-free per call.
/// </remarks>
public sealed class GestureRecognizer
{
    private const double MinSpread = 1e-6;

    private readonly IInputSink _sink;
    private readonly GestureRecognizerOptions _options;
    private State _state;
    private byte _idA;
    private byte _idB;
    private DisplayPoint _startCenter;
    private double _startSpread;
    private DisplayPoint _prevCenter;
    private double _prevSpread;

    public GestureRecognizer(IInputSink sink, GestureRecognizerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _options = options ?? new GestureRecognizerOptions();
    }

    private enum State : byte
    {
        Idle,
        Possible,
        Scrolling,
        Pinching,
        Blocked,
    }

    /// <summary>Maps touch positions to display points; thresholds scale with its content size.</summary>
    public CoordinateMapper Mapper { get; set; } = CoordinateMapper.Identity;

    /// <summary>Number of contacts in the last processed frame.</summary>
    public int ContactCount { get; private set; }

    /// <summary>True when a gesture has Began and not yet Ended/Cancelled.</summary>
    public bool IsActive => _state is State.Scrolling or State.Pinching;

    /// <summary>Processes the complete current touch state.</summary>
    /// <param name="touches">All active touches from the datagram.</param>
    /// <param name="penInRange">True when the pen is in range or in contact in this frame.</param>
    public void Process(ReadOnlySpan<TouchSample> touches, bool penInRange)
    {
        int count = 0;
        TouchSample first = default, second = default, a = default, b = default;
        bool foundA = false, foundB = false;
        for (int i = 0; i < touches.Length; i++)
        {
            TouchSample t = touches[i];
            if (!t.Contact)
            {
                continue;
            }

            count++;
            if (count == 1)
            {
                first = t;
            }
            else if (count == 2)
            {
                second = t;
            }

            if (t.PointerId == _idA && !foundA)
            {
                a = t;
                foundA = true;
            }
            else if (t.PointerId == _idB && !foundB)
            {
                b = t;
                foundB = true;
            }
        }

        ContactCount = count;

        if (penInRange)
        {
            if (IsActive)
            {
                Emit(GesturePhase.Cancelled, 0, 0, 1, _prevCenter, synthetic: false);
            }

            _state = State.Blocked;
            return;
        }

        switch (_state)
        {
            case State.Blocked:
                if (count == 0)
                {
                    _state = State.Idle;
                }

                return;

            case State.Idle:
                if (count >= 2)
                {
                    Start(first, second);
                }

                return;
        }

        if (!foundA || !foundB)
        {
            if (IsActive)
            {
                Emit(GesturePhase.Ended, 0, 0, 1, _prevCenter, synthetic: false);
            }

            _state = State.Idle;
            if (count >= 2)
            {
                Start(first, second);
            }

            return;
        }

        Measure(a, b, out DisplayPoint center, out double spread);
        switch (_state)
        {
            case State.Possible:
                Decide(center, spread);
                break;

            case State.Scrolling:
            {
                double dx = center.X - _prevCenter.X;
                double dy = center.Y - _prevCenter.Y;
                if (dx != 0 || dy != 0)
                {
                    Emit(GesturePhase.Changed, dx, dy, 1, center, synthetic: false);
                }

                break;
            }

            case State.Pinching:
            {
                double scale = spread / Math.Max(_prevSpread, MinSpread);
                if (scale != 1 || center != _prevCenter)
                {
                    Emit(GesturePhase.Changed, 0, 0, scale, center, synthetic: false);
                }

                break;
            }
        }

        _prevCenter = center;
        _prevSpread = spread;
    }

    /// <summary>Aborts any gesture as if all fingers lifted (watchdog / session end). Emits Cancelled if active.</summary>
    public void Cancel(bool synthetic = true)
    {
        if (IsActive)
        {
            Emit(GesturePhase.Cancelled, 0, 0, 1, _prevCenter, synthetic);
        }

        _state = State.Idle;
        ContactCount = 0;
    }

    private void Start(TouchSample first, TouchSample second)
    {
        _idA = first.PointerId;
        _idB = second.PointerId;
        Measure(first, second, out _startCenter, out _startSpread);
        _prevCenter = _startCenter;
        _prevSpread = _startSpread;
        _state = State.Possible;
    }

    private void Decide(DisplayPoint center, double spread)
    {
        double reference = Mapper.ShortSide;
        double dx = center.X - _startCenter.X;
        double dy = center.Y - _startCenter.Y;
        double travel = Math.Sqrt((dx * dx) + (dy * dy));
        double spreadChange = Math.Abs(spread - _startSpread);

        if (spreadChange >= _options.PinchThreshold * reference && spreadChange >= travel)
        {
            _state = State.Pinching;
            Emit(GesturePhase.Began, 0, 0, spread / Math.Max(_startSpread, MinSpread), center, synthetic: false);
        }
        else if (travel >= _options.ScrollThreshold * reference)
        {
            _state = State.Scrolling;
            Emit(GesturePhase.Began, dx, dy, 1, center, synthetic: false);
        }
    }

    private void Measure(TouchSample a, TouchSample b, out DisplayPoint center, out double spread)
    {
        DisplayPoint pa = Mapper.Map(a.X, a.Y);
        DisplayPoint pb = Mapper.Map(b.X, b.Y);
        center = new DisplayPoint((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
        double sx = pa.X - pb.X;
        double sy = pa.Y - pb.Y;
        spread = Math.Sqrt((sx * sx) + (sy * sy));
    }

    private void Emit(GesturePhase phase, double dx, double dy, double scale, DisplayPoint center, bool synthetic)
    {
        GestureKind kind = _state == State.Pinching ? GestureKind.Pinch : GestureKind.Scroll;
        _sink.OnGestureEvent(new GestureEvent(kind, phase, dx, dy, scale, center, synthetic));
    }
}
