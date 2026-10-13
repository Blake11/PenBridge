namespace PenBridge.Core.Input;

/// <summary>A finger pointer as sampled by the phone head, before filtering. Local only: never sent on the wire.</summary>
/// <param name="PointerId">Platform pointer id (stable while the finger is down).</param>
/// <param name="Contact">True while the finger touches the surface.</param>
/// <param name="Canceled">True when the platform canceled the pointer (e.g. Android <c>FLAG_CANCELED</c>, palm rejection).</param>
/// <param name="X">Normalized 0..65535.</param>
/// <param name="Y">Normalized 0..65535.</param>
/// <param name="Size">
/// Contact size normalized to 0..1 (Android: touch major over the device's touch-major range, else
/// <c>MotionEvent.getSize</c>). 0 means unknown and never counts as a palm.
/// </param>
public readonly record struct RawTouch(byte PointerId, bool Contact, bool Canceled, ushort X, ushort Y, float Size = 0f);

/// <summary>Options for <see cref="InputFilter"/>.</summary>
public sealed record InputFilterOptions
{
    /// <summary>How long touches stay suppressed after the pen was last in range or in contact.</summary>
    public TimeSpan PenSuppression { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>When true and no pen is present, a single finger is reported as a <see cref="PenTool.FingerFallback"/> pen sample.</summary>
    public bool FingerFallbackEnabled { get; init; } = true;

    /// <summary>Synthetic pressure (0..65535) for finger-fallback samples.</summary>
    public ushort FingerFallbackPressure { get; init; } = 0x8000;

    /// <summary>
    /// How long a lone finger is held back before it starts a finger-fallback stroke. A second finger landing within
    /// this window makes it a gesture (touches only, never a stroke). Zero arms at once.
    /// </summary>
    public TimeSpan FingerFallbackArmDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Contact size (<see cref="RawTouch.Size"/>, 0..1) at or above which a contact counts as a palm. Conservative
    /// default (about 35 mm on a 6.8" phone when the size is touch major over the screen diagonal); a value above 1
    /// disables size-based palm rejection.
    /// </summary>
    public float PalmSizeThreshold { get; init; } = 0.2f;

    /// <summary>How long touches stay suppressed after the last finger lifted following a palm.</summary>
    public TimeSpan PalmSuppression { get; init; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>Result of <see cref="InputFilter.Process"/>.</summary>
/// <param name="HasPen">True when <see cref="Pen"/> should be sent.</param>
/// <param name="Pen">The pen (or finger-fallback) sample to send.</param>
/// <param name="TouchCount">Number of touches written to the destination span.</param>
/// <param name="ReplayCount">
/// Number of held finger-fallback samples (<see cref="InputFilter.ReplaySamples"/>) to send before <see cref="Pen"/>,
/// oldest first.
/// </param>
public readonly record struct InputFilterResult(bool HasPen, PenSample Pen, int TouchCount, int ReplayCount = 0);

/// <summary>
/// Phone-side, pen-first input filter. Feed it the full current pointer state on every platform input event; it
/// returns what to put into the next datagram.
/// </summary>
/// <remarks>
/// <para>Pen: while the pen is in range or in contact, and for <see cref="InputFilterOptions.PenSuppression"/>
/// afterwards, touches are suppressed. A finger that landed during suppression, or was canceled by the platform,
/// stays suppressed until it lifts (a resting palm never leaks through).</para>
/// <para>Palm: a contact with <see cref="RawTouch.Size"/> at or above <see cref="InputFilterOptions.PalmSizeThreshold"/>
/// suppresses every touch until all fingers lift, then for <see cref="InputFilterOptions.PalmSuppression"/>; a finger
/// landing in that window is suppressed until it lifts and restarts the window.</para>
/// <para>Finger fallback: with no pen, a lone finger is held for <see cref="InputFilterOptions.FingerFallbackArmDelay"/>.
/// If a second finger lands first, it is a gesture: the held samples are dropped and the fingers are reported as raw
/// touches until all lift. Otherwise the stroke starts and the held samples are replayed
/// (<see cref="InputFilterResult.ReplayCount"/>); a finger that lifts within the delay is replayed as a whole tap
/// ending in a sample with <see cref="PenState.None"/>. A finger that does not move produces no event, so the head
/// calls <see cref="Process"/> again at <see cref="PendingDeadline"/>.</para>
/// <para>Not thread-safe; allocation-free per call.</para>
/// </remarks>
public sealed class InputFilter
{
    /// <summary>Maximum touches per datagram.</summary>
    public const int MaxTouches = 10;

    /// <summary>
    /// Capacity of the finger-fallback hold buffer. Beyond it the newest sample replaces the last slot (the touch-down
    /// point and the latest position are always kept); 128 covers 100 ms at well over 1 kHz.
    /// </summary>
    public const int MaxHeldSamples = 128;

    private readonly InputFilterOptions _options;
    private readonly bool[] _suppressed = new bool[256];
    private readonly bool[] _present = new bool[256];
    private readonly byte[] _previousIds = new byte[256];
    private readonly PenSample[] _held = new PenSample[MaxHeldSamples];
    private int _previousCount;
    private bool _penSeen;
    private TimeSpan _lastPenTime;
    private FingerMode _mode;
    private int _heldCount;
    private byte _heldId;
    private TimeSpan _holdStart;
    private int _replayCount;
    private bool _palm;
    private bool _palmTouching;
    private TimeSpan _palmLiftTime;

    public InputFilter(InputFilterOptions? options = null)
    {
        _options = options ?? new InputFilterOptions();
    }

    private enum FingerMode : byte
    {
        /// <summary>No unsuppressed finger down.</summary>
        Idle,
        /// <summary>A lone finger is being held back before arming finger fallback.</summary>
        Holding,
        /// <summary>A finger-fallback stroke is in progress.</summary>
        Fallback,
        /// <summary>Raw touches (gesture, or fallback disabled) until all fingers lift.</summary>
        Touches,
    }

    public InputFilterOptions Options => _options;

    /// <summary>
    /// When a lone finger is held back: the time at which it arms. If no input event arrives before then, call
    /// <see cref="Process"/> again with the unchanged state at (or after) this time. Null otherwise.
    /// </summary>
    public TimeSpan? PendingDeadline => _mode == FingerMode.Holding ? _holdStart + _options.FingerFallbackArmDelay : null;

    /// <summary>The held samples to send before the result's pen sample; valid until the next <see cref="Process"/>.</summary>
    public ReadOnlySpan<PenSample> ReplaySamples => _held.AsSpan(0, _replayCount);

    /// <summary>Filters one snapshot of input state.</summary>
    /// <param name="timestamp">Monotonic event time (e.g. Android event uptime).</param>
    /// <param name="pen">The stylus/eraser sample, or null when no stylus pointer is present.</param>
    /// <param name="touches">All current finger pointers (exclude a pointer that is lifting in this event).</param>
    /// <param name="touchesOut">Destination for touches to send; at most <see cref="MaxTouches"/> are written.</param>
    public InputFilterResult Process(TimeSpan timestamp, in PenSample? pen, ReadOnlySpan<RawTouch> touches, Span<TouchSample> touchesOut)
    {
        _replayCount = 0;
        ForgetLiftedPointers(touches);

        bool anyContact = false;
        bool palmNow = false;
        for (int i = 0; i < touches.Length; i++)
        {
            RawTouch t = touches[i];
            if (t.Contact)
            {
                anyContact = true;
                palmNow |= t.Size >= _options.PalmSizeThreshold && t.Size > 0f;
            }
        }

        if (_palm && !_palmTouching && timestamp - _palmLiftTime >= _options.PalmSuppression)
        {
            _palm = false;
        }

        if (palmNow)
        {
            _palm = true;
        }

        if (_palm)
        {
            if (anyContact)
            {
                _palmTouching = true;
                SuppressAll(touches);
                ClearFingerMode();
            }
            else if (_palmTouching)
            {
                _palmTouching = false;
                _palmLiftTime = timestamp;
            }
        }

        bool penActive = pen.HasValue && (pen.GetValueOrDefault().State & (PenState.InRange | PenState.Contact)) != 0;
        if (penActive)
        {
            _penSeen = true;
            _lastPenTime = timestamp;
            SuppressAll(touches);
            ClearFingerMode();
            return new InputFilterResult(true, pen.GetValueOrDefault(), 0);
        }

        if (_palm || (_penSeen && timestamp - _lastPenTime < _options.PenSuppression))
        {
            SuppressAll(touches);
            ClearFingerMode();
            return default;
        }

        int active = 0;
        RawTouch single = default;
        int limit = Math.Min(touchesOut.Length, MaxTouches);
        int written = 0;
        for (int i = 0; i < touches.Length; i++)
        {
            RawTouch t = touches[i];
            if (t.Canceled)
            {
                _suppressed[t.PointerId] = true;
            }

            if (_suppressed[t.PointerId] || !t.Contact)
            {
                continue;
            }

            active++;
            single = t;
            if (written < limit)
            {
                touchesOut[written++] = new TouchSample(t.PointerId, true, t.X, t.Y);
            }
        }

        if (active == 0)
        {
            bool tap = _mode == FingerMode.Holding && !anyContact && _heldCount > 0;
            if (tap)
            {
                return EndTap();
            }

            ClearFingerMode();
            return default;
        }

        if (active >= 2)
        {
            ClearFingerMode();
            _mode = FingerMode.Touches;
            return new InputFilterResult(false, default, written);
        }

        if (_mode == FingerMode.Touches || !_options.FingerFallbackEnabled)
        {
            _mode = FingerMode.Touches;
            return new InputFilterResult(false, default, written);
        }

        PenSample sample = FallbackSample(single);
        if (_mode == FingerMode.Fallback)
        {
            return new InputFilterResult(true, sample, 0);
        }

        if (_mode == FingerMode.Holding && single.PointerId != _heldId)
        {
            // The held finger was replaced by another lone finger: start over with the new one.
            ClearFingerMode();
        }

        if (_mode == FingerMode.Idle)
        {
            if (_options.FingerFallbackArmDelay <= TimeSpan.Zero)
            {
                _mode = FingerMode.Fallback;
                return new InputFilterResult(true, sample, 0);
            }

            _mode = FingerMode.Holding;
            _heldId = single.PointerId;
            _holdStart = timestamp;
            _heldCount = 0;
        }

        Hold(sample);
        if (timestamp - _holdStart < _options.FingerFallbackArmDelay)
        {
            return default;
        }

        // Armed: replay everything held before the newest sample, then continue the stroke live.
        _mode = FingerMode.Fallback;
        _replayCount = _heldCount - 1;
        PenSample current = _held[_heldCount - 1];
        _heldCount = 0;
        return new InputFilterResult(true, current, 0, _replayCount);
    }

    /// <summary>Clears all state (e.g. when a session restarts).</summary>
    public void Reset()
    {
        Array.Clear(_suppressed);
        _previousCount = 0;
        _penSeen = false;
        _lastPenTime = default;
        ClearFingerMode();
        _replayCount = 0;
        _palm = false;
        _palmTouching = false;
        _palmLiftTime = default;
    }

    private PenSample FallbackSample(in RawTouch t) => new(
        t.X,
        t.Y,
        _options.FingerFallbackPressure,
        0,
        0,
        0,
        PenTool.FingerFallback,
        PenState.InRange | PenState.Contact,
        PenButtons.None);

    private void Hold(in PenSample sample)
    {
        if (_heldCount < _held.Length)
        {
            _held[_heldCount++] = sample;
        }
        else
        {
            _held[^1] = sample;
        }
    }

    /// <summary>The held finger lifted before arming: replay the whole tap and end it out of range.</summary>
    private InputFilterResult EndTap()
    {
        _replayCount = _heldCount;
        PenSample last = _held[_heldCount - 1];
        _heldCount = 0;
        _mode = FingerMode.Idle;
        PenSample end = last with { Pressure = 0, State = PenState.None };
        return new InputFilterResult(true, end, 0, _replayCount);
    }

    private void ClearFingerMode()
    {
        _mode = FingerMode.Idle;
        _heldCount = 0;
    }

    /// <summary>Un-suppresses ids that were present in the previous call but are gone now (the finger lifted).</summary>
    private void ForgetLiftedPointers(ReadOnlySpan<RawTouch> touches)
    {
        for (int i = 0; i < touches.Length; i++)
        {
            _present[touches[i].PointerId] = true;
        }

        for (int i = 0; i < _previousCount; i++)
        {
            byte id = _previousIds[i];
            if (!_present[id])
            {
                _suppressed[id] = false;
            }
        }

        // Record this call's distinct ids and clear the presence marks again.
        _previousCount = 0;
        for (int i = 0; i < touches.Length; i++)
        {
            byte id = touches[i].PointerId;
            if (_present[id])
            {
                _present[id] = false;
                _previousIds[_previousCount++] = id;
            }
        }
    }

    private void SuppressAll(ReadOnlySpan<RawTouch> touches)
    {
        for (int i = 0; i < touches.Length; i++)
        {
            _suppressed[touches[i].PointerId] = true;
        }
    }
}
