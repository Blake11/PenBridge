using PenBridge.Core.Diagnostics;

namespace PenBridge.Core.Tests.Diagnostics;

/// <summary>The text dump (#279): every summary line and record field, computed by hand.</summary>
public class LatencyDumpTests
{
    private const long Frequency = 1_000_000_000;

    private static readonly LatencyReport Report = new(
        [new SessionClock(0xAB, 2, 1, 3, 1500, 0, 2.5, 400, 200)],
        Receive: new LatencyStats(5, 1300, 2300, 3300, 4300),
        Inject: new LatencyStats(4, 1200, 2200, 3200, 4200),
        InjectContact: new LatencyStats(3, 1100, 2100, 3100, 4100),
        PenDown: new LatencyStats(2, 1000, 2000, 3000, 4000),
        UnmeasuredInputs: 0,
        NegativeReceive: 0);

    [Fact]
    public void Summary_lines_hold_every_statistic_in_milliseconds_in_a_fixed_order()
    {
        Assert.Equal(
            [
                "session 000000ab: inputs=3 client_pings=2 round_trips=1 offset_ms=1.500 drift_ppm=2.5 min_rtt_ms=0.400 offset_error_ms=±0.200",
                "pen_down_to_inject: count=2 p50_ms=2.000 p95_ms=3.000 max_ms=4.000 min_ms=1.000",
                "contact_to_inject: count=3 p50_ms=2.100 p95_ms=3.100 max_ms=4.100 min_ms=1.100",
                "send_to_inject: count=4 p50_ms=2.200 p95_ms=3.200 max_ms=4.200 min_ms=1.200",
                "send_to_receive: count=5 p50_ms=2.300 p95_ms=3.300 max_ms=4.300 min_ms=1.300",
            ],
            LatencyDump.Summarize(Report));
    }

    [Fact]
    public void Unmeasured_and_negative_counts_are_listed_only_when_above_zero()
    {
        Assert.DoesNotContain(LatencyDump.Summarize(Report), l => l.StartsWith("unmeasured_inputs", StringComparison.Ordinal) || l.StartsWith("negative_receive", StringComparison.Ordinal));

        IReadOnlyList<string> one = LatencyDump.Summarize(Report with { UnmeasuredInputs = 1, NegativeReceive = 1 });
        Assert.Equal("unmeasured_inputs=1 (sessions without a client Ping: no clock offset)", one[^2]);
        Assert.Equal("negative_receive=1 (within the offset error when small)", one[^1]);

        IReadOnlyList<string> many = LatencyDump.Summarize(Report with { UnmeasuredInputs = 7, NegativeReceive = 9 });
        Assert.Contains("unmeasured_inputs=7 (sessions without a client Ping: no clock offset)", many);
        Assert.Contains("negative_receive=9 (within the offset error when small)", many);
        Assert.Equal(Report.Sessions.Count + 4 + 2, many.Count);

        IReadOnlyList<string> onlyNegative = LatencyDump.Summarize(Report with { NegativeReceive = 2 });
        Assert.Equal("negative_receive=2 (within the offset error when small)", onlyNegative[^1]);
        Assert.DoesNotContain(onlyNegative, l => l.StartsWith("unmeasured_inputs", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_values_print_as_not_available()
    {
        var report = new LatencyReport(
            [new SessionClock(2, 0, 0, 4, double.NaN, double.NaN, 0, double.NaN, double.NaN)],
            LatencyStats.Empty,
            LatencyStats.Empty,
            LatencyStats.Empty,
            LatencyStats.Empty,
            4,
            0);

        IReadOnlyList<string> lines = LatencyDump.Summarize(report);

        Assert.Equal("session 00000002: inputs=4 client_pings=0 round_trips=0 offset_ms=n/a drift_ppm=0.0 min_rtt_ms=n/a offset_error_ms=±n/a", lines[0]);
        Assert.Equal("pen_down_to_inject: count=0 p50_ms=n/a p95_ms=n/a max_ms=n/a min_ms=n/a", lines[1]);
        Assert.Equal("send_to_receive: count=0 p50_ms=n/a p95_ms=n/a max_ms=n/a min_ms=n/a", lines[4]);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => LatencyDump.Summarize(null!));
        Assert.Throws<ArgumentNullException>(() => LatencyDump.Write(null!, [], Frequency));
        Assert.Throws<ArgumentNullException>(() => LatencyDump.Write(new StringWriter(), [], Frequency, null!));
    }

    [Fact]
    public void Every_record_row_has_its_kind_session_flags_client_time_and_server_times_from_the_first_record()
    {
        LatencyRecord[] records =
        [
            new(LatencyRecordKind.ClientPing, LatencyFrameInfo.None, 1, 1_000, 10_000_000, 10_000_000),
            new(LatencyRecordKind.ServerRtt, LatencyFrameInfo.None, 1, 0, 11_000_000, 11_400_000),
            new(LatencyRecordKind.Input, LatencyFrameInfo.PenInRange | LatencyFrameInfo.PenContact, 1, 2_000, 13_000_000, 13_500_000),
            new(LatencyRecordKind.Input, LatencyFrameInfo.Tls | LatencyFrameInfo.Touches, 5, 3_000, 12_000_000, 12_000_000),
            new((LatencyRecordKind)9, LatencyFrameInfo.None, 6, 0, 12_500_000, 12_700_000),
        ];
        var writer = new StringWriter();

        LatencyDump.Write(writer, records, Frequency);

        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(LatencyDump.Header, lines[0]);
        Assert.Equal("# records=5 frequency=1000000000", lines[1]);
        int columns = Array.IndexOf(lines, LatencyDump.Columns);
        Assert.Equal(
            [
                "client_ping\t1\t0\t1000\t0.0\t0.0",
                "server_rtt\t1\t0\t0\t1000.0\t1400.0",
                "input\t1\t6\t2000\t3000.0\t3500.0",
                "input\t5\t9\t3000\t2000.0\t2000.0",
                "unknown\t6\t0\t0\t2500.0\t2700.0",
            ],
            lines[(columns + 1)..]);
    }

    [Fact]
    public void A_dump_without_records_stops_after_the_summary()
    {
        LatencyRecord[] records = [new(LatencyRecordKind.ClientPing, LatencyFrameInfo.None, 1, 1_000, 10_000_000, 10_000_000)];
        var writer = new StringWriter();

        LatencyDump.Write(writer, records, Frequency, includeRecords: false);

        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.StartsWith("#", l, StringComparison.Ordinal));
        Assert.DoesNotContain(LatencyDump.Columns, lines);
        Assert.Equal("# records=1 frequency=1000000000", lines[1]);
    }

    [Fact]
    public void An_empty_dump_has_the_header_the_summary_and_the_column_line()
    {
        var writer = new StringWriter();

        LatencyDump.Write(writer, [], Frequency);

        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            [
                LatencyDump.Header,
                "# records=0 frequency=1000000000",
                "# pen_down_to_inject: count=0 p50_ms=n/a p95_ms=n/a max_ms=n/a min_ms=n/a",
                "# contact_to_inject: count=0 p50_ms=n/a p95_ms=n/a max_ms=n/a min_ms=n/a",
                "# send_to_inject: count=0 p50_ms=n/a p95_ms=n/a max_ms=n/a min_ms=n/a",
                "# send_to_receive: count=0 p50_ms=n/a p95_ms=n/a max_ms=n/a min_ms=n/a",
                LatencyDump.Columns,
            ],
            lines.Select(l => l.TrimEnd('\r')));
    }
}
