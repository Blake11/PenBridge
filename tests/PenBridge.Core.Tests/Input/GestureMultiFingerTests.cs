using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>Finger-count kinds (ADR-0023): three-finger drag, four-finger swipe/pinch, settling, 5+ fingers.</summary>
public class GestureMultiFingerTests
{
    // Square 1000x1000 surface onto a 1000x1000 display: drag 2% = 20 pt, swipe/system pinch 5% = 50 pt.
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));
    private readonly RecordingSink _sink = new();
    private readonly GestureRecognizer _rec;
    private long _t;

    public GestureMultiFingerTests()
    {
        var options = new GestureRecognizerOptions
        {
            ScrollAcceleration = GestureAcceleration.None,
            DragAcceleration = GestureAcceleration.None,
        };
        _rec = new GestureRecognizer(_sink, options) { Mapper = Square };
    }

    private List<GestureEvent> Gestures => _sink.Gestures.Where(g => g.Kind != GestureKind.TouchDown).ToList();

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    private static TouchSample F(byte id, double x, double y) => new(id, true, N(x), N(y));

    private void Frame(params TouchSample[] touches)
    {
        _t += 8_000;
        _rec.Process(touches, false, _t);
    }

    /// <summary>Three fingers in a row at y, shifted by dx.</summary>
    private void Three(double y, double dx = 0) => Frame(F(1, 400 + dx, y), F(2, 500 + dx, y), F(3, 600 + dx, y));

    /// <summary>Four fingers on a square of half-size r around (cx, cy).</summary>
    private void Four(double cx = 500, double cy = 500, double r = 100) =>
        Frame(F(1, cx - r, cy - r), F(2, cx + r, cy - r), F(3, cx + r, cy + r), F(4, cx - r, cy + r));

    [Fact]
    public void Three_fingers_drag_with_relative_deltas_and_end_on_lift()
    {
        Three(500);
        Three(525); // 25 pt >= 20
        Three(535);
        Frame(F(1, 400, 535), F(2, 500, 535)); // a finger lifts

        Assert.Equal([GesturePhase.Began, GesturePhase.Changed, GesturePhase.Ended], Gestures.Select(g => g.Phase));
        Assert.All(Gestures, g => Assert.Equal(GestureKind.Drag, g.Kind));
        Assert.Equal(25, Gestures[0].DeltaY, 0.1);
        Assert.Equal(10, Gestures[1].DeltaY, 0.1);
        Assert.Equal(0, Gestures[1].DeltaX, 0.1);
        Assert.Equal(0, Gestures[2].DeltaY);
        Assert.False(_rec.IsActive);
    }

    [Fact]
    public void Three_fingers_below_the_drag_threshold_emit_nothing()
    {
        Three(500);
        Three(515);
        Assert.Empty(Gestures);
    }

    [Fact]
    public void Staggered_landing_settles_on_the_final_finger_count()
    {
        Frame(F(1, 400, 500), F(2, 600, 500));
        Frame(F(1, 400, 530), F(2, 600, 530)); // 30 pt: not yet a scroll
        Frame(F(1, 400, 530), F(2, 600, 530), F(3, 500, 530)); // third lands: restart on three
        Frame(F(1, 400, 555), F(2, 600, 555), F(3, 500, 555));

        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal(GestureKind.Drag, began.Kind);
        Assert.Equal(25, began.DeltaY, 0.1); // since the three settled, not since the first landing
    }

    [Fact]
    public void Two_three_four_fingers_settle_on_a_four_finger_swipe()
    {
        Frame(F(1, 400, 400), F(2, 600, 400));
        Frame(F(1, 400, 400), F(2, 600, 400), F(3, 600, 600));
        Frame(F(1, 400, 400), F(2, 600, 400), F(3, 600, 600), F(4, 400, 600));
        Four(500, 440);
        Assert.Equal(GestureKind.SystemSwipe, Assert.Single(Gestures).Kind);
    }

    [Fact]
    public void Fingers_lifting_before_a_threshold_do_not_count()
    {
        Three(500);
        Frame(F(1, 400, 500), F(2, 600, 500)); // one lifts before anything was decided
        Frame(F(1, 400, 550), F(2, 600, 550));
        Assert.Equal(GestureKind.Scroll, Assert.Single(Gestures).Kind);
    }

    [Theory]
    [InlineData(0, -60, GestureDirection.Up)]
    [InlineData(0, 60, GestureDirection.Down)]
    [InlineData(-60, 0, GestureDirection.Left)]
    [InlineData(60, 0, GestureDirection.Right)]
    [InlineData(-60, 40, GestureDirection.Left)] // dominant axis
    public void Four_finger_swipe_reports_direction_and_progress(double dx, double dy, GestureDirection direction)
    {
        Four();
        Four(500 + dx, 500 + dy);
        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal(GestureKind.SystemSwipe, began.Kind);
        Assert.Equal(GesturePhase.Began, began.Phase);
        Assert.Equal(direction, began.Direction);
        Assert.Equal(0.06, began.Progress, 3);
        Assert.Equal(0, began.DeltaX);
        Assert.Equal(1, began.ScaleDelta);
    }

    [Fact]
    public void Swipe_progress_is_cumulative_along_the_locked_axis()
    {
        Four();
        Four(500, 440); // up 60
        Four(500, 400); // up 100
        Four(560, 420); // sideways movement doesn't change the axis
        Frame(F(1, 460, 320), F(2, 660, 320), F(3, 660, 520)); // a finger lifts

        GestureEvent[] g = [.. Gestures];
        Assert.Equal([GesturePhase.Began, GesturePhase.Changed, GesturePhase.Changed, GesturePhase.Ended], g.Select(e => e.Phase));
        Assert.All(g, e => Assert.Equal(GestureDirection.Up, e.Direction));
        Assert.Equal(0.10, g[1].Progress, 3);
        Assert.Equal(0.08, g[2].Progress, 3);
        Assert.Equal(0.08, g[3].Progress, 3); // Ended carries the last progress
    }

    [Theory]
    [InlineData(100, 60, GestureDirection.In)]
    [InlineData(100, 140, GestureDirection.Out)]
    public void Four_finger_pinch_reports_direction_and_progress(double r0, double r1, GestureDirection direction)
    {
        Four(r: r0);
        Four(r: r1); // spread = 2 * mean distance = 2 * r * sqrt(2)
        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal(GestureKind.SystemPinch, began.Kind);
        Assert.Equal(direction, began.Direction);
        Assert.Equal(2 * 40 * Math.Sqrt(2) / 1000, began.Progress, 3);
    }

    [Fact]
    public void Four_finger_pinch_needs_spread_change_at_least_the_travel()
    {
        Four(r: 100);
        Four(cx: 500, cy: 580, r: 125); // spread change ~71, travel 80
        Assert.Equal(GestureKind.SystemSwipe, Assert.Single(Gestures).Kind);
    }

    [Fact]
    public void Four_finger_pinch_progress_can_go_back()
    {
        Four(r: 100);
        Four(r: 60);
        Four(r: 110);
        Assert.Equal(-2 * 10 * Math.Sqrt(2) / 1000, Gestures[^1].Progress, 3);
        Assert.Equal(GestureDirection.In, Gestures[^1].Direction);
    }

    [Fact]
    public void Five_or_more_fingers_are_no_gesture_until_all_lift()
    {
        Frame(F(1, 300, 500), F(2, 400, 500), F(3, 500, 500), F(4, 600, 500), F(5, 700, 500));
        Frame(F(1, 300, 600), F(2, 400, 600), F(3, 500, 600), F(4, 600, 600), F(5, 700, 600));
        Frame(F(1, 300, 700), F(2, 400, 700), F(3, 500, 700), F(4, 600, 700)); // down to four: still ignored
        Frame(F(1, 300, 800), F(2, 400, 800), F(3, 500, 800), F(4, 600, 800));
        Assert.Empty(Gestures);

        Frame();
        Frame(F(1, 400, 500), F(2, 600, 500));
        Frame(F(1, 400, 560), F(2, 600, 560));
        Assert.Equal(GestureKind.Scroll, Assert.Single(Gestures).Kind);
    }

    [Fact]
    public void Extra_fingers_after_the_lock_are_ignored()
    {
        Three(500);
        Three(530); // drag locked
        Frame(F(1, 400, 540), F(2, 500, 540), F(3, 600, 540), F(4, 100, 100));
        Assert.All(Gestures, g => Assert.Equal(GestureKind.Drag, g.Kind));
        Assert.Equal(10, Gestures[^1].DeltaY, 0.1);
    }

    [Fact]
    public void After_a_gesture_ends_the_remaining_fingers_start_nothing_until_all_lift()
    {
        Four();
        Four(500, 440); // swipe
        Frame(F(1, 400, 340), F(2, 600, 340), F(3, 600, 540)); // lift one: Ended
        Frame(F(1, 400, 300), F(2, 600, 300), F(3, 600, 500)); // three moving: no drag
        Assert.Equal([GesturePhase.Began, GesturePhase.Ended], Gestures.Select(g => g.Phase));
    }

    [Fact]
    public void Pen_cancels_a_swipe_with_its_direction_and_progress()
    {
        Four();
        Four(500, 440);
        _rec.Process([F(1, 400, 340), F(2, 600, 340), F(3, 600, 540), F(4, 400, 540)], true, _t + 8_000);
        GestureEvent cancelled = Gestures[^1];
        Assert.Equal(GestureKind.SystemSwipe, cancelled.Kind);
        Assert.Equal(GesturePhase.Cancelled, cancelled.Phase);
        Assert.Equal(GestureDirection.Up, cancelled.Direction);
        Assert.Equal(0.06, cancelled.Progress, 3);
    }

    [Fact]
    public void Cancel_ends_a_drag_as_cancelled()
    {
        Three(500);
        Three(530);
        _rec.Cancel(_t + 1);
        Assert.Equal([GesturePhase.Began, GesturePhase.Cancelled], Gestures.Select(g => g.Phase));
        Assert.All(Gestures, g => Assert.Equal(GestureKind.Drag, g.Kind));
        Assert.True(Gestures[^1].Synthetic);
    }

    [Fact]
    public void Two_finger_and_drag_events_carry_no_direction_or_progress()
    {
        Three(500);
        Three(530);
        Assert.All(Gestures, g =>
        {
            Assert.Equal(GestureDirection.None, g.Direction);
            Assert.Equal(0, g.Progress);
        });
    }
}
