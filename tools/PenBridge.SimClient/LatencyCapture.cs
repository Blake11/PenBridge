using System.Globalization;
using PenBridge.Core.Diagnostics;

namespace PenBridge.SimClient;

/// <summary>
/// The SimClient server's latency capture, on with <c>PENBRIDGE_LATENCY_LOG=1</c> like the Mac app: dumps go to the
/// same folder (<c>~/Library/Logs/PenBridge</c> on macOS, else <c>~/.penbridge/logs</c>) as <c>latency-sim-*.tsv</c>, at
/// session end and when the server stops. Validates the pipeline on one machine (one clock, so the offset model only
/// has to absorb the two session epochs).
/// </summary>
internal sealed class LatencyCapture
{
    private readonly Lock _lock = new();

    private LatencyCapture()
    {
    }

    public LatencyRecorder Recorder { get; } = new();

    public static string DumpDirectory { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "PenBridge")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".penbridge", "logs");

    public static LatencyCapture? CreateIfEnabled() => LatencyRecorder.IsEnabledByEnvironment ? new LatencyCapture() : null;

    public void Dump(string reason)
    {
        lock (_lock)
        {
            LatencyRecord[] records = Recorder.Snapshot(clear: true);
            if (records.Length == 0)
            {
                return;
            }

            Directory.CreateDirectory(DumpDirectory);
            string path = Path.Combine(DumpDirectory, "latency-sim-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".tsv");
            using (var writer = new StreamWriter(path, append: false))
            {
                LatencyDump.Write(writer, records, TimeProvider.System.TimestampFrequency);
            }

            Console.WriteLine($"Latency dump ({reason}): {records.Length} records -> {path}");
        }
    }
}
