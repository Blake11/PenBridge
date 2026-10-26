#if DEBUG
using System.Diagnostics;
using Android.OS;
using Android.Util;
using PenBridge.Core.Protocol;

namespace PenBridge.Android.Mirror;

/// <summary>
/// Debug only: plays an Annex-B test file through the real mirror path (<see cref="MirrorView.Show"/> +
/// <see cref="MirrorView.Feed"/>) at the file's frame rate, looping, and logs every 2 s to logcat (tag PenBridge):
/// frames fed / rendered / gated, render rate, and decode and render latency p50/p95. With <c>loss</c> one frame is
/// skipped every 3 s to exercise the keyframe gate (the decoder then waits for the file's next keyframe). With
/// <c>epochs</c> the same config is shown again every 3 s (a new epoch with the same codec and size, as a bitrate step
/// would send) before a non-key frame, with frame numbers restarting: the gate closes, asks for one keyframe and
/// reopens at the file's next keyframe.
/// The feeding thread stands in for the network receive thread: below normal priority, off the UI thread.
/// </summary>
internal sealed class MirrorTestPlayer : IDisposable
{
    private const string Tag = "PenBridge";
    private readonly MirrorView _view;
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly Thread _thread;
    private readonly string _path;
    private readonly MirrorCodec _codec;
    private readonly int _fps;
    private readonly bool _loss;
    private readonly bool _epochs;
    private readonly int _width;
    private readonly int _height;
    private volatile bool _running = true;
    private long _rendered;
    private long _keyframeSignals;

    public MirrorTestPlayer(MirrorView view, string path, MirrorCodec codec, int width, int height, int fps, bool loss, bool epochs = false)
    {
        _epochs = epochs;
        _view = view;
        _path = path;
        _codec = codec;
        _width = width;
        _height = height;
        _fps = fps;
        _loss = loss;
        _view.FrameRendered = (_, _) => Interlocked.Increment(ref _rendered);
        _view.KeyframeNeeded = () => Interlocked.Increment(ref _keyframeSignals);
        _view.Error = reason => Log.Warn(Tag, "Mirror test: decoder error: " + reason);
        _thread = new Thread(Run) { IsBackground = true, Name = "PenBridge.MirrorTest", Priority = System.Threading.ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(TimeSpan.FromSeconds(2));
        _view.FrameRendered = null;
        _view.KeyframeNeeded = null;
        _view.Error = null;
        _ui.Dispose();
    }

    private void Run()
    {
        try
        {
            byte[] data = File.ReadAllBytes(_path);
            List<AnnexBAccessUnits.Unit> units = AnnexBAccessUnits.Split(data, _codec);
            int keys = 0;
            foreach (AnnexBAccessUnits.Unit u in units)
            {
                keys += u.Keyframe ? 1 : 0;
            }

            Log.Info(Tag, $"Mirror test: {_path}, {_codec.Name()} {_width}x{_height}@{_fps}, {units.Count} access units, {keys} keyframes{(_loss ? ", loss every 3 s" : string.Empty)}{(_epochs ? ", new epoch every 3 s" : string.Empty)}");
            if (units.Count == 0)
            {
                return;
            }

            var config = new MirrorVideoConfig(_codec, _width, _height, _fps);
            _ui.Post(() => _view.Show(config));
            Thread.Sleep(300); // surface + decoder start

            long interval = Stopwatch.Frequency / _fps;
            long lossEvery = 3 * _fps;
            long next = Stopwatch.GetTimestamp();
            long logAt = next + (2 * Stopwatch.Frequency);
            long lastRendered = 0;
            long lastLog = next;
            uint frame = 0;
            long sent = 0;
            int i = 0;
            while (_running)
            {
                AnnexBAccessUnits.Unit u = units[i];
                i = (i + 1) % units.Count;
                frame++;
                sent++;
                if (_epochs && sent % lossEvery == 0 && !u.Keyframe)
                {
                    using var shown = new ManualResetEventSlim();
                    _ui.Post(() =>
                    {
                        _view.Show(config); // shown already: a new epoch, same codec and size
                        shown.Set();
                    });
                    shown.Wait(TimeSpan.FromSeconds(1));
                    frame = 1;
                    Log.Info(Tag, "Mirror test: new epoch (same config)");
                }

                if (!_loss || frame % lossEvery != 0)
                {
                    _view.Feed(data.AsSpan(u.Offset, u.Length), u.Keyframe, frame);
                }

                next += interval;
                long now = Stopwatch.GetTimestamp();
                if (now >= logAt)
                {
                    long rendered = Interlocked.Read(ref _rendered);
                    double seconds = (now - lastLog) / (double)Stopwatch.Frequency;
                    LogStats((rendered - lastRendered) / seconds);
                    lastRendered = rendered;
                    lastLog = now;
                    logAt = now + (2 * Stopwatch.Frequency);
                }

                long wait = next - Stopwatch.GetTimestamp();
                if (wait > 0)
                {
                    Thread.Sleep(TimeSpan.FromTicks(wait * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
                }
                else if (wait < -interval * 4)
                {
                    next = Stopwatch.GetTimestamp(); // fell behind: don't burst to catch up
                }
            }
        }
        catch (Exception e) when (e is IOException or Java.Lang.Exception)
        {
            Log.Warn(Tag, "Mirror test failed: " + e.Message);
        }
        finally
        {
            _ui.Post(_view.Hide);
        }
    }

    private void LogStats(double renderFps)
    {
        MirrorDecoder d = _view.Decoder;
        d.DecodeLatency.TryGet(out long dec50, out long dec95, out int n);
        d.RenderLatency.TryGet(out long ren50, out long ren95, out _);
        Log.Info(Tag, $"Mirror test: fed {d.FramesFed}, gated {d.FramesGated}, keyframe signals {Interlocked.Read(ref _keyframeSignals)}, rendered {Interlocked.Read(ref _rendered)} ({renderFps:F1} fps); decode p50/p95 {dec50 / 1000.0:F1}/{dec95 / 1000.0:F1} ms, queue-to-screen p50/p95 {ren50 / 1000.0:F1}/{ren95 / 1000.0:F1} ms (n={n})");
    }
}
#endif
