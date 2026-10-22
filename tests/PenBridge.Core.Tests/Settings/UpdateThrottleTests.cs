using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Settings;

/// <summary>The ActiveArea coalescing of a moving window target (ADR-0036): one update per 50 ms plus a final one.</summary>
public class UpdateThrottleTests
{
    private static readonly TimeSpan Interval = UpdateThrottle.ActiveAreaInterval;

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void The_interval_is_50_ms() => Assert.Equal(Ms(50), UpdateThrottle.ActiveAreaInterval);

    [Fact]
    public void The_first_change_is_sent_at_once()
    {
        var throttle = new UpdateThrottle(Interval);
        Assert.Equal(ThrottleStep.SendNow, throttle.Change(Ms(1000), out TimeSpan delay));
        Assert.Equal(TimeSpan.Zero, delay);
        Assert.False(throttle.IsArmed);
    }

    [Fact]
    public void A_change_within_the_interval_arms_one_timer_for_the_rest_of_it()
    {
        var throttle = new UpdateThrottle(Interval);
        _ = throttle.Change(Ms(1000), out _);

        Assert.Equal(ThrottleStep.Arm, throttle.Change(Ms(1016), out TimeSpan delay));
        Assert.Equal(Ms(34), delay);
        Assert.True(throttle.IsArmed);

        // Further changes wait for the same timer.
        Assert.Equal(ThrottleStep.None, throttle.Change(Ms(1032), out delay));
        Assert.Equal(TimeSpan.Zero, delay);
        Assert.Equal(ThrottleStep.None, throttle.Change(Ms(1048), out _));
    }

    [Fact]
    public void The_timer_sends_the_latest_once_and_the_burst_ends_with_it()
    {
        var throttle = new UpdateThrottle(Interval);
        _ = throttle.Change(Ms(1000), out _);
        _ = throttle.Change(Ms(1016), out _);
        _ = throttle.Change(Ms(1040), out _);

        Assert.True(throttle.Fire(Ms(1050)));
        Assert.False(throttle.IsArmed);

        // Nothing changed since: a second fire (a stale timer) sends nothing.
        Assert.False(throttle.Fire(Ms(1100)));
    }

    [Fact]
    public void A_continuous_move_sends_at_most_one_update_per_interval_and_a_final_one()
    {
        var throttle = new UpdateThrottle(Interval);
        var sent = new List<double>();
        double? timerAt = null;

        // A drag with an AX notification every 16 ms for 500 ms; the timer fires exactly when due.
        for (double t = 0; t <= 500; t += 16)
        {
            if (timerAt is { } due && due <= t)
            {
                timerAt = null;
                if (throttle.Fire(Ms(due)))
                {
                    sent.Add(due);
                }
            }

            switch (throttle.Change(Ms(t), out TimeSpan delay))
            {
                case ThrottleStep.SendNow:
                    sent.Add(t);
                    break;
                case ThrottleStep.Arm:
                    timerAt = t + delay.TotalMilliseconds;
                    break;
            }
        }

        // The drag stops at 496 ms; the armed timer still fires: the final position goes out.
        Assert.NotNull(timerAt);
        Assert.True(throttle.Fire(Ms(timerAt!.Value)));
        sent.Add(timerAt.Value);

        for (int i = 1; i < sent.Count; i++)
        {
            Assert.True(sent[i] - sent[i - 1] >= Interval.TotalMilliseconds - 1e-9, $"sends {i - 1} -> {i}: {sent[i - 1]} -> {sent[i]} ms, closer than {Interval.TotalMilliseconds} ms");
        }

        Assert.InRange(sent.Count, 9, 11);
        Assert.InRange(sent[^1], 496, double.PositiveInfinity);
    }

    [Fact]
    public void A_change_after_a_quiet_interval_is_sent_at_once_again()
    {
        var throttle = new UpdateThrottle(Interval);
        _ = throttle.Change(Ms(0), out _);
        _ = throttle.Change(Ms(10), out _);
        Assert.True(throttle.Fire(Ms(50)));
        Assert.Equal(ThrottleStep.SendNow, throttle.Change(Ms(100), out _));
    }

    [Fact]
    public void Reset_drops_the_pending_change_and_the_last_send()
    {
        var throttle = new UpdateThrottle(Interval);
        _ = throttle.Change(Ms(0), out _);
        Assert.Equal(ThrottleStep.Arm, throttle.Change(Ms(10), out _));

        throttle.Reset();
        Assert.False(throttle.IsArmed);
        Assert.False(throttle.Fire(Ms(50))); // the old timer is stale
        Assert.Equal(ThrottleStep.SendNow, throttle.Change(Ms(20), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void The_interval_must_be_positive(int ms) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpdateThrottle(Ms(ms)));
}
