using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// Two-finger tap rules (ADR-0023): timing, slop, landings, pen and cancel. The double-tap window is off here so a tap
/// is emitted on its lift; GestureDoubleTapTests covers holding it.
/// </summary>
public class GestureTapTests
{
    // Square 1000x1000 surface onto a 1000x1000 display: tap slop 2% = 20 pt.
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));
    private readonly RecordingSink _sink = new();

    /// <summary>Recorded gestures without the TouchDown landing events (covered by GestureTouchDownTests).</summary>
    private List<GestureEvent> Gestures => _sink.Gestures.Where(g => g.Kind != GestureKind.TouchDown).ToList();
    private static readonly GestureRecognizerOptions Immediate = new() { DoubleTapWindow = TimeSpan.Zero };
    private readonly GestureRecognizer _rec;

    public GestureTapTests()
    {
        _rec = new GestureRecognizer(_sink, Immediate) { Mapper = Square };
    }

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    private static TouchSample F(byte id, double x, double y) => new(id, true, N(x), N(y));

    private static long Ms(double ms) => (long)(ms * 1000);

    private void Frame(double ms, bool pen = false, params TouchSample[] touches) => _rec.Process(touches, pen, Ms(ms));

    private void Lift(double ms) => Frame(ms);

    private GestureEvent[] Taps() => Gestures.Where(g => g.Kind == GestureKind.Tap).ToArray();

    [Fact]
    public void Two_fingers_landing_and_lifting_quickly_tap_at_their_midpoint()
    {
        Frame(0, false, F(1, 400, 500), F(2, 600, 520));
        Frame(40, false, F(1, 402, 501), F(2, 601, 521));
        Lift(80);

        GestureEvent tap = Assert.Single(Gestures);
        Assert.Equal(GestureKind.Tap, tap.Kind);
        Assert.Equal(GesturePhase.Ended, tap.Phase);
        Assert.Equal(500, tap.Center.X, 0.1);
        Assert.Equal(510, tap.Center.Y, 0.1);
        Assert.Equal(Ms(80), tap.TimestampUs);
        Assert.False(tap.Synthetic);
        Assert.False(_rec.IsActive);
    }

    [Fact]
    public void Staggered_landing_and_lifting_still_taps()
    {
        Frame(0, false, F(1, 400, 500));
        Frame(30, false, F(1, 400, 500), F(2, 600, 500));
        Frame(100, false, F(2, 600, 500));
        Lift(150);
        Assert.Single(Taps());
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    [InlineData(400, false)]
    public void Duration_is_measured_from_the_first_landing_to_the_last_lift(double liftMs, bool taps)
    {
        Frame(0, false, F(1, 400, 500));
        Frame(150, false, F(1, 400, 500), F(2, 600, 500)); // second finger only 50 ms before the lift
        Lift(liftMs);
        Assert.Equal(taps ? 1 : 0, Taps().Length);
    }

    [Fact]
    public void A_held_pair_is_no_tap_even_without_a_lift_frame_in_time()
    {
        Frame(0, false, F(1, 400, 500), F(2, 600, 500));
        Frame(250, false, F(1, 400, 500), F(2, 600, 500));
        Lift(260);
        Assert.Empty(Gestures);
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(25, false)]
    public void Each_finger_may_move_at_most_the_tap_slop(double move, bool taps)
    {
        Frame(0, false, F(1, 400, 500), F(2, 600, 500));
        Frame(40, false, F(1, 400, 500), F(2, 600 + move, 500)); // spread change below the pinch threshold
        Lift(80);
        Assert.Equal(taps ? 1 : 0, Taps().Length);
    }

    [Fact]
    public void Slop_counts_from_where_each_finger_landed_even_after_the_other_lifted()
    {
        Frame(0, false, F(1, 400, 500), F(2, 600, 500));
        Frame(40, false, F(2, 600, 500));
        Frame(60, false, F(2, 600, 530));
        Lift(80);
        Assert.Empty(Gestures);
    }

    [Fact]
    public void Tap_slop_scales_with_the_mapped_short_side()
    {
        var rec = new GestureRecognizer(_sink, Immediate) { Mapper = new CoordinateMapper(1000, 1000, new DisplayRect(0, 0, 2000, 2000)) };
        rec.Process([new TouchSample(1, true, 20000, 30000), new TouchSample(2, true, 40000, 30000)], false, 0);
        rec.Process([new TouchSample(1, true, 20000, 30000), new TouchSample(2, true, 40000, 30983)], false, Ms(40)); // ~30 pt < 40
        rec.Process([], false, Ms(80));
        Assert.Single(Taps());
    }

    [Fact]
    public void Single_finger_tap_is_ignored()
    {
        Frame(0, false, F(1, 400, 500));
        Lift(50);
        Assert.Empty(Gestures);
    }

    [Fact]
    public void Three_fingers_are_no_tap()
    {
        Frame(0, false, F(1, 400, 500), F(2, 600, 500), F(3, 500, 300));
        Lift(50);
        Assert.Empty(Gestures);
    }

    [Fact]
    public void A_third_finger_landing_after_one_lifted_is_no_tap()
    {
        Frame(0, false, F(1, 400, 500), F(2, 600, 500));
        Frame(30, false, F(2, 600, 500));
        Frame(60, false, F(2, 600, 500), F(3, 400, 500));
        Lift(90);
        Assert.Empty(Taps());
    }

    [Fact]
    public void A_started_gesture_is_no_tap()
    {
        var rec = new GestureRecognizer(_sink, Immediate with { ScrollThreshold = 0.01 }) { Mapper = Square };
        rec.Process([F(1, 400, 500), F(2, 600, 500)], false, 0);
        rec.Process([F(1, 400, 515), F(2, 600, 515)], false, Ms(30)); // 15 pt: a scroll, but within the tap slop
        rec.Process([], false, Ms(60));
        Assert.Equal([GesturePhase.Began, GesturePhase.Ended], Gestures.Select(g => g.Phase));
        Assert.Empty(Taps());
    }

    [Fact]
    public void Pen_in_range_during_the_tap_blocks_it()
    {
        Frame(0, false, F(1, 400, 500), F(2, 600, 500));
        Frame(30, true, F(1, 400, 500), F(2, 600, 500));
        Frame(60, false, F(1, 400, 500), F(2, 600, 500));
        Lift(90);
        Assert.Empty(Gestures);
    }

    [Fact]
    public void Pen_in_range_on_the_lift_frame_blocks_it()
    {
        Frame(0, false, F(1, 400, 500), F(2, 600, 500));
        Frame(60, true);
        Assert.Empty(Gestures);
    }

    [Fact]
    public void Fingers_landing_while_the_pen_is_in_range_are_no_tap()
    {
        Frame(0, true, F(1, 400, 500), F(2, 600, 500));
        Frame(30, false, F(1, 400, 500), F(2, 600, 500));
        Lift(60);
        Assert.Empty(Gestures);
    }

    [Fact]
    public void Cancel_drops_a_pending_tap_and_fingers_still_down_afterwards()
    {
        Frame(0, false, F(1, 400, 500), F(2, 600, 500));
        _rec.Cancel(Ms(20));
        Frame(40, false, F(1, 400, 500), F(2, 600, 500));
        Lift(60);
        Assert.Empty(Gestures);

        // The next touch sequence taps normally.
        Frame(100, false, F(1, 400, 500), F(2, 600, 500));
        Lift(150);
        Assert.Single(Taps());
    }

    [Fact]
    public void Cancel_without_fingers_down_does_not_block_the_next_tap()
    {
        _rec.Cancel(0);
        Frame(10, false, F(1, 400, 500), F(2, 600, 500));
        Lift(60);
        Assert.Single(Taps());
    }

    [Fact]
    public void Tap_duration_and_slop_are_configurable()
    {
        var rec = new GestureRecognizer(_sink, Immediate with { TapMaxDuration = TimeSpan.FromMilliseconds(400), TapSlop = 0.03 })
        {
            Mapper = Square,
        };
        rec.Process([F(1, 400, 500), F(2, 600, 500)], false, 0);
        rec.Process([F(1, 400, 500), F(2, 625, 500)], false, Ms(100));
        rec.Process([], false, Ms(350));
        Assert.Single(Taps());
    }

    [Fact]
    public void Consecutive_taps_each_emit_one_event()
    {
        for (int i = 0; i < 3; i++)
        {
            Frame(i * 300, false, F(1, 400, 500), F(2, 600, 500));
            Lift((i * 300) + 50);
        }

        Assert.Equal(3, Taps().Length);
    }

    [Fact]
    public void Fingers_landing_while_still_pen_blocked_are_no_tap()
    {
        Frame(0, true); // pen in range without fingers: blocked until a pen-free frame with no contacts
        Frame(30, false, F(1, 400, 500), F(2, 600, 500));
        Lift(60);
        Assert.Empty(_sink.Gestures);
    }

    [Fact]
    public void A_finger_re_landing_with_a_reused_pointer_id_is_no_tap()
    {
        Frame(0, false, F(1, 400, 500), F(2, 600, 500));
        Frame(30, false, F(2, 600, 500));
        Frame(60, false, F(2, 600, 500), F(1, 400, 500)); // Android reuses the lifted finger's id
        Lift(90);
        Assert.Empty(Taps());
    }
}
