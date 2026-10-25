using System.Globalization;
using PenBridge.Core.Diagnostics;

namespace PenBridge.SimClient;

/// <summary>
/// The SimClient server's latency capture. It always records in memory (the in-process commands print pen and video
/// latency from it); with <c>PENBRIDGE_LATENCY_LOG=1</c>, like the Mac app, it also dumps to the same folder (<c>~/Library/Logs/PenBridge</c> on macOS, else <c>~/.penbridge/logs</c>) as <c>latency-sim-*.tsv</c>, at
/// session end and when the server stops. Validates the pipeline on one machine (one clock, so the offset model only
/// has to absorb the two session epochs).
/// </summary>
internal sealed class LatencyCapture
{
    private readonly Lock _lock = new();

    private LatencyCapture(bool dumps)
    {
        Dumps = dumps;
    }

    /// <summary>Whether <see cref="Dump"/> writes files (<c>PENBRIDGE_LATENCY_LOG=1</c>).</summary>
    public bool Dumps { get; }

    public LatencyRecorder Recorder { get; } = new();

    public static string DumpDirectory { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "PenBridge")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".penbridge", "logs");

    public static LatencyCapture Create() => new(LatencyRecorder.IsEnabledByEnvironment);

    /// <summary>One line: pen (send → inject) and video (capture → render) p50/p95 of everything recorded so far.</summary>
    public string Summarize()
    {
        LatencyRecord[] records = Recorder.Snapshot();
        LatencyReport report = LatencyAnalyzer.Analyze(records, TimeProvider.System.TimestampFrequency);
        string unmeasured = report.UnmeasuredInputs + report.UnmeasuredVideo > 0
            ? string.Create(CultureInfo.InvariantCulture, $" ({report.UnmeasuredInputs} pen / {report.UnmeasuredVideo} video samples without a clock estimate yet)")
            : string.Empty;
        return $"Latency: pen send->inject {Format(report.Inject)}; video capture->render {Format(report.Video)}{unmeasured}";
    }

    public void Dump(string reason)
    {
        if (!Dumps)
        {
            return;
        }

        lock (_lock)
        {
            LatencyRecord[] records = Recorder.Snapshot(clear: true);
            if (records.Length == 0)
            {
                return;
            }

            Directory.CreateDirectory(DumpDirectory);
            string path = Path.Combine(DumpDirectory, "latency-sim-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".tsv");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; // 0600
            }

            using (var writer = new StreamWriter(path, options))
            {
                LatencyDump.Write(writer, records, TimeProvider.System.TimestampFrequency);
            }

            Console.WriteLine($"Latency dump ({reason}): {records.Length} records -> {path}");
        }
    }

    private static string Format(LatencyStats stats) => stats.Count == 0
        ? "no samples"
        : string.Create(CultureInfo.InvariantCulture, $"p50 {stats.P50Us / 1000:F2} ms, p95 {stats.P95Us / 1000:F2} ms, max {stats.MaxUs / 1000:F2} ms (n={stats.Count})");
}
