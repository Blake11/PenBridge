using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

public class GestureRecognizerTests
{
    // Square 1000x1000 surface onto a 1000x1000 display: 1 display point ~= 65.535 normalized units.
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));
    private readonly RecordingSink _sink = new();

    /// <summary>Recorded gestures without the TouchDown landing events (covered by GestureTouchDownTests).</summary>
    private List<GestureEvent> Gestures => _sink.Gestures.Where(g => g.Kind != GestureKind.TouchDown).ToList();
    private readonly GestureRecognizer _rec;

    public GestureRecognizerTests()
    {
        _rec = new GestureRecognizer(_sink) { Mapper = Square };
    }

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    private void Two(double ax, double ay, double bx, double by, bool pen = false) =>
        _rec.Process([new TouchSample(1, true, N(ax), N(ay)), new TouchSample(2, true, N(bx), N(by))], pen, 0);

    private GesturePhase[] Phases() => Gestures.Select(g => g.Phase).ToArray();

    [Fact]
    public void Small_movements_below_threshold_emit_nothing()
    {
        Two(400, 500, 600, 500);
        Two(410, 510, 610, 510); // ~14 pt travel < 40 pt threshold (4% of 1000)
        Two(395, 500, 605, 500); // spread +10 pt < 40
        Assert.Empty(Gestures);
        Assert.False(_rec.IsActive);
    }

    [Fact]
    public void Two_finger_pan_is_scroll_began_changed_ended()
    {
        Two(400, 500, 600, 500);
        Two(400, 550, 600, 550);
        Two(400, 570, 600, 570);
        _rec.Process([new TouchSample(1, true, N(400), N(570))], false, 0);

        Assert.Equal([GesturePhase.Began, GesturePhase.Changed, GesturePhase.Ended], Phases());
        Assert.All(Gestures, g => Assert.Equal(GestureKind.Scroll, g.Kind));
        Assert.Equal(50, Gestures[0].DeltaY, 0.1); // Began carries travel since landing
        Assert.Equal(20, Gestures[1].DeltaY, 0.1);
        Assert.Equal(0, Gestures[1].DeltaX, 0.1);
        Assert.Equal(500, Gestures[1].Center.X, 0.1);
        Assert.Equal(0, Gestures[2].DeltaY);
    }

    [Fact]
    public void Spreading_fingers_is_pinch_with_multiplicative_scale()
    {
        Two(400, 500, 600, 500); // spread 200
        Two(350, 500, 650, 500); // spread 300
        Two(300, 500, 700, 500); // spread 400
        _rec.Process([], false, 0);

        Assert.Equal([GesturePhase.Began, GesturePhase.Changed, GesturePhase.Ended], Phases());
        Assert.All(Gestures, g => Assert.Equal(GestureKind.Pinch, g.Kind));
        Assert.Equal(1.5, Gestures[0].ScaleDelta, 0.01);
        Assert.Equal(400.0 / 300, Gestures[1].ScaleDelta, 0.01);
        Assert.Equal(500, Gestures[1].Center.X, 0.1);
        Assert.Equal(500, Gestures[1].Center.Y, 0.1);
        Assert.Equal(1, Gestures[2].ScaleDelta);
    }

    [Fact]
    public void Pinching_in_scales_below_one()
    {
        Two(300, 500, 700, 500);
        Two(350, 500, 650, 500);
        Assert.Equal(0.75, Assert.Single(Gestures).ScaleDelta, 0.01);
    }

    [Fact]
    public void Decision_locks_scroll_even_if_fingers_later_spread()
    {
        Two(400, 500, 600, 500);
        Two(400, 560, 600, 560); // scroll chosen
        Two(300, 560, 700, 560); // pure spread afterwards
        Assert.All(Gestures, g => Assert.Equal(GestureKind.Scroll, g.Kind));
        Assert.Equal(GesturePhase.Changed, Gestures[^1].Phase);
    }

    [Fact]
    public void Decision_locks_pinch_even_if_fingers_later_pan()
    {
        Two(400, 500, 600, 500);
        Two(350, 500, 650, 500); // pinch chosen
        Two(350, 700, 650, 700); // pure pan afterwards
        Assert.All(Gestures, g => Assert.Equal(GestureKind.Pinch, g.Kind));
        GestureEvent changed = Gestures[^1];
        Assert.Equal(GesturePhase.Changed, changed.Phase);
        Assert.Equal(1, changed.ScaleDelta, 0.001);
        Assert.Equal(700, changed.Center.Y, 0.1);
    }

    [Fact]
    public void Mixed_motion_picks_the_dominant_component()
    {
        // Travel 50, spread change 20 -> scroll.
        Two(400, 500, 600, 500);
        Two(390, 550, 610, 550);
        Assert.Equal(GestureKind.Scroll, Assert.Single(Gestures).Kind);

        _sink.Clear();
        _rec.Process([], false, 0);
        _sink.Clear();

        // Travel 20, spread change 60 -> pinch.
        Two(400, 500, 600, 500);
        Two(370, 520, 630, 520);
        Assert.Equal(GestureKind.Pinch, Assert.Single(Gestures).Kind);
    }

    [Fact]
    public void Thresholds_scale_with_mapped_content_short_side()
    {
        // 4% of a 2000 pt short side is 80 pt, so a 50 pt pan is still undecided.
        _rec.Mapper = new CoordinateMapper(1000, 1000, new DisplayRect(0, 0, 2000, 2000));
        _rec.Process([new TouchSample(1, true, 20000, 20000), new TouchSample(2, true, 30000, 20000)], false, 0);
        _rec.Process([new TouchSample(1, true, 20000, 21638), new TouchSample(2, true, 30000, 21638)], false, 0); // ~50 pt
        Assert.Empty(Gestures);
    }

    [Fact]
    public void Thresholds_are_configurable()
    {
        var rec = new GestureRecognizer(_sink, new GestureRecognizerOptions { ScrollThreshold = 0.005 }) { Mapper = Square };
        rec.Process([new TouchSample(1, true, N(400), N(500)), new TouchSample(2, true, N(600), N(500))], false, 0);
        rec.Process([new TouchSample(1, true, N(400), N(510)), new TouchSample(2, true, N(600), N(510))], false, 0);
        Assert.Equal(GestureKind.Scroll, Assert.Single(Gestures).Kind);
    }

    [Fact]
    public void Pen_coming_into_range_cancels_and_blocks_until_fingers_lift()
    {
        Two(400, 500, 600, 500);
        Two(400, 560, 600, 560);
        Two(400, 580, 600, 580, pen: true);
        Assert.Equal([GesturePhase.Began, GesturePhase.Cancelled], Phases());
        Assert.False(_rec.IsActive);

        // Pen gone but fingers still down: no new gesture.
        Two(400, 700, 600, 700);
        Two(400, 900, 600, 900);
        Assert.Equal(2, Gestures.Count);

        _rec.Process([], false, 0);
        Two(400, 500, 600, 500);
        Two(400, 560, 600, 560);
        Assert.Equal(GesturePhase.Began, Gestures[^1].Phase);
    }

    [Fact]
    public void Pen_during_undecided_gesture_emits_nothing()
    {
        Two(400, 500, 600, 500);
        Two(400, 510, 600, 510, pen: true);
        Assert.Empty(Gestures);
    }

    [Fact]
    public void Third_finger_is_ignored_and_lifting_a_tracked_finger_ends()
    {
        Two(400, 500, 600, 500);
        _rec.Process([new TouchSample(1, true, N(400), N(560)), new TouchSample(2, true, N(600), N(560)), new TouchSample(3, true, N(100), N(100))], false, 0);
        Assert.Equal(GestureKind.Scroll, Assert.Single(Gestures).Kind);
        Assert.Equal(60, Gestures[0].DeltaY, 0.1);

        // Finger 2 lifts; 1 and 3 remain -> Ended, then a new undecided gesture on (1, 3).
        _rec.Process([new TouchSample(1, true, N(400), N(560)), new TouchSample(3, true, N(100), N(100))], false, 0);
        Assert.Equal(GesturePhase.Ended, Gestures[^1].Phase);
        Assert.False(_rec.IsActive);
        Assert.Equal(2, _rec.ContactCount);
    }

    [Fact]
    public void Tracks_first_two_contacts_by_landing_order_not_span_order()
    {
        _rec.Process([new TouchSample(5, true, N(400), N(500))], false, 0);
        // Fingers 2 and 9 land together after 5; span order puts 2 and 9 first.
        _rec.Process([new TouchSample(2, true, N(600), N(500)), new TouchSample(9, true, N(900), N(900)), new TouchSample(5, true, N(400), N(500))], false, 0);
        _rec.Process([new TouchSample(2, true, N(600), N(560)), new TouchSample(9, true, N(900), N(900)), new TouchSample(5, true, N(400), N(560))], false, 0);

        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal(GestureKind.Scroll, began.Kind);
        Assert.Equal(60, began.DeltaY, 0.1);
        Assert.Equal(500, began.Center.X, 0.1);
    }

    [Fact]
    public void Unconfigured_mapper_ignores_frames()
    {
        var rec = new GestureRecognizer(_sink);
        rec.Process([new TouchSample(1, true, 0, 0), new TouchSample(2, true, 9000, 0)], false, 0);
        rec.Process([new TouchSample(1, true, 0, 30000), new TouchSample(2, true, 9000, 30000)], false, 0);
        Assert.Empty(Gestures);
        Assert.Equal(0, rec.ContactCount);
    }

    [Fact]
    public void Non_contact_touches_do_not_count()
    {
        _rec.Process([new TouchSample(1, true, 1, 1), new TouchSample(2, false, 9, 9)], false, 0);
        Assert.Equal(1, _rec.ContactCount);
    }

    [Fact]
    public void Keepalive_frames_without_motion_emit_nothing()
    {
        Two(400, 500, 600, 500);
        Two(400, 560, 600, 560);
        Two(400, 560, 600, 560);
        Two(400, 560, 600, 560);
        Assert.Single(Gestures);
    }

    [Fact]
    public void Cancel_without_active_gesture_emits_nothing()
    {
        Two(400, 500, 600, 500);
        _rec.Cancel(0);
        Assert.Empty(Gestures);
        Assert.Equal(0, _rec.ContactCount);
    }
}
