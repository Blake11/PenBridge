using PenBridge.Core.Input;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Input;

/// <summary>One Mac-side event, pen or gesture, in the order the sink received it.</summary>
internal readonly record struct PipelineEvent(bool IsPen, PenEvent Pen, GestureEvent Gesture)
{
    /// <summary>Short name: the pen event kind ("ProximityEnter", "Down", …) or "Kind.Phase" ("Scroll.Began", "Tap").</summary>
    public string Name => IsPen ? Pen.Kind.ToString()
        : Gesture.Kind is GestureKind.Tap or GestureKind.SmartZoom or GestureKind.TouchDown ? Gesture.Kind.ToString()
        : $"{Gesture.Kind}.{Gesture.Phase}";

    public override string ToString() => Name;
}

internal sealed class OrderedSink : IInputSink
{
    public List<PipelineEvent> Events { get; } = new(4096);

    public void OnPenEvent(in PenEvent e) => Events.Add(new PipelineEvent(true, e, default));

    public void OnGestureEvent(in GestureEvent e) => Events.Add(new PipelineEvent(false, default, e));
}

/// <summary>
/// The whole input path in one process, as the Android head and the Mac server run it: raw finger state per platform
/// event → phone <see cref="InputFilter"/> (with the head's arm timer: <see cref="InputFilter.PendingDeadline"/> re-run
/// at the ceiling millisecond) → frames as the head queues them (replays split into 8-sample history batches, nothing
/// sent while a lone finger is held) → the client's keepalives (40 ms while touching, 200 ms while hovering, two
/// release repeats) → <see cref="InputPayload"/> write/read round-trip → Mac <see cref="PenStateTracker"/> and its
/// <see cref="GestureRecognizer"/> with the tracker's watchdog/held-tap timer. Zero network latency; the Mac clock
/// follows the phone clock. Times are whole milliseconds since the session started.
/// </summary>
internal sealed class GesturePipeline
{
    /// <summary>A landscape S23 Ultra surface on a 1512×982 display: content 1512×705 pt, letterboxed.</summary>
    public static readonly CoordinateMapper Mapper = new(3088, 1440, new DisplayRect(0, 0, 1512, 982));

    public const float FingerSize = 0.05f;
    public const float PalmSize = 0.4f;

    private static readonly TimeSpan ContactKeepalive = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan HoverKeepalive = TimeSpan.FromMilliseconds(200);
    private const int ReleaseRepeats = 2;
    private const int EventIntervalMs = 8;

    private readonly ManualTimeProvider _macClock = new();
    private readonly List<RawTouch> _fingers = new(10);
    private readonly List<RawTouch> _history = new(40);
    private readonly TouchSample[] _out = new TouchSample[InputFilter.MaxTouches];
    private readonly PenSample[] _samples = new PenSample[256];
    private readonly byte[] _wire = new byte[InputPayload.MaxSize];
    private TimeSpan _macNow;
    private TimeSpan _lastEmit;
    private TimeSpan? _armAt;
    private PenSample? _pen;

    // Client keepalive state.
    private PenSample? _lastPen;
    private readonly TouchSample[] _lastTouches = new TouchSample[InputFilter.MaxTouches];
    private int _lastTouchCount;
    private bool _wasActive;
    private int _repeatsLeft;
    private TimeSpan? _keepaliveAt;

    public GesturePipeline(InputFilterOptions? filterOptions = null)
    {
        Filter = new InputFilter(filterOptions);
        Tracker = new PenStateTracker(Sink, _macClock) { Mapper = Mapper };
    }

    public InputFilter Filter { get; }

    public PenStateTracker Tracker { get; }

    public OrderedSink Sink { get; } = new();

    public List<PipelineEvent> Events => Sink.Events;

    /// <summary>Datagrams delivered to the Mac.</summary>
    public int Frames { get; private set; }

    /// <summary>Normalized coordinates of a point in content points (0..1512 × 0..705).</summary>
    public static (ushort X, ushort Y) At(double xPt, double yPt) => (
        (ushort)Math.Clamp(Math.Round(xPt / Mapper.ContentRect.Width * 65535), 0, 65535),
        (ushort)Math.Clamp(Math.Round(yPt / Mapper.ContentRect.Height * 65535), 0, 65535));

    public (double X, double Y) PositionOf(byte id)
    {
        RawTouch t = _fingers[IndexOf(id)];
        return (t.X / 65535.0 * Mapper.ContentRect.Width, t.Y / 65535.0 * Mapper.ContentRect.Height);
    }

    public string[] Names(int from = 0) => Events.Skip(from).Select(e => e.Name).ToArray();

    public PenEvent[] PenEvents(int from = 0) => Events.Skip(from).Where(e => e.IsPen).Select(e => e.Pen).ToArray();

    public GestureEvent[] GestureEvents(int from = 0) => Events.Skip(from).Where(e => !e.IsPen).Select(e => e.Gesture).ToArray();

    /// <summary>A finger (or palm) lands at a point given in content points.</summary>
    public void Down(int ms, byte id, double xPt, double yPt, float size = FingerSize)
    {
        RunTimersUntil(Ms(ms));
        (ushort x, ushort y) = At(xPt, yPt);
        _fingers.Add(new RawTouch(id, true, false, x, y, size));
        Emit(Ms(ms));
    }

    /// <summary>The given fingers lift, one platform event each (ACTION_POINTER_UP / ACTION_UP) at the same time.</summary>
    public void Up(int ms, params byte[] ids)
    {
        RunTimersUntil(Ms(ms));
        foreach (byte id in ids)
        {
            _fingers.RemoveAt(IndexOf(id));
            Emit(Ms(ms));
        }
    }

    /// <summary>The platform cancels every pointer (ACTION_CANCEL): reported once as canceled, then gone.</summary>
    public void CancelAll(int ms)
    {
        RunTimersUntil(Ms(ms));
        for (int i = 0; i < _fingers.Count; i++)
        {
            _fingers[i] = _fingers[i] with { Canceled = true };
        }

        Emit(Ms(ms));
        _fingers.Clear();
    }

    /// <summary>
    /// Moves the fingers from <paramref name="startMs"/> for <paramref name="durationMs"/>: an ACTION_MOVE every 8 ms,
    /// each with one batched (historical) position at the midpoint. <paramref name="path"/> gives a finger's position
    /// in content points from its id, its position at the start, and the progress 0..1.
    /// </summary>
    public void Animate(int startMs, int durationMs, Func<byte, (double X, double Y), double, (double X, double Y)> path)
    {
        var start = new Dictionary<byte, (double, double)>();
        foreach (RawTouch t in _fingers)
        {
            start[t.PointerId] = PositionOf(t.PointerId);
        }

        for (int ms = startMs + EventIntervalMs; ms <= startMs + durationMs; ms += EventIntervalMs)
        {
            RunTimersUntil(Ms(ms));
            double p = (double)(ms - startMs) / durationMs;
            double half = (ms - (EventIntervalMs / 2.0) - startMs) / durationMs;
            _history.Clear();
            for (int i = 0; i < _fingers.Count; i++)
            {
                RawTouch t = _fingers[i];
                (double hx, double hy) = path(t.PointerId, start[t.PointerId], half);
                (ushort ux, ushort uy) = At(hx, hy);
                _history.Add(t with { X = ux, Y = uy });
                (double x, double y) = path(t.PointerId, start[t.PointerId], p);
                (ushort nx, ushort ny) = At(x, y);
                _fingers[i] = t with { X = nx, Y = ny };
            }

            Emit(Ms(ms), withHistory: true);
        }
    }

    /// <summary>Moves every finger by the same offset (content points), linearly.</summary>
    public void Glide(int startMs, int durationMs, double dxPt, double dyPt) =>
        Animate(startMs, durationMs, (_, s, p) => (s.X + (dxPt * p), s.Y + (dyPt * p)));

    /// <summary>Moves each finger toward (negative <paramref name="byPt"/>) or away from the fingers' centroid.</summary>
    public void Spread(int startMs, int durationMs, double byPt)
    {
        double cx = 0, cy = 0;
        foreach (RawTouch t in _fingers)
        {
            (double x, double y) = PositionOf(t.PointerId);
            cx += x / _fingers.Count;
            cy += y / _fingers.Count;
        }

        Animate(startMs, durationMs, (_, s, p) =>
        {
            double dx = s.X - cx, dy = s.Y - cy;
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            double k = (len + (byPt * p)) / len;
            return (cx + (dx * k), cy + (dy * k));
        });
    }

    /// <summary>The pen sample of this event (null: no stylus pointer), sent with the current fingers.</summary>
    public void Pen(int ms, PenSample? pen)
    {
        RunTimersUntil(Ms(ms));
        _pen = pen;
        Emit(Ms(ms));
    }

    /// <summary>Lets time pass with no input event: the arm timer, keepalives and Mac timers fire as due.</summary>
    public void Wait(int untilMs) => RunTimersUntil(Ms(untilMs));

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private int IndexOf(byte id)
    {
        for (int i = 0; i < _fingers.Count; i++)
        {
            if (_fingers[i].PointerId == id)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Finger {id} is not down.");
    }

    /// <summary>DrawSurfaceView.Emit: filter the current state and queue the resulting frame(s).</summary>
    private void Emit(TimeSpan t, bool withHistory = false)
    {
        if (t < _lastEmit)
        {
            t = _lastEmit;
        }

        _lastEmit = t;
        RawTouch[] fingers = _fingers.ToArray();
        RawTouch[] history = withHistory ? _history.ToArray() : [];
        InputFilterResult result = Filter.Process(t, _pen, fingers, _out, history);
        if (result.HasPen)
        {
            PenSample current = result.Pen;
            ReadOnlySpan<PenSample> replay = current.Tool == PenTool.FingerFallback ? Filter.ReplaySamples : default;
            if (replay.Length > _samples.Length - 1)
            {
                replay = replay[^(_samples.Length - 1)..];
            }

            replay.CopyTo(_samples);
            int n = replay.Length;
            int i = 0;
            while (n - i > InputPayload.MaxHistory)
            {
                Send(t, _samples[i + InputPayload.MaxHistory], [], _samples.AsSpan(i, InputPayload.MaxHistory));
                i += InputPayload.MaxHistory + 1;
            }

            Send(t, current, [], _samples.AsSpan(i, n - i));
        }
        else if (Filter.PendingDeadline is null)
        {
            Send(t, null, _out.AsSpan(0, result.TouchCount), []);
        }

        _armAt = Filter.PendingDeadline is { } d ? Ms(Math.Ceiling(d.TotalMilliseconds)) : null;
    }

    /// <summary>PenBridgeClient.SendState: serialize, deliver to the Mac, re-arm the keepalive.</summary>
    private void Send(TimeSpan t, PenSample? pen, ReadOnlySpan<TouchSample> touches, ReadOnlySpan<PenSample> history)
    {
        _lastPen = pen;
        touches.CopyTo(_lastTouches);
        _lastTouchCount = touches.Length;
        bool contact = touches.Length > 0 || (pen is { } p && (p.State & PenState.Contact) != 0);
        bool active = contact || (pen is { } h && (h.State & PenState.InRange) != 0);
        if (!active && _wasActive)
        {
            _repeatsLeft = ReleaseRepeats;
        }

        _wasActive = active;
        Deliver(t, pen, touches, history);
        ArmKeepalive(t, contact, active);
    }

    private void ArmKeepalive(TimeSpan t, bool contact, bool active) =>
        _keepaliveAt = contact ? t + ContactKeepalive
            : active ? t + HoverKeepalive
            : _repeatsLeft > 0 ? t + ContactKeepalive
            : null;

    private void OnKeepalive(TimeSpan t)
    {
        _keepaliveAt = null;
        bool contact = _lastTouchCount > 0 || (_lastPen is { } p && (p.State & PenState.Contact) != 0);
        bool active = contact || (_lastPen is { } h && (h.State & PenState.InRange) != 0);
        if (!active)
        {
            if (_repeatsLeft <= 0)
            {
                return;
            }

            _repeatsLeft--;
        }

        Deliver(t, _lastPen, _lastTouches.AsSpan(0, _lastTouchCount), []);
        ArmKeepalive(t, contact, active);
    }

    private void Deliver(TimeSpan t, PenSample? pen, ReadOnlySpan<TouchSample> touches, ReadOnlySpan<PenSample> history)
    {
        AdvanceMac(t);
        uint timeUs = (uint)(t.Ticks / TimeSpan.TicksPerMicrosecond);
        int size = InputPayload.Write(_wire, timeUs, pen, touches, history);
        Assert.True(InputPayload.TryRead(_wire.AsSpan(0, size), out InputPayload payload));
        Tracker.ProcessFrame(payload.TimeUs, payload.Pen, payload.History, payload.Touches);
        Frames++;
    }

    private void AdvanceMac(TimeSpan t)
    {
        if (t > _macNow)
        {
            _macClock.Advance(t - _macNow);
            _macNow = t;
        }
    }

    /// <summary>Fires every timer due up to <paramref name="t"/> in time order (phone arm, keepalive, then Mac).</summary>
    private void RunTimersUntil(TimeSpan t)
    {
        for (int guard = 0; guard < 10_000; guard++)
        {
            TimeSpan? mac = Tracker.WatchdogDueIn is { } d ? _macNow + d : null;
            TimeSpan? next = Earliest(Earliest(_armAt, _keepaliveAt), mac);
            if (next is not { } at || at > t)
            {
                AdvanceMac(t);
                return;
            }

            if (_armAt == at)
            {
                _armAt = null;
                Emit(at);
            }
            else if (_keepaliveAt == at)
            {
                OnKeepalive(at);
            }
            else
            {
                AdvanceMac(at);
                Tracker.CheckWatchdog();
                if (Tracker.WatchdogDueIn == TimeSpan.Zero)
                {
                    throw new InvalidOperationException("The Mac timer stays due after CheckWatchdog.");
                }
            }
        }

        throw new InvalidOperationException("Timers did not settle.");
    }

    private static TimeSpan? Earliest(TimeSpan? a, TimeSpan? b) =>
        a is not { } x ? b : b is not { } y ? a : (x <= y ? x : y);
}
