namespace PenBridge.Core.Input;

/// <summary>Options for <see cref="PenStateTracker"/>.</summary>
public sealed record PenStateTrackerOptions
{
    /// <summary>
    /// Watchdog while the pen is in contact or touches are active: the phone sends at least every 50 ms then, so
    /// silence this long means datagrams are lost; the full release (pen-up, touches-up, proximity-leave) is synthesized.
    /// </summary>
    public TimeSpan ContactTimeout { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Watchdog while the pen only hovers. The phone sends a hover keepalive every 250 ms, so silence this long
    /// means the link is gone and proximity-leave is synthesized.
    /// </summary>
    public TimeSpan HoverTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Nominal spacing of history samples (the phone batches samples at about 240 Hz). History samples are placed
    /// this far apart, ending at the frame time, and compressed if the previous frame is closer than that.
    /// </summary>
    public TimeSpan HistorySampleInterval { get; init; } = TimeSpan.FromMicroseconds(4167);

    public GestureRecognizerOptions Gestures { get; init; } = new();
}

/// <summary>
/// Mac-side pen state machine: turns full-state input frames into edge events, forwards touches to its
/// <see cref="GestureRecognizer"/>, and runs the input watchdog.
/// </summary>
/// <remarks>
/// <para>
/// Apply SurfaceInfo first: frames are dropped (no events, watchdog not armed) until a configured
/// <see cref="Mapper"/> is set.
/// </para>
/// <para>
/// Event order within one sample: Up, ButtonUp, ButtonDown, Down, then Move/Drag (only if something changed and no
/// Down/Up was emitted). A tool change releases contact and buttons, emits ToolChanged, then re-applies the new
/// sample. Leaving range emits Up and ButtonUp first, then ProximityLeave. An in-range sample with an unknown tool
/// (including <see cref="PenTool.None"/>) is ignored.
/// </para>
/// <para>
/// Timestamps: the frame's u32 <c>timeUs</c> is unwrapped to a monotonic 64-bit µs clock (forward steps under
/// 2^31 µs; a backwards step holds the clock). Not thread-safe: the caller serializes <see cref="ProcessFrame"/>,
/// <see cref="CheckWatchdog"/> and <see cref="Reset"/>. Allocation-free per call. The tracker owns no timer
/// (idle ≈ 0% CPU): after each frame, schedule a one-shot timer for <see cref="WatchdogDueIn"/> and call
/// <see cref="CheckWatchdog"/> when it fires.
/// </para>
/// </remarks>
public sealed class PenStateTracker
{
    private readonly IInputSink _sink;
    private readonly TimeProvider _time;
    private readonly PenStateTrackerOptions _options;
    private readonly long _historyIntervalUs;
    private long _lastFrameTimestamp;
    private bool _hasTime;
    private uint _lastRawTimeUs;
    private long _frameTimeUs;
    private long _lastEventUs;
    private long _sampleTimeUs;
    private bool _inRange;
    private bool _contact;
    private PenButtons _buttons;
    private PenTool _tool;
    private PenSample _last;
    private CoordinateMapper _mapper;

    public PenStateTracker(IInputSink sink, TimeProvider? timeProvider = null, PenStateTrackerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new PenStateTrackerOptions();
        _historyIntervalUs = Math.Max(0, _options.HistorySampleInterval.Ticks / TimeSpan.TicksPerMicrosecond);
        Gestures = new GestureRecognizer(sink, _options.Gestures);
    }

    public GestureRecognizer Gestures { get; }

    /// <summary>Maps normalized positions to display points for pen and gesture events. Set from SurfaceInfo before frames.</summary>
    public CoordinateMapper Mapper
    {
        get => _mapper;
        set
        {
            _mapper = value;
            Gestures.Mapper = value;
        }
    }

    /// <summary>False until a configured <see cref="Mapper"/> is set; frames are dropped meanwhile.</summary>
    public bool IsMapperConfigured => _mapper.IsConfigured;

    public bool IsInRange => _inRange;

    public bool IsInContact => _contact;

    public PenButtons Buttons => _buttons;

    public PenTool Tool => _inRange ? _tool : PenTool.None;

    /// <summary>
    /// Time until <see cref="CheckWatchdog"/> has work to do, or null when nothing is pending (no timer needed): the
    /// watchdog timeout, or the gesture recognizer's held-tap deadline
    /// (<see cref="GestureRecognizer.PendingDeadlineUs"/>) if that comes first. Zero when already due.
    /// </summary>
    public TimeSpan? WatchdogDueIn
    {
        get
        {
            TimeSpan? due = null;
            if (TryGetTimeout(out TimeSpan timeout))
            {
                due = timeout - _time.GetElapsedTime(_lastFrameTimestamp);
            }

            if (Gestures.PendingDeadlineUs is { } deadline)
            {
                TimeSpan gestureDue = TimeSpan.FromMicroseconds(deadline - EstimatedNowUs());
                if (due is not { } d || gestureDue < d)
                {
                    due = gestureDue;
                }
            }

            return due is { } r ? (r > TimeSpan.Zero ? r : TimeSpan.Zero) : null;
        }
    }

    /// <summary>Processes one valid, in-order input frame (a decrypted datagram or TLS InputFrame).</summary>
    /// <param name="timeUs">The frame's <c>timeUs</c> (µs since session start, wraps).</param>
    /// <param name="pen">The current pen sample, or null when the frame carries no pen.</param>
    /// <param name="history">Earlier pen samples from the same frame, oldest first; applied before <paramref name="pen"/>.</param>
    /// <param name="touches">All active touches.</param>
    public void ProcessFrame(uint timeUs, in PenSample? pen, ReadOnlySpan<PenSample> history, ReadOnlySpan<TouchSample> touches)
    {
        if (!_mapper.IsConfigured)
        {
            return;
        }

        _lastFrameTimestamp = _time.GetTimestamp();
        long previousFrameUs = _frameTimeUs;
        bool hadTime = _hasTime;
        long now = Unwrap(timeUs);

        if (history.Length > 0)
        {
            long interval = _historyIntervalUs;
            if (hadTime)
            {
                interval = Math.Min(interval, (now - previousFrameUs) / (history.Length + 1));
            }

            for (int i = 0; i < history.Length; i++)
            {
                _sampleTimeUs = now - ((history.Length - i) * interval);
                Step(history[i], present: true);
            }
        }

        _sampleTimeUs = now;
        Step(pen.GetValueOrDefault(), pen.HasValue);
        Gestures.Process(touches, _inRange, EventTime(now));
    }

    /// <summary>
    /// Resolves a held gesture tap whose deadline passed (<see cref="GestureRecognizer.CheckDeadline"/>, at the
    /// estimated current frame time), then fires the watchdog if the active timeout elapsed since the last frame:
    /// synthesizes the full release (see <see cref="Reset"/>). Returns true if the watchdog fired.
    /// </summary>
    public bool CheckWatchdog()
    {
        if (Gestures.PendingDeadlineUs is { } deadline)
        {
            long nowUs = EstimatedNowUs();
            if (nowUs >= deadline)
            {
                Gestures.CheckDeadline(EventTime(nowUs));
            }
        }

        if (!TryGetTimeout(out TimeSpan timeout) || _time.GetElapsedTime(_lastFrameTimestamp) < timeout)
        {
            return false;
        }

        Reset();
        return true;
    }

    /// <summary>
    /// Releases everything with synthetic events, in order: Up and ButtonUps, gesture Cancelled (touches-up), then
    /// ProximityLeave. Used by the watchdog and on session end. A two-finger tap still held for its double-tap window
    /// is dropped (<see cref="GestureRecognizer.Cancel"/>), so no late right-click follows a link loss.
    /// </summary>
    public void Reset()
    {
        long elapsedUs = _time.GetElapsedTime(_lastFrameTimestamp).Ticks / TimeSpan.TicksPerMicrosecond;
        _sampleTimeUs = _frameTimeUs + Math.Max(0, elapsedUs);
        if (_inRange)
        {
            ReleaseContactAndButtons(synthetic: true);
        }

        Gestures.Cancel(EventTime(_sampleTimeUs), synthetic: true);
        if (_inRange)
        {
            EmitLeave(synthetic: true);
        }
    }

    /// <summary>The frame clock now: the last frame's time plus the host time elapsed since it arrived.</summary>
    private long EstimatedNowUs()
    {
        long elapsedUs = _time.GetElapsedTime(_lastFrameTimestamp).Ticks / TimeSpan.TicksPerMicrosecond;
        return _frameTimeUs + Math.Max(0, elapsedUs);
    }

    private long Unwrap(uint raw)
    {
        if (!_hasTime)
        {
            _hasTime = true;
            _frameTimeUs = raw;
        }
        else
        {
            int delta = unchecked((int)(raw - _lastRawTimeUs));
            if (delta > 0)
            {
                _frameTimeUs += delta;
            }
        }

        _lastRawTimeUs = raw;
        return _frameTimeUs;
    }

    /// <summary>Clamps to the last emitted time so event timestamps never go backwards.</summary>
    private long EventTime(long t)
    {
        if (t < _lastEventUs)
        {
            t = _lastEventUs;
        }

        _lastEventUs = t;
        return t;
    }

    private bool TryGetTimeout(out TimeSpan timeout)
    {
        if (_contact || Gestures.ContactCount > 0 || Gestures.IsActive)
        {
            timeout = _options.ContactTimeout;
            return true;
        }

        if (_inRange)
        {
            timeout = _options.HoverTimeout;
            return true;
        }

        timeout = default;
        return false;
    }

    private static bool IsInRangeSample(in PenSample s) => (s.State & (PenState.InRange | PenState.Contact)) != 0;

    private static bool IsKnownTool(PenTool tool) => tool is PenTool.Pen or PenTool.Eraser or PenTool.FingerFallback;

    private void Step(in PenSample next, bool present)
    {
        bool nextIn = present && IsInRangeSample(next);
        if (nextIn && !IsKnownTool(next.Tool))
        {
            return;
        }

        if (!_inRange)
        {
            if (!nextIn)
            {
                return;
            }

            _inRange = true;
            _contact = false;
            _buttons = PenButtons.None;
            _tool = next.Tool;
            _last = next;
            Emit(PenEventKind.ProximityEnter, next, PenButtons.None, PenTool.None, synthetic: false);
            Apply(next);
            return;
        }

        if (!nextIn)
        {
            ReleaseContactAndButtons(synthetic: false);
            EmitLeave(synthetic: false);
            return;
        }

        if (next.Tool != _tool)
        {
            ReleaseContactAndButtons(synthetic: false);
            PenTool previous = _tool;
            _tool = next.Tool;
            _last = _last with { Tool = next.Tool };
            Emit(PenEventKind.ToolChanged, next, PenButtons.None, previous, synthetic: false);
        }

        Apply(next);
    }

    private void Apply(in PenSample next)
    {
        bool contact = (next.State & PenState.Contact) != 0;
        bool changed = !SameMotion(_last, next);

        if (_contact && !contact)
        {
            _contact = false;
            changed = false;
            Emit(PenEventKind.Up, next, PenButtons.None, PenTool.None, synthetic: false);
        }

        PenButtons released = _buttons & ~next.Buttons;
        PenButtons pressed = next.Buttons & ~_buttons;
        EmitButtons(PenEventKind.ButtonUp, released, next, synthetic: false);
        EmitButtons(PenEventKind.ButtonDown, pressed, next, synthetic: false);
        _buttons = next.Buttons;

        if (!_contact && contact)
        {
            _contact = true;
            changed = false;
            Emit(PenEventKind.Down, next, PenButtons.None, PenTool.None, synthetic: false);
        }

        if (changed)
        {
            Emit(contact ? PenEventKind.Drag : PenEventKind.Move, next, PenButtons.None, PenTool.None, synthetic: false);
        }

        _last = next;
    }

    private void EmitLeave(bool synthetic)
    {
        _inRange = false;
        PenSample gone = _last with { State = PenState.None, Pressure = 0, Buttons = PenButtons.None };
        Emit(PenEventKind.ProximityLeave, gone, PenButtons.None, PenTool.None, synthetic);
        _last = gone;
    }

    private void ReleaseContactAndButtons(bool synthetic)
    {
        if (_contact)
        {
            _contact = false;
            _last = _last with { State = PenState.InRange, Pressure = 0 };
            Emit(PenEventKind.Up, _last, PenButtons.None, PenTool.None, synthetic);
        }

        if (_buttons != PenButtons.None)
        {
            PenButtons released = _buttons;
            _buttons = PenButtons.None;
            _last = _last with { Buttons = PenButtons.None };
            EmitButtons(PenEventKind.ButtonUp, released, _last, synthetic);
        }
    }

    private void EmitButtons(PenEventKind kind, PenButtons buttons, in PenSample sample, bool synthetic)
    {
        if ((buttons & PenButtons.Primary) != 0)
        {
            Emit(kind, sample, PenButtons.Primary, PenTool.None, synthetic);
        }

        if ((buttons & PenButtons.Secondary) != 0)
        {
            Emit(kind, sample, PenButtons.Secondary, PenTool.None, synthetic);
        }
    }

    private static bool SameMotion(in PenSample a, in PenSample b) =>
        a.X == b.X && a.Y == b.Y && a.Pressure == b.Pressure && a.TiltX == b.TiltX && a.TiltY == b.TiltY
        && a.Rotation == b.Rotation;

    private void Emit(PenEventKind kind, in PenSample sample, PenButtons button, PenTool previousTool, bool synthetic)
    {
        _sink.OnPenEvent(new PenEvent(
            kind, sample, _mapper.Map(sample.X, sample.Y), button, previousTool, synthetic, EventTime(_sampleTimeUs)));
    }
}
