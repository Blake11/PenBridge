using PenBridge.Core.Diagnostics;

namespace PenBridge.Core.Tests.Diagnostics;

/// <summary>Clock-offset estimation and offset-corrected statistics on synthetic two-clock sessions.</summary>
public class LatencyAnalyzerTests
{
    private const long Frequency = 1_000_000_000; // macOS Stopwatch: ns
    private const uint Session = 7;
    private const double Wrap = 4294967296.0;

    /// <summary>
    /// A synthetic session in true time τ (µs). Server clock S = τ + 5 s; the client clock runs <paramref name="driftPpm"/>
    /// slow and starts at <paramref name="clientStartUs"/>. Up and down delays are 800 µs plus jitter that is zero
    /// for one exchange in 13, so the minima are exact. Input: frames every 4 ms; strokes of 50 contact + 10 hover
    /// frames; true receive latency 3000..3900 µs, injection 200 µs later.
    /// </summary>
    private static List<LatencyRecord> Synthetic(double seconds, double driftPpm, double clientStartUs, out int strokes)
    {
        const double ServerBase = 5_000_000;
        double rate = 1 - (driftPpm * 1e-6);
        double Client(double tau) => clientStartUs + (tau * rate);
        long Server(double tau) => (long)((tau + ServerBase) * 1000);
        double Jitter(int k) => (k * 7919 % 13) * 250.0;

        var records = new List<LatencyRecord>();
        double end = seconds * 1e6;
        int ping = 0;
        int frame = 0;
        strokes = 0;
        double nextPing = 1_000_000;
        for (double tau = 0; tau < end; tau += 4000, frame++)
        {
            if (tau >= nextPing)
            {
                // Client Ping: one-way up. Server Ping: its own round trip (up and down jitter independent).
                records.Add(new LatencyRecord(LatencyRecordKind.ClientPing, LatencyFrameInfo.None, Session, (ulong)Client(tau), Server(tau + 800 + Jitter(ping)), Server(tau + 800 + Jitter(ping))));
                double rtt = 1600 + (2 * Jitter(ping + 5));
                records.Add(new LatencyRecord(LatencyRecordKind.ServerRtt, LatencyFrameInfo.None, Session, 0, Server(tau + 500), Server(tau + 500 + rtt)));
                ping++;
                nextPing += 2_000_000;
            }

            int phase = frame % 60;
            bool contact = phase < 50;
            if (phase == 0)
            {
                strokes++;
            }

            double rx = 3000 + ((frame % 10) * 100);
            LatencyFrameInfo flags = LatencyFrameInfo.PenInRange | (contact ? LatencyFrameInfo.PenContact : LatencyFrameInfo.None);
            uint timeUs = unchecked((uint)(ulong)Client(tau));
            records.Add(new LatencyRecord(LatencyRecordKind.Input, flags, Session, timeUs, Server(tau + rx), Server(tau + rx + 200)));
        }

        return records;
    }

    [Fact]
    public void Offset_and_latencies_are_recovered_without_drift()
    {
        List<LatencyRecord> records = Synthetic(20, 0, 123_456_789, out int strokes);

        LatencyReport report = LatencyAnalyzer.Analyze(records.ToArray(), Frequency);

        SessionClock clock = Assert.Single(report.Sessions);
        Assert.True(clock.HasOffset);
        Assert.Equal(0, clock.DriftPpm); // 20 s is below MinDriftSpan
        Assert.Equal(1600, clock.MinRttUs, 1);
        Assert.Equal(800, clock.UncertaintyUs, 1);

        // Server µs count from the first record (input 0, read at S = 5 s + 3000 µs), so S_rel − C = (τ − 3000) − (start + τ).
        Assert.Equal(1_000_000 - 3000 + 800, clock.ReferenceUs, 1); // the first client Ping, read 800 µs after τ = 1 s
        Assert.Equal(-3000 - 123_456_789, clock.OffsetUs, 1);

        Assert.Equal(3400, report.Receive.P50Us, 1);
        Assert.Equal(3900, report.Receive.P95Us, 1);
        Assert.Equal(3900, report.Receive.MaxUs, 1);
        Assert.Equal(3600, report.Inject.P50Us, 1);
        Assert.Equal(strokes, report.PenDown.Count);
        Assert.Equal(0, report.NegativeReceive);
        Assert.Equal(0, report.UnmeasuredInputs);
    }

    [Fact]
    public void Drift_is_fitted_and_wrapped_time_us_is_unwrapped()
    {
        // 5 minutes at 50 ppm: the clocks drift 15 ms apart, and timeUs wraps 60 s in.
        List<LatencyRecord> records = Synthetic(300, 50, Wrap - 60_000_000, out int strokes);

        LatencyReport report = LatencyAnalyzer.Analyze(records.ToArray(), Frequency);

        SessionClock clock = Assert.Single(report.Sessions);
        Assert.InRange(clock.DriftPpm, 49, 51);
        Assert.InRange(report.Receive.P50Us, 3350, 3450);
        Assert.InRange(report.Receive.MinUs, 2950, 3050);
        Assert.InRange(report.Receive.MaxUs, 3850, 3950);
        Assert.InRange(report.Inject.P95Us, 4050, 4150);
        Assert.Equal(strokes, report.PenDown.Count);
        Assert.Equal(0, report.NegativeReceive);
    }

    [Fact]
    public void Without_a_drift_fit_long_sessions_would_skew_by_the_drift()
    {
        // Guard for the test itself: the drift in the synthetic data is large enough that a constant offset fails.
        List<LatencyRecord> records = Synthetic(300, 50, 0, out _);
        IEnumerable<LatencyRecord> earlyPings = records.Where(r => r.Kind != LatencyRecordKind.Input && r.Server < (60L * Frequency));
        IEnumerable<LatencyRecord> lateInputs = records.Where(r => r.Kind == LatencyRecordKind.Input && r.Server > (290L * Frequency));

        // Pings spanning under a minute (no drift fit) applied to inputs 4 minutes later: off by about 50 ppm × 280 s.
        LatencyReport early = LatencyAnalyzer.Analyze([.. earlyPings, .. lateInputs], Frequency);
        Assert.True(early.Receive.P50Us > 3400 + 8000, early.Receive.P50Us.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_session_without_client_pings_is_reported_unmeasured()
    {
        LatencyRecord[] records =
        [
            new(LatencyRecordKind.Input, LatencyFrameInfo.PenContact, 1, 10, 1000, 2000),
            new(LatencyRecordKind.ServerRtt, LatencyFrameInfo.None, 1, 0, 1000, 5000),
        ];

        LatencyReport report = LatencyAnalyzer.Analyze(records, Frequency);

        SessionClock clock = Assert.Single(report.Sessions);
        Assert.False(clock.HasOffset);
        Assert.Equal(1, report.UnmeasuredInputs);
        Assert.Equal(0, report.Inject.Count);
        Assert.True(double.IsNaN(report.Inject.P50Us));
    }

    [Fact]
    public void Sessions_get_separate_offsets()
    {
        // Same true latency (2 ms), very different client epochs per session.
        LatencyRecord[] records =
        [
            new(LatencyRecordKind.ClientPing, LatencyFrameInfo.None, 1, 1_000, 10_000_000, 10_000_000),
            new(LatencyRecordKind.Input, LatencyFrameInfo.PenContact, 1, 2_000, 13_000_000, 13_000_000),
            new(LatencyRecordKind.ClientPing, LatencyFrameInfo.None, 2, 900_000_000, 20_000_000, 20_000_000),
            new(LatencyRecordKind.Input, LatencyFrameInfo.PenContact, 2, 900_001_000, 23_000_000, 23_000_000),
        ];

        LatencyReport report = LatencyAnalyzer.Analyze(records, Frequency);

        Assert.Equal(2, report.Sessions.Count);
        Assert.Equal(2, report.PenDown.Count);
        Assert.Equal(2000, report.Inject.MinUs, 1);
        Assert.Equal(2000, report.Inject.MaxUs, 1);
    }

    [Fact]
    public void Pen_down_counts_only_the_first_contact_frame_of_each_stroke()
    {
        LatencyFrameInfo hover = LatencyFrameInfo.PenInRange;
        LatencyFrameInfo contact = hover | LatencyFrameInfo.PenContact;
        LatencyRecord Input(ulong t, LatencyFrameInfo f, long delayUs) => new(LatencyRecordKind.Input, f, 1, t, ((long)t + delayUs) * 1000, ((long)t + delayUs) * 1000);
        LatencyRecord[] records =
        [
            new(LatencyRecordKind.ClientPing, LatencyFrameInfo.None, 1, 0, 0, 0),
            Input(100, hover, 1000),
            Input(200, contact, 5000), // down
            Input(300, contact, 1000),
            Input(400, hover, 1000),
            Input(500, contact, 7000), // down
            Input(600, contact, 1000),
        ];

        LatencyReport report = LatencyAnalyzer.Analyze(records, Frequency);

        Assert.Equal(2, report.PenDown.Count);
        Assert.Equal(5000, report.PenDown.MinUs, 1);
        Assert.Equal(7000, report.PenDown.MaxUs, 1);
        Assert.Equal(4, report.InjectContact.Count);
        Assert.Equal(6, report.Inject.Count);
    }

    [Theory]
    [InlineData(50, 5)]
    [InlineData(95, 10)]
    [InlineData(100, 10)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    public void Percentiles_use_nearest_rank(double percent, double expected)
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.Equal(expected, LatencyStats.Percentile(sorted, percent));
    }

    [Fact]
    public void Stats_of_one_value_and_of_none()
    {
        double[] one = [42];
        Assert.Equal(new LatencyStats(1, 42, 42, 42, 42), LatencyStats.From(one));
        Assert.Equal(0, LatencyStats.From([]).Count);
    }

    [Theory]
    [InlineData(5u, 10.0, 5.0)]
    [InlineData(5u, Wrap + 100, Wrap + 5)]
    [InlineData(4294967000u, Wrap + 100, 4294967000.0)] // just before a wrap, the approximation already past it
    [InlineData(10u, -50.0, 10.0)] // never negative
    public void Unwrap_picks_the_closest_epoch(uint raw, double approximate, double expected) =>
        Assert.Equal(expected, LatencyAnalyzer.Unwrap(raw, approximate));

    [Fact]
    public void Lower_line_follows_the_minima_not_the_jitter()
    {
        var times = new List<double>();
        var values = new List<double>();
        for (int i = 0; i < 100; i++)
        {
            double t = i * 2e6;
            times.Add(t);
            values.Add(1000 + (t * 20e-6) + (i % 5 == 0 ? 0 : 3000)); // 20 ppm slope, one clean sample in five
        }

        LatencyAnalyzer.ClockLine line = LatencyAnalyzer.FitLowerLine(times, values, 30e6, 60e6);

        Assert.Equal(20e-6, line.Slope, 1e-9);
        Assert.Equal(1000, line.At(0), 0.5);
        Assert.Equal(1000 + (198e6 * 20e-6), line.At(198e6), 0.5);
    }

    [Fact]
    public void To_us_is_exact_for_large_timestamps()
    {
        Assert.Equal(1.5, LatencyAnalyzer.ToUs(1_000_000_001_500, 1_000_000_000_000, Frequency));
        Assert.Equal(86_400e6, LatencyAnalyzer.ToUs(86_400 * Frequency, 0, Frequency));
        Assert.Equal(10, LatencyAnalyzer.ToUs(100, 0, TimeSpan.TicksPerSecond));
    }
}
