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

    // MotionEvent.FLAG_CANCELED (API 33) and MotionEvent.TOOL_TYPE_PALM (API 33); plain constants so API 29+ can read them.
    private const int FlagCanceled = 0x20;
    private const int ToolTypePalm = 5;

    private readonly PadSendQueue _queue;
    private readonly InkEchoView? _ink;
    private readonly RawTouch[] _fingers = new RawTouch[MaxRawTouches];
    private readonly TouchSample[] _touchOut = new TouchSample[InputFilter.MaxTouches];
    private readonly PenSample[] _samples = new PenSample[MaxSamplesPerEvent];
    private readonly Java.Lang.Runnable _penLeaveRunnable;
    private InputFilter _filter;
    private InputFilterOptions? _pendingFilterOptions;
    private int _fingerCount;
    private PenSample _pen;
    private bool _penPresent;
    private bool _penLeavePending;
    private long _lastPenMs = long.MinValue / 2;
    private bool _active;
    private bool _inkContact;
    private bool _unbufferedRequested;

    public DrawSurfaceView(Context context, PadSendQueue queue, InkEchoView? ink, InputFilterOptions filterOptions)
        : base(context)
    {
        _queue = queue;
        _ink = ink;
        _filter = new InputFilter(filterOptions);
        _penLeaveRunnable = new Java.Lang.Runnable(OnPenLeaveTimeout);
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
        RemoveCallbacks(_penLeaveRunnable);
        _penLeavePending = false;
        _penPresent = false;
        _fingerCount = 0;
        _filter.Reset();
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

            _fingers[_fingerCount++] = new RawTouch((byte)id, true, canceled, Normalize(e.GetX(i), Width), Normalize(e.GetY(i), Height));
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

        Emit(e.EventTime, history);

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

        Emit(e.EventTime, history);
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
            Emit(e.EventTime, 0);
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
        if (disposing)
        {
            _penLeaveRunnable.Dispose();
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

    private static ushort NormalizePressure(float pressure)
    {
        if (!(pressure > 0))
        {
            return 0;
        }

        return pressure >= 1f ? ushort.MaxValue : (ushort)((pressure * 65535f) + 0.5f);
    }

    private void RequestUnbuffered(MotionEvent e)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (!_unbufferedRequested)
            {
                _unbufferedRequested = true;
                RequestUnbufferedDispatch((int)InputSourceType.ClassPointer);
            }
        }
        else if (e.ActionMasked == MotionEventActions.Down)
        {
            RequestUnbufferedDispatch(e); // per gesture on API 29
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
            TiltConverter.ToRotation(orientation),
            tool,
            state,
            buttons);
    }

    /// <summary>Runs the filter over the current state and queues the frame(s). <paramref name="historyCount"/>
    /// samples in <see cref="_samples"/> precede the current pen sample.</summary>
    private void Emit(long eventTimeMs, int historyCount)
    {
        PenSample? pen = _penPresent ? _pen : null;
        InputFilterResult result = _filter.Process(
            TimeSpan.FromMilliseconds(eventTimeMs), pen, _fingers.AsSpan(0, _fingerCount), _touchOut);

        bool active;
        if (result.HasPen)
        {
            PenSample current = result.Pen;
            bool contact = (current.State & PenState.Contact) != 0;
            active = contact;
            if (current.Tool == PenTool.FingerFallback || historyCount == 0)
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

            EchoInk(current, contact, current.Tool == PenTool.FingerFallback ? 0 : historyCount, eventTimeMs);
        }
        else
        {
            active = result.TouchCount > 0;
            _queue.Enqueue(default, false, _touchOut.AsSpan(0, result.TouchCount), default);
            _inkContact = false;
        }

        SetActive(active);
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
        RemoveCallbacks(_penLeaveRunnable);
        _penLeavePending = true;
        PostDelayed(_penLeaveRunnable, PenLeaveDelayMs);
    }

    private void CancelPenLeave()
    {
        if (_penLeavePending)
        {
            _penLeavePending = false;
            RemoveCallbacks(_penLeaveRunnable);
        }
    }

    private void OnPenLeaveTimeout()
    {
        if (!_penLeavePending)
        {
            return;
        }

        _penLeavePending = false;
        _penPresent = false;
        _lastPenMs = SystemClock.UptimeMillis();
        Emit(_lastPenMs, 0);
        TryApplyPendingFilter();
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
