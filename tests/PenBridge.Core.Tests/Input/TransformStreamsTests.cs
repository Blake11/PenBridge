using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>Magnify/rotate stream split of a two-finger transform: dominance picks the first stream (#214).</summary>
public class TransformStreamsTests
{
    private readonly TransformStreams _streams = new();

    [Fact]
    public void Small_steps_are_held_back()
    {
        Assert.Equal(default, _streams.Step(0.01, 0.5));
        Assert.False(_streams.MagnifyOpen);
        Assert.False(_streams.RotateOpen);
    }

    [Fact]
    public void A_twist_with_spread_drift_opens_rotate_only()
    {
        // 20 degrees = 0.35 rad of tangential travel against a 6 % spread change: rotation dominates, although the
        // magnification alone is past the start threshold.
        TransformStreamStep step = _streams.Step(0.06, 20);

        Assert.True(step.RotateBegins);
        Assert.True(step.PostRotate);
        Assert.Equal(20, step.Degrees);
        Assert.True(step.RotateFirst);
        Assert.False(step.MagnifyBegins);
        Assert.False(step.PostMagnify);
        Assert.False(_streams.MagnifyOpen);
    }

    [Fact]
    public void A_pinch_with_angle_wobble_opens_magnify_only()
    {
        TransformStreamStep step = _streams.Step(0.15, -3); // 3 degrees = 0.05 rad < 0.15

        Assert.True(step.MagnifyBegins);
        Assert.True(step.PostMagnify);
        Assert.Equal(0.15, step.Magnification);
        Assert.False(step.RotateFirst);
        Assert.False(step.RotateBegins);
        Assert.False(step.PostRotate);
    }

    [Fact]
    public void Held_amounts_post_as_the_first_changed()
    {
        Assert.Equal(default, _streams.Step(0.005, 0.4));
        TransformStreamStep step = _streams.Step(0.004, 0.8); // 1.2 degrees = 0.021 rad: rotate opens with all of it

        Assert.True(step.RotateBegins);
        Assert.Equal(1.2, step.Degrees, 1e-9);
        Assert.False(step.MagnifyBegins);
    }

    [Fact]
    public void Rotation_is_compared_in_radians_against_magnification()
    {
        // 1.5 degrees = 0.026 rad beats 0.025 magnification; 1.3 degrees = 0.0227 rad loses to it.
        Assert.True(_streams.Step(0.025, 1.5).RotateBegins);
        _streams.Reset();
        Assert.True(_streams.Step(0.025, 1.3).MagnifyBegins);
    }

    [Fact]
    public void Open_streams_post_each_step_and_the_other_stays_closed_under_its_join_threshold()
    {
        _streams.Step(0.03, 20); // rotate
        TransformStreamStep step = _streams.Step(0.05, 4);

        Assert.True(step.PostRotate);
        Assert.Equal(4, step.Degrees);
        Assert.False(step.RotateBegins);
        Assert.False(step.MagnifyBegins);
        Assert.False(step.PostMagnify);

        Assert.False(_streams.Step(0.1, 2).MagnifyBegins); // 0.15 since rotate opened < 0.25
    }

    [Fact]
    public void Magnify_joins_a_rotation_past_its_join_threshold_without_a_jump()
    {
        _streams.Step(0.03, 20);
        _streams.Step(0.1, 5);
        TransformStreamStep join = _streams.Step(0.16, 5); // 0.26 since rotate opened >= 0.25

        Assert.True(join.MagnifyBegins);
        Assert.False(join.PostMagnify); // the held drift is dropped
        Assert.True(join.PostRotate);
        Assert.True(_streams.MagnifyOpen);

        TransformStreamStep next = _streams.Step(0.02, 1);
        Assert.True(next.PostMagnify);
        Assert.Equal(0.02, next.Magnification);
        Assert.False(next.MagnifyBegins);
    }

    [Fact]
    public void Rotate_joins_a_pinch_past_its_join_threshold()
    {
        _streams.Step(0.3, 5);
        Assert.False(_streams.Step(0.1, 9).RotateBegins); // 9 degrees since magnify opened
        TransformStreamStep join = _streams.Step(0.1, 7); // 16 degrees

        Assert.True(join.RotateBegins);
        Assert.False(join.PostRotate);
        Assert.False(join.RotateFirst);
    }

    [Fact]
    public void Opposite_drift_cancels_out()
    {
        _streams.Step(0.03, 20);
        _streams.Step(0.2, 1);
        Assert.False(_streams.Step(-0.2, 1).MagnifyBegins);
        Assert.False(_streams.Step(0.2, 1).MagnifyBegins); // 0.2 since rotate opened
    }

    [Fact]
    public void A_large_first_step_opens_only_the_dominant_stream()
    {
        TransformStreamStep step = _streams.Step(0.3, 40); // 0.70 rad > 0.3

        Assert.True(step.RotateBegins);
        Assert.True(step.RotateFirst);
        Assert.False(step.MagnifyBegins);
        Assert.False(step.PostMagnify);
    }

    [Fact]
    public void The_join_counts_from_when_the_first_stream_opened()
    {
        // The Began carries 0.30 magnification and 14 degrees (0.24 rad < 0.30): magnify opens. The 14 degrees that came
        // with it don't count toward rotate's join, so 1 degree more doesn't open rotate; 15 degrees more does.
        Assert.True(_streams.Step(0.30, 14).MagnifyBegins);
        Assert.False(_streams.Step(0.01, 1).RotateBegins);
        Assert.False(_streams.Step(0.01, 13).RotateBegins); // 14 since magnify opened
        Assert.True(_streams.Step(0.01, 1).RotateBegins); // 15
    }

    [Fact]
    public void A_zero_component_never_opens()
    {
        // Rotate toggle off (ADR-0033): the recognizer sends rotation 0, so magnify always wins.
        for (int i = 0; i < 50; i++)
        {
            _streams.Step(0.03, 0);
        }

        Assert.True(_streams.MagnifyOpen);
        Assert.False(_streams.RotateOpen);

        // Zoom toggle off: magnification 0.
        _streams.Reset();
        for (int i = 0; i < 50; i++)
        {
            _streams.Step(0, 3);
        }

        Assert.True(_streams.RotateOpen);
        Assert.False(_streams.MagnifyOpen);
    }

    [Fact]
    public void Non_finite_values_count_as_zero()
    {
        Assert.Equal(default, _streams.Step(double.NaN, double.PositiveInfinity));
        Assert.True(_streams.Step(double.NaN, 3).RotateBegins);
    }

    [Fact]
    public void Reset_forgets_the_gesture()
    {
        _streams.Step(0.3, 40);
        _streams.Reset();

        Assert.False(_streams.MagnifyOpen);
        Assert.False(_streams.RotateOpen);
        Assert.Equal(default, _streams.Step(0.01, 0));
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.08)]
    [InlineData(-0.08)]
    public void Recognized_twist_with_spread_drift_rotates(double drift)
    {
        // End to end with the recognizer: two fingers 200 pt apart turn 40 degrees in 2-degree steps while the spread
        // drifts by `drift`. The transform locks only once the arc reaches 40 pt (~23 degrees), so its Began carries
        // that much turn and the drift together; magnify used to open first and Preview zoomed.
        var sink = new RecordingSink();
        var rec = new GestureRecognizer(sink) { Mapper = new CoordinateMapper(1000, 1000, new DisplayRect(0, 0, 1000, 1000)) };
        for (int i = 0; i <= 20; i++)
        {
            double degrees = i * 2;
            double radius = 100 * (1 + (drift * i / 20));
            double a = degrees * Math.PI / 180;
            double ox = radius * Math.Cos(a);
            double oy = radius * Math.Sin(a);
            rec.Process([Finger(1, 500 - ox, 500 - oy), Finger(2, 500 + ox, 500 + oy)], false, i * 8000);
        }

        bool rotateFirst = false;
        bool magnifyOpened = false;
        foreach (GestureEvent e in sink.Gestures)
        {
            if (e.Kind != GestureKind.Pinch)
            {
                continue;
            }

            TransformStreamStep step = _streams.Step(
                GestureOutput.ToMagnification(e.ScaleDelta), GestureOutput.ToCounterClockwiseDegrees(e.RotationDelta));
            rotateFirst |= step.RotateBegins && !_streams.MagnifyOpen;
            magnifyOpened |= step.MagnifyBegins;
        }

        Assert.True(rotateFirst);
        Assert.False(magnifyOpened);
    }

    [Fact]
    public void Recognized_pinch_with_angle_wobble_zooms()
    {
        var sink = new RecordingSink();
        var rec = new GestureRecognizer(sink) { Mapper = new CoordinateMapper(1000, 1000, new DisplayRect(0, 0, 1000, 1000)) };
        for (int i = 0; i <= 20; i++)
        {
            double radius = 100 + (i * 5);
            double a = 4 * Math.Sin(i) * Math.PI / 180; // up to 4 degrees of wobble
            rec.Process(
                [Finger(1, 500 - (radius * Math.Cos(a)), 500 - (radius * Math.Sin(a))), Finger(2, 500 + (radius * Math.Cos(a)), 500 + (radius * Math.Sin(a)))],
                false,
                i * 8000);
        }

        bool magnifyFirst = false;
        bool rotateOpened = false;
        foreach (GestureEvent e in sink.Gestures)
        {
            if (e.Kind != GestureKind.Pinch)
            {
                continue;
            }

            TransformStreamStep step = _streams.Step(
                GestureOutput.ToMagnification(e.ScaleDelta), GestureOutput.ToCounterClockwiseDegrees(e.RotationDelta));
            magnifyFirst |= step.MagnifyBegins && !_streams.RotateOpen;
            rotateOpened |= step.RotateBegins;
        }

        Assert.True(magnifyFirst);
        Assert.False(rotateOpened);
    }

    private static TouchSample Finger(byte id, double x, double y) =>
        new(id, true, (ushort)Math.Round(x * 65535.0 / 1000.0), (ushort)Math.Round(y * 65535.0 / 1000.0));
}
