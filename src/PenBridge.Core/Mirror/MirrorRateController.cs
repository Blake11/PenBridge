using PenBridge.Core.Protocol;

namespace PenBridge.Core.Mirror;

/// <summary>
/// The Mac's input guard (ADR-0038 "input always wins", <c>docs/specs/screen-mirroring.md</c> "QoS"): it watches the
/// input path and steps the video down while it suffers, and back up once it's healthy.
/// </summary>
/// <remarks>
/// <para><b>Signals.</b> Degraded when the Ping RTT exceeds the baseline (minimum over the last 10 s) by more than 8 ms
/// in 2 consecutive samples (until a sample is healthy again), when an input datagram during contact arrives more than
/// 30 ms later than the phone's own spacing says or input datagrams around a contact were lost (a sequence gap,
/// <see cref="OnInputDatagram"/>), or when a MirrorReport shows more than 2 % fragment loss; the last two keep the guard
/// degraded for <see cref="StepInterval"/>.</para>
/// <para><b>Ladder</b>, one step per <see cref="StepInterval"/> while degraded: bitrate × 0.7 down to the plan's floor,
/// then the fps halves (120 → 60 → 30, never below 30 by halving; snapped down to a divisor of the phone's refresh,
/// 90 → 45 → 30). At that bottom the video pauses (<see cref="MirrorGuardLevel.Paused"/>, MirrorState Throttled) only
/// once the guard has been degraded without a break for <see cref="SustainedBeforePause"/>, counted from the later of the
/// start of the degradation and the last level change (the bottom gets that long to prove too much), so one blip never
/// pauses. <b>Recovery</b>: after <see cref="HealthyBeforeRecovery"/> healthy, one step up per
/// <see cref="RecoveryInterval"/>: unpause at the bottom (the floor bitrate and the lowest fps), then the fps back to the
/// plan, then bitrate × 1.15 up to the target.</para>
/// <para><b>Driving it.</b> Feed the signals, then call <see cref="Update"/>; it returns true when <see cref="Level"/>
/// changed (apply the new bitrate and fps to the running stream, no new epoch; or Throttled when paused, or resume when
/// unpaused). Nothing runs on its own: after each call arm a one-shot
/// timer for <see cref="NextUpdateIn"/> (null = nothing pending) so recovery happens without new input. Input datagrams
/// are the exception: call <see cref="OnInputDatagram"/> for every one, and only when it returns true (a new input
/// degradation) wake the guard off the input thread (e.g. fire its timer now); the armed timer carries the later steps.
/// RTT samples and reports come at most a few per second: call <see cref="Update"/> after each.</para>
/// <para><b>Threads.</b> <see cref="OnInputDatagram"/> is lock-free and allocation-free and must be called from one
/// thread (the input receive thread). Every other member is thread-safe (one internal lock).</para>
/// <para><b>Lifetime.</b> One guard per session: input sequences restart with a session.</para>
/// </remarks>
public sealed class MirrorRateController
{
    /// <summary>RTT above the baseline by more than this counts as high.</summary>
    public static readonly TimeSpan RttMargin = TimeSpan.FromMilliseconds(8);

    /// <summary>The baseline is the minimum RTT over this window.</summary>
    public static readonly TimeSpan RttBaselineWindow = TimeSpan.FromSeconds(10);

    /// <summary>Consecutive high RTT samples that make the guard degraded.</summary>
    public const int HighRttSamples = 2;

    /// <summary>An input datagram during contact this much later than the phone's spacing is a gap.</summary>
    public static readonly TimeSpan InputGap = TimeSpan.FromMilliseconds(30);

    /// <summary>Fragment loss above this fraction over one report is degraded.</summary>
    public const double LossThreshold = 0.02;

    /// <summary>One step down per this interval while degraded; also how long a gap or loss signal lasts.</summary>
    public static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Healthy this long before the first step up.</summary>
    public static readonly TimeSpan HealthyBeforeRecovery = TimeSpan.FromSeconds(2);

    /// <summary>At the bottom of the ladder, degraded without a break this long before the video pauses.</summary>
    public static readonly TimeSpan SustainedBeforePause = TimeSpan.FromSeconds(2);

    /// <summary>One step up per this interval while healthy.</summary>
    public static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(1);

    /// <summary>Bitrate factor of a step down.</summary>
    public const double BitrateDown = 0.7;

    /// <summary>Bitrate factor of a step up.</summary>
    public const double BitrateUp = 1.15;

    /// <summary>Halving never takes the fps below this.</summary>
    public const int MinHalvedFps = 30;

    /// <summary>
    /// RTT samples closer than this to the first sample of the newest slot share that slot (their minimum, aged by the
    /// newest), so the history covers the whole window at any ping rate: exact up to 10 pings/s, at most this much too
    /// long (a lower baseline) above.
    /// </summary>
    public static readonly TimeSpan RttBucket = TimeSpan.FromMilliseconds(100);

    // One slot per bucket over the window, plus the partial ones at both ends.
    private const int RttCapacity = 128;

    // "Never happened": far enough from long.MinValue that now - Never can't overflow.
    private const long Never = long.MinValue / 2;

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly long[] _rttAt = new long[RttCapacity];
    private readonly long[] _rttFirstAt = new long[RttCapacity];
    private readonly long[] _rttTicks = new long[RttCapacity];
    private readonly long _gapTicks;
    private readonly long _bucketTicks;
    private int _rttStart;
    private int _rttCount;
    private int _highRtt;
    private bool _rttDegraded;
    private long _rttClearedAt = Never;
    private long _signalUntil = Never;
    private long _lastDownAt = Never;
    private long _lastUpAt = Never;
    private long _consumedGapAt = Never;
    private long _degradedSince = Never; // start of the current degradation without a break
    private long _levelAt = Never; // the last change of the level (step, start, retarget)

    // Plan and position.
    private bool _active;
    private int _target;
    private int _floor;
    private int _plannedFps;
    private int _phoneMaxFps;
    private int _bitrate;
    private int _halvings;
    private bool _paused;

    // Loss-report bookkeeping (OnMirrorReport).
    private bool _haveReport;
    private byte _reportEpoch;
    private uint _reportLost;
    private ulong _reportSent;

    // Input thread only (OnInputDatagram), except _gapAt, which is published.
    private readonly long _stepTicks;
    private bool _haveInput;
    private bool _inContact;
    private long _lastInputAt;
    private uint _lastInputTimeUs;
    private ulong _lastInputSequence;
    private long _inputSignalUntil = Never;
    private long _gapAt = Never;

    /// <summary>Creates the guard; <paramref name="time"/> drives every timestamp (inject a fake in tests).</summary>
    public MirrorRateController(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _gapTicks = ToTicks(InputGap);
        _stepTicks = ToTicks(StepInterval);
        _bucketTicks = ToTicks(RttBucket);
    }

    /// <summary>The current level; the plan's target, fps and not paused right after <see cref="Start"/>. Default before it.</summary>
    public MirrorGuardLevel Level
    {
        get
        {
            lock (_lock)
            {
                return CurrentLevel();
            }
        }
    }

    /// <summary>True while degraded right now.</summary>
    public bool IsDegraded
    {
        get
        {
            lock (_lock)
            {
                long now = _time.GetTimestamp();
                FoldInputGap();
                return Degraded(now);
            }
        }
    }

    /// <summary>The RTT baseline (minimum over the last 10 s), null without samples in the window.</summary>
    public TimeSpan? RttBaseline
    {
        get
        {
            lock (_lock)
            {
                long now = _time.GetTimestamp();
                return Baseline(now) is long b ? FromTicks(b) : null;
            }
        }
    }

    /// <summary>
    /// Starts a stream at full quality (MirrorStart, or a new codec): bitrate = target, the planned fps, not paused.
    /// The RTT history and active signals are kept (they describe the network, not the stream).
    /// </summary>
    public void Start(in MirrorStreamPlan plan)
    {
        lock (_lock)
        {
            SetPlan(plan);
            _bitrate = _target;
            _halvings = 0;
            _paused = false;
            _active = true;
            _haveReport = false;
            _levelAt = _time.GetTimestamp();
        }
    }

    /// <summary>
    /// Replans without losing the guard's position (target, region, window size, quality or fps changed): the bitrate
    /// keeps its fraction of the target within the new floor..target, the fps keeps its halvings, a pause stays.
    /// Starts the stream when it isn't running.
    /// </summary>
    public void Retarget(in MirrorStreamPlan plan)
    {
        lock (_lock)
        {
            long now = _time.GetTimestamp();
            if (!_active)
            {
                SetPlan(plan);
                _bitrate = _target;
                _halvings = 0;
                _paused = false;
                _active = true;
                _levelAt = now;
                return;
            }

            MirrorGuardLevel before = CurrentLevel();
            double fraction = _target > 0 ? (double)_bitrate / _target : 1;
            SetPlan(plan);
            _bitrate = Math.Clamp((int)Math.Round(_target * fraction), _floor, _target);
            while (_halvings > 0 && HalvedFps(_halvings) == HalvedFps(_halvings - 1))
            {
                _halvings--;
            }

            if (CurrentLevel() != before)
            {
                _levelAt = now;
            }
        }
    }

    /// <summary>Ends the stream (MirrorStop, session end); <see cref="Update"/> then changes nothing.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _active = false;
            _haveReport = false;
        }
    }

    /// <summary>One Ping RTT sample of the session (the server's own pings).</summary>
    public void OnRttSample(TimeSpan rtt)
    {
        if (rtt < TimeSpan.Zero)
        {
            return;
        }

        lock (_lock)
        {
            long now = _time.GetTimestamp();
            FoldInputGap();
            long ticks = ToTicks(rtt);
            AddRtt(now, ticks);
            long baseline = Baseline(now) ?? ticks;
            if (ticks > baseline + ToTicks(RttMargin))
            {
                if (++_highRtt >= HighRttSamples && !_rttDegraded)
                {
                    if (now >= _signalUntil)
                    {
                        _degradedSince = now; // healthy until now: a new degradation starts
                    }

                    _rttDegraded = true;
                }
            }
            else
            {
                _highRtt = 0;
                if (_rttDegraded)
                {
                    _rttDegraded = false;
                    _rttClearedAt = now;
                }
            }
        }
    }

    /// <summary>
    /// One authenticated input datagram (every one, empty probes and keepalives too): its <paramref name="sequence"/>,
    /// whether the pen or a touch is in contact in it, and its <c>timeUs</c> (phone clock). Two input signals, each a
    /// degraded signal for <see cref="StepInterval"/>:
    /// <list type="bullet">
    /// <item>Late: while the previous datagram was in contact, the arrival spacing beyond the phone's own spacing is more
    /// than <see cref="InputGap"/>. Comparing with the phone's spacing keeps a still pen (40 ms contact keepalives) from
    /// reading as a gap; a <c>timeUs</c> that goes backwards skips this check.</item>
    /// <item>Lost: the sequence skips numbers while the previous or this datagram is in contact (the lost ones may have
    /// carried the contact, e.g. a pen-down).</item>
    /// </list>
    /// A sequence at or below the previous one starts over without a signal (the receive path already drops replays, so
    /// this is a new sequence space). Returns true when this datagram starts a new input degradation (no input signal was
    /// active): wake the guard then (see the class remarks); false otherwise, also while a signal is extended.
    /// Call from the input receive thread only; lock-free and allocation-free.
    /// </summary>
    public bool OnInputDatagram(ulong sequence, bool inContact, uint timeUs)
    {
        long now = _time.GetTimestamp();
        bool gap = false;
        if (_haveInput && sequence > _lastInputSequence)
        {
            gap = sequence - _lastInputSequence > 1 && (_inContact || inContact);
            if (!gap && _inContact)
            {
                int sentUs = unchecked((int)(timeUs - _lastInputTimeUs));
                if (sentUs >= 0)
                {
                    long sentTicks = (long)(sentUs * (double)_time.TimestampFrequency / 1_000_000);
                    gap = now - _lastInputAt - sentTicks > _gapTicks;
                }
            }
        }

        _haveInput = true;
        _inContact = inContact;
        _lastInputAt = now;
        _lastInputTimeUs = timeUs;
        _lastInputSequence = sequence;
        if (!gap)
        {
            return false;
        }

        bool started = now >= _inputSignalUntil;
        _inputSignalUntil = now + _stepTicks;
        Volatile.Write(ref _gapAt, now);
        return started;
    }

    /// <summary>Fragment loss over one report: <paramref name="lost"/> of <paramref name="sent"/> fragments. More than 2 % is a degraded signal.</summary>
    public void OnVideoLoss(uint lost, uint sent)
    {
        if (sent == 0 || lost <= sent * LossThreshold)
        {
            return;
        }

        lock (_lock)
        {
            FoldInputGap();
            Signal(_time.GetTimestamp());
        }
    }

    /// <summary>
    /// A MirrorReport for the current epoch, with the fragments the Mac sent in that epoch so far: computes the loss
    /// since the previous report of the epoch (counters wrap) and feeds <see cref="OnVideoLoss"/>. Reports of another
    /// epoch are ignored; the first report of an epoch counts from its MirrorConfig.
    /// </summary>
    public void OnMirrorReport(in MirrorReportMessage report, byte currentEpoch, ulong fragmentsSentInEpoch)
    {
        if (report.Epoch != currentEpoch)
        {
            return;
        }

        uint lost;
        ulong sent;
        lock (_lock)
        {
            bool same = _haveReport && _reportEpoch == currentEpoch;
            lost = same ? unchecked(report.FragmentsLost - _reportLost) : report.FragmentsLost;
            sent = same ? fragmentsSentInEpoch - Math.Min(_reportSent, fragmentsSentInEpoch) : fragmentsSentInEpoch;
            _haveReport = true;
            _reportEpoch = currentEpoch;
            _reportLost = report.FragmentsLost;
            _reportSent = fragmentsSentInEpoch;
        }

        OnVideoLoss(lost, (uint)Math.Min(sent, uint.MaxValue));
    }

    /// <summary>
    /// Evaluates the signals at the current time and takes at most one step. True when <see cref="Level"/> changed.
    /// </summary>
    public bool Update()
    {
        lock (_lock)
        {
            long now = _time.GetTimestamp();
            FoldInputGap();
            if (!_active)
            {
                return false;
            }

            if (Degraded(now))
            {
                if (_lastDownAt != Never && now - _lastDownAt < ToTicks(StepInterval))
                {
                    return false;
                }

                if (StepDown(now))
                {
                    _lastDownAt = now;
                    _levelAt = now;
                    return true;
                }

                return false;
            }

            if (now - HealthyFrom() < ToTicks(HealthyBeforeRecovery)
                || (_lastUpAt != Never && now - _lastUpAt < ToTicks(RecoveryInterval)))
            {
                return false;
            }

            if (StepUp())
            {
                _lastUpAt = now;
                _levelAt = now;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// How long until <see cref="Update"/> may change something without a new signal (arm a one-shot timer); null when
    /// nothing is pending (stopped, at full quality and healthy, or paused while the RTT stays high).
    /// </summary>
    public TimeSpan? NextUpdateIn
    {
        get
        {
            lock (_lock)
            {
                long now = _time.GetTimestamp();
                FoldInputGap();
                if (!_active)
                {
                    return null;
                }

                long? due = null;
                if (Degraded(now))
                {
                    if (CanStepDown())
                    {
                        due = _lastDownAt == Never ? now : Math.Max(now, _lastDownAt + ToTicks(StepInterval));
                        if (OnlyPauseLeft())
                        {
                            due = Math.Max(due.Value, PauseDue());
                        }
                    }

                    if (!_rttDegraded && IsBelowPlan())
                    {
                        // The signal window ends; recovery starts counting then.
                        due = Earliest(due, _signalUntil);
                    }
                }
                else if (IsBelowPlan())
                {
                    long up = HealthyFrom() + ToTicks(HealthyBeforeRecovery);
                    if (_lastUpAt != Never)
                    {
                        up = Math.Max(up, _lastUpAt + ToTicks(RecoveryInterval));
                    }

                    due = Math.Max(now, up);
                }

                return due is long d ? FromTicks(d - now) : null;
            }
        }
    }

    private static long? Earliest(long? a, long b) => a is long x ? Math.Min(x, b) : b;

    private long ToTicks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    private TimeSpan FromTicks(long ticks) => TimeSpan.FromSeconds((double)ticks / _time.TimestampFrequency);

    private void SetPlan(in MirrorStreamPlan plan)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(plan.TargetBitrateKbps, 1, nameof(plan));
        ArgumentOutOfRangeException.ThrowIfLessThan(plan.Fps, 1, nameof(plan));
        _target = plan.TargetBitrateKbps;
        _floor = Math.Clamp(plan.FloorBitrateKbps, 1, _target);
        _plannedFps = plan.Fps;
        _phoneMaxFps = plan.PhoneMaxFps;
    }

    private MirrorGuardLevel CurrentLevel() => _active ? new MirrorGuardLevel(_bitrate, HalvedFps(_halvings), _paused) : default;

    private int HalvedFps(int halvings)
    {
        int fps = _plannedFps;
        for (int i = 0; i < halvings && fps > MinHalvedFps; i++)
        {
            fps = MirrorStreamPlanner.SnapFps(Math.Max(MinHalvedFps, fps / 2), _phoneMaxFps);
        }

        return fps;
    }

    private bool CanStepDown() => _bitrate > _floor || HalvedFps(_halvings) > MinHalvedFps || !_paused;

    private bool OnlyPauseLeft() => _bitrate <= _floor && HalvedFps(_halvings) <= MinHalvedFps && !_paused;

    private long PauseDue() => Math.Max(_degradedSince, _levelAt) + ToTicks(SustainedBeforePause);

    // The fewest halvings that reach the lowest fps (where halving stops changing it).
    private int BottomHalvings()
    {
        int halvings = 0;
        while (halvings < 8 && HalvedFps(halvings + 1) < HalvedFps(halvings))
        {
            halvings++;
        }

        return halvings;
    }

    private bool IsBelowPlan() => _paused || _halvings > 0 || _bitrate < _target;

    private bool StepDown(long now)
    {
        if (_bitrate > _floor)
        {
            _bitrate = Math.Max(_floor, (int)Math.Round(_bitrate * BitrateDown, MidpointRounding.AwayFromZero));
            return true;
        }

        if (HalvedFps(_halvings) > MinHalvedFps)
        {
            _halvings++;
            return true;
        }

        if (!_paused && now >= PauseDue())
        {
            _paused = true;
            return true;
        }

        return false;
    }

    private bool StepUp()
    {
        if (_paused)
        {
            // Resume at the bottom: the floor and the lowest fps (a retarget during the pause may have moved them).
            _paused = false;
            _bitrate = _floor;
            _halvings = BottomHalvings();
            return true;
        }

        if (_halvings > 0)
        {
            _halvings--;
            return true;
        }

        if (_bitrate < _target)
        {
            _bitrate = Math.Min(_target, Math.Max(_bitrate + 1, (int)Math.Round(_bitrate * BitrateUp, MidpointRounding.AwayFromZero)));
            return true;
        }

        return false;
    }

    private bool Degraded(long now) => _rttDegraded || now < _signalUntil;

    private long HealthyFrom() => Math.Max(_signalUntil, _rttClearedAt);

    private void Signal(long at)
    {
        if (!_rttDegraded && at >= _signalUntil)
        {
            _degradedSince = at; // healthy until then: a new degradation starts
        }

        _signalUntil = Math.Max(_signalUntil, at + ToTicks(StepInterval));
    }

    private void FoldInputGap()
    {
        long gapAt = Volatile.Read(ref _gapAt);
        if (gapAt != _consumedGapAt)
        {
            _consumedGapAt = gapAt;
            Signal(gapAt);
        }
    }

    private void AddRtt(long at, long ticks)
    {
        if (_rttCount > 0)
        {
            int last = (_rttStart + _rttCount - 1) % RttCapacity;
            if (at - _rttFirstAt[last] < _bucketTicks)
            {
                _rttAt[last] = at;
                _rttTicks[last] = Math.Min(_rttTicks[last], ticks);
                return;
            }
        }

        int index = (_rttStart + _rttCount) % RttCapacity;
        if (_rttCount == RttCapacity)
        {
            _rttStart = (_rttStart + 1) % RttCapacity;
        }
        else
        {
            _rttCount++;
        }

        _rttAt[index] = at;
        _rttFirstAt[index] = at;
        _rttTicks[index] = ticks;
    }

    private long? Baseline(long now)
    {
        long window = ToTicks(RttBaselineWindow);
        long? min = null;
        for (int i = 0; i < _rttCount; i++)
        {
            int index = (_rttStart + i) % RttCapacity;
            if (now - _rttAt[index] <= window && (min is null || _rttTicks[index] < min))
            {
                min = _rttTicks[index];
            }
        }

        return min;
    }
}
