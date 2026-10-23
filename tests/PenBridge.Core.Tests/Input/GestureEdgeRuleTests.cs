using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// The recognizer's rules at their edges (ADR-0023, ADR-0024, ADR-0026): when a held tap is emitted, what blocks and
/// unblocks gestures, how a baseline restarts, the toggles and gains on the values, and the one-finger click count.
/// </summary>
public class GestureEdgeRuleTests
{
    // Square 1000x1000 surface onto a 1000x1000 display: thresholds are fractions of 1000 pt.
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));

    // One display point per normalized unit, so distances between whole units are exact (ties are reachable).
    private static readonly CoordinateMapper Exact = new(65535, 65535, new DisplayRect(0, 0, 65535, 65535));

    private static readonly GestureRecognizerOptions Linear = new()
    {
        ScrollAcceleration = GestureAcceleration.None,
        DragAcceleration = GestureAcceleration.None,
    };

    private static readonly GestureRecognizerOptions Cursor = Linear with { OneFingerCursor = true };

    private readonly RecordingSink _sink = new();

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    private static TouchSample F(byte id, double x, double y) => new(id, true, N(x), N(y));

    private static TouchSample W(byte id, double x, double y) => new(id, false, N(x), N(y));

    private static TouchSample U(byte id, int x, int y) => new(id, true, (ushort)x, (ushort)y);

    private static long Ms(double ms) => (long)Math.Round(ms * 1000);

    private GestureRecognizer Rec(GestureRecognizerOptions? options = null) => new(_sink, options) { Mapper = Square };

    private List<GestureEvent> Gestures => _sink.Gestures.Where(g => g.Kind != GestureKind.TouchDown).ToList();

    private (GestureKind, GesturePhase)[] Kinds() => Gestures.Select(g => (g.Kind, g.Phase)).ToArray();

    private static void Two(GestureRecognizer rec, double ms, double ax, double ay, double bx, double by) =>
        rec.Process([F(1, ax, ay), F(2, bx, by)], false, Ms(ms));

    private static void Four(GestureRecognizer rec, double ms, double cx = 500, double cy = 500, double r = 100) =>
        rec.Process([F(1, cx - r, cy - r), F(2, cx + r, cy - r), F(3, cx + r, cy + r), F(4, cx - r, cy + r)], false, Ms(ms));

    private static void Click(GestureRecognizer rec, double landMs, double liftMs, double x = 500, double y = 500)
    {
        rec.Process([F(1, x, y)], false, Ms(landMs));
        rec.Process([], false, Ms(liftMs));
    }

    // --- Held two-finger tap ---

    [Fact]
    public void A_third_landing_in_the_next_sequence_emits_the_held_tap_before_its_touch_down()
    {
        var rec = Rec();
        Two(rec, 0, 400, 500, 600, 500);
        rec.Process([], false, Ms(50)); // a tap, held for a double tap
        rec.Process([F(3, 400, 500), F(4, 600, 500)], false, Ms(100));
        rec.Process([F(4, 600, 500)], false, Ms(120));
        rec.Process([F(4, 600, 500), F(5, 400, 500)], false, Ms(140)); // the sequence's third landing: no double tap

        Assert.Equal(
            [(GestureKind.TouchDown, Ms(0)), (GestureKind.TouchDown, Ms(100)), (GestureKind.Tap, Ms(140)), (GestureKind.TouchDown, Ms(140))],
            _sink.Gestures.Select(g => (g.Kind, g.TimestampUs)));
    }

    [Fact]
    public void A_withdrawn_touch_emits_the_held_tap_at_once()
    {
        var rec = Rec();
        Two(rec, 0, 400, 500, 600, 500);
        rec.Process([], false, Ms(50));
        rec.Process([W(3, 500, 500)], false, Ms(100));
        rec.Process([], false, Ms(400));
        rec.CheckDeadline(Ms(1000));

        GestureEvent tap = Assert.Single(Gestures);
        Assert.Equal((GestureKind.Tap, Ms(100)), (tap.Kind, tap.TimestampUs));
    }

    // --- Pen entering ---

    [Fact]
    public void Two_fingers_down_when_the_pen_enters_are_no_tap()
    {
        var rec = Rec(new GestureRecognizerOptions { DoubleTapWindow = TimeSpan.Zero });
        Two(rec, 0, 400, 500, 600, 500);
        rec.PenEntering(Ms(10));
        rec.Process([], false, Ms(30));
        Assert.Empty(Gestures);
    }

    [Fact]
    public void Fingers_down_when_the_pen_enters_stay_blocked_after_it_leaves()
    {
        var rec = Rec(Linear);
        Two(rec, 0, 400, 400, 600, 400);
        rec.PenEntering(Ms(10));
        Two(rec, 20, 400, 400, 600, 400);
        Two(rec, 30, 400, 460, 600, 460);
        Two(rec, 40, 400, 520, 600, 520);
        Assert.Empty(Gestures);
    }

    [Fact]
    public void The_pen_entering_with_no_finger_down_blocks_nothing()
    {
        var rec = Rec(Linear);
        rec.PenEntering(Ms(0));
        Two(rec, 10, 400, 400, 600, 400);
        Two(rec, 20, 400, 450, 600, 450);

        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal((GestureKind.Scroll, GesturePhase.Began), (began.Kind, began.Phase));
    }

    // --- Withdrawn touches ---

    [Fact]
    public void A_withdrawn_touch_forgets_the_click_before_it()
    {
        var rec = Rec(Cursor);
        Click(rec, 0, 50);
        rec.Process([W(5, 500, 500)], false, Ms(100));
        rec.Process([], false, Ms(120));
        Click(rec, 150, 200);

        Assert.Equal([1, 1], Gestures.Where(g => g.Kind == GestureKind.Click).Select(g => g.ClickCount));
    }

    [Fact]
    public void Withdrawn_touches_gone_without_an_empty_frame_release_the_block()
    {
        var rec = Rec(Linear);
        Two(rec, 0, 400, 500, 600, 500);
        rec.Process([F(1, 400, 500), W(2, 600, 500)], false, Ms(10)); // finger 1 was present then: blocked with it
        rec.Process([F(1, 400, 500), F(3, 600, 500)], false, Ms(20));
        rec.Process([F(1, 400, 520), F(3, 600, 520)], false, Ms(25));
        Assert.Empty(Gestures);

        rec.Process([F(3, 600, 520), F(4, 400, 520)], false, Ms(30)); // finger 1 lifted: every touch of then is gone
        rec.Process([F(3, 600, 570), F(4, 400, 570)], false, Ms(40));

        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal((GestureKind.Scroll, GesturePhase.Began), (began.Kind, began.Phase));
        Assert.Equal(50, began.DeltaY, 0.1);
    }

    // --- Baseline and finishing ---

    [Fact]
    public void A_finger_replaced_in_one_frame_restarts_the_measurement_from_the_new_pair()
    {
        var rec = Rec(Linear);
        Two(rec, 0, 400, 400, 600, 400);
        rec.Process([F(1, 400, 400), F(3, 600, 400)], false, Ms(10)); // finger 2 lifted, finger 3 landed
        rec.Process([F(1, 400, 450), F(3, 600, 450)], false, Ms(20));

        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal((GestureKind.Scroll, GesturePhase.Began), (began.Kind, began.Phase));
        Assert.Equal(50, began.DeltaY, 0.1);
    }

    [Fact]
    public void After_a_gesture_ends_with_a_finger_left_nothing_new_starts_until_all_lift()
    {
        var rec = Rec(Linear);
        Two(rec, 0, 400, 400, 600, 400);
        Two(rec, 10, 400, 450, 600, 450);
        rec.Process([F(1, 400, 450)], false, Ms(20)); // Ended
        rec.Process([F(1, 400, 450), F(3, 600, 450)], false, Ms(30));
        rec.Process([F(1, 400, 520), F(3, 600, 520)], false, Ms(40));
        rec.Process([F(1, 400, 600), F(3, 600, 600)], false, Ms(50));

        Assert.Equal([(GestureKind.Scroll, GesturePhase.Began), (GestureKind.Scroll, GesturePhase.Ended)], Kinds());
    }

    // --- Two fingers: values ---

    [Fact]
    public void Rotate_off_a_spread_that_also_turns_carries_no_rotation()
    {
        var rec = Rec();
        rec.RotateEnabled = false;
        Two(rec, 0, 400, 500, 600, 500);
        Two(rec, 10, 350, 490, 650, 510); // spread 200 → 300.7 pt, turned 3.8°
        Two(rec, 20, 340, 510, 660, 490); // turned the other way and spread a little more

        Assert.Equal([(GestureKind.Pinch, GesturePhase.Began), (GestureKind.Pinch, GesturePhase.Changed)], Kinds());
        Assert.Equal(Math.Sqrt((300 * 300) + (20 * 20)) / 200, Gestures[0].ScaleDelta, 3);
        Assert.All(Gestures, g => Assert.Equal(0, g.RotationDelta));
    }

    [Fact]
    public void A_spread_change_equal_to_the_travel_is_a_pinch()
    {
        AssertExact(17000, 20000, 30000, 33000, 36000);
        var rec = new GestureRecognizer(_sink, Linear) { Mapper = Exact };
        rec.Process([U(1, 20000, 30000), U(2, 30000, 30000)], false, Ms(0));
        rec.Process([U(1, 17000, 36000), U(2, 33000, 36000)], false, Ms(10)); // spread +6000, centroid travel 6000

        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal((GestureKind.Pinch, GesturePhase.Began), (began.Kind, began.Phase));
        Assert.Equal(1.6, began.ScaleDelta, 9);
    }

    [Fact]
    public void A_straight_vertical_start_with_some_drift_locks_vertically()
    {
        var rec = Rec(Linear);
        Two(rec, 0, 400, 400, 600, 400);
        Two(rec, 10, 410, 450, 610, 450); // |dx| 10 ≤ 0.5 · |dy| 50
        Two(rec, 20, 420, 480, 620, 480);

        Assert.Equal([(GestureKind.Scroll, GesturePhase.Began), (GestureKind.Scroll, GesturePhase.Changed)], Kinds());
        Assert.All(Gestures, g => Assert.Equal(0, g.DeltaX));
        Assert.Equal(50, Gestures[0].DeltaY, 0.1);
        Assert.Equal(30, Gestures[1].DeltaY, 0.1);
    }

    [Fact]
    public void The_scroll_gain_multiplies_the_began_delta()
    {
        var rec = Rec(Linear with { ScrollGain = 2 });
        Two(rec, 0, 400, 400, 600, 400);
        Two(rec, 10, 450, 400, 650, 400);

        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal(100, began.DeltaX, 0.1);
        Assert.Equal(0, began.DeltaY);
    }

    // --- Four fingers ---

    [Fact]
    public void Four_fingers_below_both_thresholds_do_nothing()
    {
        var rec = Rec();
        Four(rec, 0);
        Four(rec, 10, 530); // travel 30 pt < 50 pt
        Four(rec, 20, r: 110); // back, spread +28 pt < 50 pt
        Assert.Empty(Gestures);
    }

    [Fact]
    public void A_swipe_or_system_pinch_emits_changed_only_when_something_changed()
    {
        var rec = Rec();
        Four(rec, 0);
        Four(rec, 10, 560);
        Four(rec, 20, 560); // the same frame again: nothing
        Four(rec, 30, 560, 530); // across the swipe: same progress, new center
        Assert.Equal([(GestureKind.SystemSwipe, GesturePhase.Began), (GestureKind.SystemSwipe, GesturePhase.Changed)], Kinds());
        Assert.Equal(0.06, Gestures[1].Progress, 3);
        Assert.Equal(530, Gestures[1].Center.Y, 0.1);

        rec.Process([], false, Ms(40));
        _sink.Clear();
        Four(rec, 1000);
        Four(rec, 1010, r: 150);
        Four(rec, 1020, r: 150);
        Four(rec, 1030, r: 170); // spreads around the same center
        Assert.Equal([(GestureKind.SystemPinch, GesturePhase.Began), (GestureKind.SystemPinch, GesturePhase.Changed)], Kinds());
        Assert.Equal(2 * Math.Sqrt(2) * 70 / 1000, Gestures[1].Progress, 3);
    }

    [Fact]
    public void Four_fingers_whose_spread_change_equals_the_travel_pinch()
    {
        AssertExact(23000, 25000, 27000, 30000, 34000, 35000, 37000, 41000);
        var rec = new GestureRecognizer(_sink) { Mapper = Exact };
        rec.Process(Diamond(30000, 30000, 5000), false, Ms(0));
        rec.Process(Diamond(30000, 34000, 7000), false, Ms(10)); // spread 10000 → 14000, travel 4000 (≥ 0.05 · 65535)

        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal((GestureKind.SystemPinch, GestureDirection.Out), (began.Kind, began.Direction));

        static TouchSample[] Diamond(int cx, int cy, int r) =>
            [U(1, cx - r, cy), U(2, cx + r, cy), U(3, cx, cy - r), U(4, cx, cy + r)];
    }

    // --- One finger ---

    [Fact]
    public void A_pointer_move_catches_up_from_where_the_finger_landed()
    {
        var rec = Rec(Cursor);
        rec.Process([F(1, 500, 500)], false, Ms(0));
        rec.Process([F(1, 520, 500)], false, Ms(200));

        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal((GestureKind.PointerMove, GesturePhase.Began), (began.Kind, began.Phase));
        Assert.Equal(20, began.DeltaX, 0.1);
        Assert.Equal(0, began.DeltaY, 0.1);
    }

    [Fact]
    public void A_pointer_after_a_horizontal_scroll_moves_on_both_axes()
    {
        var rec = Rec(Cursor);
        Two(rec, 0, 400, 400, 600, 400);
        Two(rec, 10, 450, 400, 650, 400); // locked horizontally
        rec.Process([], false, Ms(20));
        rec.Process([F(1, 500, 500)], false, Ms(500));
        rec.Process([F(1, 600, 500)], false, Ms(700));
        rec.Process([F(1, 620, 520)], false, Ms(710));

        GestureEvent changed = Gestures[^1];
        Assert.Equal((GestureKind.PointerMove, GesturePhase.Changed), (changed.Kind, changed.Phase));
        Assert.Equal(20, changed.DeltaX, 0.1);
        Assert.Equal(20, changed.DeltaY, 0.1);
    }

    [Fact]
    public void A_second_finger_starts_the_two_finger_decision_in_its_landing_frame()
    {
        var rec = Rec(Cursor);
        rec.Process([F(1, 400, 400)], false, Ms(0));
        rec.Process([F(1, 400, 400), F(2, 600, 400)], false, Ms(150));
        rec.Process([F(1, 400, 450), F(2, 600, 450)], false, Ms(160));

        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal((GestureKind.Scroll, GesturePhase.Began), (began.Kind, began.Phase));
        Assert.Equal(50, began.DeltaY, 0.1);
    }

    [Fact]
    public void A_tap_and_a_half_drag_forgets_the_click()
    {
        var rec = Rec(Cursor);
        Click(rec, 0, 50);
        rec.Process([F(1, 500, 500)], false, Ms(100));
        rec.Process([F(1, 560, 500)], false, Ms(150)); // PointerDrag
        rec.Process([], false, Ms(200));
        Click(rec, 250, 280);

        Assert.Equal(
            [(GestureKind.Click, GesturePhase.Ended), (GestureKind.PointerDrag, GesturePhase.Began), (GestureKind.PointerDrag, GesturePhase.Ended), (GestureKind.Click, GesturePhase.Ended)],
            Kinds());
        Assert.Equal([1, 1], Gestures.Where(g => g.Kind == GestureKind.Click).Select(g => g.ClickCount));
    }

    [Fact]
    public void A_press_lifted_after_the_tap_duration_forgets_the_click()
    {
        var rec = Rec(Cursor with { ClickWindow = TimeSpan.FromMilliseconds(1000) });
        Click(rec, 0, 50);
        Click(rec, 100, 450); // held 350 ms > 300 ms with no frame in between: no click, no drag
        Click(rec, 500, 530);

        Assert.Equal([1, 1], Gestures.Where(g => g.Kind == GestureKind.Click).Select(g => g.ClickCount));
        Assert.All(Gestures, g => Assert.Equal(GestureKind.Click, g.Kind));
    }

    [Fact]
    public void A_press_held_still_drags_without_catching_up_its_drift()
    {
        var rec = Rec(Cursor);
        Click(rec, 0, 50);
        rec.Process([F(1, 500, 500)], false, Ms(100));
        rec.Process([F(1, 500, 505)], false, Ms(300));
        rec.Process([F(1, 503, 506)], false, Ms(420)); // held 320 ms > 300 ms, within the 15 pt slop

        GestureEvent began = Gestures[^1];
        Assert.Equal((GestureKind.PointerDrag, GesturePhase.Began), (began.Kind, began.Phase));
        Assert.Equal((0.0, 0.0), (began.DeltaX, began.DeltaY));
    }

    private static void AssertExact(params int[] values)
    {
        foreach (int v in values)
        {
            Assert.Equal(v, Exact.MapGesture(v, v).X);
        }
    }
}
