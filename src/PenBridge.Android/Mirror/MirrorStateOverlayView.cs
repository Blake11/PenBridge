using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using PenBridge.Core.Input;

namespace PenBridge.Android.Mirror;

/// <summary>What the mirror overlay shows over the active area.</summary>
internal enum MirrorOverlayState
{
    /// <summary>Nothing (streaming, or mirroring off).</summary>
    None = 0,

    /// <summary>Video paused (e.g. PenBridge paused on the Mac, the input guard).</summary>
    Paused = 1,

    /// <summary>Mirroring can't run; the reason text says why.</summary>
    Unavailable = 2,
}

/// <summary>
/// A drawing-only layer above the pad surface: a scrim over the active area with a title ("Mirroring paused" /
/// "Mirroring unavailable") and an optional reason line. It never takes touches or hover, so input reaches the pad
/// unchanged. UI thread only; drawing allocates nothing (the text layout is rebuilt only when the state changes).
/// </summary>
internal sealed class MirrorStateOverlayView : View
{
    private const float TitleSp = 18f;
    private const float ReasonSp = 14f;
    private const float GapDp = 6f;

    private readonly float _density;
    private readonly Paint _scrim = new();
    private readonly TextPaint _titlePaint = new(PaintFlags.AntiAlias);
    private readonly TextPaint _reasonPaint = new(PaintFlags.AntiAlias);
    private readonly Rect _activePx = new();
    private MirrorOverlayState _state;
    private string _title = string.Empty;
    private string _reason = string.Empty;
    private NormalizedRect _area = NormalizedRect.Full;
    private StaticLayout? _reasonLayout;

    public MirrorStateOverlayView(Context context)
        : base(context)
    {
        global::Android.Util.DisplayMetrics? metrics = context.Resources?.DisplayMetrics;
        _density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        _scrim.Color = Color.Argb(170, 0, 0, 0);
        _titlePaint.Color = Color.White;
        _titlePaint.TextSize = global::Android.Util.TypedValue.ApplyDimension(global::Android.Util.ComplexUnitType.Sp, TitleSp, metrics);
        _titlePaint.TextAlign = Paint.Align.Center;
        _titlePaint.SetTypeface(Typeface.DefaultBold);
        _reasonPaint.Color = Color.Argb(220, 255, 255, 255);
        _reasonPaint.TextSize = global::Android.Util.TypedValue.ApplyDimension(global::Android.Util.ComplexUnitType.Sp, ReasonSp, metrics);
        Clickable = false;
        Focusable = false;
        ImportantForAccessibility = ImportantForAccessibility.Yes;
        Visibility = ViewStates.Gone;
    }

    public MirrorOverlayState State => _state;

    /// <summary>Shows <paramref name="state"/> with an optional reason line (Unavailable reasons, spec "MirrorState").</summary>
    public void SetState(MirrorOverlayState state, string? reason = null)
    {
        reason ??= string.Empty;
        if (state == _state && reason == _reason)
        {
            return;
        }

        _state = state;
        _reason = reason;
        _title = state switch
        {
            MirrorOverlayState.Paused => "Mirroring paused",
            MirrorOverlayState.Unavailable => "Mirroring unavailable",
            _ => string.Empty,
        };
        ContentDescription = reason.Length == 0 ? _title : _title + ". " + reason;
        _reasonLayout?.Dispose();
        _reasonLayout = null;
        Visibility = state == MirrorOverlayState.None ? ViewStates.Gone : ViewStates.Visible;
        RebuildLayout();
        Invalidate();
    }

    public void SetActiveArea(NormalizedRect area)
    {
        if (area == _area)
        {
            return;
        }

        _area = area;
        RebuildLayout();
        Invalidate();
    }

    public override bool OnTouchEvent(MotionEvent? e) => false;

    public override bool OnHoverEvent(MotionEvent? e) => false;

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        RebuildLayout();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_state == MirrorOverlayState.None || _activePx.IsEmpty)
        {
            return;
        }

        canvas.DrawRect(_activePx, _scrim);
        float gap = GapDp * _density;
        float titleHeight = _titlePaint.FontSpacing;
        float reasonHeight = _reasonLayout?.Height ?? 0;
        float total = titleHeight + (reasonHeight > 0 ? gap + reasonHeight : 0);
        float top = _activePx.ExactCenterY() - (total / 2f);
        canvas.DrawText(_title, _activePx.ExactCenterX(), top - _titlePaint.Ascent(), _titlePaint);
        if (_reasonLayout is { } layout)
        {
            canvas.Save();
            canvas.Translate(_activePx.ExactCenterX() - (layout.Width / 2f), top + titleHeight + gap);
            layout.Draw(canvas);
            canvas.Restore();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scrim.Dispose();
            _titlePaint.Dispose();
            _reasonPaint.Dispose();
            _activePx.Dispose();
            _reasonLayout?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RebuildLayout()
    {
        (int l, int t, int r, int b) = MirrorGeometry.ActivePixels(_area, Width, Height);
        _activePx.Set(l, t, r, b);
        _reasonLayout?.Dispose();
        _reasonLayout = null;
        int width = (int)Math.Min(r - l - (32 * _density), 480 * _density);
        if (_state == MirrorOverlayState.None || _reason.Length == 0 || width <= 0)
        {
            return;
        }

        _reasonLayout = StaticLayout.Builder.Obtain(_reason, 0, _reason.Length, _reasonPaint, width)
            .SetAlignment(global::Android.Text.Layout.Alignment.AlignCenter!)!
            .Build();
    }
}
