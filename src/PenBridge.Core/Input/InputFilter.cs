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

/// <summary>What a lone finger does with finger fallback (<see cref="InputFilterOptions.FingerMode"/>).</summary>
public enum FingerFallbackMode : byte
{
    /// <summary>
    /// The finger moves the cursor without pressing (hover samples, absolute position). A quick tap clicks;
    /// double-tap-and-hold starts a left drag until lift (ADR-0022). The default.
    /// </summary>
    Cursor = 0,

    /// <summary>The finger draws like a pen with <see cref="InputFilterOptions.FingerFallbackPressure"/>.</summary>
    Draw = 1,
}

/// <summary>Options for <see cref="InputFilter"/>.</summary>
public sealed record InputFilterOptions
{
    /// <summary>How long touches stay suppressed after the pen was last in range or in contact.</summary>
    public TimeSpan PenSuppression { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>When true and no pen is present, a single finger is reported as a <see cref="PenTool.FingerFallback"/> pen sample.</summary>
    public bool FingerFallbackEnabled { get; init; } = true;

    /// <summary>Whether a lone finger moves the cursor (default) or draws.</summary>
    public FingerFallbackMode FingerMode { get; init; } = FingerFallbackMode.Cursor;

    /// <summary>
    /// Cursor mode: a touch that lifts sooner than this after landing (and within <see cref="TapSlop"/>) is a tap,
    /// i.e. a click. A longer touch is never a click.
    /// </summary>
    public TimeSpan TapMaxDuration { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Cursor mode: how far a tap may move from where it landed, as a fraction of the surface (normalized units:
    /// 0.02 is 2% of the width on x and of the height on y, combined as a distance).
    /// </summary>
    public float TapSlop { get; init; } = 0.02f;

    /// <summary>
    /// Cursor mode: a touch landing within this time after a tap lifted, and within <see cref="DoubleTapSlop"/> of it,
    /// is the second touch of a double tap: it clicks again (a double-click) when it is a tap too, else it drags.
    /// </summary>
    public TimeSpan DoubleTapWindow { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Cursor mode: how near a double tap's second touch must land to the first, as for <see cref="TapSlop"/>.</summary>
    public float DoubleTapSlop { get; init; } = 0.05f;

    /// <summary>Synthetic pressure (0..65535) for finger-fallback contact samples.</summary>
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
/// stays suppressed until it lifts (a resting palm never leaks through). When every pointer of a call was canceled
/// (the platform canceled the whole stream), they all count as lifted next call: ids seen again are new fingers.</para>
/// <para>Palm: a contact with <see cref="RawTouch.Size"/> at or above <see cref="InputFilterOptions.PalmSizeThreshold"/>
/// suppresses every touch until all fingers lift, then for <see cref="InputFilterOptions.PalmSuppression"/>; a finger
/// landing in that window is suppressed until it lifts and restarts the window.</para>
/// <para>Withdrawn touches (ADR-0024): a touch already sent as a contact that then gets suppressed (palm, platform
/// cancel, pen suppression after the pen left) stays in every touch result as present with <c>Contact=false</c> until
/// that pointer lifts, so the Mac cancels the gesture instead of reading a lift (a tap, or a scroll's momentum). Pen
/// samples carry no touches. A touch never sent (a palm itself, a finger landing while suppressed) is never reported.</para>
/// <para>Finger fallback: with no pen, a lone finger is held for <see cref="InputFilterOptions.FingerFallbackArmDelay"/>.
/// If a second finger lands first, it is a gesture: the held samples are dropped and the fingers are reported as raw
/// touches until all lift. Otherwise the finger arms and the held samples are replayed
/// (<see cref="InputFilterResult.ReplayCount"/>); a finger that lifts within the delay is replayed as a whole touch
/// ending in a sample with <see cref="PenState.None"/>, also when a new lone finger replaces it in the same snapshot
/// (the new finger's hold starts at once). Each event's batched finger history is held too, so nothing is lost; once
/// armed, the finger's history comes back through <see cref="ReplaySamples"/> as well. A finger that does not move
/// produces no event, so the head calls <see cref="Process"/> again at <see cref="PendingDeadline"/>.</para>
/// <para>Cursor mode (<see cref="FingerFallbackMode.Cursor"/>, the default, ADR-0022): the finger's samples are
/// hover samples (in range, no contact), so it moves the cursor and the client's hover keepalive keeps it alive. A tap
/// (lifts sooner than <see cref="InputFilterOptions.TapMaxDuration"/> after landing, moved at most
/// <see cref="InputFilterOptions.TapSlop"/>) is replayed as a click where it landed: a hover sample, a contact sample,
/// then <see cref="PenState.None"/>. Any other lift ends with <see cref="PenState.None"/>. A touch that lands within
/// <see cref="InputFilterOptions.DoubleTapWindow"/> after a tap and within <see cref="InputFilterOptions.DoubleTapSlop"/>
/// of it is held until decided: lifting as a tap clicks again at exactly the first tap's point (so the host counts a
/// double-click); moving beyond the tap slop or staying down for the tap duration (never before the arming delay)
/// starts a contact stroke from where it landed, a left drag until lift. That press never lands exactly on the tap's
/// point (nudged by one unit if it would), so the host starts the drag with click state 1
/// (<see cref="TabletEventMath.ClickSlopFor"/>).</para>
/// <para>Draw mode (<see cref="FingerFallbackMode.Draw"/>): the finger's samples are contact samples, a stroke. A held
/// tap landing within <see cref="InputFilterOptions.DoubleTapWindow"/> and <see cref="InputFilterOptions.DoubleTapSlop"/>
/// of the previous tap is replayed at exactly that tap's point, so the host counts a double-click.</para>
/// <para>Not thread-safe; allocation-free per call.</para>
/// </remarks>
public sealed class InputFilter
{
    /// <summary>Maximum touches per datagram.</summary>
    public const int MaxTouches = 10;

    /// <summary>
    /// Capacity of the finger-fallback hold buffer (held samples and history). Beyond it the newest sample replaces
    /// the last slot (the touch-down point and the latest position are always kept); 128 covers 100 ms at well over
    /// 1 kHz.
    /// </summary>
    public const int MaxHeldSamples = 128;

    private readonly InputFilterOptions _options;
    private readonly bool[] _suppressed = new bool[256];
    private readonly bool[] _reported = new bool[256]; // sent as a contact touch since it landed (ADR-0024)
    private readonly bool[] _present = new bool[256];
    private bool _allCanceled; // the previous call's pointers were all canceled (the platform canceled the stream)
    private readonly byte[] _previousIds = new byte[256];
    private readonly long _tapSlopSq;
    private readonly long _doubleTapSlopSq;
    private PenSample[] _held = new PenSample[MaxHeldSamples];
    private PenSample[] _replay = new PenSample[MaxHeldSamples];
    private int _previousCount;
    private bool _penSeen;
    private TimeSpan _lastPenTime;
    private FingerMode _mode;
    private FingerKind _kind;
    private int _heldCount;
    private byte _heldId;
    private TimeSpan _holdStart;
    private int _replayCount;
    private bool _palm;
    private bool _palmTouching;
    private TimeSpan _palmLiftTime;

    // The current finger: where it landed and whether it moved beyond the tap slop since.
    private bool _downSet;
    private ushort _downX;
    private ushort _downY;
    private bool _moved;
    private PenSample _lastPointer;

    // The last tap, for double-tap detection (cursor mode) and double-click snapping (draw mode).
    private bool _snapTap;
    private bool _tapValid;
    private TimeSpan _tapTime;
    private ushort _tapX;
    private ushort _tapY;

    public InputFilter(InputFilterOptions? options = null)
    {
        _options = options ?? new InputFilterOptions();
        _tapSlopSq = SlopSquared(_options.TapSlop);
        _doubleTapSlopSq = SlopSquared(_options.DoubleTapSlop);
    }

    private enum FingerMode : byte
    {
        /// <summary>No unsuppressed finger down.</summary>
        Idle,
        /// <summary>A lone finger is being held back (arming delay; a double tap's second touch until decided).</summary>
        Holding,
        /// <summary>A finger-fallback contact stroke is in progress (drawing, or a double-tap drag).</summary>
        Fallback,
        /// <summary>Cursor mode: the finger moves the cursor (hover samples).</summary>
        Pointer,
        /// <summary>Raw touches (gesture, or fallback disabled) until all fingers lift.</summary>
        Touches,
    }

    private enum FingerKind : byte
    {
        /// <summary>Draw mode: contact samples.</summary>
        Draw,
        /// <summary>Cursor mode: hover samples; a tap clicks.</summary>
        Pointer,
        /// <summary>Cursor mode, a double tap's second touch: a tap clicks again, else a contact stroke (drag).</summary>
        Press,
    }

    public InputFilterOptions Options => _options;

    /// <summary>
    /// When a lone finger is held back: the time at which it arms. If no input event arrives before then, call
    /// <see cref="Process"/> again with the unchanged state at (or after) this time. Null otherwise.
    /// </summary>
    public TimeSpan? PendingDeadline => _mode == FingerMode.Holding ? _holdStart + HoldDuration(_kind) : null;

    /// <summary>
    /// The finger-fallback samples to send before the result's pen sample, oldest first (held samples and this event's
    /// history); valid until the next <see cref="Process"/>.
    /// </summary>
    public ReadOnlySpan<PenSample> ReplaySamples => _replay.AsSpan(0, _replayCount);

    /// <summary>Filters one snapshot of input state.</summary>
    /// <param name="timestamp">Monotonic event time (e.g. Android event uptime).</param>
    /// <param name="pen">The stylus/eraser sample, or null when no stylus pointer is present.</param>
    /// <param name="touches">All current finger pointers (exclude a pointer that is lifting in this event).</param>
    /// <param name="touchesOut">Destination for touches to send; at most <see cref="MaxTouches"/> are written.</param>
    /// <param name="history">
    /// Earlier finger positions batched into this event (e.g. Android historical samples), each pointer's entries in
    /// time order. Only the lone finger's entries are used (held, or replayed while it is armed).
    /// </param>
    public InputFilterResult Process(
        TimeSpan timestamp,
        in PenSample? pen,
        ReadOnlySpan<RawTouch> touches,
        Span<TouchSample> touchesOut,
        ReadOnlySpan<RawTouch> history = default)
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
            return new InputFilterResult(false, default, WriteWithdrawn(touches, touchesOut));
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
                if (_reported[t.PointerId] && written < limit)
                {
                    // Already sent as a touch: stays on the wire as not in contact until it lifts (ADR-0024).
                    touchesOut[written++] = new TouchSample(t.PointerId, false, t.X, t.Y);
                }

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
            if (_mode is FingerMode.Holding or FingerMode.Fallback or FingerMode.Pointer)
            {
                if (anyContact)
                {
                    // The finger was canceled (or suppressed), not lifted: never a tap.
                    ClearFingerMode();
                    return new InputFilterResult(false, default, written);
                }

                return FinishFinger(timestamp);
            }

            _mode = FingerMode.Idle;
            return new InputFilterResult(false, default, written);
        }

        if (active >= 2)
        {
            ClearFingerMode();
            _mode = FingerMode.Touches;
            return Touches(touchesOut, written);
        }

        if (_mode == FingerMode.Touches || !_options.FingerFallbackEnabled)
        {
            _mode = FingerMode.Touches;
            return Touches(touchesOut, written);
        }

        if (_mode is FingerMode.Holding or FingerMode.Pointer && single.PointerId != _heldId)
        {
            // The finger lifted and another lone finger landed in the same snapshot: end the first one (a tap is
            // replayed), then start the new one.
            InputFilterResult ended = FinishFinger(timestamp);
            StartFinger(timestamp, single);
            if (_mode == FingerMode.Holding)
            {
                AppendHistory(_held, ref _heldCount, history, single.PointerId);
                Append(_held, ref _heldCount, FingerSample(single));
            }

            return ended;
        }

        if (_mode == FingerMode.Idle)
        {
            StartFinger(timestamp, single);
        }

        if (_mode is FingerMode.Fallback or FingerMode.Pointer)
        {
            AppendHistory(_replay, ref _replayCount, history, single.PointerId);
            PenSample live = FingerSample(single);
            _lastPointer = live;
            return new InputFilterResult(true, live, 0, _replayCount);
        }

        AppendHistory(_held, ref _heldCount, history, single.PointerId);
        Append(_held, ref _heldCount, FingerSample(single));
        if (!ShouldArm(timestamp))
        {
            return default;
        }

        // Armed: replay everything held before the newest sample, then continue live.
        _mode = _kind == FingerKind.Pointer ? FingerMode.Pointer : FingerMode.Fallback;
        int n = TakeHeld();
        _replayCount = n - 1;
        _lastPointer = _replay[n - 1];
        return new InputFilterResult(true, _replay[n - 1], 0, _replayCount);
    }

    /// <summary>Clears all state (e.g. when a session restarts).</summary>
    public void Reset()
    {
        Array.Clear(_suppressed);
        Array.Clear(_reported);
        _allCanceled = false;
        _previousCount = 0;
        _penSeen = false;
        _lastPenTime = default;
        ClearFingerMode();
        _replayCount = 0;
        _palm = false;
        _palmTouching = false;
        _palmLiftTime = default;
    }

    private static long SlopSquared(float fraction)
    {
        double units = (fraction > 0f ? Math.Min(fraction, 2f) : 0f) * (double)ushort.MaxValue;
        return (long)(units * units);
    }

    private static long DistanceSquared(ushort x0, ushort y0, ushort x1, ushort y1)
    {
        long dx = x1 - x0;
        long dy = y1 - y0;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>How long a finger of <paramref name="kind"/> is held before it can arm.</summary>
    private TimeSpan HoldDuration(FingerKind kind)
    {
        TimeSpan arm = _options.FingerFallbackArmDelay;
        return kind == FingerKind.Press && _options.TapMaxDuration > arm ? _options.TapMaxDuration : arm;
    }

    private bool ShouldArm(TimeSpan timestamp)
    {
        TimeSpan elapsed = timestamp - _holdStart;
        if (elapsed < _options.FingerFallbackArmDelay)
        {
            return false;
        }

        // A double tap's second touch stays held while it may still be a tap; moving or staying down makes it a drag.
        return _kind != FingerKind.Press || _moved || elapsed >= _options.TapMaxDuration;
    }

    private bool IsTap(TimeSpan timestamp) => _downSet && !_moved && timestamp - _holdStart < _options.TapMaxDuration;

    /// <summary>A new lone finger: picks its kind (second touch of a double tap or not) and holds it, or arms at once.</summary>
    private void StartFinger(TimeSpan timestamp, in RawTouch t)
    {
        bool near = _tapValid
            && timestamp - _tapTime <= _options.DoubleTapWindow
            && DistanceSquared(_tapX, _tapY, t.X, t.Y) <= _doubleTapSlopSq;
        bool draw = _options.FingerMode == FingerFallbackMode.Draw;
        _kind = draw ? FingerKind.Draw
            : near ? FingerKind.Press
            : FingerKind.Pointer;
        _snapTap = draw && near;
        _tapValid = near; // a tap counts only for the very next touch

        _heldId = t.PointerId;
        _holdStart = timestamp;
        _heldCount = 0;
        _downSet = false;
        _moved = false;
        _mode = HoldDuration(_kind) > TimeSpan.Zero ? FingerMode.Holding
            : _kind == FingerKind.Pointer ? FingerMode.Pointer
            : FingerMode.Fallback;
    }

    /// <summary>The current finger lifted (not canceled): replays a click or the held touch, or ends the pointer.</summary>
    private InputFilterResult FinishFinger(TimeSpan timestamp)
    {
        bool tap = IsTap(timestamp);
        FingerMode mode = _mode;
        FingerKind kind = _kind;
        _mode = FingerMode.Idle;
        _tapValid = false;

        if (tap && kind == FingerKind.Pointer)
        {
            _heldCount = 0;
            return Click(timestamp, _downX, _downY);
        }

        if (tap && kind == FingerKind.Press && mode == FingerMode.Holding)
        {
            // Second tap of a double tap: click again where the first tap clicked, so the host counts a double-click.
            _heldCount = 0;
            return Click(timestamp, _tapX, _tapY);
        }

        if (tap && kind == FingerKind.Draw && mode == FingerMode.Holding && _heldCount > 0)
        {
            // Draw mode: a tap near the previous one is replayed at exactly its point, so the host (which repeats a
            // finger's click count only at the same point) counts a double-click.
            ushort x = _snapTap ? _tapX : _downX;
            ushort y = _snapTap ? _tapY : _downY;
            InputFilterResult held = EndHeld();
            if (_snapTap)
            {
                for (int i = 0; i < _replayCount; i++)
                {
                    _replay[i] = _replay[i] with { X = x, Y = y };
                }

                held = held with { Pen = held.Pen with { X = x, Y = y } };
            }

            _tapValid = true;
            _tapTime = timestamp;
            _tapX = x;
            _tapY = y;
            return held;
        }

        if (mode == FingerMode.Holding && _heldCount > 0)
        {
            return EndHeld();
        }

        _heldCount = 0;
        return mode == FingerMode.Pointer
            ? new InputFilterResult(true, _lastPointer with { Pressure = 0, State = PenState.None }, 0)
            : default;
    }

    /// <summary>A click at (x, y): hover there, touch, then out of range. Records it as the last tap.</summary>
    private InputFilterResult Click(TimeSpan timestamp, ushort x, ushort y)
    {
        PenSample hover = PointerSample(x, y);
        _replay[0] = hover;
        _replay[1] = ContactSample(x, y);
        _replayCount = 2;
        _tapValid = true;
        _tapTime = timestamp;
        _tapX = x;
        _tapY = y;
        return new InputFilterResult(true, hover with { State = PenState.None }, 0, _replayCount);
    }

    /// <summary>The sample for the current finger (hover for a pointer, contact otherwise); tracks its movement.</summary>
    private PenSample FingerSample(in RawTouch t)
    {
        if (!_downSet)
        {
            _downSet = true;
            _downX = t.X;
            _downY = t.Y;
            if (_kind == FingerKind.Press && t.X == _tapX && t.Y == _tapY)
            {
                // The drag's press must not land exactly on the tap's click point: the host repeats the click count
                // only there (TabletEventMath.ClickSlopFor), and a drag starts with click state 1.
                return ContactSample(t.X == ushort.MaxValue ? (ushort)(t.X - 1) : (ushort)(t.X + 1), t.Y);
            }
        }
        else if (!_moved && DistanceSquared(_downX, _downY, t.X, t.Y) > _tapSlopSq)
        {
            _moved = true;
        }

        return _kind == FingerKind.Pointer ? PointerSample(t.X, t.Y) : ContactSample(t.X, t.Y);
    }

    private PenSample ContactSample(ushort x, ushort y) => new(
        x,
        y,
        _options.FingerFallbackPressure,
        0,
        0,
        0,
        PenTool.FingerFallback,
        PenState.InRange | PenState.Contact,
        PenButtons.None);

    private static PenSample PointerSample(ushort x, ushort y) =>
        new(x, y, 0, 0, 0, 0, PenTool.FingerFallback, PenState.InRange, PenButtons.None);

    private static void Append(PenSample[] buffer, ref int count, in PenSample sample)
    {
        if (count < buffer.Length)
        {
            buffer[count++] = sample;
        }
        else
        {
            buffer[^1] = sample;
        }
    }

    private void AppendHistory(PenSample[] buffer, ref int count, ReadOnlySpan<RawTouch> history, byte pointerId)
    {
        for (int i = 0; i < history.Length; i++)
        {
            RawTouch h = history[i];
            if (h.PointerId == pointerId && h.Contact && !h.Canceled)
            {
                Append(buffer, ref count, FingerSample(h));
            }
        }
    }

    /// <summary>Moves the held samples into the replay buffer (swap, no copy) and returns their count.</summary>
    private int TakeHeld()
    {
        (_held, _replay) = (_replay, _held);
        int n = _heldCount;
        _heldCount = 0;
        return n;
    }

    /// <summary>The held finger lifted before arming (and was no click): replay the whole touch, end out of range.</summary>
    private InputFilterResult EndHeld()
    {
        _replayCount = TakeHeld();
        PenSample last = _replay[_replayCount - 1];
        _mode = FingerMode.Idle;
        PenSample end = last with { Pressure = 0, State = PenState.None };
        return new InputFilterResult(true, end, 0, _replayCount);
    }

    private void ClearFingerMode()
    {
        _mode = FingerMode.Idle;
        _heldCount = 0;
        _tapValid = false;
    }

    /// <summary>Un-suppresses ids that were present in the previous call but are gone now (the finger lifted).</summary>
    private void ForgetLiftedPointers(ReadOnlySpan<RawTouch> touches)
    {
        bool allCanceled = touches.Length > 0;
        for (int i = 0; i < touches.Length; i++)
        {
            _present[touches[i].PointerId] = true;
            allCanceled &= touches[i].Canceled;
        }

        for (int i = 0; i < _previousCount; i++)
        {
            // Lifted, or the whole stream was canceled last time (Android ACTION_CANCEL: every pointer is gone after
            // it and ids restart, so an id seen again is a new finger; one still canceled is suppressed again).
            byte id = _previousIds[i];
            if (!_present[id] || _allCanceled)
            {
                _suppressed[id] = false;
                _reported[id] = false;
            }
        }

        _allCanceled = allCanceled;

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

    /// <summary>Raw touches to send: marks the contacts as reported, so a later suppression keeps them on the wire.</summary>
    private InputFilterResult Touches(ReadOnlySpan<TouchSample> touchesOut, int written)
    {
        for (int i = 0; i < written; i++)
        {
            if (touchesOut[i].Contact)
            {
                _reported[touchesOut[i].PointerId] = true;
            }
        }

        return new InputFilterResult(false, default, written);
    }

    /// <summary>Writes every reported (already-sent) touch as not in contact; returns the count (ADR-0024).</summary>
    private int WriteWithdrawn(ReadOnlySpan<RawTouch> touches, Span<TouchSample> touchesOut)
    {
        int limit = Math.Min(touchesOut.Length, MaxTouches);
        int written = 0;
        for (int i = 0; i < touches.Length && written < limit; i++)
        {
            RawTouch t = touches[i];
            if (_reported[t.PointerId])
            {
                touchesOut[written++] = new TouchSample(t.PointerId, false, t.X, t.Y);
            }
        }

        return written;
    }

    private void SuppressAll(ReadOnlySpan<RawTouch> touches)
    {
        for (int i = 0; i < touches.Length; i++)
        {
            _suppressed[touches[i].PointerId] = true;
        }
    }
}
