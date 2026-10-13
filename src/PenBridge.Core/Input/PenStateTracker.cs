namespace PenBridge.Core.Input;

/// <summary>Options for <see cref="PenStateTracker"/>.</summary>
public sealed record PenStateTrackerOptions
{
    /// <summary>
    /// Watchdog while the pen is in contact or touches are active: the phone sends at least every 50 ms then, so
    /// silence this long means datagrams are lost and pen-up, touches-up and proximity-leave are synthesized.
    /// </summary>
    public TimeSpan ContactTimeout { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Watchdog while the pen only hovers. The phone has no keepalive for a still hovering pen, so this is longer
    /// than <see cref="ContactTimeout"/> to avoid proximity flapping.
    /// </summary>
    public TimeSpan HoverTimeout { get; init; } = TimeSpan.FromSeconds(1);

    public GestureRecognizerOptions Gestures { get; init; } = new();
}

/// <summary>
/// Mac-side pen state machine: turns full-state input frames into edge events, forwards touches to its
/// <see cref="GestureRecognizer"/>, and runs the input watchdog.
/// </summary>
/// <remarks>
/// Event order within one sample: Up, ButtonUp, ButtonDown, Down, then Move/Drag (only if something changed and no
/// Down/Up was emitted). A tool change releases contact and buttons, emits ToolChanged, then re-applies the new
/// sample. Leaving range emits Up and ButtonUp first, then ProximityLeave. Not thread-safe: the caller serializes
/// <see cref="ProcessFrame"/>, <see cref="CheckWatchdog"/> and <see cref="Reset"/>. Allocation-free per call.
/// The tracker does not own a timer (idle ≈ 0% CPU): after each frame, schedule a one-shot timer for
/// <see cref="WatchdogDueIn"/> and call <see cref="CheckWatchdog"/> when it fires.
/// </remarks>
public sealed class PenStateTracker
{
    private readonly IInputSink _sink;
    private readonly TimeProvider _time;
    private readonly PenStateTrackerOptions _options;
    private long _lastFrameTimestamp;
    private bool _inRange;
    private bool _contact;
    private PenButtons _buttons;
    private PenTool _tool;
    private PenSample _last;
    private CoordinateMapper _mapper = CoordinateMapper.Identity;

    public PenStateTracker(IInputSink sink, TimeProvider? timeProvider = null, PenStateTrackerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new PenStateTrackerOptions();
        Gestures = new GestureRecognizer(sink, _options.Gestures);
    }

    public GestureRecognizer Gestures { get; }

    /// <summary>Maps normalized positions to display points for both pen and gesture events.</summary>
    public CoordinateMapper Mapper
    {
        get => _mapper;
        set
        {
            _mapper = value.IsConfigured ? value : CoordinateMapper.Identity;
            Gestures.Mapper = _mapper;
        }
    }

    public bool IsInRange => _inRange;

    public bool IsInContact => _contact;

    public PenButtons Buttons => _buttons;

    public PenTool Tool => _inRange ? _tool : PenTool.None;

    /// <summary>
    /// Time until <see cref="CheckWatchdog"/> would fire, or null when nothing is active (no timer needed).
    /// Zero when already due.
    /// </summary>
    public TimeSpan? WatchdogDueIn
    {
        get
        {
            if (!TryGetTimeout(out TimeSpan timeout))
            {
                return null;
            }

            TimeSpan remaining = timeout - _time.GetElapsedTime(_lastFrameTimestamp);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Processes one valid, in-order input frame (a decrypted datagram or TLS InputFrame).</summary>
    /// <param name="pen">The current pen sample, or null when the frame carries no pen.</param>
    /// <param name="history">Earlier pen samples from the same frame, oldest first; applied before <paramref name="pen"/>.</param>
    /// <param name="touches">All active touches.</param>
    public void ProcessFrame(in PenSample? pen, ReadOnlySpan<PenSample> history, ReadOnlySpan<TouchSample> touches)
    {
        _lastFrameTimestamp = _time.GetTimestamp();
        for (int i = 0; i < history.Length; i++)
        {
            Step(history[i], present: true);
        }

        Step(pen.GetValueOrDefault(), pen.HasValue);
        Gestures.Process(touches, _inRange);
    }

    /// <summary>
    /// Fires the watchdog if the active timeout elapsed since the last frame: synthesizes pen-up, button-ups,
    /// proximity-leave and cancels any gesture. Returns true if it fired.
    /// </summary>
    public bool CheckWatchdog()
    {
        if (!TryGetTimeout(out TimeSpan timeout) || _time.GetElapsedTime(_lastFrameTimestamp) < timeout)
        {
            return false;
        }

        Reset();
        return true;
    }

    /// <summary>Releases everything with synthetic events (session end, watchdog).</summary>
    public void Reset()
    {
        if (_inRange)
        {
            Leave(synthetic: true);
        }

        Gestures.Cancel(synthetic: true);
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

    private void Step(in PenSample next, bool present)
    {
        bool nextIn = present && IsInRangeSample(next);
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
            Leave(synthetic: false);
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

    private void Leave(bool synthetic)
    {
        ReleaseContactAndButtons(synthetic);
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
        _sink.OnPenEvent(new PenEvent(kind, sample, _mapper.Map(sample.X, sample.Y), button, previousTool, synthetic));
    }
}
