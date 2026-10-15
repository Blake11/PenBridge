using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// One finger as a relative trackpad cursor (ADR-0026, <see cref="GestureRecognizerOptions.OneFingerCursor"/>): move,
/// click, double click, tap-and-a-half drag, and how a second finger, the pen and cancels end them.
/// </summary>
public class GestureOneFingerTests
{
    // Square 1000x1000 surface onto a 1000x1000 display. These tests use a 35 pt pointer slop (3.5%) for round numbers;
    // tap 300 ms, double tap 400 ms / 200 pt.
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));

    private static readonly GestureRecognizerOptions Linear = new()
    {
        OneFingerCursor = true,
        PointerSlop = 0.035,
        DragAcceleration = GestureAcceleration.None,
    };

    private readonly RecordingSink _sink = new();
    private GestureRecognizer _rec;

    public GestureOneFingerTests()
    {
        _rec = new GestureRecognizer(_sink, Linear) { Mapper = Square };
    }

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    private static TouchSample F(byte id, double x, double y) => new(id, true, N(x), N(y));

    private static long Ms(double ms) => (long)(ms * 1000);

    private void Frame(double ms, params TouchSample[] touches) => _rec.Process(touches, false, Ms(ms));

    private void PenFrame(double ms, params TouchSample[] touches) => _rec.Process(touches, true, Ms(ms));

    private void Lift(double ms) => Frame(ms);

    /// <summary>Recorded gestures without the TouchDown landing events.</summary>
    private List<GestureEvent> Events => _sink.Gestures.Where(g => g.Kind != GestureKind.TouchDown).ToList();

    private string[] Names() => Events.Select(e => e.Kind is GestureKind.Click ? $"Click{e.ClickCount}" : e.Kind is GestureKind.Tap ? "Tap" : $"{e.Kind}.{e.Phase}").ToArray();

    private void Tap(double ms, double x = 500, double y = 500, byte id = 1)
    {
        Frame(ms, F(id, x, y));
        Frame(ms + 40, F(id, x + 2, y + 1));
        Lift(ms + 80);
    }

    [Fact]
    public void Off_by_default_a_lone_finger_is_no_gesture()
    {
        _rec = new GestureRecognizer(_sink) { Mapper = Square };
        Frame(0, F(1, 500, 500));
        Frame(40, F(1, 600, 500));
        Lift(80);
        Tap(200);
        Assert.Empty(Events);
    }

    [Fact]
    public void Movement_within_the_slop_moves_nothing_then_the_cursor_catches_up_the_whole_movement()
    {
        Frame(0, F(1, 500, 500));
        Frame(10, F(1, 520, 500));
        Frame(20, F(1, 534, 500)); // 34 pt: still inside the 35 pt slop
        Assert.Empty(Events);

        Frame(30, F(1, 540, 504)); // crosses it: Began carries the movement since the landing
        Frame(40, F(1, 560, 494));
        Frame(50, F(1, 560, 494)); // no movement: no event
        Lift(60);

        Assert.Equal(["PointerMove.Began", "PointerMove.Changed", "PointerMove.Ended"], Names());
        GestureEvent began = Events[0];
        Assert.Equal(40, began.DeltaX, 1);
        Assert.Equal(4, began.DeltaY, 1);
        Assert.Equal(60, Events.Sum(e => e.DeltaX), 1); // all of the finger's travel
        Assert.Equal(20, Events[1].DeltaX, 1);
        Assert.Equal(-10, Events[1].DeltaY, 1);
        Assert.Equal(0, Events[2].DeltaX);
        Assert.Equal(0, Events[2].ClickCount);
        Assert.False(_rec.IsActive);
    }

    [Fact]
    public void Pointer_moves_use_the_drag_gain_and_acceleration()
    {
        _rec = new GestureRecognizer(_sink, new GestureRecognizerOptions { OneFingerCursor = true, DragGain = 2, DragAcceleration = GestureAcceleration.None }) { Mapper = Square };
        Frame(0, F(1, 500, 500));
        Frame(10, F(1, 540, 500));
        Frame(20, F(1, 550, 500));
        Assert.Equal(80, Events[0].DeltaX, 1);
        Assert.Equal(20, Events[1].DeltaX, 1);

        _sink.Clear();
        _rec = new GestureRecognizer(_sink, new GestureRecognizerOptions { OneFingerCursor = true }) { Mapper = Square };
        Frame(100, F(1, 500, 500));
        Frame(110, F(1, 540, 500));
        for (int i = 1; i <= 5; i++)
        {
            Frame(110 + (i * 8), F(1, 540 + (i * 60), 500)); // 7500 pt/s: past the fast speed
        }

        Assert.True(Events[^1].DeltaX > 60 * 2, "fast movement is accelerated");
    }

    [Fact]
    public void A_quick_tap_clicks_on_its_lift_at_once()
    {
        Frame(0, F(1, 500, 500));
        Frame(40, F(1, 520, 510)); // within the slop
        Lift(120);

        GestureEvent click = Assert.Single(Events);
        Assert.Equal(GestureKind.Click, click.Kind);
        Assert.Equal(GesturePhase.Ended, click.Phase);
        Assert.Equal(1, click.ClickCount);
        Assert.Equal(Ms(120), click.TimestampUs);
        Assert.Equal(500, click.Center.X, 0.1);
        Assert.Null(_rec.PendingDeadlineUs); // never held
    }

    [Theory]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void A_tap_lasts_at_most_the_tap_duration(double liftMs, bool clicks)
    {
        Frame(0, F(1, 500, 500));
        Frame(liftMs - 10, F(1, 500, 500));
        Lift(liftMs);
        Assert.Equal(clicks ? 1 : 0, Events.Count(e => e.Kind == GestureKind.Click));
        Assert.DoesNotContain(Events, e => e.Kind == GestureKind.PointerMove);
    }

    [Fact]
    public void A_moved_touch_is_no_click()
    {
        Frame(0, F(1, 500, 500));
        Frame(20, F(1, 560, 500));
        Frame(40, F(1, 500, 500)); // back at the landing point
        Lift(60);
        Assert.Equal(["PointerMove.Began", "PointerMove.Changed", "PointerMove.Ended"], Names());
    }

    [Fact]
    public void Two_taps_in_the_window_double_click_and_a_third_triple_clicks()
    {
        Tap(0);
        Tap(80 + 350, 520, 480); // 350 ms after the first lift, 28 pt away
        Tap(510 + 350, 500, 500);
        Assert.Equal(["Click1", "Click2", "Click3"], Names());
    }

    [Theory]
    [InlineData(401, 0)]   // landed after the double-tap window
    [InlineData(100, 201)] // landed beyond the double-tap slop
    public void Taps_apart_in_time_or_place_are_single_clicks(double gapMs, double dx)
    {
        Tap(0);
        Tap(80 + gapMs, 500 + dx, 500);
        Assert.Equal(["Click1", "Click1"], Names());
    }

    [Fact]
    public void Tap_and_a_half_drags_from_the_cursor_and_releases_on_lift()
    {
        Tap(0);
        Frame(200, F(1, 500, 500));
        Frame(220, F(1, 520, 500)); // inside the slop: the button is not down yet
        Frame(240, F(1, 550, 500)); // past it: drag
        Frame(260, F(1, 580, 520));
        Lift(280);

        Assert.Equal(["Click1", "PointerDrag.Began", "PointerDrag.Changed", "PointerDrag.Ended"], Names());
        Assert.Equal(50, Events[1].DeltaX, 1); // since the landing at 500
        Assert.Equal(30, Events[2].DeltaX, 1);
        Assert.Equal(20, Events[2].DeltaY, 1);
        Assert.Equal(0, Events[1].ClickCount);

        Tap(500); // a drag ends the click chain
        Assert.Equal(1, Events[^1].ClickCount);
    }

    [Fact]
    public void Holding_still_after_a_tap_begins_the_drag_at_the_tap_duration()
    {
        Tap(0);
        Frame(200, F(1, 500, 500));
        Frame(480, F(1, 501, 500));
        Assert.Equal(["Click1"], Names());
        Frame(501, F(1, 501, 500)); // 301 ms down
        Assert.Equal(GesturePhase.Began, Events[^1].Phase);
        Assert.Equal(GestureKind.PointerDrag, Events[^1].Kind);
        Assert.Equal(0, Events[^1].DeltaX);
        Lift(600);
        Assert.Equal(["Click1", "PointerDrag.Began", "PointerDrag.Ended"], Names());
    }

    [Fact]
    public void Holding_still_without_a_tap_before_is_neither_a_click_nor_a_drag()
    {
        Frame(0, F(1, 500, 500));
        Frame(400, F(1, 500, 500));
        Frame(800, F(1, 500, 500));
        Lift(900);
        Assert.Empty(Events);
    }

    [Fact]
    public void A_second_finger_releases_a_drag_first_then_scrolls()
    {
        Tap(0);
        Frame(200, F(1, 500, 500));
        Frame(220, F(1, 560, 500)); // dragging
        Frame(240, F(1, 560, 500), F(2, 760, 500));
        Assert.Equal(["Click1", "PointerDrag.Began", "PointerDrag.Ended"], Names());
        Assert.False(_rec.IsActive);

        for (int i = 1; i <= 6; i++)
        {
            Frame(240 + (i * 10), F(1, 560, 500 + (i * 15)), F(2, 760, 500 + (i * 15)));
        }

        Assert.Equal(GestureKind.Scroll, Events[3].Kind);
        Assert.Equal(GesturePhase.Began, Events[3].Phase);
    }

    [Fact]
    public void A_second_finger_ends_a_move_and_a_landing_inside_the_slop_moves_nothing()
    {
        Frame(0, F(1, 500, 500));
        Frame(20, F(1, 560, 500));
        Frame(40, F(1, 560, 500), F(2, 760, 500));
        Assert.Equal(["PointerMove.Began", "PointerMove.Ended"], Names());

        _sink.Clear();
        Lift(100);
        Frame(200, F(1, 500, 500));
        Frame(210, F(1, 520, 500)); // drifting inside the slop while the second finger comes
        Frame(230, F(1, 520, 500), F(2, 720, 500));
        Frame(260, F(1, 521, 500), F(2, 721, 500));
        Lift(320);
        _rec.CheckDeadline(Ms(1000)); // the held two-finger tap's window passed
        Assert.Equal(["Tap"], Names()); // a staggered two-finger tap, no click
    }

    [Fact]
    public void A_staggered_two_finger_tap_is_a_right_click_and_never_a_click()
    {
        _rec = new GestureRecognizer(_sink, Linear with { DoubleTapWindow = TimeSpan.Zero }) { Mapper = Square };
        Frame(0, F(1, 400, 500));
        Frame(30, F(1, 400, 500), F(2, 600, 500));
        Frame(100, F(2, 600, 500)); // the first lifts first: the leftover finger is no pointer
        Lift(150);
        Assert.Equal(["Tap"], Names());
    }

    [Fact]
    public void A_finger_left_over_after_a_scroll_never_moves_or_clicks()
    {
        Frame(0, F(1, 400, 500), F(2, 600, 500));
        for (int i = 1; i <= 6; i++)
        {
            Frame(i * 10, F(1, 400, 500 + (i * 15)), F(2, 600, 500 + (i * 15)));
        }

        Frame(80, F(2, 600, 590));
        Frame(100, F(2, 700, 590));
        Lift(120);
        Assert.DoesNotContain(Events, e => e.Kind is GestureKind.PointerMove or GestureKind.Click or GestureKind.PointerDrag);
    }

    [Fact]
    public void A_held_two_finger_tap_right_clicks_before_a_following_click()
    {
        Frame(0, F(1, 400, 500), F(2, 600, 500));
        Lift(60);
        Assert.Empty(Events); // held for the double-tap window
        Tap(160);
        Assert.Equal(["Tap", "Click1"], Names());
    }

    [Fact]
    public void A_held_two_finger_tap_right_clicks_before_a_pointer_move()
    {
        Frame(0, F(1, 400, 500), F(2, 600, 500));
        Lift(60);
        Frame(160, F(1, 500, 500));
        Frame(170, F(1, 560, 500));
        Assert.Equal(["Tap", "PointerMove.Began"], Names());
    }

    [Fact]
    public void A_two_finger_gesture_between_taps_breaks_the_click_chain()
    {
        _rec = new GestureRecognizer(_sink, Linear with { DoubleTapWindow = TimeSpan.Zero }) { Mapper = Square };
        Tap(0);
        Frame(100, F(1, 400, 500), F(2, 600, 500)); // a two-finger tap
        Lift(140);
        Tap(200);
        Assert.Equal(1, Events[^1].ClickCount);
        Assert.Equal(GestureKind.Click, Events[^1].Kind);
    }

    [Fact]
    public void A_lift_and_a_landing_in_one_frame_click_and_start_a_new_pointer()
    {
        Frame(0, F(1, 500, 500));
        Frame(60, F(2, 300, 300)); // 1 lifted, 2 landed
        Frame(80, F(2, 360, 300));
        Lift(100);
        Assert.Equal(["Click1", "PointerMove.Began", "PointerMove.Ended"], Names());
    }

    [Fact]
    public void The_pen_cancels_a_drag_and_a_tap_in_progress()
    {
        Tap(0);
        Frame(200, F(1, 500, 500));
        Frame(220, F(1, 560, 500));
        PenFrame(240, F(1, 560, 500));
        Frame(260, F(1, 600, 500));
        Lift(280);
        Assert.Equal(["Click1", "PointerDrag.Began", "PointerDrag.Cancelled"], Names());

        _sink.Clear();
        Frame(400, F(1, 500, 500));
        _rec.PenEntering(Ms(420));
        Lift(440);
        Assert.Empty(Events); // no click for a touch the pen interrupted
    }

    [Fact]
    public void A_cancel_releases_a_drag_and_drops_a_tap()
    {
        Tap(0);
        Frame(200, F(1, 500, 500));
        Frame(220, F(1, 560, 500));
        _rec.Cancel(Ms(300));
        GestureEvent cancelled = Events[^1];
        Assert.Equal(GestureKind.PointerDrag, cancelled.Kind);
        Assert.Equal(GesturePhase.Cancelled, cancelled.Phase);
        Assert.True(cancelled.Synthetic);

        Frame(320, F(1, 600, 500)); // still down after the cancel: blocked until it lifts
        Lift(340);
        Assert.Equal(3, Events.Count);

        Frame(400, F(1, 500, 500));
        _rec.Cancel(Ms(420));
        Lift(440);
        Assert.Equal(3, Events.Count);
    }

    [Fact]
    public void A_withdrawn_touch_cancels_a_move_and_is_no_click()
    {
        Frame(0, F(1, 500, 500));
        Frame(20, F(1, 560, 500));
        Frame(40, new TouchSample(1, false, N(560), N(500)));
        Lift(60);
        Assert.Equal(["PointerMove.Began", "PointerMove.Cancelled"], Names());

        _sink.Clear();
        Frame(200, F(1, 500, 500));
        Frame(220, new TouchSample(1, false, N(500), N(500)));
        Lift(240);
        Assert.Empty(Events);
    }

    [Theory]
    [InlineData(-1)] // nothing lost
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void A_double_tap_loses_and_doubles_no_click_when_any_one_frame_is_lost(int lost)
    {
        // As the client sends it with FingerTouches: the contact state again before each lift (echo), and the lift
        // repeats at the lift's own time.
        // (frame time, touches): the repeats go out 40 and 80 ms after a lift but carry its time.
        (double Ms, TouchSample[] Touches)[] frames =
        [
            (0, [F(1, 500, 500)]),
            (60, [F(1, 500, 500)]), // echo before the lift
            (60, []),
            (60, []),
            (60, []),
            (250, [F(1, 505, 500)]),
            (300, [F(1, 505, 500)]),
            (300, []),
            (300, []),
            (300, []),
        ];
        for (int i = 0; i < frames.Length; i++)
        {
            if (i != lost)
            {
                Frame(frames[i].Ms, frames[i].Touches);
            }
        }

        Assert.Equal(["Click1", "Click2"], Names());
    }

    [Fact]
    public void The_default_slop_is_about_a_millimetre_of_phone_travel()
    {
        Assert.Equal(0.015, new GestureRecognizerOptions().PointerSlop);
        _rec = new GestureRecognizer(_sink, Default) { Mapper = Square };
        Frame(0, F(1, 500, 500));
        Frame(50, F(1, 530, 500)); // 30 pt: past the pointer slop but inside the tap slop while a finger may still land
        Frame(99, F(1, 534, 500));
        Assert.Empty(Events);
        Frame(100, F(1, 534, 500)); // settled: the 15 pt pointer slop applies, and the cursor catches up all 34 pt
        GestureEvent began = Assert.Single(Events);
        Assert.Equal(GestureKind.PointerMove, began.Kind);
        Assert.Equal(34, began.DeltaX, 0);
    }

    private static readonly GestureRecognizerOptions Default = new() { OneFingerCursor = true, DragAcceleration = GestureAcceleration.None };

    [Fact]
    public void A_move_past_the_tap_slop_starts_at_once()
    {
        _rec = new GestureRecognizer(_sink, Default) { Mapper = Square };
        Frame(0, F(1, 500, 500));
        Frame(8, F(1, 520, 500));
        Frame(16, F(1, 540, 500)); // 40 pt > 35 pt tap slop after 16 ms
        GestureEvent began = Assert.Single(Events);
        Assert.Equal(GestureKind.PointerMove, began.Kind);
        Assert.Equal(40, began.DeltaX, 0);
    }

    [Fact]
    public void A_staggered_two_finger_tap_whose_first_finger_drifts_between_the_slops_still_right_clicks()
    {
        _rec = new GestureRecognizer(_sink, Default with { DoubleTapWindow = TimeSpan.Zero }) { Mapper = Square };
        Frame(0, F(1, 400, 500));
        Frame(40, F(1, 425, 500)); // 25 pt: beyond the pointer slop, within the tap slop
        Frame(70, F(1, 428, 500), F(2, 600, 500));
        Lift(150);
        Assert.Equal(["Tap"], Names());
    }

    [Fact]
    public void The_second_tap_of_a_smart_zoom_may_drift_between_the_slops()
    {
        _rec = new GestureRecognizer(_sink, Default) { Mapper = Square };
        Frame(0, F(1, 400, 500), F(2, 600, 500));
        Lift(60);
        Frame(200, F(1, 410, 500));
        Frame(240, F(1, 432, 500)); // drifts 22 pt before the second finger lands
        Frame(270, F(1, 432, 500), F(2, 610, 500));
        Lift(330);
        Assert.Equal(["SmartZoom.Ended"], Names());
    }

    [Fact]
    public void Moving_soon_after_a_click_but_beyond_the_click_slop_is_a_move_not_a_drag()
    {
        Tap(0);
        Frame(200, F(1, 560, 500)); // 60 pt from the click: beyond the 50 pt click slop (the 200 pt smart-zoom slop would drag)
        Frame(220, F(1, 620, 500));
        Lift(240);
        Assert.Equal(["Click1", "PointerMove.Began", "PointerMove.Ended"], Names());
    }

    [Fact]
    public void All_fingers_replaced_in_one_frame_start_a_new_sequence()
    {
        _rec = new GestureRecognizer(_sink, Default with { DoubleTapWindow = TimeSpan.Zero }) { Mapper = Square };
        Frame(0, F(1, 400, 500), F(2, 600, 500));
        Frame(60, F(3, 500, 300)); // both lifted (a tap) and a new lone finger landed, in one snapshot
        Frame(80, F(3, 560, 300));
        Lift(100);
        Assert.Equal(["Tap", "PointerMove.Began", "PointerMove.Ended"], Names());

        _sink.Clear();
        Frame(200, F(4, 500, 500));
        Frame(250, F(5, 500, 500)); // a lone finger replaced by another: a click, then a new pointer
        Lift(280);
        Assert.Equal(["Click1", "Click2"], Names()); // the second landed on the first click's spot within the window
    }

    [Fact]
    public void A_phone_with_finger_fallback_off_gets_no_one_finger_gestures()
    {
        _rec.PhoneFingerFallback = false;
        Tap(0);
        Frame(200, F(1, 500, 500));
        Frame(220, F(1, 600, 500));
        Lift(240);
        Assert.Empty(Events);

        _rec.PhoneFingerFallback = true; // turned back on (a new SurfaceInfo): the next touch is a pointer again
        Tap(400);
        Assert.Equal(["Click1"], Names());
    }

    [Fact]
    public void Options_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GestureRecognizer(_sink, new GestureRecognizerOptions { PointerSlop = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GestureRecognizer(_sink, new GestureRecognizerOptions { PointerSlop = double.NaN }));
    }
}
