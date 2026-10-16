using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Path = Android.Graphics.Path;

namespace PenBridge.Android.Pad;

/// <summary>
/// Faint local trail of the last ~1 s of pen contact positions, fading out (ADR-0019). It is only feedback, not the
/// Mac canvas. Its own view stacked above the input surface: it takes no input (touches and hover fall through) and
/// redraws only while points are alive.
/// </summary>
/// <remarks>
/// Preallocated ring of points; adding and drawing allocate nothing. Each frame batches the segments into a fixed set
/// of reusable <see cref="Path"/>s, one per (age, pressure) bucket, so a frame is at most
/// <c>AlphaBuckets × WidthBuckets</c> draw calls however many points are alive. UI thread only.
/// </remarks>
internal sealed class InkEchoView : View
{
    private const int Capacity = 2048;
    private const long FadeMs = 1000;
    private const int MaxAlpha = 110;
    private const int AlphaBuckets = 4;
    private const int WidthBuckets = 3;
    private const int Buckets = AlphaBuckets * WidthBuckets;

    private readonly ushort[] _x = new ushort[Capacity];
    private readonly ushort[] _y = new ushort[Capacity];
    private readonly ushort[] _pressure = new ushort[Capacity];
    private readonly long[] _time = new long[Capacity];
    private readonly bool[] _strokeStart = new bool[Capacity];
    private readonly Paint _paint;
    private readonly Path[] _paths = new Path[Buckets];
    private readonly int[] _pathEnd = new int[Buckets]; // ring index the bucket's path currently ends at, or -1
    private readonly bool[] _pathUsed = new bool[Buckets];
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
        _paint.StrokeJoin = Paint.Join.Round;
        for (int b = 0; b < Buckets; b++)
        {
            _paths[b] = new Path();
        }
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

    /// <summary>Ink colour (opaque; the fade is applied as alpha). Readable on the chosen background.</summary>
    public void SetColor(Color color)
    {
        _paint.Color = color;
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

        for (int b = 0; b < Buckets; b++)
        {
            _paths[b].Rewind();
            _pathEnd[b] = -1;
            _pathUsed[b] = false;
        }

        float sx = Width / 65535f;
        float sy = Height / 65535f;
        for (int k = 0; k < _count; k++)
        {
            int i = (_head + k) % Capacity;
            long age = Math.Max(0, now - _time[i]);
            int alphaBucket = (int)Math.Min(AlphaBuckets - 1, age * AlphaBuckets / FadeMs); // 0 = newest
            int widthBucket = Math.Min(WidthBuckets - 1, _pressure[i] * WidthBuckets / 65536);
            int b = (alphaBucket * WidthBuckets) + widthBucket;
            Path path = _paths[b];
            _pathUsed[b] = true;

            float x = _x[i] * sx;
            float y = _y[i] * sy;
            if (_strokeStart[i])
            {
                path.MoveTo(x, y);
                path.LineTo(x + 0.01f, y); // zero-length segment: drawn as a round dot
            }
            else
            {
                int p = (i + Capacity - 1) % Capacity;
                if (_pathEnd[b] != p)
                {
                    path.MoveTo(_x[p] * sx, _y[p] * sy);
                }

                path.LineTo(x, y);
            }

            _pathEnd[b] = i;
        }

        for (int b = Buckets - 1; b >= 0; b--) // oldest first, so the newest ink is on top
        {
            if (!_pathUsed[b])
            {
                continue;
            }

            int alphaBucket = b / WidthBuckets;
            int widthBucket = b % WidthBuckets;
            _paint.Alpha = MaxAlpha * (AlphaBuckets - alphaBucket) / AlphaBuckets;
            _paint.StrokeWidth = _minWidth + ((_maxWidth - _minWidth) * widthBucket / (WidthBuckets - 1));
            canvas.DrawPath(_paths[b], _paint);
        }

        PostInvalidateOnAnimation();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _paint.Dispose();
            foreach (Path path in _paths)
            {
                path.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
