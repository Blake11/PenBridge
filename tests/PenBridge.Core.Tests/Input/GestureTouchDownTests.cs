using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>The TouchDown landing event that lets the Mac stop a scroll coast (ADR-0023).</summary>
public class GestureTouchDownTests
{
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));
    private readonly RecordingSink _sink = new();
    private readonly GestureRecognizer _rec;

    public GestureTouchDownTests()
    {
        _rec = new GestureRecognizer(_sink) { Mapper = Square };
    }

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    private static TouchSample F(byte id, double x, double y) => new(id, true, N(x), N(y));

    private GestureEvent[] TouchDowns() => _sink.Gestures.Where(g => g.Kind == GestureKind.TouchDown).ToArray();

    [Fact]
    public void Landing_emits_one_touch_down_at_the_first_contact()
    {
        _rec.Process([new TouchSample(7, false, 1, 1), F(3, 250, 750)], false, 1_000);
        GestureEvent e = Assert.Single(_sink.Gestures);
        Assert.Equal(GestureKind.TouchDown, e.Kind);
        Assert.Equal(GesturePhase.Began, e.Phase);
        Assert.Equal(250, e.Center.X, 0.1);
        Assert.Equal(750, e.Center.Y, 0.1);
        Assert.Equal(0, e.DeltaX);
        Assert.Equal(0, e.DeltaY);
        Assert.Equal(1, e.ScaleDelta);
        Assert.Equal(0, e.RotationDelta);
        Assert.Equal(1_000, e.TimestampUs);
        Assert.False(e.Synthetic);
        Assert.False(_rec.IsActive);
    }

    [Fact]
    public void Every_frame_with_a_new_contact_emits_one_at_the_new_finger()
    {
        _rec.Process([F(1, 400, 500)], false, 0);
        _rec.Process([F(1, 400, 500)], false, 5);
        _rec.Process([F(1, 400, 500), F(2, 600, 500)], false, 10);
        _rec.Process([F(1, 400, 500), F(2, 600, 500), F(3, 500, 300)], false, 20);
        _rec.Process([F(2, 600, 500)], false, 30); // lifts emit nothing
        _rec.Process([F(2, 600, 500), F(1, 100, 100)], false, 40); // a re-landing finger (pointer id reused)

        GestureEvent[] downs = TouchDowns();
        Assert.Equal([0L, 10L, 20L, 40L], downs.Select(d => d.TimestampUs));
        Assert.Equal(600, downs[1].Center.X, 0.1);
        Assert.Equal(300, downs[2].Center.Y, 0.1);
        Assert.Equal(100, downs[3].Center.X, 0.1);
    }

    [Fact]
    public void A_finger_landing_mid_scroll_emits_one()
    {
        _rec.Process([F(1, 400, 500), F(2, 600, 500)], false, 0);
        _rec.Process([F(1, 400, 560), F(2, 600, 560)], false, 10);
        _rec.Process([F(1, 400, 570), F(2, 600, 570), F(3, 100, 100)], false, 20);
        Assert.Equal(2, TouchDowns().Length);
    }

    [Fact]
    public void None_after_cancel_until_all_fingers_lift()
    {
        _rec.Process([F(1, 400, 500), F(2, 600, 500)], false, 0);
        _rec.Cancel(10);
        _rec.Process([F(1, 400, 500), F(2, 600, 500)], false, 20);
        _rec.Process([F(1, 400, 500), F(2, 600, 500), F(3, 100, 100)], false, 30);
        Assert.Single(TouchDowns());
        _rec.Process([], false, 40);
        _rec.Process([F(1, 400, 500)], false, 50);
        Assert.Equal(2, TouchDowns().Length);
    }

    [Fact]
    public void Two_fingers_landing_together_emit_one()
    {
        _rec.Process([F(1, 400, 500), F(2, 600, 500)], false, 0);
        Assert.Single(TouchDowns());
    }

    [Fact]
    public void Each_new_touch_sequence_emits_again()
    {
        _rec.Process([F(1, 400, 500), F(2, 600, 500)], false, 0);
        _rec.Process([], false, 10);
        _rec.Process([F(1, 400, 500), F(2, 600, 500)], false, 20);
        Assert.Equal(2, TouchDowns().Length);
    }

    [Fact]
    public void Precedes_the_gesture_it_starts()
    {
        _rec.Process([F(1, 400, 500), F(2, 600, 500)], false, 0);
        _rec.Process([F(1, 400, 560), F(2, 600, 560)], false, 10);
        Assert.Equal([GestureKind.TouchDown, GestureKind.Scroll], _sink.Gestures.Select(g => g.Kind));
    }

    [Fact]
    public void None_while_the_pen_is_in_range()
    {
        _rec.Process([F(1, 400, 500), F(2, 600, 500)], true, 0);
        Assert.Empty(_sink.Gestures);
    }

    [Fact]
    public void None_while_fingers_that_were_down_with_the_pen_stay_down()
    {
        _rec.Process([F(1, 400, 500)], true, 0); // a finger down while the pen is in range: blocked
        _rec.Process([F(1, 400, 500), F(2, 600, 500)], false, 10);
        Assert.Empty(_sink.Gestures);
        _rec.Process([], false, 20); // all lifted: unblocked
        _rec.Process([F(1, 400, 500)], false, 30);
        Assert.Single(TouchDowns());
    }

    [Fact]
    public void Fingers_landing_as_the_pen_leaves_are_new()
    {
        _rec.Process([], true, 0); // pen hovering, no fingers
        _rec.Process([F(1, 400, 500), F(2, 600, 500)], false, 10); // pen gone, two fingers land in the same frame
        _rec.Process([F(1, 400, 560), F(2, 600, 560)], false, 20);
        Assert.Equal([GestureKind.TouchDown, GestureKind.Scroll], _sink.Gestures.Select(g => g.Kind));
    }

    [Fact]
    public void Unconfigured_mapper_emits_none()
    {
        var rec = new GestureRecognizer(_sink);
        rec.Process([F(1, 400, 500)], false, 0);
        Assert.Empty(_sink.Gestures);
    }

    [Fact]
    public void Stops_a_scroll_coast()
    {
        var momentum = new ScrollMomentum();
        for (int i = 0; i < 20; i++)
        {
            momentum.AddSample(0, 8, i * 8_000);
        }

        Assert.True(momentum.TryStart(160_000));
        _rec.Process([F(1, 400, 500), F(2, 600, 500)], false, 170_000);
        momentum.Observe(Assert.Single(_sink.Gestures));
        Assert.False(momentum.IsCoasting);
    }
}
