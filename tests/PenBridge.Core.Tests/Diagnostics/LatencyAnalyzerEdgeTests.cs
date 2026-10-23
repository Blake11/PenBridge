using PenBridge.Core.Diagnostics;

namespace PenBridge.Core.Tests.Diagnostics;

/// <summary>Analyzer arithmetic checked by hand (#279): clock fields, the zero-latency edge, the drift fit and the origin.</summary>
public class LatencyAnalyzerEdgeTests
{
    private const long Frequency = 1_000_000_000; // ns; 1_000 ticks = 1 µs

    private static LatencyRecord Ping(uint session, ulong clientUs, long serverNs) =>
        new(LatencyRecordKind.ClientPing, LatencyFrameInfo.None, session, clientUs, serverNs, serverNs);

    private static LatencyRecord Rtt(uint session, long sentNs, long receivedNs) =>
        new(LatencyRecordKind.ServerRtt, LatencyFrameInfo.None, session, 0, sentNs, receivedNs);

    private static LatencyRecord Input(uint session, ulong clientUs, long receivedNs, long injectedNs) =>
        new(LatencyRecordKind.Input, LatencyFrameInfo.PenInRange | LatencyFrameInfo.PenContact, session, clientUs, receivedNs, injectedNs);

    [Fact]
    public void A_frequency_below_one_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LatencyAnalyzer.Analyze([], 0));

    [Fact]
    public void A_receive_latency_of_exactly_zero_is_not_negative_and_below_zero_is()
    {
        // The Ping gives θ = 0 (client 0 µs read at server 0); the frame sent at 1000 µs is read at 1000 µs.
        LatencyReport zero = LatencyAnalyzer.Analyze([Ping(1, 0, 0), Input(1, 1000, 1_000_000, 1_300_000)], Frequency);
        Assert.Equal(0, zero.NegativeReceive);
        Assert.Equal(0, zero.Receive.MinUs);
        Assert.Equal(300, zero.Inject.MinUs);

        LatencyReport negative = LatencyAnalyzer.Analyze([Ping(1, 0, 0), Input(1, 1000, 500_000, 800_000)], Frequency);
        Assert.Equal(1, negative.NegativeReceive);
        Assert.Equal(-500, negative.Receive.MinUs);
        Assert.Equal(-200, negative.Inject.MinUs);
    }

    [Fact]
    public void The_clock_of_a_session_with_pings_but_no_round_trip_has_no_rtt_and_no_correction()
    {
        LatencyReport report = LatencyAnalyzer.Analyze([Ping(1, 0, 2_000_000), Input(1, 1000, 3_000_000, 3_000_000)], Frequency);

        SessionClock clock = Assert.Single(report.Sessions);
        Assert.Equal(1, clock.ClientPings);
        Assert.Equal(0, clock.RoundTrips);
        Assert.Equal(1, clock.Inputs);
        Assert.Equal(0, clock.OffsetUs); // server 2000 µs (from the first record, the Ping) is origin-relative: 0 − 0
        Assert.Equal(0, clock.ReferenceUs);
        Assert.Equal(0, clock.DriftPpm);
        Assert.True(double.IsNaN(clock.MinRttUs));
        Assert.True(double.IsNaN(clock.UncertaintyUs));
    }

    [Fact]
    public void Half_the_smallest_round_trip_is_taken_off_the_offset_and_bounds_it()
    {
        LatencyReport report = LatencyAnalyzer.Analyze(
            [
                Ping(1, 0, 0),
                Rtt(1, 2_000_000, 2_600_000), // 600 µs
                Rtt(1, 3_000_000, 3_400_000), // 400 µs: the smallest
                Rtt(1, 4_000_000, 3_900_000), // negative: ignored
                Input(1, 1000, 1_000_000, 1_300_000),
            ],
            Frequency);

        SessionClock clock = Assert.Single(report.Sessions);
        Assert.Equal(2, clock.RoundTrips);
        Assert.Equal(400, clock.MinRttUs);
        Assert.Equal(200, clock.UncertaintyUs);
        Assert.Equal(-200, clock.OffsetUs);
        // θ = −200: the frame was sent at 1000 − 200 = 800 µs, read 200 µs later and injected 500 µs later.
        Assert.Equal(200, report.Receive.MinUs);
        Assert.Equal(500, report.Inject.MinUs);
    }

    [Fact]
    public void A_session_without_pings_keeps_its_round_trip_only_when_it_has_one()
    {
        LatencyReport none = LatencyAnalyzer.Analyze([Input(3, 10, 1000, 2000)], Frequency);
        SessionClock noRtt = Assert.Single(none.Sessions);
        Assert.True(double.IsNaN(noRtt.MinRttUs));
        Assert.True(double.IsNaN(noRtt.OffsetUs));
        Assert.Equal(1, noRtt.Inputs);
        Assert.Equal(1, none.UnmeasuredInputs);

        LatencyReport some = LatencyAnalyzer.Analyze([Input(3, 10, 1000, 2000), Rtt(3, 1000, 5000)], Frequency);
        SessionClock withRtt = Assert.Single(some.Sessions);
        Assert.Equal(4, withRtt.MinRttUs);
        Assert.Equal(1, withRtt.RoundTrips);
    }

    [Fact]
    public void The_origin_is_the_earliest_server_time_of_either_end_and_zero_without_records()
    {
        Assert.Equal(0, LatencyAnalyzer.Origin([]));
        Assert.Equal(500, LatencyAnalyzer.Origin([Input(1, 0, 900, 500), Input(1, 0, 700, 800)]));
        Assert.Equal(300, LatencyAnalyzer.Origin([Input(1, 0, 900, 1000), Input(1, 0, 300, 400)]));
    }

    [Fact]
    public void A_percentile_of_nothing_is_not_a_number() =>
        Assert.True(double.IsNaN(LatencyStats.Percentile([], 50)));

    [Fact]
    public void Stats_sort_their_input_and_take_nearest_ranks()
    {
        double[] five = [5, 1, 4, 2, 3];
        Assert.Equal(new LatencyStats(5, 1, 3, 5, 5), LatencyStats.From(five));
        Assert.Equal([1.0, 2, 3, 4, 5], five);

        double[] two = [20, 10];
        Assert.Equal(new LatencyStats(2, 10, 10, 20, 20), LatencyStats.From(two));
    }

    [Theory]
    [InlineData(0, 60e6, 60e6, 1e-6)] // exactly the minimum span: fitted (two windows)
    [InlineData(0, 60e6, 60e6 + 1, 0)] // a microsecond short: no drift
    [InlineData(50e6, 100e6, 60e6, 0)] // a span of 50 s is short whatever the times add up to
    [InlineData(10e6, 70e6, 60e6, 1e-6)] // not starting at zero
    public void The_drift_is_fitted_from_a_span_of_at_least_the_minimum(double first, double last, double minSpan, double slope)
    {
        double[] times = [first, last];
        double[] values = [1000 + (first * 1e-6), 1000 + (last * 1e-6)];

        LatencyAnalyzer.ClockLine line = LatencyAnalyzer.FitLowerLine(times, values, 30e6, minSpan);

        Assert.Equal(slope, line.Slope, 15);
        Assert.Equal(first, line.Reference);
        Assert.Equal(1000 + (first * 1e-6), line.At(first), 6);
    }

    [Fact]
    public void The_fit_refines_window_minima_on_detrended_values()
    {
        // 60 samples a second apart from t0 = 5 s, 1000 ppm drift (1000 µs a second); in each 10 s window one sample
        // is clean and the rest carry 3000 µs of jitter. The clean one sits at a different place per window, so the raw
        // minima lean towards early jittered samples; detrending picks the clean ones.
        const double T0 = 5e6;
        var times = new List<double>();
        var values = new List<double>();
        for (int i = 0; i < 60; i++)
        {
            double t = T0 + (i * 1e6);
            int window = i / 10;
            bool clean = i % 10 == ((window * 7) + 4) % 10;
            times.Add(t);
            values.Add(1000 + ((t - T0) * 1e-3) + (clean ? 0 : 3000));
        }

        LatencyAnalyzer.ClockLine line = LatencyAnalyzer.FitLowerLine(times, values, 10e6, 30e6);

        Assert.Equal(1e-3, line.Slope, 9);
        Assert.Equal(T0, line.Reference);
        Assert.Equal(1000, line.At(T0), 6);
        Assert.Equal(1000 + (59e6 * 1e-3), line.At(T0 + 59e6), 4);
    }

    [Fact]
    public void The_intercept_is_the_lowest_detrended_sample_even_when_the_slope_is_zero()
    {
        double[] times = [5e6, 6e6, 7e6];
        double[] values = [1300, 1100, 1200];

        LatencyAnalyzer.ClockLine line = LatencyAnalyzer.FitLowerLine(times, values, 30e6, 60e6);

        Assert.Equal(0, line.Slope);
        Assert.Equal(1100, line.Intercept);
    }
}
