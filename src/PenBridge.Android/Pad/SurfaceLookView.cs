using Android.Content;
using Android.Graphics;
using Android.Views;
using PenBridge.Android.Mirror;
using PenBridge.Core.Input;
using Path = Android.Graphics.Path;

namespace PenBridge.Android.Pad;

/// <summary>
/// The pad's background layer (ADR-0031 "Phone surface"): the active area in the background colour, the margins
/// outside it dimmed, L-shaped ticks at its corners and an optional centre cross. Everything is derived from one
/// <see cref="NormalizedRect"/> (<see cref="SetActiveArea"/>); without the Mac's ActiveArea it is the whole surface.
/// Sits under the input surface and takes no input. While mirroring (<see cref="SetMirroring"/>, ADR-0038) only the
/// dimmed margins are drawn: the active area stays transparent over the video surface below, and the ticks and cross
/// are hidden.
/// </summary>
/// <remarks>
/// Geometry is rebuilt into cached <see cref="Path"/>/<see cref="RectF"/>s only when the size, the area or the look
/// changes; <see cref="OnDraw"/> allocates nothing. UI thread only.
/// </remarks>
internal sealed class SurfaceLookView : View
{
    private const float TickLengthDp = 32f;
    private const float TickStrokeDp = 3f;
    private const float CrossArmDp = 12f;
    private const float CrossStrokeDp = 1.5f;

    private readonly float _density;
    private readonly Paint _activePaint = new();
    private readonly Paint _tickPaint = new(PaintFlags.AntiAlias);
    private readonly Paint _crossPaint = new(PaintFlags.AntiAlias);
    private readonly Path _ticks = new();
    private readonly Path _cross = new();
    private readonly RectF _active = new();
    private readonly Rect _activePx = new();
    private SurfaceLook _look = new();
    private NormalizedRect _area = NormalizedRect.Full;
    private Color _marginColor;
    private bool _isFull = true;
    private bool _mirroring;

    public SurfaceLookView(Context context)
        : base(context)
    {
        _density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        _tickPaint.SetStyle(Paint.Style.Stroke);
        _tickPaint.StrokeWidth = TickStrokeDp * _density;
        _tickPaint.StrokeCap = Paint.Cap.Square;
        _tickPaint.StrokeJoin = Paint.Join.Miter;
        _crossPaint.SetStyle(Paint.Style.Stroke);
        _crossPaint.StrokeWidth = CrossStrokeDp * _density;
        _crossPaint.StrokeCap = Paint.Cap.Butt;
        Clickable = false;
        Focusable = false;
        ImportantForAccessibility = ImportantForAccessibility.No;
        ApplyLook();
    }

    /// <summary>The active area in the current surface orientation (normalized 0..1).</summary>
    public NormalizedRect ActiveArea => _area;

    public void SetLook(SurfaceLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        if (look == _look)
        {
            return;
        }

        _look = look;
        ApplyLook();
        RebuildGeometry();
        Invalidate();
    }

    /// <summary>
    /// Sets the active area (normalized to the current surface). Out-of-range or empty rects are clamped, falling back
    /// to the full surface, so a bad value never hides the markers.
    /// </summary>
    public void SetActiveArea(NormalizedRect area)
    {
        area = Sanitize(area);
        if (area == _area)
        {
            return;
        }

        _area = area;
        RebuildGeometry();
        Invalidate();
    }

    /// <summary>
    /// Mirroring on: draw only the margins, cut out at the video's pixel rect (<see cref="MirrorGeometry"/>), so the video
    /// below shows through. Off: the normal look.
    /// </summary>
    public void SetMirroring(bool mirroring)
    {
        if (mirroring == _mirroring)
        {
            return;
        }

        _mirroring = mirroring;
        Invalidate();
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        RebuildGeometry();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_mirroring)
        {
            if (!_isFull)
            {
                canvas.Save();
                canvas.ClipOutRect(_activePx);
                canvas.DrawColor(_marginColor);
                canvas.Restore();
            }

            return;
        }

        if (_isFull)
        {
            canvas.DrawColor(_look.ActiveColor);
        }
        else
        {
            canvas.DrawColor(_marginColor);
            canvas.DrawRect(_active, _activePaint);
        }

        canvas.DrawPath(_ticks, _tickPaint);
        if (_look.CentreMarker)
        {
            canvas.DrawPath(_cross, _crossPaint);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _activePaint.Dispose();
            _tickPaint.Dispose();
            _crossPaint.Dispose();
            _ticks.Dispose();
            _cross.Dispose();
            _active.Dispose();
            _activePx.Dispose();
        }

        base.Dispose(disposing);
    }

    private static NormalizedRect Sanitize(NormalizedRect r)
    {
        if (!double.IsFinite(r.X) || !double.IsFinite(r.Y) || !double.IsFinite(r.Width) || !double.IsFinite(r.Height))
        {
            return NormalizedRect.Full;
        }

        double left = Math.Clamp(r.X, 0, 1);
        double top = Math.Clamp(r.Y, 0, 1);
        double right = Math.Clamp(r.X + r.Width, 0, 1);
        double bottom = Math.Clamp(r.Y + r.Height, 0, 1);
        return right - left <= 0 || bottom - top <= 0
            ? NormalizedRect.Full
            : new NormalizedRect(left, top, right - left, bottom - top);
    }

    private void ApplyLook()
    {
        _activePaint.Color = _look.ActiveColor;
        _marginColor = _look.MarginColor;
        int alpha = (int)MathF.Round(Math.Clamp(_look.MarkerOpacity, SurfaceLook.MinMarkerOpacity, 1f) * 255f);
        Color marker = _look.MarkerColor;
        _tickPaint.Color = marker;
        _tickPaint.Alpha = alpha;
        _crossPaint.Color = marker;
        _crossPaint.Alpha = alpha;
    }

    private void RebuildGeometry()
    {
        _ticks.Rewind();
        _cross.Rewind();
        int w = Width;
        int h = Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        float left = (float)(_area.X * w);
        float top = (float)(_area.Y * h);
        float right = (float)((_area.X + _area.Width) * w);
        float bottom = (float)((_area.Y + _area.Height) * h);
        _active.Set(left, top, right, bottom);
        _isFull = left <= 0.5f && top <= 0.5f && right >= w - 0.5f && bottom >= h - 0.5f;
        (int pl, int pt, int pr, int pb) = MirrorGeometry.ActivePixels(_area, w, h);
        _activePx.Set(pl, pt, pr, pb);

        // Ticks inside the area, inset by half the stroke so the full-surface ticks aren't clipped at the screen edge.
        float inset = _tickPaint.StrokeWidth / 2f;
        float l = left + inset;
        float t = top + inset;
        float r = right - inset;
        float b = bottom - inset;
        float len = Math.Min(TickLengthDp * _density, Math.Min(r - l, b - t) / 3f);
        AddCorner(l, t + len, l, t, l + len, t);
        AddCorner(r - len, t, r, t, r, t + len);
        AddCorner(r, b - len, r, b, r - len, b);
        AddCorner(l + len, b, l, b, l, b - len);

        float cx = (left + right) / 2f;
        float cy = (top + bottom) / 2f;
        float arm = CrossArmDp * _density;
        _cross.MoveTo(cx - arm, cy);
        _cross.LineTo(cx + arm, cy);
        _cross.MoveTo(cx, cy - arm);
        _cross.LineTo(cx, cy + arm);
    }

    private void AddCorner(float x0, float y0, float x1, float y1, float x2, float y2)
    {
        _ticks.MoveTo(x0, y0);
        _ticks.LineTo(x1, y1);
        _ticks.LineTo(x2, y2);
    }
}
