using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;

namespace PenBridge.Android.Pad;

/// <summary>
/// Faint local trail of the last ~1 s of pen contact positions, fading out (ADR-0019). It is only feedback, not the
/// Mac canvas. Its own view under the input surface: it takes no input and redraws only while points are alive.
/// </summary>
/// <remarks>Preallocated ring of points; adding and drawing allocate nothing. UI thread only.</remarks>
internal sealed class InkEchoView : View
{
    private const int Capacity = 2048;
    private const long FadeMs = 1000;
    private const int MaxAlpha = 110;
    private const int AlphaSteps = 16;

    private readonly ushort[] _x = new ushort[Capacity];
    private readonly ushort[] _y = new ushort[Capacity];
    private readonly ushort[] _pressure = new ushort[Capacity];
    private readonly long[] _time = new long[Capacity];
    private readonly bool[] _strokeStart = new bool[Capacity];
    private readonly Paint _paint;
    private readonly float _minWidth;
    private readonly float _maxWidth;
    private int _head;
    private int _count;

    public InkEchoView(Context context)
        : base(context)
    {
        float density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        _minWidth = 1.5f * density;
        _maxWidth = 5f * density;
        _paint = new Paint(PaintFlags.AntiAlias) { Color = Color.White, StrokeCap = Paint.Cap.Round };
        _paint.SetStyle(Paint.Style.Stroke);
        Clickable = false;
        Focusable = false;
        ImportantForAccessibility = ImportantForAccessibility.No;
    }

    /// <summary>Adds a contact point in normalized 0..65535 coordinates; <paramref name="newStroke"/> starts a new line.</summary>
    public void Add(ushort x, ushort y, ushort pressure, long uptimeMs, bool newStroke)
    {
        int slot;
        if (_count == Capacity)
        {
            slot = _head;
            _head = (_head + 1) % Capacity;
            _strokeStart[_head] = true;
        }
        else
        {
            slot = (_head + _count) % Capacity;
            _count++;
        }

        _x[slot] = x;
        _y[slot] = y;
        _pressure[slot] = pressure;
        _time[slot] = uptimeMs;
        _strokeStart[slot] = newStroke;
        Invalidate();
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);

        long now = SystemClock.UptimeMillis();
        while (_count > 0 && now - _time[_head] >= FadeMs)
        {
            _head = (_head + 1) % Capacity;
            _count--;
            if (_count > 0)
            {
                _strokeStart[_head] = true;
            }
        }

        if (_count == 0)
        {
            return;
        }

        float sx = Width / 65535f;
        float sy = Height / 65535f;
        int lastAlpha = -1;
        float lastWidth = -1;
        for (int k = 0; k < _count; k++)
        {
            int i = (_head + k) % Capacity;
            long age = Math.Max(0, now - _time[i]);
            int alpha = (int)((FadeMs - age) * AlphaSteps / FadeMs) * MaxAlpha / AlphaSteps;
            float width = _minWidth + ((_maxWidth - _minWidth) * (_pressure[i] >> 12) / 15f);
            if (alpha != lastAlpha)
            {
                _paint.Alpha = alpha;
                lastAlpha = alpha;
            }

            if (width != lastWidth)
            {
                _paint.StrokeWidth = width;
                lastWidth = width;
            }

            float x = _x[i] * sx;
            float y = _y[i] * sy;
            if (_strokeStart[i])
            {
                canvas.DrawPoint(x, y, _paint);
            }
            else
            {
                int p = (i + Capacity - 1) % Capacity;
                canvas.DrawLine(_x[p] * sx, _y[p] * sy, x, y, _paint);
            }
        }

        PostInvalidateOnAnimation();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _paint.Dispose();
        }

        base.Dispose(disposing);
    }
}
