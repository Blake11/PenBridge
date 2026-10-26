#if DEBUG
using System.Diagnostics;
using System.Globalization;
using AppKit;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Input;
using PenBridge.Mac.Input;
using PenBridge.Core.Protocol;

namespace PenBridge.Mac.Mirror;

/// <summary>
/// Debug only: runs the mirror pipeline without a phone and writes the Annex-B stream to a file, for ffprobe/ffplay or a
/// VideoToolbox decode check. Off unless the dump path is set.
/// </summary>
/// <remarks>
/// <para>Each value is read from the environment first, then from the app's user defaults (<c>open</c>, which
/// scripts/mac-run.sh uses, drops environment variables: <c>defaults write com.blake11.penbridge PenBridgeMirrorDump
/// /tmp/m.hevc</c>, and <c>defaults delete</c> it after).</para>
/// <list type="table">
/// <item><term>PENBRIDGE_MIRROR_DUMP / PenBridgeMirrorDump</term><description>output file (required); <c>.idx</c> next to it lists every frame</description></item>
/// <item><term>PENBRIDGE_MIRROR_CODEC / PenBridgeMirrorCodec</term><description><c>hevc</c> (default) or <c>h264</c></description></item>
/// <item><term>PENBRIDGE_MIRROR_TARGET / PenBridgeMirrorTarget</term><description><c>display</c> (default, the pen's target display), <c>region</c> (its centre half), <c>window</c> (the frontmost app window, clipped), <c>all</c>, or <c>synthetic</c> (a test pattern, no Screen Recording needed)</description></item>
/// <item><term>PENBRIDGE_MIRROR_SECONDS</term><description>run time, default 10; a keyframe is requested at 1/3 and the bitrate cut to 70 % at 2/3</description></item>
/// <item><term>PENBRIDGE_MIRROR_FPS</term><description>default: the source's refresh rate</description></item>
/// <item><term>PENBRIDGE_MIRROR_SIZE</term><description><c>WxH</c> bound, aspect kept (default: the source's pixel size, at most 3840×2160)</description></item>
/// <item><term>PENBRIDGE_MIRROR_BITRATE</term><description>kbps, default 12000</description></item>
/// <item><term>PENBRIDGE_MIRROR_QUIT</term><description><c>1</c>: quit PenBridge when done</description></item>
/// </list>
/// </remarks>
internal static class MirrorDumpHook
{
    private static MirrorController? _controller;
    private static DumpSink? _sink;

    /// <summary>Starts a dump when configured. Main thread (reads the display list and the window list).</summary>
    public static void StartIfRequested(DisplayTargetResolver resolver, ILoggerFactory loggers)
    {
        string? path = Setting("PENBRIDGE_MIRROR_DUMP", "PenBridgeMirrorDump");
        if (string.IsNullOrEmpty(path) || _controller is not null)
        {
            return;
        }

        ILogger log = loggers.CreateLogger("PenBridge.Mac.Mirror.MirrorDumpHook");
        try
        {
            MirrorCodec codec = string.Equals(Setting("PENBRIDGE_MIRROR_CODEC", "PenBridgeMirrorCodec"), "h264", StringComparison.OrdinalIgnoreCase)
                ? MirrorCodec.H264
                : MirrorCodec.Hevc;
            string kind = (Setting("PENBRIDGE_MIRROR_TARGET", "PenBridgeMirrorTarget") ?? "display").ToLowerInvariant();
            int seconds = Int("PENBRIDGE_MIRROR_SECONDS", "PenBridgeMirrorSeconds") ?? 10;
            int bitrate = Int("PENBRIDGE_MIRROR_BITRATE", "PenBridgeMirrorBitrate") ?? 12000;
            bool quit = Setting("PENBRIDGE_MIRROR_QUIT", "PenBridgeMirrorQuit") == "1";
            (int boundW, int boundH) = Size(Setting("PENBRIDGE_MIRROR_SIZE", "PenBridgeMirrorSize")) ?? (3840, 2160);

            MirrorTarget? target = kind == "synthetic" ? null : BuildTarget(kind, resolver);
            if (target is null && kind != "synthetic")
            {
                MirrorLog.DumpFailed(log, $"no {kind} target");
                return;
            }

            (int srcW, int srcH) = target?.SourcePixelSize() ?? (2160, 1160);
            int fps = Int("PENBRIDGE_MIRROR_FPS", "PenBridgeMirrorFps") ?? (target is null ? 60 : RefreshOf(target));
            (int w, int h) = Fit(srcW, srcH, Math.Min(boundW, srcW), Math.Min(boundH, srcH));
            var settings = new MirrorStreamSettings(w, h, fps, bitrate, codec);

            _sink = new DumpSink(path);
            _controller = new MirrorController(_sink, loggers.CreateLogger<MirrorController>());
            _ = RunAsync(_controller, _sink, target, settings, seconds, quit, log);
        }
#pragma warning disable CA1031 // Debug hook: report and carry on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            MirrorLog.DumpFailed(log, ex.Message);
        }
    }

    private static async Task RunAsync(MirrorController controller, DumpSink sink, MirrorTarget? target, MirrorStreamSettings settings, int seconds, bool quit, ILogger log)
    {
        TimeSpan cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        long start = Stopwatch.GetTimestamp();
        try
        {
            if (target is null)
            {
                await controller.StartSyntheticAsync(settings).ConfigureAwait(false);
            }
            else
            {
                await controller.StartAsync(target, settings).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromSeconds(seconds / 3.0)).ConfigureAwait(false);
            controller.RequestKeyframe();
            await Task.Delay(TimeSpan.FromSeconds(seconds / 3.0)).ConfigureAwait(false);
            MirrorStreamSettings slower = settings with { BitrateKbps = Math.Max(1, settings.BitrateKbps * 7 / 10) };
            if (target is null)
            {
                await controller.ReconfigureSyntheticAsync(slower).ConfigureAwait(false);
            }
            else
            {
                await controller.ReconfigureAsync(target, slower).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromSeconds(seconds / 3.0)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MirrorException or InvalidOperationException)
        {
            MirrorLog.DumpFailed(log, ex.Message);
        }
        finally
        {
            await controller.StopAsync().ConfigureAwait(false);
            double wall = Stopwatch.GetElapsedTime(start).TotalSeconds;
            double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalSeconds;
            sink.Finish(log, wall > 0 ? cpu / wall * 100 : 0);
            controller.Dispose();
            if (quit)
            {
                DispatchQueue.MainQueue.DispatchAsync(() => NSApplication.SharedApplication.Terminate(null));
            }
        }
    }

    private static MirrorTarget? BuildTarget(string kind, DisplayTargetResolver resolver)
    {
        if (!resolver.HasDisplay)
        {
            return null;
        }

        DisplayInfo main = resolver.Target;
        var display = new MirrorDisplay(main.Id, main.Bounds);
        DisplayRect b = main.Bounds;
        switch (kind)
        {
            case "all":
                var all = new MirrorDisplay[resolver.Displays.Count];
                for (int i = 0; i < all.Length; i++)
                {
                    all[i] = new MirrorDisplay(resolver.Displays[i].Id, resolver.Displays[i].Bounds);
                }

                return MirrorTarget.ForAllDisplays(all);
            case "region":
                return MirrorTarget.ForDisplay(display, new DisplayRect(b.X + (b.Width / 4), b.Y + (b.Height / 4), b.Width / 2, b.Height / 2));
            case "window":
                List<WindowListEntry> windows = WindowList.OnScreenAppWindows(Environment.ProcessId);
                if (windows.Count == 0)
                {
                    return null;
                }

                DisplayRect w = windows[0].Bounds;
                foreach (DisplayInfo d in resolver.Displays)
                {
                    DisplayRect db = d.Bounds;
                    double cx = w.X + (w.Width / 2), cy = w.Y + (w.Height / 2);
                    if (cx >= db.X && cx < db.X + db.Width && cy >= db.Y && cy < db.Y + db.Height)
                    {
                        return MirrorTarget.ForDisplay(new MirrorDisplay(d.Id, d.Bounds), w);
                    }
                }

                return MirrorTarget.ForDisplay(display, w);
            default:
                return MirrorTarget.ForDisplay(display, b);
        }
    }

    private static int RefreshOf(MirrorTarget target)
    {
        using var monitor = new DisplayRefreshMonitor();
        return monitor.RefreshHz(target);
    }

    /// <summary>The largest even size with the source's aspect ratio inside the bound.</summary>
    private static (int W, int H) Fit(int srcW, int srcH, int maxW, int maxH)
    {
        double scale = Math.Min(1, Math.Min((double)maxW / srcW, (double)maxH / srcH));
        return (Math.Max(16, (int)(srcW * scale) & ~1), Math.Max(16, (int)(srcH * scale) & ~1));
    }

    private static string? Setting(string env, string key)
        => Environment.GetEnvironmentVariable(env) is { Length: > 0 } v ? v : NSUserDefaults.StandardUserDefaults.StringForKey(key);

    private static int? Int(string env, string key)
        => int.TryParse(Setting(env, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static (int, int)? Size(string? value)
    {
        string[]? parts = value?.Split('x');
        return parts is { Length: 2 }
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
            ? (w, h)
            : null;
    }

    /// <summary>Writes access units and an index line per frame (bytes, keyframe, capture µs, capture→encoded µs).</summary>
    private sealed class DumpSink : IMirrorFrameSink, IDisposable
    {
        private const int MaxSamples = 1 << 16;

        private readonly string _path;
        private readonly FileStream _file;
        private readonly StreamWriter _index;
        private readonly long[] _latencyUs = new long[MaxSamples];
        private readonly Lock _lock = new();
        private int _frames;
        private int _keyframes;
        private long _bytes;
        private bool _finished;

        public DumpSink(string path)
        {
            _path = path;
            _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _index = new StreamWriter(path + ".idx");
            _index.WriteLine("frame,bytes,keyframe,captureUs,latencyUs");
        }

        public void OnFrame(ReadOnlySpan<byte> accessUnit, bool keyframe, long captureTimeUs)
        {
            long latency = MirrorClock.NowUs() - captureTimeUs;
            lock (_lock)
            {
                if (_finished)
                {
                    return;
                }

                _file.Write(accessUnit);
                if (_frames < MaxSamples)
                {
                    _latencyUs[_frames] = latency;
                }

                _index.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{_frames},{accessUnit.Length},{(keyframe ? 1 : 0)},{captureTimeUs},{latency}"));
                _frames++;
                _keyframes += keyframe ? 1 : 0;
                _bytes += accessUnit.Length;
            }
        }

        public void Finish(ILogger log, double cpuPercent)
        {
            lock (_lock)
            {
                _finished = true;
                Dispose();
                int n = Math.Min(_frames, MaxSamples);
                Array.Sort(_latencyUs, 0, n);
                double P(double q) => n == 0 ? 0 : _latencyUs[Math.Min(n - 1, (int)(q * n))] / 1000.0;
                MirrorLog.DumpFinished(log, _frames, _keyframes, _bytes, _path, P(0.5), P(0.95), n == 0 ? 0 : _latencyUs[n - 1] / 1000.0, cpuPercent);
            }
        }

        public void Dispose()
        {
            _file.Dispose();
            _index.Dispose();
        }
    }
}
#endif
