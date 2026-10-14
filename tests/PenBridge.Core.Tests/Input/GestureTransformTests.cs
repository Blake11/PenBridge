using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>Pinch/rotate transform, rotation unwrap and scroll gain (ADR-0023).</summary>
public class GestureTransformTests
{
    // Square 1000x1000 surface onto a 1000x1000 display: thresholds are 4% = 40 pt.
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));
    private readonly RecordingSink _sink = new();
    private GestureRecognizer _rec;

    public GestureTransformTests()
    {
        _rec = new GestureRecognizer(_sink) { Mapper = Square };
    }

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    private void Two(double ax, double ay, double bx, double by, long t = 0) =>
        _rec.Process([new TouchSample(1, true, N(ax), N(ay)), new TouchSample(2, true, N(bx), N(by))], false, t);

    /// <summary>Finger 1 and 2 on a line through (cx, cy) at <paramref name="degrees"/> (finger 1 → finger 2, y down).</summary>
    private void Angle(double degrees, double radius = 100, double cx = 500, double cy = 500)
    {
        double a = degrees * Math.PI / 180;
        double ox = radius * Math.Cos(a);
        double oy = radius * Math.Sin(a);
        Two(cx - ox, cy - oy, cx + ox, cy + oy);
    }

    private void Lift() => _rec.Process([], false, 0);

    [Fact]
    public void Rotation_alone_starts_a_transform_with_clockwise_positive_degrees()
    {
        Angle(0);
        Angle(30); // arc = 0.52 rad * 100 = 52 pt >= 40, spread and travel ~0
        GestureEvent began = Assert.Single(_sink.Gestures);
        Assert.Equal(GestureKind.Pinch, began.Kind);
        Assert.Equal(GesturePhase.Began, began.Phase);
        Assert.Equal(30, began.RotationDelta, 0.5);
        Assert.Equal(1, began.ScaleDelta, 0.01);
        Assert.Equal(0, began.DeltaX);
        Assert.Equal(0, began.DeltaY);
    }

    [Fact]
    public void Counterclockwise_turn_is_negative()
    {
        Angle(0);
        Angle(-30);
        Assert.Equal(-30, Assert.Single(_sink.Gestures).RotationDelta, 0.5);
    }

    [Fact]
    public void Clockwise_on_screen_means_the_right_finger_moves_down()
    {
        Two(400, 500, 600, 500);
        Two(400, 440, 600, 560); // right finger down, left finger up: clockwise with y down
        Assert.True(Assert.Single(_sink.Gestures).RotationDelta > 0);
    }

    [Fact]
    public void Rotation_below_the_threshold_emits_nothing()
    {
        Angle(0);
        Angle(15); // arc 26 pt < 40
        Assert.Empty(_sink.Gestures);
    }

    [Fact]
    public void Rotation_arc_uses_half_the_spread()
    {
        // 15 degrees at radius 200 (spread 400): arc = 0.26 * 200 = 52 pt -> transform.
        Angle(0, radius: 200);
        Angle(15, radius: 200);
        Assert.Equal(GestureKind.Pinch, Assert.Single(_sink.Gestures).Kind);
    }

    [Fact]
    public void Rotation_threshold_is_configurable()
    {
        _rec = new GestureRecognizer(_sink, new GestureRecognizerOptions { RotationThreshold = 0.02 }) { Mapper = Square };
        Angle(0);
        Angle(15); // arc 26 pt >= 20
        Assert.Equal(15, Assert.Single(_sink.Gestures).RotationDelta, 0.5);
    }

    [Fact]
    public void Rotation_crossing_its_threshold_still_loses_to_larger_travel()
    {
        Angle(0);
        Angle(30, cx: 500, cy: 570); // arc 52, travel 70 -> scroll
        Assert.Equal(GestureKind.Scroll, Assert.Single(_sink.Gestures).Kind);
    }

    [Fact]
    public void Rotation_that_exceeds_travel_wins_over_a_crossed_scroll_threshold()
    {
        Angle(0);
        Angle(40, cx: 500, cy: 545); // arc 70, travel 45 -> transform
        Assert.Equal(GestureKind.Pinch, Assert.Single(_sink.Gestures).Kind);
    }

    [Fact]
    public void Transform_events_carry_scale_and_rotation_together()
    {
        Angle(0, radius: 100);
        Angle(0, radius: 150); // pinch starts on spread
        Angle(20, radius: 180);
        Lift();

        Assert.Equal([GesturePhase.Began, GesturePhase.Changed, GesturePhase.Ended], _sink.Gestures.Select(g => g.Phase));
        Assert.All(_sink.Gestures, g => Assert.Equal(GestureKind.Pinch, g.Kind));
        Assert.Equal(1.5, _sink.Gestures[0].ScaleDelta, 0.01);
        Assert.Equal(0, _sink.Gestures[0].RotationDelta, 0.01);
        Assert.Equal(1.2, _sink.Gestures[1].ScaleDelta, 0.01);
        Assert.Equal(20, _sink.Gestures[1].RotationDelta, 0.3);
        Assert.Equal(0, _sink.Gestures[2].RotationDelta);
        Assert.Equal(1, _sink.Gestures[2].ScaleDelta);
    }

    [Fact]
    public void Began_rotation_is_the_angle_since_landing()
    {
        Angle(0);
        Angle(10);
        Angle(20);
        Angle(35);
        GestureEvent began = Assert.Single(_sink.Gestures);
        Assert.Equal(35, began.RotationDelta, 0.5);
    }

    [Fact]
    public void Rotation_is_unwrapped_across_180_degrees()
    {
        Angle(160);
        for (int d = 170; d <= 260; d += 10)
        {
            Angle(d); // atan2 wraps from +180 to -180 between 180 and 190
        }

        Assert.All(_sink.Gestures, g => Assert.InRange(g.RotationDelta, 0, 35));
        double total = _sink.Gestures.Sum(g => g.RotationDelta);
        Assert.Equal(100, total, 0.5);
    }

    [Fact]
    public void Rotation_accumulated_before_the_decision_is_unwrapped_too()
    {
        Angle(-175);
        Angle(170); // -15 degrees through the wrap: still below the threshold
        Assert.Empty(_sink.Gestures);
        Angle(150);
        Assert.Equal(-35, Assert.Single(_sink.Gestures).RotationDelta, 0.5);
    }

    [Fact]
    public void Half_turns_beyond_180_keep_accumulating()
    {
        Angle(0);
        for (int d = 20; d <= 400; d += 20)
        {
            Angle(d);
        }

        Assert.Equal(400, _sink.Gestures.Sum(g => g.RotationDelta), 0.5);
    }

    [Fact]
    public void Scroll_stays_exclusive_of_rotation_and_scale()
    {
        Two(400, 500, 600, 500);
        Two(400, 560, 600, 560); // scroll
        Angle(45, radius: 150, cy: 560);
        Assert.All(_sink.Gestures, g =>
        {
            Assert.Equal(GestureKind.Scroll, g.Kind);
            Assert.Equal(0, g.RotationDelta);
            Assert.Equal(1, g.ScaleDelta);
        });
    }

    [Fact]
    public void Transform_without_change_emits_nothing()
    {
        Angle(0);
        Angle(30);
        Angle(30);
        Angle(30);
        Assert.Single(_sink.Gestures);
    }

    [Fact]
    public void Pen_cancels_a_transform()
    {
        Angle(0);
        Angle(30);
        _rec.Process([new TouchSample(1, true, N(400), N(500)), new TouchSample(2, true, N(600), N(500))], true, 0);
        Assert.Equal([GesturePhase.Began, GesturePhase.Cancelled], _sink.Gestures.Select(g => g.Phase));
        Assert.Equal(GestureKind.Pinch, _sink.Gestures[1].Kind);
        Assert.Equal(0, _sink.Gestures[1].RotationDelta);
        Assert.Equal(1, _sink.Gestures[1].ScaleDelta);
    }

    [Fact]
    public void Scroll_gain_multiplies_scroll_deltas_but_not_thresholds_or_center()
    {
        _rec = new GestureRecognizer(_sink, new GestureRecognizerOptions { ScrollGain = 2 }) { Mapper = Square };
        Two(400, 500, 600, 500);
        Two(400, 530, 600, 530); // 30 pt: below 40 even with gain 2
        Assert.Empty(_sink.Gestures);
        Two(400, 550, 600, 550);
        Two(400, 570, 600, 570);
        Assert.Equal(100, _sink.Gestures[0].DeltaY, 0.1);
        Assert.Equal(40, _sink.Gestures[1].DeltaY, 0.1);
        Assert.Equal(570, _sink.Gestures[1].Center.Y, 0.1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_scroll_gain_throws(double gain) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new GestureRecognizer(_sink, new GestureRecognizerOptions { ScrollGain = gain }));

    [Fact]
    public void Negative_threshold_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new GestureRecognizer(_sink, new GestureRecognizerOptions { RotationThreshold = -0.1 }));

    [Fact]
    public void Transform_tap_and_scroll_do_not_allocate_per_frame()
    {
        var sink = new CountingSink();
        var rec = new GestureRecognizer(sink) { Mapper = Square };
        var two = new TouchSample[2];
        var none = Array.Empty<TouchSample>();
        long t = 0;
        Alloc.AssertZero(4_000, i =>
        {
            int phase = i % 40;
            t += 8_000;
            if (phase < 10)
            {
                double a = phase * 12 * Math.PI / 180; // rotate in 12 degree steps, then lift
                two[0] = new TouchSample(1, true, N(500 - (100 * Math.Cos(a))), N(500 - (100 * Math.Sin(a))));
                two[1] = new TouchSample(2, true, N(500 + (100 * Math.Cos(a))), N(500 + (100 * Math.Sin(a))));
                rec.Process(two, false, t);
            }
            else if (phase is 10 or 13 or 26 or 32 or 39)
            {
                rec.Process(none, false, t);
            }
            else if (phase is 11 or 12)
            {
                two[0] = new TouchSample(1, true, N(400), N(500)); // tap
                two[1] = new TouchSample(2, true, N(600), N(500));
                rec.Process(two, false, t);
            }
            else if (phase < 25)
            {
                two[0] = new TouchSample(1, true, N(400), N(300 + (phase * 20))); // scroll
                two[1] = new TouchSample(2, true, N(600), N(300 + (phase * 20)));
                rec.Process(two, false, t);
            }
            else if (phase == 25)
            {
                rec.Process(two, true, t); // pen cancels the scroll
            }
            else if (phase < 31)
            {
                two[0] = new TouchSample(1, true, N(400 - (phase * 10)), N(500)); // pinch
                two[1] = new TouchSample(2, true, N(600 + (phase * 10)), N(500));
                rec.Process(two, false, t);
            }
            else if (phase == 31)
            {
                rec.Cancel(t);
            }
            else
            {
                two[0] = new TouchSample(1, true, N(400), N(500));
                two[1] = new TouchSample(2, true, N(600), N(500 + phase));
                rec.Process(two, false, t);
            }
        }, () => sink.Transforms, () => sink.Taps, () => sink.Scrolls);
    }

    private sealed class CountingSink : IInputSink
    {
        public long Transforms { get; private set; }

        public long Taps { get; private set; }

        public long Scrolls { get; private set; }

        public void OnPenEvent(in PenEvent e)
        {
        }

        public void OnGestureEvent(in GestureEvent e)
        {
            switch (e.Kind)
            {
                case GestureKind.Pinch:
                    Transforms++;
                    break;
                case GestureKind.Tap:
                    Taps++;
                    break;
                default:
                    Scrolls++;
                    break;
            }
        }
    }
}
