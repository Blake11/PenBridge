namespace PenBridge.Core.Input;

/// <summary>Options for <see cref="ScrollMomentum"/>. Speeds are in scroll-delta units (display points) per second.</summary>
public sealed record ScrollMomentumOptions
{
    /// <summary>The release velocity is the movement over this much time before the last scroll delta.</summary>
    public TimeSpan VelocityWindow { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// If the fingers rested this long (no scroll delta) before lifting, there is no momentum: the user stopped the
    /// scroll on purpose.
    /// </summary>
    public TimeSpan MaxReleasePause { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Release speeds below this start no momentum.</summary>
    public double MinStartSpeed { get; init; } = 150;

    /// <summary>Coasting stops once the decayed speed falls below this.</summary>
    public double StopSpeed { get; init; } = 20;

    /// <summary>Release speeds are capped to this (direction kept), so a one-frame jump can't fling the page.</summary>
    public double MaxSpeed { get; init; } = 12_000;

    /// <summary>
    /// Exponential decay time constant: the speed falls to 1/e after this long (v(t) = v0·e^(−t/τ)). The total coast
    /// distance is v0·τ.
    /// </summary>
    public TimeSpan DecayTimeConstant { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Suggested period for the head's one-shot coasting timer. Steps are exact for any tick time.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMicroseconds(16_667);
}

/// <summary>
/// Scroll momentum (inertia) after a two-finger scroll lifts (ADR-0023): estimates the release velocity from the last
/// scroll deltas, then yields exponentially decaying steps until the speed falls below
/// <see cref="ScrollMomentumOptions.StopSpeed"/>.
/// </summary>
/// <remarks>
/// <para>
/// Usage (Mac head): feed every <see cref="GestureEvent"/> to <see cref="Observe"/> (or call <see cref="AddSample"/>
/// for scroll Began/Changed deltas, <see cref="TryStart"/> on Ended and <see cref="Reset"/> on Cancelled). When it
/// starts coasting, arm a one-shot timer for <see cref="ScrollMomentumOptions.TickInterval"/>; on each tick call
/// <see cref="Step"/> with the current time on the event clock, post the step while it returns true and re-arm, and
/// post the momentum end when it returns false. Call <see cref="Reset"/> to stop at once (pen in range, new touch,
/// pause, watchdog, session end). No timer runs while idle.
/// </para>
/// <para>
/// The steps are deterministic: the coast position is v0·τ·(1 − e^(−t/τ)) since release, and each step is the
/// difference since the previous one, so the total does not depend on the tick rate. The coast stops at the stop
/// speed, so it covers (v0 − StopSpeed)·τ. Time is the
/// <see cref="GestureEvent.TimestampUs"/> clock (µs). Not thread-safe; allocation-free after construction.
/// </para>
/// </remarks>
public sealed class ScrollMomentum
{
    /// <summary>Scroll deltas kept for the velocity estimate (covers the window at well over 240 Hz).</summary>
    public const int Capacity = 64;

    private readonly long[] _times = new long[Capacity];
    private readonly double[] _dxs = new double[Capacity];
    private readonly double[] _dys = new double[Capacity];
    private readonly long _windowUs;
    private readonly long _pauseUs;
    private readonly double _tauSeconds;
    private int _head; // index of the next write
    private int _count;
    private double _v0x;
    private double _v0y;
    private double _speed0;
    private long _startUs;
    private long _lastStepUs;
    private double _emittedX;
    private double _emittedY;

    public ScrollMomentum(ScrollMomentumOptions? options = null)
    {
        Options = options ?? new ScrollMomentumOptions();
        if (Options.VelocityWindow <= TimeSpan.Zero || Options.MaxReleasePause < TimeSpan.Zero
            || Options.DecayTimeConstant <= TimeSpan.Zero || Options.TickInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Durations must be positive (MaxReleasePause: not negative).");
        }

        if (!(Options.StopSpeed > 0) || !(Options.MinStartSpeed >= Options.StopSpeed) || !(Options.MaxSpeed >= Options.MinStartSpeed)
            || !double.IsFinite(Options.MaxSpeed))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Speeds must satisfy 0 < StopSpeed <= MinStartSpeed <= MaxSpeed (finite).");
        }

        _windowUs = Options.VelocityWindow.Ticks / TimeSpan.TicksPerMicrosecond;
        _pauseUs = Options.MaxReleasePause.Ticks / TimeSpan.TicksPerMicrosecond;
        _tauSeconds = Options.DecayTimeConstant.TotalSeconds;
    }

    public ScrollMomentumOptions Options { get; }

    /// <summary>True between a successful <see cref="TryStart"/> and the <see cref="Step"/> that returns false (or a reset).</summary>
    public bool IsCoasting { get; private set; }

    /// <summary>Release velocity of the current coast, units per second (0 when not coasting).</summary>
    public double ReleaseVelocityX => IsCoasting ? _v0x : 0;

    /// <summary>See <see cref="ReleaseVelocityX"/>.</summary>
    public double ReleaseVelocityY => IsCoasting ? _v0y : 0;

    /// <summary>
    /// Event time (µs) at which the coast ends (the speed reaches <see cref="ScrollMomentumOptions.StopSpeed"/>);
    /// 0 when not coasting.
    /// </summary>
    public long EndTimeUs => IsCoasting
        ? _startUs + (long)Math.Ceiling(_tauSeconds * Math.Log(_speed0 / Options.StopSpeed) * 1e6)
        : 0;

    /// <summary>
    /// Routes one gesture event: scroll Began clears and records, Changed records, Ended tries to start coasting,
    /// Cancelled and every other gesture (pinch, tap) stop everything. Returns true when coasting started.
    /// </summary>
    public bool Observe(in GestureEvent e)
    {
        if (e.Kind != GestureKind.Scroll)
        {
            Reset();
            return false;
        }

        switch (e.Phase)
        {
            case GesturePhase.Began:
                Reset();
                AddSample(e.DeltaX, e.DeltaY, e.TimestampUs);
                return false;
            case GesturePhase.Changed:
                AddSample(e.DeltaX, e.DeltaY, e.TimestampUs);
                return false;
            case GesturePhase.Ended:
                return TryStart(e.TimestampUs);
            default:
                Reset();
                return false;
        }
    }

    /// <summary>
    /// Records one scroll delta (Began or Changed) at its event time. Stops any coast in progress (the fingers are
    /// scrolling again). The first delta after a reset only sets the time base: it spans an unknown time since landing.
    /// </summary>
    public void AddSample(double dx, double dy, long timestampUs)
    {
        IsCoasting = false;
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
        {
            return;
        }

        if (_count > 0)
        {
            long newest = _times[(_head + Capacity - 1) % Capacity];
            if (timestampUs < newest)
            {
                timestampUs = newest; // never step back in time
            }
        }

        _times[_head] = timestampUs;
        _dxs[_head] = dx;
        _dys[_head] = dy;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity)
        {
            _count++;
        }
    }

    /// <summary>
    /// The fingers lifted (scroll Ended) at <paramref name="releaseUs"/>: computes the release velocity and starts
    /// coasting if it is fast enough and the fingers did not rest longer than
    /// <see cref="ScrollMomentumOptions.MaxReleasePause"/> before lifting. The recorded deltas are cleared either way.
    /// Returns true when coasting started.
    /// </summary>
    public bool TryStart(long releaseUs)
    {
        IsCoasting = false;
        bool started = ComputeVelocity(releaseUs, out double vx, out double vy);
        ClearSamples();
        if (!started)
        {
            return false;
        }

        double speed = Math.Sqrt((vx * vx) + (vy * vy));
        if (speed < Options.MinStartSpeed)
        {
            return false;
        }

        if (speed > Options.MaxSpeed)
        {
            double k = Options.MaxSpeed / speed;
            vx *= k;
            vy *= k;
            speed = Options.MaxSpeed;
        }

        _v0x = vx;
        _v0y = vy;
        _speed0 = speed;
        _startUs = releaseUs;
        _lastStepUs = releaseUs;
        _emittedX = 0;
        _emittedY = 0;
        IsCoasting = true;
        return true;
    }

    /// <summary>
    /// Advances the coast to <paramref name="nowUs"/> (event clock). Returns true with the displacement since the
    /// previous step (post it as a momentum-phase scroll); returns false with zero deltas when the coast has ended or
    /// is not running (post the momentum end once). A time before the previous step yields a zero step.
    /// </summary>
    public bool Step(long nowUs, out double dx, out double dy)
    {
        dx = 0;
        dy = 0;
        if (!IsCoasting)
        {
            return false;
        }

        if (nowUs < _lastStepUs)
        {
            nowUs = _lastStepUs;
        }

        double t = (nowUs - _startUs) / 1e6;
        double decay = Math.Exp(-t / _tauSeconds);
        if (_speed0 * decay < Options.StopSpeed)
        {
            IsCoasting = false;
            return false;
        }

        double travelled = _tauSeconds * (1 - decay);
        double x = _v0x * travelled;
        double y = _v0y * travelled;
        dx = x - _emittedX;
        dy = y - _emittedY;
        _emittedX = x;
        _emittedY = y;
        _lastStepUs = nowUs;
        return true;
    }

    /// <summary>Stops coasting and forgets the recorded deltas (cancel, pen in range, new touch, session end).</summary>
    public void Reset()
    {
        IsCoasting = false;
        ClearSamples();
    }

    private void ClearSamples()
    {
        _count = 0;
        _head = 0;
    }

    private bool ComputeVelocity(long releaseUs, out double vx, out double vy)
    {
        vx = 0;
        vy = 0;
        if (_count < 2)
        {
            return false;
        }

        int newest = (_head + Capacity - 1) % Capacity;
        long lastUs = _times[newest];
        if (releaseUs - lastUs > _pauseUs)
        {
            return false;
        }

        // Sum the deltas newer than the window start; the sample at or before it (or the oldest) is the time base.
        long windowStart = lastUs - _windowUs;
        double sumX = 0;
        double sumY = 0;
        long baseUs = lastUs;
        for (int n = 0; n < _count; n++)
        {
            int i = (newest - n + Capacity) % Capacity;
            baseUs = _times[i];
            if (baseUs <= windowStart || n == _count - 1)
            {
                break;
            }

            sumX += _dxs[i];
            sumY += _dys[i];
        }

        long durationUs = lastUs - baseUs;
        if (durationUs <= 0)
        {
            return false;
        }

        vx = sumX * 1e6 / durationUs;
        vy = sumY * 1e6 / durationUs;
        return true;
    }
}
