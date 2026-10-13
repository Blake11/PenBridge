using Android.Content;
using Android.Graphics;
using Android.Views;
using PenBridge.Core.Net;

namespace PenBridge.Android.Pad;

/// <summary>
/// Tiny dim status dot in a corner (ADR-0019): green = connected over UDP, amber = TLS fallback, connecting or
/// reconnecting, red = disconnected. A finger tap, or a swipe that starts on it, raises <see cref="Tapped"/>.
/// Stylus input (and fingers while the pen is near, i.e. a palm) pass through to the surface underneath. Only a
/// gesture's first pointer can activate it: the pad root doesn't split motion events, so a later pointer landing on
/// the dot goes to the surface that owns the gesture (and a DOWN while the surface is active is declined too).
/// </summary>
internal sealed class StatusDotView : View
{
    private static readonly Color Green = Color.Rgb(60, 200, 90);
    private static readonly Color Amber = Color.Rgb(240, 170, 40);
    private static readonly Color Red = Color.Rgb(230, 60, 50);

    private readonly DrawSurfaceView _surface;
    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private readonly float _radius;
    private PadConnectionState _state;
    private bool _tracking;

    public StatusDotView(Context context, DrawSurfaceView surface)
        : base(context)
    {
        _surface = surface;
        _radius = 5f * (context.Resources?.DisplayMetrics?.Density ?? 1f);
        ContentDescription = "Connection status";
    }

    /// <summary>Tap (or swipe from the dot) by a finger.</summary>
    public event EventHandler? Tapped;

    public void SetState(PadConnectionState state)
    {
        if (state == _state)
        {
            return;
        }

        _state = state;
        Invalidate();
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null)
        {
            return false;
        }

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                MotionEventToolType tool = e.GetToolType(0);
                if (tool is MotionEventToolType.Stylus or MotionEventToolType.Eraser || _surface.IsPenNear || _surface.IsActive)
                {
                    return false; // not consumed: the surface below gets this gesture
                }

                _tracking = true;
                return true;
            case MotionEventActions.Up when _tracking:
                _tracking = false;
                Tapped?.Invoke(this, EventArgs.Empty);
                return true;
            case MotionEventActions.Cancel:
                _tracking = false;
                return true;
            default:
                return _tracking;
        }
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);

        _paint.Color = _state.Kind switch
        {
            PadConnectionKind.Connected when _state.Transport == InputTransport.Tls => Amber,
            PadConnectionKind.Connected => Green,
            PadConnectionKind.Connecting or PadConnectionKind.Reconnecting => Amber,
            _ => Red,
        };
        _paint.Alpha = 140;
        canvas.DrawCircle(Width / 2f, Height / 2f, _radius, _paint);
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
