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

    /// <summary>
    /// Mouse mode (ADR-0032): display points the cursor moves per millimetre of pen movement on the phone, before
    /// <see cref="InputSettings.MouseSpeed"/> and the acceleration. Must be positive and finite.
    /// </summary>
    public double MousePointsPerMillimetre { get; init; } = 8;

    /// <summary>Mouse mode: the acceleration curve (on the cursor speed in points/s) when <see cref="InputSettings.MouseAcceleration"/> is on.</summary>
    public GestureAcceleration MouseAcceleration { get; init; } = new();

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
/// (including <see cref="PenTool.None"/>) is ignored. Coming into range first ends the gestures
/// (<see cref="GestureRecognizer.PenEntering"/>: a held Tap, an active gesture's Cancelled), then ProximityEnter.
/// </para>
/// <para>
/// Timestamps: the frame's u32 <c>timeUs</c> is unwrapped to a monotonic 64-bit µs clock (forward steps under
/// 2^31 µs; a backwards step holds the clock). That clock is the per-session sample-dedupe watermark: history samples
/// of a frame that doesn't advance it are skipped (<see cref="ProcessFrame"/>); a new session has a new tracker, and a
/// SurfaceInfo change keeps the watermark (the phone clock runs on). Between frames (held-tap deadline, synthesized
/// releases) the clock is estimated as that clock plus the host time since the frame that last advanced it arrived;
/// the watchdog's silence is measured from the last frame of any time. Not thread-safe: the caller serializes <see cref="ProcessFrame"/>,
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
    private long _clockTimestamp;
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

    // Settings (ADR-0032): the ones in effect, and newer ones waiting for the stroke to end.
    private readonly ICursorLocator? _locator;
    private InputSettings _settings = InputSettings.Default;
    private InputSettings? _pending;
    private PressureCurve _curve = PressureCurve.Linear;
    private bool _eraserStroke; // Eraser action: the current contact decided its tool at contact-down
    private bool _eraserLatched;

    // Mouse mode: the relative cursor.
    private bool _relative; // the events are positioned at _cursor
    private bool _anchored;
    private bool _hasCursor;
    private DisplayPoint _cursor;
    private PenSample _ref;
    private long _refUs;
    private double _mouseSpeed;
    private double _mmPerUnitX = DefaultSurfaceMillimetres / CoordinateMapper.NormalizedMax;
    private double _mmPerUnitY = DefaultSurfaceMillimetres / CoordinateMapper.NormalizedMax;

    /// <summary>Surface size assumed until <see cref="SetSurfaceSize"/> is called (mm per side).</summary>
    public const double DefaultSurfaceMillimetres = 100;

    public PenStateTracker(IInputSink sink, TimeProvider? timeProvider = null, PenStateTrackerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new PenStateTrackerOptions();
        _historyIntervalUs = Math.Max(0, _options.HistorySampleInterval.Ticks / TimeSpan.TicksPerMicrosecond);
        if (!(_options.MousePointsPerMillimetre > 0) || !double.IsFinite(_options.MousePointsPerMillimetre))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MousePointsPerMillimetre must be positive and finite.");
        }

        ArgumentNullException.ThrowIfNull(_options.MouseAcceleration, nameof(PenStateTrackerOptions.MouseAcceleration));
        _options.MouseAcceleration.Validate(nameof(PenStateTrackerOptions.MouseAcceleration));
        _locator = sink as ICursorLocator;
        Gestures = new GestureRecognizer(sink, _options.Gestures);
    }

    /// <summary>The settings in effect for the pen (a newer set waits while the pen is in contact).</summary>
    public InputSettings Settings => _settings;

    /// <summary>
    /// Applies input settings (ADR-0032/0033). Gesture settings apply at once (<see cref="GestureRecognizer.ApplySettings"/>).
    /// Pen settings (stylus mode, side button, pressure curve) apply from the next sample, but while the pen is in
    /// contact they wait until it lifts: a stroke finishes as it started. Not thread-safe (call it where frames are
    /// processed, e.g. under the server's input lock); allocation-free.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range (<see cref="InputSettings.Validate"/>).</exception>
    public void ApplySettings(InputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Gestures.ApplySettings(settings);
        if (_contact)
        {
            _pending = settings;
        }
        else
        {
            Adopt(settings);
        }
    }

    /// <summary>
    /// The phone surface's physical size in the current orientation (from SurfaceInfo: pixels / DPI), for Mouse mode's
    /// millimetre-based movement. Non-positive or non-finite values keep <see cref="DefaultSurfaceMillimetres"/>.
    /// </summary>
    public void SetSurfaceSize(double widthMm, double heightMm)
    {
        _mmPerUnitX = (widthMm > 0 && double.IsFinite(widthMm) ? widthMm : DefaultSurfaceMillimetres) / CoordinateMapper.NormalizedMax;
        _mmPerUnitY = (heightMm > 0 && double.IsFinite(heightMm) ? heightMm : DefaultSurfaceMillimetres) / CoordinateMapper.NormalizedMax;
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
    /// <param name="history">
    /// Earlier pen samples from the same frame, oldest first; applied before <paramref name="pen"/>, but only if
    /// the frame time advances past the previous frame's (sample dedupe: a resent click is not applied twice).
    /// </param>
    /// <param name="touches">All active touches.</param>
    public void ProcessFrame(uint timeUs, in PenSample? pen, ReadOnlySpan<PenSample> history, ReadOnlySpan<TouchSample> touches)
    {
        if (!_mapper.IsConfigured)
        {
            return;
        }

        long arrival = _time.GetTimestamp();
        _lastFrameTimestamp = arrival; // any frame proves the link is alive (watchdog)
        long previousFrameUs = _frameTimeUs;
        bool hadTime = _hasTime;
        long now = Unwrap(timeUs);
        if (!hadTime || now > previousFrameUs)
        {
            // Only a frame that advances the frame clock re-anchors the estimated clock. A copy with the same (or an
            // earlier) time, such as the phone's release repeats or a resend, arrives later than the first copy;
            // anchoring to it would rewind the estimate and push the held-tap deadline back by the copy's delay.
            _clockTimestamp = arrival;
        }

        // Sample dedupe (ProtocolCapabilities.SampleDedupe): history samples were taken before the frame time, so a
        // frame whose time doesn't advance past the last applied frame carries only samples already applied (a
        // phone's click resend). Its current sample is still applied: it is the full state and idempotent.
        if (history.Length > 0 && (!hadTime || now > previousFrameUs))
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
        _sampleTimeUs = EstimatedNowUs();
        if (_inRange)
        {
            ReleaseContactAndButtons(synthetic: true);
        }

        Gestures.Cancel(EventTime(_sampleTimeUs), synthetic: true);
        if (_inRange)
        {
            EmitLeave(synthetic: true);
        }

        AdoptPending();
    }

    /// <summary>
    /// The frame clock now: the frame clock plus the host time elapsed since the frame that last advanced it arrived
    /// (a repeat of an earlier frame time doesn't re-anchor it).
    /// </summary>
    private long EstimatedNowUs()
    {
        long elapsedUs = _time.GetElapsedTime(_clockTimestamp).Ticks / TimeSpan.TicksPerMicrosecond;
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

    private void Step(in PenSample raw, bool present)
    {
        AdoptPending();
        bool nextIn = present && IsInRangeSample(raw);
        if (nextIn && !IsKnownTool(raw.Tool))
        {
            return;
        }

        PenSample next = nextIn ? Transform(raw) : raw;
        if (nextIn)
        {
            UpdateCursor(next);
        }

        if (!_inRange)
        {
            if (!nextIn)
            {
                return;
            }

            // A held two-finger tap and an open gesture end first: they act at the cursor this pen is about to move.
            Gestures.PenEntering(EventTime(_sampleTimeUs));
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
            AdoptPending(); // the stroke finished in the old settings; the rest of the sample uses the new ones
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
        _anchored = false;
        _eraserStroke = false;
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
        DisplayPoint at = _relative ? _cursor : _mapper.Map(sample.X, sample.Y);
        _sink.OnPenEvent(new PenEvent(
            kind, sample, at, button, previousTool, synthetic, EventTime(_sampleTimeUs), _settings.SideButton));
    }

    private void AdoptPending()
    {
        if (_pending is { } pending && !_contact)
        {
            _pending = null;
            Adopt(pending);
        }
    }

    private void Adopt(InputSettings settings)
    {
        if (settings.StylusMode != _settings.StylusMode)
        {
            _anchored = false; // Mouse mode starts from the cursor where it is
        }

        _settings = settings;
        _curve = settings.PressureCurve;
    }

    /// <summary>
    /// Applies the pen settings to an in-range sample: the pressure curve and click threshold (pen and eraser only),
    /// then the Eraser side-button action (the tool is the eraser while the button is held, latched at contact-down).
    /// </summary>
    private PenSample Transform(in PenSample raw)
    {
        PenSample s = raw;
        bool stylus = s.Tool is PenTool.Pen or PenTool.Eraser;
        if (stylus && (s.State & PenState.Contact) != 0 && !_curve.IsIdentity)
        {
            s = _curve.TryApply(s.Pressure, _contact, out ushort pressure)
                ? s with { Pressure = pressure }
                : s with { State = PenState.InRange, Pressure = 0 };
        }

        if (_settings.SideButton != SideButtonAction.Eraser || s.Tool != PenTool.Pen)
        {
            _eraserStroke = false;
            return s;
        }

        bool held = (s.Buttons & PenButtons.Primary) != 0;
        bool eraser;
        if ((s.State & PenState.Contact) != 0)
        {
            if (!_eraserStroke)
            {
                _eraserStroke = true; // contact-down: latch (ADR-0022)
                _eraserLatched = held;
            }

            eraser = _eraserLatched;
        }
        else
        {
            _eraserStroke = false;
            eraser = held;
        }

        return s with { Tool = eraser ? PenTool.Eraser : PenTool.Pen, Buttons = s.Buttons & ~PenButtons.Primary };
    }

    /// <summary>
    /// Mouse mode: the first in-range sample anchors the cursor where it is (no movement); each later one moves it by
    /// the pen's movement in millimetres × <see cref="PenStateTrackerOptions.MousePointsPerMillimetre"/> ×
    /// <see cref="InputSettings.MouseSpeed"/>, accelerated by speed when <see cref="InputSettings.MouseAcceleration"/>
    /// is on, and keeps it on the target's displays. Pen mode, and the finger fallback, stay absolute.
    /// </summary>
    private void UpdateCursor(in PenSample s)
    {
        _relative = _settings.StylusMode == StylusMode.Mouse && s.Tool is PenTool.Pen or PenTool.Eraser;
        if (!_relative)
        {
            _anchored = false;
            return;
        }

        if (!_anchored)
        {
            _anchored = true;
            if (_locator is not null && _locator.TryGetCursorLocation(out DisplayPoint location))
            {
                _cursor = location;
            }
            else if (!_hasCursor)
            {
                DisplayRect b = _mapper.TargetRect;
                _cursor = new DisplayPoint(b.X + (b.Width / 2), b.Y + (b.Height / 2));
            }

            _hasCursor = true;
            _cursor = _mapper.ClampToDisplays(_cursor);
            _ref = s;
            _refUs = _sampleTimeUs;
            _mouseSpeed = 0;
            return;
        }

        int dx = s.X - _ref.X;
        int dy = s.Y - _ref.Y;
        long dt = Math.Clamp(_sampleTimeUs - _refUs, MinSpeedIntervalUs, MaxSpeedIntervalUs);
        _ref = s;
        _refUs = _sampleTimeUs;
        if (dx == 0 && dy == 0)
        {
            return;
        }

        double scale = _options.MousePointsPerMillimetre * _settings.MouseSpeed;
        double px = dx * _mmPerUnitX * scale;
        double py = dy * _mmPerUnitY * scale;
        if (_settings.MouseAcceleration)
        {
            double speed = Math.Sqrt((px * px) + (py * py)) * 1e6 / dt;
            _mouseSpeed = (_mouseSpeed + speed) / 2;
            double gain = _options.MouseAcceleration.GainAt(_mouseSpeed);
            px *= gain;
            py *= gain;
        }

        _cursor = _mapper.ClampToDisplays(new DisplayPoint(_cursor.X + px, _cursor.Y + py));
    }

    private const long MinSpeedIntervalUs = 1_000;
    private const long MaxSpeedIntervalUs = 100_000;
}
