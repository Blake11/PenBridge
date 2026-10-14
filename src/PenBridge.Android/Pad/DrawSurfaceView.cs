using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using PenBridge.Core.Input;

namespace PenBridge.Android.Pad;

/// <summary>
/// Full-size input surface: samples stylus (touch, hover, generic motion incl. every historical sample) and fingers,
/// runs Core's pen-first <see cref="InputFilter"/>, and queues complete state frames for the sender thread.
/// </summary>
/// <remarks>
/// Allocation-free per event on the managed side (preallocated buffers, no LINQ/closures/boxing/logging); the
/// binding's MotionEvent peer per callback is the only allocation and is outside our control. UI thread only.
/// Hover-exit and stylus-up don't leave range at once: Android sends HOVER_EXIT right before a touch-down, and
/// HOVER_ENTER right after an up, so range ends only if no stylus event follows within <see cref="PenLeaveDelayMs"/>.
/// </remarks>
internal sealed class DrawSurfaceView : View
{
    private const int PenLeaveDelayMs = 120;
    private const int MaxRawTouches = 32;
    private const int MaxSamplesPerEvent = 256;
    private const int MaxFingerHistory = 256;

    // MotionEvent.FLAG_CANCELED (API 33) and MotionEvent.TOOL_TYPE_PALM (API 33); plain constants so API 29+ can read them.
    private const int FlagCanceled = 0x20;
    private const int ToolTypePalm = 5;

    private readonly PadSendQueue _queue;
    private readonly InkEchoView? _ink;
    private readonly RawTouch[] _fingers = new RawTouch[MaxRawTouches];
    private readonly int[] _fingerIndex = new int[MaxRawTouches]; // pointer index in the current touch event
    private readonly RawTouch[] _fingerHistory = new RawTouch[MaxFingerHistory];
    private readonly TouchSample[] _touchOut = new TouchSample[InputFilter.MaxTouches];
    private readonly PenSample[] _samples = new PenSample[MaxSamplesPerEvent];
    private readonly Java.Lang.Runnable _penLeaveRunnable;
    private readonly Java.Lang.Runnable _armRunnable;

    /// <summary>
    /// Owned main-looper handler for the timers. <c>View.RemoveCallbacks</c> doesn't reach runnables already queued on
    /// the looper once the view is detached, so a disposed runnable could still run (#78); a Handler always can.
    /// </summary>
    private readonly Handler _handler = new(Looper.MainLooper!);
    private bool _disposed;
    private InputFilter _filter;
    private InputFilterOptions? _pendingFilterOptions;
    private int _fingerCount;
    private PenSample _pen;
    private bool _penPresent;
    private bool _penLeavePending;
    private long _lastPenMs = long.MinValue / 2;
    private bool _active;
    private bool _inkContact;
    private long _armAtMs = -1;
    private long _lastEmitMs = long.MinValue;
    private int _rangeDeviceId = int.MinValue;
    private float _touchMajorMax;

    public DrawSurfaceView(Context context, PadSendQueue queue, InkEchoView? ink, InputFilterOptions filterOptions)
        : base(context)
    {
        _queue = queue;
        _ink = ink;
        _filter = new InputFilter(filterOptions);
        _penLeaveRunnable = new Java.Lang.Runnable(OnPenLeaveTimeout);
        _armRunnable = new Java.Lang.Runnable(OnArmTimeout);
        Focusable = true;
        FocusableInTouchMode = true;
        SetBackgroundColor(Color.Rgb(16, 16, 18));
    }

    /// <summary>Raised when input switches between active (pen contact or touches sent) and idle.</summary>
    public Action<bool>? ActiveChanged { get; set; }

    /// <summary>Raised from <see cref="OnSizeChanged"/> with the new size in pixels.</summary>
    public Action<int, int>? SurfaceSizeChanged { get; set; }

    /// <summary>Raised when the window regains focus (re-apply immersive mode).</summary>
    public Action? WindowFocusRegained { get; set; }

    /// <summary>True while the last frame had pen contact or touches.</summary>
    public bool IsActive => _active;

    /// <summary>True while a stylus is in range, or left it less than the palm-suppression window ago.</summary>
    public bool IsPenNear =>
        _penPresent || SystemClock.UptimeMillis() - _lastPenMs < (long)_filter.Options.PenSuppression.TotalMilliseconds;

    /// <summary>Replaces the filter options (e.g. finger pressure); applied now if idle, else when input goes idle.</summary>
    public void SetFilterOptions(InputFilterOptions options)
    {
        _pendingFilterOptions = options;
        TryApplyPendingFilter();
    }

    /// <summary>Forgets all pointers and sends an empty state (pad hidden or paused).</summary>
    public void ReleaseAll()
    {
        _handler.RemoveCallbacks(_penLeaveRunnable);
        _penLeavePending = false;
        _penPresent = false;
        _fingerCount = 0;
        _filter.Reset();
        CancelArm();
        _queue.Enqueue(default, false, default, default);
        SetActive(false);
        _inkContact = false;
        _ink?.Clear();
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null)
        {
            return false;
        }

        MotionEventActions action = e.ActionMasked;
        if (action == MotionEventActions.Down)
        {
            RequestUnbuffered(e);
        }

        bool cancelAll = action == MotionEventActions.Cancel;
        bool lifting = action is MotionEventActions.Up or MotionEventActions.PointerUp;
        int actionIndex = lifting ? e.ActionIndex : -1;
        bool canceledFlag = ((int)e.Flags & FlagCanceled) != 0;
        int stylus = -1;
        int liftedCanceledId = -1;

        _fingerCount = 0;
        UpdateTouchMajorRange(e);
        int count = e.PointerCount;
        for (int i = 0; i < count; i++)
        {
            MotionEventToolType tool = e.GetToolType(i);
            if (tool is MotionEventToolType.Stylus or MotionEventToolType.Eraser)
            {
                if (stylus < 0)
                {
                    stylus = i;
                }

                continue;
            }

            bool isLifting = i == actionIndex;
            bool canceled = cancelAll || (isLifting && canceledFlag) || (int)tool == ToolTypePalm;
            if (isLifting && !canceled)
            {
                continue; // a lifting finger is no longer part of the state
            }

            if (_fingerCount == _fingers.Length)
            {
                continue;
            }

            int id = e.GetPointerId(i);
            if (isLifting)
            {
                liftedCanceledId = id;
            }

            _fingerIndex[_fingerCount] = i;
            _fingers[_fingerCount++] = new RawTouch(
                (byte)id, true, canceled, Normalize(e.GetX(i), Width), Normalize(e.GetY(i), Height), ContactSize(e, i));
        }

        int history = 0;
        if (stylus >= 0)
        {
            bool penUp = cancelAll || stylus == actionIndex;
            history = CollectPen(e, stylus, contact: !penUp);
            if (penUp)
            {
                SchedulePenLeave();
            }
            else
            {
                CancelPenLeave();
            }
        }

        Emit(e.EventTime, history, stylus >= 0 ? 0 : CollectFingerHistory(e));

        // Canceled pointers were reported once (so the filter suppresses them); they are gone afterwards.
        if (cancelAll || action == MotionEventActions.Up)
        {
            _fingerCount = 0;
        }
        else if (liftedCanceledId >= 0)
        {
            RemoveFinger((byte)liftedCanceledId);
        }

        return true;
    }

    public override bool OnHoverEvent(MotionEvent? e)
    {
        if (e is null)
        {
            return false;
        }

        int stylus = FindStylus(e);
        if (stylus < 0)
        {
            return base.OnHoverEvent(e);
        }

        MotionEventActions action = e.ActionMasked;
        if (action == MotionEventActions.HoverEnter)
        {
            RequestUnbuffered(e);
        }

        int history = CollectPen(e, stylus, contact: false);
        if (action == MotionEventActions.HoverExit)
        {
            SchedulePenLeave();
        }
        else
        {
            CancelPenLeave();
        }

        Emit(e.EventTime, history, 0);
        return true;
    }

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e is null)
        {
            return false;
        }

        MotionEventActions action = e.ActionMasked;
        if ((action is MotionEventActions.ButtonPress or MotionEventActions.ButtonRelease) && _penPresent && FindStylus(e) >= 0)
        {
            _pen = _pen with { Buttons = MapButtons(e.ButtonState) };
            _lastPenMs = e.EventTime;
            Emit(e.EventTime, 0, 0);
            return true;
        }

        return base.OnGenericMotionEvent(e);
    }

    public override void OnWindowFocusChanged(bool hasWindowFocus)
    {
        base.OnWindowFocusChanged(hasWindowFocus);
        if (hasWindowFocus)
        {
            WindowFocusRegained?.Invoke();
        }
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        if (w > 0 && h > 0)
        {
            SurfaceSizeChanged?.Invoke(w, h);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true; // a callback already dequeued becomes a no-op
            _handler.RemoveCallbacks(_penLeaveRunnable);
            _handler.RemoveCallbacks(_armRunnable);
            _penLeavePending = false;
            _armAtMs = -1;
            _penLeaveRunnable.Dispose();
            _armRunnable.Dispose();
            _handler.Dispose();
        }

        base.Dispose(disposing);
    }

    private static int FindStylus(MotionEvent e)
    {
        int count = e.PointerCount;
        for (int i = 0; i < count; i++)
        {
            if (e.GetToolType(i) is MotionEventToolType.Stylus or MotionEventToolType.Eraser)
            {
                return i;
            }
        }

        return -1;
    }

    private static PenButtons MapButtons(MotionEventButtonState state)
    {
        PenButtons buttons = PenButtons.None;
        if ((state & MotionEventButtonState.StylusPrimary) != 0)
        {
            buttons |= PenButtons.Primary;
        }

        if ((state & MotionEventButtonState.StylusSecondary) != 0)
        {
            buttons |= PenButtons.Secondary;
        }

        return buttons;
    }

    private static ushort Normalize(float px, int size)
    {
        if (size <= 0 || !(px > 0))
        {
            return 0;
        }

        float n = px * 65535f / size;
        return n >= 65535f ? ushort.MaxValue : (ushort)(n + 0.5f);
    }

    /// <summary>Contact size 0..1: touch major over the device's touch-major range, else <c>getSize</c>.</summary>
    private float ContactSize(MotionEvent e, int index)
    {
        if (_touchMajorMax > 0f)
        {
            float major = e.GetTouchMajor(index);
            if (major > 0f)
            {
                return Clamp01(major / _touchMajorMax);
            }
        }

        return Clamp01(e.GetSize(index));
    }

    private static float Clamp01(float v) => v > 0f ? (v < 1f ? v : 1f) : 0f;

    /// <summary>Caches the touch-major axis range; looked up again only when events come from another device.</summary>
    private void UpdateTouchMajorRange(MotionEvent e)
    {
        int deviceId = e.DeviceId;
        if (deviceId == _rangeDeviceId)
        {
            return;
        }

        _rangeDeviceId = deviceId;
        _touchMajorMax = 0f;
        using InputDevice? device = e.Device;
        using InputDevice.MotionRange? range = device?.GetMotionRange(Axis.TouchMajor, e.Source);
        if (range is not null && range.Max > 0f)
        {
            _touchMajorMax = range.Max;
        }
    }

    private static ushort NormalizePressure(float pressure)
    {
        if (!(pressure > 0))
        {
            return 0;
        }

        return pressure >= 1f ? ushort.MaxValue : (ushort)((pressure * 65535f) + 0.5f);
    }

    /// <summary>Called on every stylus hover-enter and every first-pointer down.</summary>
    private void RequestUnbuffered(MotionEvent e)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            RequestUnbufferedDispatch((int)InputSourceType.ClassPointer);
        }
        else
        {
            RequestUnbufferedDispatch(e);
        }
    }

    /// <summary>Fills <see cref="_samples"/> with the stylus' historical samples (oldest first) and the current one;
    /// updates the pen state. Returns the number of historical samples.</summary>
    private int CollectPen(MotionEvent e, int index, bool contact)
    {
        PenTool tool = e.GetToolType(index) == MotionEventToolType.Eraser ? PenTool.Eraser : PenTool.Pen;
        PenState state = contact ? PenState.InRange | PenState.Contact : PenState.InRange;
        PenButtons buttons = MapButtons(e.ButtonState);
        int width = Width;
        int height = Height;

        int historySize = e.HistorySize;
        int first = Math.Max(0, historySize - (_samples.Length - 1));
        int n = 0;
        for (int h = first; h < historySize; h++)
        {
            _samples[n++] = MakePen(
                e.GetHistoricalX(index, h),
                e.GetHistoricalY(index, h),
                contact ? e.GetHistoricalPressure(index, h) : 0f,
                e.GetHistoricalAxisValue(Axis.Tilt, index, h),
                e.GetHistoricalAxisValue(Axis.Orientation, index, h),
                width,
                height,
                tool,
                state,
                buttons);
        }

        _pen = MakePen(
            e.GetX(index),
            e.GetY(index),
            contact ? e.GetPressure(index) : 0f,
            e.GetAxisValue(Axis.Tilt, index),
            e.GetAxisValue(Axis.Orientation, index),
            width,
            height,
            tool,
            state,
            buttons);
        _samples[n] = _pen;
        _penPresent = true;
        _lastPenMs = e.EventTime;
        return n;
    }

    private static PenSample MakePen(
        float x, float y, float pressure, float tilt, float orientation, int width, int height, PenTool tool, PenState state, PenButtons buttons)
    {
        TiltConverter.ToTilt(tilt, orientation, out short tiltX, out short tiltY);
        return new PenSample(
            Normalize(x, width),
            Normalize(y, height),
            NormalizePressure(pressure),
            tiltX,
            tiltY,
            0, // AXIS_ORIENTATION is the tilt azimuth (in tiltX/tiltY), not barrel rotation
            tool,
            state,
            buttons);
    }

    /// <summary>Runs the filter over the current state and queues the frame(s). <paramref name="historyCount"/>
    /// samples in <see cref="_samples"/> precede the current pen sample; <paramref name="fingerHistoryCount"/> entries
    /// of <see cref="_fingerHistory"/> are this event's batched finger positions. Times are clamped to be monotonic
    /// (hardware event times and the arm timer's deadline can interleave out of order).</summary>
    private void Emit(long eventTimeMs, int historyCount, int fingerHistoryCount)
    {
        if (eventTimeMs < _lastEmitMs)
        {
            eventTimeMs = _lastEmitMs;
        }

        _lastEmitMs = eventTimeMs;
        PenSample? pen = _penPresent ? _pen : null;
        InputFilterResult result = _filter.Process(
            TimeSpan.FromMilliseconds(eventTimeMs),
            pen,
            _fingers.AsSpan(0, _fingerCount),
            _touchOut,
            _fingerHistory.AsSpan(0, fingerHistoryCount));

        bool active;
        if (result.HasPen)
        {
            PenSample current = result.Pen;
            bool contact = (current.State & PenState.Contact) != 0;
            active = contact;
            if (current.Tool == PenTool.FingerFallback)
            {
                // Held samples (after the arming delay) and this event's finger history, oldest first.
                ReadOnlySpan<PenSample> replay = _filter.ReplaySamples;
                if (replay.Length > _samples.Length - 1)
                {
                    replay = replay[^(_samples.Length - 1)..];
                }

                replay.CopyTo(_samples);
                historyCount = replay.Length;
            }

            if (historyCount == 0)
            {
                _queue.Enqueue(current, true, default, default);
            }
            else
            {
                // Split long batches: each frame carries up to 8 history samples before its own pen sample.
                int i = 0;
                while (historyCount - i > PadSendQueue.MaxHistory)
                {
                    _queue.Enqueue(_samples[i + PadSendQueue.MaxHistory], true, default, _samples.AsSpan(i, PadSendQueue.MaxHistory));
                    i += PadSendQueue.MaxHistory + 1;
                }

                _queue.Enqueue(current, true, default, _samples.AsSpan(i, historyCount - i));
            }

            EchoInk(current, contact, historyCount, eventTimeMs);
        }
        else
        {
            active = result.TouchCount > 0;
            if (_filter.PendingDeadline is null)
            {
                // While a lone finger is held back nothing is sent (the previous frame already had no input).
                _queue.Enqueue(default, false, _touchOut.AsSpan(0, result.TouchCount), default);
            }

            _inkContact = false;
        }

        SetActive(active);
        ScheduleArm();
    }

    /// <summary>Writes every reported finger's batched positions into <see cref="_fingerHistory"/> (per pointer in
    /// time order; the newest are kept when they don't fit). Returns their count.</summary>
    private int CollectFingerHistory(MotionEvent e)
    {
        int historySize = e.HistorySize;
        if (historySize == 0 || _fingerCount == 0)
        {
            return 0;
        }

        int width = Width;
        int height = Height;
        int first = Math.Max(0, historySize - (_fingerHistory.Length / _fingerCount));
        int n = 0;
        for (int h = first; h < historySize; h++)
        {
            for (int f = 0; f < _fingerCount; f++)
            {
                RawTouch t = _fingers[f];
                int index = _fingerIndex[f];
                _fingerHistory[n++] = t with
                {
                    X = Normalize(e.GetHistoricalX(index, h), width),
                    Y = Normalize(e.GetHistoricalY(index, h), height),
                };
            }
        }

        return n;
    }

    private void EchoInk(in PenSample current, bool contact, int historyCount, long timeMs)
    {
        if (_ink is null || !contact)
        {
            _inkContact = false;
            return;
        }

        for (int i = 0; i < historyCount; i++)
        {
            ref readonly PenSample s = ref _samples[i];
            if ((s.State & PenState.Contact) != 0)
            {
                _ink.Add(s.X, s.Y, s.Pressure, timeMs, !_inkContact);
                _inkContact = true;
            }
        }

        _ink.Add(current.X, current.Y, current.Pressure, timeMs, !_inkContact);
        _inkContact = true;
    }

    private void SetActive(bool active)
    {
        if (active == _active)
        {
            return;
        }

        _active = active;
        if (!active)
        {
            TryApplyPendingFilter();
        }

        ActiveChanged?.Invoke(active);
    }

    private void TryApplyPendingFilter()
    {
        if (_pendingFilterOptions is { } options && !_active && !_penPresent)
        {
            _pendingFilterOptions = null;
            _filter = new InputFilter(options);
        }
    }

    private void SchedulePenLeave()
    {
        _handler.RemoveCallbacks(_penLeaveRunnable);
        _penLeavePending = true;
        _handler.PostDelayed(_penLeaveRunnable, PenLeaveDelayMs);
    }

    private void CancelPenLeave()
    {
        if (_penLeavePending)
        {
            _penLeavePending = false;
            _handler.RemoveCallbacks(_penLeaveRunnable);
        }
    }

    private void OnPenLeaveTimeout()
    {
        if (_disposed || !_penLeavePending)
        {
            return;
        }

        _penLeavePending = false;
        _penPresent = false;
        _lastPenMs = SystemClock.UptimeMillis();
        Emit(_lastPenMs, 0, 0);
        TryApplyPendingFilter();
    }

    /// <summary>A lone finger that doesn't move sends no events: re-run the filter when its arming delay ends.</summary>
    private void ScheduleArm()
    {
        if (_filter.PendingDeadline is not { } deadline)
        {
            CancelArm();
            return;
        }

        long atMs = (long)Math.Ceiling(deadline.TotalMilliseconds);
        if (atMs == _armAtMs)
        {
            return;
        }

        _handler.RemoveCallbacks(_armRunnable);
        _armAtMs = atMs;
        _handler.PostDelayed(_armRunnable, Math.Max(0, atMs - SystemClock.UptimeMillis()));
    }

    private void CancelArm()
    {
        if (_armAtMs >= 0)
        {
            _armAtMs = -1;
            _handler.RemoveCallbacks(_armRunnable);
        }
    }

    private void OnArmTimeout()
    {
        if (_disposed || _armAtMs < 0)
        {
            return;
        }

        long atMs = _armAtMs;
        _armAtMs = -1;
        Emit(atMs, 0, 0);
    }

    private void RemoveFinger(byte id)
    {
        for (int i = 0; i < _fingerCount; i++)
        {
            if (_fingers[i].PointerId == id)
            {
                _fingers[i] = _fingers[--_fingerCount];
                return;
            }
        }
    }
}
