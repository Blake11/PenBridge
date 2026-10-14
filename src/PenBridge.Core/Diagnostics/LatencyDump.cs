using System.Globalization;

namespace PenBridge.Core.Diagnostics;

/// <summary>
/// Writes latency records as a text dump: <c>#</c> summary lines (the <see cref="LatencyAnalyzer"/> report, what
/// <c>scripts/latency-report.sh</c> prints) followed by one tab-separated row per record. Runs off the hot path
/// (on demand or at session end), so it may allocate.
/// </summary>
public static class LatencyDump
{
    public const string Header = "# PenBridge latency dump v1";

    /// <summary>Column header of the record rows (server times in µs from the first record).</summary>
    public const string Columns = "kind\tsession\tflags\tclient_us\tserver_us\tserver_end_us";

    public static void Write(TextWriter writer, ReadOnlySpan<LatencyRecord> records, long timestampFrequency, bool includeRecords = true) =>
        Write(writer, records, timestampFrequency, LatencyAnalyzer.Analyze(records, timestampFrequency), includeRecords);

    /// <summary>Same, with a <paramref name="report"/> already computed from <paramref name="records"/>.</summary>
    public static void Write(TextWriter writer, ReadOnlySpan<LatencyRecord> records, long timestampFrequency, LatencyReport report, bool includeRecords = true)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(report);
        writer.WriteLine(Header);
        writer.WriteLine(FormattableString.Invariant($"# records={records.Length} frequency={timestampFrequency}"));
        foreach (string line in Summarize(report))
        {
            writer.WriteLine("# " + line);
        }

        if (!includeRecords)
        {
            return;
        }

        writer.WriteLine(Columns);
        long origin = LatencyAnalyzer.Origin(records);
        foreach (ref readonly LatencyRecord r in records)
        {
            writer.WriteLine(FormattableString.Invariant(
                $"{Name(r.Kind)}\t{r.SessionId}\t{(byte)r.Flags}\t{r.ClientUs}\t{LatencyAnalyzer.ToUs(r.Server, origin, timestampFrequency):F1}\t{LatencyAnalyzer.ToUs(r.ServerEnd, origin, timestampFrequency):F1}"));
        }
    }

    /// <summary>Human-readable summary lines (no <c>#</c> prefix), milliseconds with three decimals.</summary>
    public static IReadOnlyList<string> Summarize(LatencyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>();
        foreach (SessionClock c in report.Sessions)
        {
            lines.Add(FormattableString.Invariant(
                $"session {c.SessionId:x8}: inputs={c.Inputs} client_pings={c.ClientPings} round_trips={c.RoundTrips} offset_ms={Ms(c.OffsetUs)} drift_ppm={c.DriftPpm:F1} min_rtt_ms={Ms(c.MinRttUs)} offset_error_ms=±{Ms(c.UncertaintyUs)}"));
        }

        lines.Add(Line("pen_down_to_inject", report.PenDown));
        lines.Add(Line("contact_to_inject", report.InjectContact));
        lines.Add(Line("send_to_inject", report.Inject));
        lines.Add(Line("send_to_receive", report.Receive));
        if (report.UnmeasuredInputs > 0)
        {
            lines.Add(FormattableString.Invariant($"unmeasured_inputs={report.UnmeasuredInputs} (sessions without a client Ping: no clock offset)"));
        }

        if (report.NegativeReceive > 0)
        {
            lines.Add(FormattableString.Invariant($"negative_receive={report.NegativeReceive} (within the offset error when small)"));
        }

        return lines;
    }

    private static string Line(string name, LatencyStats s) => FormattableString.Invariant(
        $"{name}: count={s.Count} p50_ms={Ms(s.P50Us)} p95_ms={Ms(s.P95Us)} max_ms={Ms(s.MaxUs)} min_ms={Ms(s.MinUs)}");

    private static string Ms(double us) => double.IsNaN(us) ? "n/a" : (us / 1000).ToString("F3", CultureInfo.InvariantCulture);

    private static string Name(LatencyRecordKind kind) => kind switch
    {
        LatencyRecordKind.Input => "input",
        LatencyRecordKind.ClientPing => "client_ping",
        LatencyRecordKind.ServerRtt => "server_rtt",
        _ => "unknown",
    };
}
