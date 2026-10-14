using System.Globalization;
using System.Runtime.InteropServices;
using Foundation;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Diagnostics;

namespace PenBridge.Mac.Services;

/// <summary>
/// Debug latency capture (P3-D): owns the server's <see cref="LatencyRecorder"/> and writes it to
/// <c>~/Library/Logs/PenBridge/latency-*.tsv</c> (<see cref="LatencyDump"/>) at session end, on <c>SIGUSR1</c>, and on
/// quit. Off unless <c>PENBRIDGE_LATENCY_LOG=1</c> is in the environment or the <see cref="DefaultsKey"/> user default
/// is true (<c>defaults write com.blake11.penbridge PenBridgeLatencyLog -bool true</c>); off means no recorder, so
/// the input path only pays a null check. Report with <c>scripts/latency-report.sh</c>.
/// </summary>
/// <remarks>Dumps run on the thread pool, never on the input path. They hold timings only: no keys, PINs or input positions.</remarks>
internal sealed partial class LatencyLog : IDisposable
{
    public const string DefaultsKey = "PenBridgeLatencyLog";

    /// <summary>SIGUSR1 on macOS (not in <see cref="PosixSignal"/>; raw values are accepted on Unix).</summary>
    private const int SigUsr1 = 30;

    private readonly ILogger _log;
    private readonly long _frequency;
    private readonly Lock _fileLock = new();
    private readonly PosixSignalRegistration? _signal;

    private LatencyLog(ILogger log, TimeProvider time)
    {
        _log = log;
        _frequency = time.TimestampFrequency;
        try
        {
            _signal = PosixSignalRegistration.Create((PosixSignal)SigUsr1, OnSignal);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or ArgumentOutOfRangeException)
        {
            Log.SignalUnavailable(_log, ex.GetType().Name);
        }

        Log.Enabled(_log, DumpDirectory);
    }

    /// <summary>Recorder for <c>PenBridgeServerOptions.LatencyRecorder</c>; timestamps come from the server's TimeProvider.</summary>
    public LatencyRecorder Recorder { get; } = new();

    public static string DumpDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "PenBridge");

    /// <summary>A capture when the debug switch is on, else null.</summary>
    public static LatencyLog? CreateIfEnabled(ILoggerFactory loggers, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        ArgumentNullException.ThrowIfNull(time);
        bool enabled = LatencyRecorder.IsEnabledByEnvironment || NSUserDefaults.StandardUserDefaults.BoolForKey(DefaultsKey);
        return enabled ? new LatencyLog(loggers.CreateLogger<LatencyLog>(), time) : null;
    }

    /// <summary>Writes (and drops) everything recorded so far, off the calling thread.</summary>
    public void DumpInBackground(string reason) => _ = Task.Run(() => Dump(reason, clear: true));

    /// <summary>Writes the held records to a new file; returns its path, or null when there was nothing to write.</summary>
    public string? Dump(string reason, bool clear)
    {
        lock (_fileLock)
        {
            LatencyRecord[] records = Recorder.Snapshot(clear);
            if (records.Length == 0)
            {
                return null;
            }

            string path = Path.Combine(DumpDirectory, "latency-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".tsv");
            try
            {
                Directory.CreateDirectory(DumpDirectory);
                using (var writer = new StreamWriter(path, append: false))
                {
                    LatencyDump.Write(writer, records, _frequency);
                }

                Log.Dumped(_log, records.Length, reason, path);
                foreach (string line in LatencyDump.Summarize(LatencyAnalyzer.Analyze(records, _frequency)))
                {
                    Log.Summary(_log, line);
                }

                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.DumpFailed(_log, path, ex);
                return null;
            }
        }
    }

    /// <summary>Stops listening for SIGUSR1 and writes what is left.</summary>
    public void Dispose()
    {
        _signal?.Dispose();
        Dump("quit", clear: true);
    }

    private void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true; // SIGUSR1 would otherwise terminate the app
        _ = Task.Run(() => Dump("SIGUSR1", clear: false));
    }

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Information, "Latency capture on; dumps go to {Directory} (session end, SIGUSR1, quit)")]
        public static partial void Enabled(ILogger logger, string directory);

        [LoggerMessage(2, LogLevel.Information, "Latency dump ({Reason}): {Count} records → {Path}")]
        public static partial void Dumped(ILogger logger, int count, string reason, string path);

        [LoggerMessage(3, LogLevel.Information, "Latency: {Line}")]
        public static partial void Summary(ILogger logger, string line);

        [LoggerMessage(4, LogLevel.Error, "Latency dump to {Path} failed")]
        public static partial void DumpFailed(ILogger logger, string path, Exception exception);

        [LoggerMessage(5, LogLevel.Warning, "SIGUSR1 latency dumps unavailable ({Error}); dumps happen at session end and quit")]
        public static partial void SignalUnavailable(ILogger logger, string error);
    }
}
