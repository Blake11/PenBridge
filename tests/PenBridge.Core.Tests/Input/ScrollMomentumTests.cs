using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

public class ScrollMomentumTests
{
    private const long FrameUs = 8_000;
    private readonly ScrollMomentum _m = new();

    /// <summary>Feeds <paramref name="frames"/> deltas of (dx, dy) every 8 ms from <paramref name="startUs"/>; returns the last time.</summary>
    private long Feed(int frames, double dx, double dy, long startUs = 0)
    {
        long t = startUs;
        for (int i = 0; i < frames; i++)
        {
            t = startUs + (i * FrameUs);
            _m.AddSample(dx, dy, t);
        }

        return t;
    }

    [Fact]
    public void Release_velocity_is_the_recent_movement_rate()
    {
        long last = Feed(20, 0, 8); // 8 pt / 8 ms = 1000 pt/s
        Assert.True(_m.TryStart(last + FrameUs));
        Assert.True(_m.IsCoasting);
        Assert.Equal(1000, _m.ReleaseVelocityY, 1);
        Assert.Equal(0, _m.ReleaseVelocityX, 1);
    }

    [Fact]
    public void Velocity_uses_only_the_window_before_the_last_delta()
    {
        long t = Feed(30, 1, 0); // slow: 125 pt/s
        Feed(15, 16, 0, t + FrameUs); // fast for the last 120 ms: 2000 pt/s
        Assert.True(_m.TryStart(t + (16 * FrameUs)));
        Assert.Equal(2000, _m.ReleaseVelocityX, 1);
    }

    [Fact]
    public void The_first_delta_after_a_reset_only_sets_the_time_base()
    {
        _m.AddSample(0, 500, 0); // a Began delta spans the unknown time since landing
        _m.AddSample(0, 8, FrameUs);
        _m.AddSample(0, 8, 2 * FrameUs);
        Assert.True(_m.TryStart(3 * FrameUs));
        Assert.Equal(1000, _m.ReleaseVelocityY, 1);
    }

    [Theory]
    [InlineData(50_000, true)]
    [InlineData(51_000, false)]
    [InlineData(300_000, false)]
    public void A_pause_before_the_lift_means_no_momentum(long pauseUs, bool coasts)
    {
        long last = Feed(20, 0, 8);
        Assert.Equal(coasts, _m.TryStart(last + pauseUs));
        Assert.Equal(coasts, _m.IsCoasting);
    }

    [Fact]
    public void Slow_release_starts_no_momentum()
    {
        long last = Feed(20, 0, 1); // 125 pt/s < 150
        Assert.False(_m.TryStart(last + FrameUs));
        Assert.False(_m.Step(last + (2 * FrameUs), out double dx, out double dy));
        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
    }

    [Fact]
    public void Too_few_samples_start_no_momentum()
    {
        Assert.False(_m.TryStart(0));
        _m.AddSample(0, 100, 0);
        Assert.False(_m.TryStart(FrameUs));
    }

    [Fact]
    public void Release_speed_is_capped_keeping_its_direction()
    {
        long last = Feed(10, 300, 400); // 62500 pt/s
        Assert.True(_m.TryStart(last));
        Assert.Equal(12_000 * 0.6, _m.ReleaseVelocityX, 1);
        Assert.Equal(12_000 * 0.8, _m.ReleaseVelocityY, 1);
    }

    [Fact]
    public void Coast_follows_the_exponential_curve()
    {
        long release = Feed(20, 0, 8) + FrameUs;
        Assert.True(_m.TryStart(release));
        Assert.True(_m.Step(release + 400_000, out _, out double dy)); // one step of exactly tau
        Assert.Equal(1000 * 0.4 * (1 - Math.Exp(-1)), dy, 0.01);
    }

    [Fact]
    public void Steps_shrink_and_add_up_to_the_same_coast_for_any_tick_rate()
    {
        var a = new ScrollMomentum();
        var b = new ScrollMomentum();
        for (int i = 0; i < 20; i++)
        {
            a.AddSample(-6, 3, i * FrameUs);
            b.AddSample(-6, 3, i * FrameUs);
        }

        long release = 20 * FrameUs;
        Assert.True(a.TryStart(release));
        Assert.True(b.TryStart(release));

        double sumA = 0;
        double previous = double.MaxValue;
        for (long t = release + 16_667; t <= release + 500_000; t += 16_667)
        {
            Assert.True(a.Step(t, out double dx, out _));
            Assert.True(Math.Abs(dx) < previous);
            previous = Math.Abs(dx);
            sumA += dx;
        }

        long end = release + (29 * 16_667L);
        Assert.True(b.Step(release + 7_000, out double first, out _));
        double sumB = first;
        Assert.True(b.Step(end, out double second, out _));
        sumB += second;
        Assert.Equal(sumA, sumB, 1e-9);
        Assert.True(sumA < 0); // keeps the sign of the release velocity
    }

    [Fact]
    public void Coast_stops_below_the_stop_speed_and_stays_stopped()
    {
        long release = Feed(20, 0, 8) + FrameUs;
        Assert.True(_m.TryStart(release));
        long endUs = _m.EndTimeUs;
        Assert.Equal(release + (long)Math.Ceiling(0.4 * Math.Log(1000 / 20.0) * 1e6), endUs);

        Assert.True(_m.Step(endUs - 1_000, out _, out double dy));
        Assert.True(dy > 0);
        Assert.False(_m.Step(endUs + 1, out double dx2, out double dy2));
        Assert.Equal(0, dx2);
        Assert.Equal(0, dy2);
        Assert.False(_m.IsCoasting);
        Assert.Equal(0, _m.EndTimeUs);
        Assert.False(_m.Step(endUs + 20_000, out _, out _));
    }

    [Fact]
    public void Total_coast_is_the_decay_integral_up_to_the_stop()
    {
        long release = Feed(20, 0, 8) + FrameUs;
        Assert.True(_m.TryStart(release));
        double total = 0;
        long t = release;
        while (_m.Step(t += 16_667, out _, out double dy))
        {
            total += dy;
        }

        Assert.InRange(total, (1000 - 20) * 0.4 - 1, 1000 * 0.4);
    }

    [Fact]
    public void Step_before_the_previous_one_is_a_zero_step()
    {
        long release = Feed(20, 0, 8) + FrameUs;
        Assert.True(_m.TryStart(release));
        Assert.True(_m.Step(release + 50_000, out _, out _));
        Assert.True(_m.Step(release + 10_000, out double dx, out double dy));
        Assert.Equal(0, dx);
        Assert.Equal(0, dy);
    }

    [Fact]
    public void Reset_stops_the_coast_at_once()
    {
        long release = Feed(20, 0, 8) + FrameUs;
        Assert.True(_m.TryStart(release));
        _m.Reset();
        Assert.False(_m.IsCoasting);
        Assert.False(_m.Step(release + 16_000, out _, out _));
        Assert.False(_m.TryStart(release)); // samples are gone too
    }

    [Fact]
    public void New_scroll_delta_stops_the_coast()
    {
        long release = Feed(20, 0, 8) + FrameUs;
        Assert.True(_m.TryStart(release));
        _m.AddSample(0, 4, release + 20_000);
        Assert.False(_m.IsCoasting);
        Assert.False(_m.Step(release + 30_000, out _, out _));
    }

    [Fact]
    public void Samples_do_not_carry_over_to_the_next_release()
    {
        long release = Feed(20, 0, 8) + FrameUs;
        Assert.True(_m.TryStart(release));
        Assert.False(_m.TryStart(release + 1_000));
    }

    [Fact]
    public void Observe_routes_scroll_phases()
    {
        Assert.False(_m.Observe(Scroll(GesturePhase.Began, 0, 100, 0)));
        for (int i = 1; i <= 10; i++)
        {
            Assert.False(_m.Observe(Scroll(GesturePhase.Changed, 0, 8, i * FrameUs)));
        }

        Assert.True(_m.Observe(Scroll(GesturePhase.Ended, 0, 0, 11 * FrameUs)));
        Assert.Equal(1000, _m.ReleaseVelocityY, 1);
    }

    [Fact]
    public void Observe_cancelled_scroll_gives_no_momentum()
    {
        _m.Observe(Scroll(GesturePhase.Began, 0, 100, 0));
        for (int i = 1; i <= 10; i++)
        {
            _m.Observe(Scroll(GesturePhase.Changed, 0, 8, i * FrameUs));
        }

        Assert.False(_m.Observe(Scroll(GesturePhase.Cancelled, 0, 0, 11 * FrameUs)));
        Assert.False(_m.IsCoasting);
    }

    [Theory]
    [InlineData(GestureKind.Pinch)]
    [InlineData(GestureKind.Tap)]
    public void Observe_other_gestures_stop_the_coast(GestureKind kind)
    {
        long release = Feed(20, 0, 8) + FrameUs;
        Assert.True(_m.TryStart(release));
        Assert.False(_m.Observe(new GestureEvent(kind, GesturePhase.Ended, 0, 0, 1, 0, default, false, release + 1)));
        Assert.False(_m.IsCoasting);
    }

    [Fact]
    public void Began_clears_older_samples()
    {
        Feed(20, 0, 8);
        _m.Observe(Scroll(GesturePhase.Began, 0, 5, 1_000_000));
        Assert.False(_m.TryStart(1_008_000)); // only the Began delta: no rate
    }

    [Fact]
    public void Non_finite_deltas_are_ignored()
    {
        long last = Feed(20, 0, 8);
        _m.AddSample(double.NaN, 0, last + FrameUs);
        Assert.True(_m.TryStart(last + FrameUs));
        Assert.True(double.IsFinite(_m.ReleaseVelocityX));
    }

    [Fact]
    public void Ring_buffer_keeps_working_past_capacity()
    {
        long last = Feed(ScrollMomentum.Capacity * 3, 0, 8);
        Assert.True(_m.TryStart(last + FrameUs));
        Assert.Equal(1000, _m.ReleaseVelocityY, 1);
    }

    [Fact]
    public void Invalid_options_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScrollMomentum(new ScrollMomentumOptions { DecayTimeConstant = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScrollMomentum(new ScrollMomentumOptions { StopSpeed = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScrollMomentum(new ScrollMomentumOptions { MinStartSpeed = 10, StopSpeed = 20 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScrollMomentum(new ScrollMomentumOptions { MaxSpeed = double.PositiveInfinity }));
    }

    [Fact]
    public void Feeding_and_coasting_do_not_allocate()
    {
        var m = new ScrollMomentum();
        var steps = new long[1];
        long ran = 0;
        Alloc.AssertZero(2_000, i =>
        {
            long t0 = (long)i * 10_000_000; // longer than any coast
            m.Observe(new GestureEvent(GestureKind.Scroll, GesturePhase.Began, 0, 50, 1, 0, default, false, t0));
            for (int k = 1; k <= 12; k++)
            {
                m.Observe(new GestureEvent(GestureKind.Scroll, GesturePhase.Changed, 3, 8 + (i % 5), 1, 0, default, false, t0 + (k * FrameUs)));
            }

            if (m.Observe(new GestureEvent(GestureKind.Scroll, GesturePhase.Ended, 0, 0, 1, 0, default, false, t0 + (13 * FrameUs))))
            {
                long t = t0 + (13 * FrameUs);
                while (m.Step(t += 16_667, out _, out _))
                {
                    steps[0]++;
                }
            }

            _ = m.EndTimeUs;
            ran++;
        }, () => ran, () => steps[0]);
    }

    private static GestureEvent Scroll(GesturePhase phase, double dx, double dy, long t) =>
        new(GestureKind.Scroll, phase, dx, dy, 1, 0, default, false, t);
}
