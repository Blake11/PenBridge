namespace PenBridge.Core.Input;

/// <summary>A finger pointer as sampled by the phone head, before filtering.</summary>
/// <param name="PointerId">Platform pointer id (stable while the finger is down).</param>
/// <param name="Contact">True while the finger touches the surface.</param>
/// <param name="Canceled">True when the platform canceled the pointer (e.g. Android <c>FLAG_CANCELED</c>, palm rejection).</param>
/// <param name="X">Normalized 0..65535.</param>
/// <param name="Y">Normalized 0..65535.</param>
public readonly record struct RawTouch(byte PointerId, bool Contact, bool Canceled, ushort X, ushort Y);

/// <summary>Options for <see cref="InputFilter"/>.</summary>
public sealed record InputFilterOptions
{
    /// <summary>How long touches stay suppressed after the pen was last in range or in contact.</summary>
    public TimeSpan PenSuppression { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>When true and no pen is present, a single finger is reported as a <see cref="PenTool.FingerFallback"/> pen sample.</summary>
    public bool FingerFallbackEnabled { get; init; } = true;

    /// <summary>Synthetic pressure (0..65535) for finger-fallback samples.</summary>
    public ushort FingerFallbackPressure { get; init; } = 0x8000;
}

/// <summary>Result of <see cref="InputFilter.Process"/>.</summary>
/// <param name="HasPen">True when <see cref="Pen"/> should be sent.</param>
/// <param name="Pen">The pen (or finger-fallback) sample to send.</param>
/// <param name="TouchCount">Number of touches written to the destination span.</param>
public readonly record struct InputFilterResult(bool HasPen, PenSample Pen, int TouchCount);

/// <summary>
/// Phone-side, pen-first input filter. Feed it the full current pointer state on every platform input event; it
/// returns what to put into the next datagram.
/// </summary>
/// <remarks>
/// Rules: while the pen is in range or in contact, and for <see cref="InputFilterOptions.PenSuppression"/>
/// afterwards, touches are suppressed. A finger that landed during suppression, or was canceled by the platform,
/// stays suppressed until it lifts (a resting palm never leaks through). With no pen, a lone finger becomes a
/// <see cref="PenTool.FingerFallback"/> pen sample; once two or more fingers are down they are reported as raw
/// touches until all fingers lift, so the end of a gesture never turns into a stroke. Not thread-safe;
/// allocation-free per call.
/// </remarks>
public sealed class InputFilter
{
    /// <summary>Maximum touches per datagram.</summary>
    public const int MaxTouches = 10;

    private readonly InputFilterOptions _options;
    private readonly bool[] _suppressed = new bool[256];
    private readonly bool[] _present = new bool[256];
    private bool _penSeen;
    private TimeSpan _lastPenTime;
    private bool _multiTouch;

    public InputFilter(InputFilterOptions? options = null)
    {
        _options = options ?? new InputFilterOptions();
    }

    public InputFilterOptions Options => _options;

    /// <summary>Filters one snapshot of input state.</summary>
    /// <param name="timestamp">Monotonic event time (e.g. Android event uptime).</param>
    /// <param name="pen">The stylus/eraser sample, or null when no stylus pointer is present.</param>
    /// <param name="touches">All current finger pointers (exclude a pointer that is lifting in this event).</param>
    /// <param name="touchesOut">Destination for touches to send; at most <see cref="MaxTouches"/> are written.</param>
    public InputFilterResult Process(TimeSpan timestamp, in PenSample? pen, ReadOnlySpan<RawTouch> touches, Span<TouchSample> touchesOut)
    {
        ForgetLiftedPointers(touches);

        bool penActive = pen.HasValue && (pen.GetValueOrDefault().State & (PenState.InRange | PenState.Contact)) != 0;
        if (penActive)
        {
            _penSeen = true;
            _lastPenTime = timestamp;
            SuppressAll(touches);
            _multiTouch = false;
            return new InputFilterResult(true, pen.GetValueOrDefault(), 0);
        }

        if (_penSeen && timestamp - _lastPenTime < _options.PenSuppression)
        {
            SuppressAll(touches);
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
            _multiTouch = false;
            return default;
        }

        if (active >= 2)
        {
            _multiTouch = true;
        }

        if (active == 1 && !_multiTouch && _options.FingerFallbackEnabled)
        {
            var sample = new PenSample(
                single.X,
                single.Y,
                _options.FingerFallbackPressure,
                0,
                0,
                0,
                PenTool.FingerFallback,
                PenState.InRange | PenState.Contact,
                PenButtons.None);
            return new InputFilterResult(true, sample, 0);
        }

        return new InputFilterResult(false, default, written);
    }

    /// <summary>Clears all state (e.g. when a session restarts).</summary>
    public void Reset()
    {
        Array.Clear(_suppressed);
        _penSeen = false;
        _lastPenTime = default;
        _multiTouch = false;
    }

    private void ForgetLiftedPointers(ReadOnlySpan<RawTouch> touches)
    {
        for (int i = 0; i < touches.Length; i++)
        {
            _present[touches[i].PointerId] = true;
        }

        for (int id = 0; id < _suppressed.Length; id++)
        {
            if (!_present[id])
            {
                _suppressed[id] = false;
            }

            _present[id] = false;
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
