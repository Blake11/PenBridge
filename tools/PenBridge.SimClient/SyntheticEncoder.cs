using System.Diagnostics;
using PenBridge.Core.Mirror;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;

namespace PenBridge.SimClient;

/// <summary>
/// The in-process server's stand-in for the Mac's capture and encoder: it reports a source and both codecs, and while
/// the phone mirrors it feeds <see cref="PenBridgeServer.SendVideoFrame"/> at the epoch's fps with fake Annex-B access
/// units sized to the epoch's bitrate (a keyframe at each epoch start and on request, three times a delta frame).
/// Not decodable video: it exercises the network path (fragmenting, pacing, loss, KeyframeRequests, MirrorReports).
/// </summary>
internal sealed class SyntheticEncoder : IDisposable
{
    /// <summary>The fake captured display: a 14" MacBook Pro panel at 60 Hz.</summary>
    public const int SourceWidth = 3024;

    public const int SourceHeight = 1964;

    public const int SourceRefreshHz = 60;

    private static readonly TimerCallback TickCallback = static s => ((SyntheticEncoder)s!).OnTick();

    private readonly PenBridgeServer _server;
    private readonly Lock _lock = new();
    private readonly ITimer _timer;
    private readonly byte[] _buffer = new byte[VideoDatagram.MaxFrameLength];
    private MirrorStreamConfig? _config;
    private bool _keyframeDue;
    private int _seed;
    private long _framesSubmitted;
    private long _keyframesSubmitted;
    private long _keyframeRequests;
    private long _configs;

    public SyntheticEncoder(PenBridgeServer server)
    {
        _server = server;
        _timer = TimeProvider.System.CreateTimer(TickCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        server.MirrorRequested += (_, r) => Console.WriteLine($"[server] mirror requested: codecs {r.Codecs}, max {r.MaxWidthPx}x{r.MaxHeightPx} @ {r.MaxFps}");
        server.MirrorConfigChanged += (_, c) => OnConfig(c);
        server.MirrorStopped += (_, _) => OnStopped();
        server.KeyframeRequested += (_, _) => OnKeyframeRequested();
        server.SetMirrorEncoders(MirrorCodecs.H264 | MirrorCodecs.Hevc);
        server.SetMirrorSource(SourceWidth, SourceHeight, SourceRefreshHz);
        server.SetMirrorState(MirrorState.Streaming);
    }

    /// <summary>Access units handed to <see cref="PenBridgeServer.SendVideoFrame"/> (the server may still drop some unsent).</summary>
    public long FramesSubmitted => Interlocked.Read(ref _framesSubmitted);

    public long KeyframesSubmitted => Interlocked.Read(ref _keyframesSubmitted);

    /// <summary><see cref="PenBridgeServer.KeyframeRequested"/> events (the server rate-limits the phone's requests).</summary>
    public long KeyframeRequests => Interlocked.Read(ref _keyframeRequests);

    public long Configs => Interlocked.Read(ref _configs);

    public void Dispose() => _timer.Dispose();

    /// <summary>Capture time now: host-monotonic µs on the server's (system) clock.</summary>
    private static long NowUs() => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));

    private void OnConfig(MirrorStreamConfig config)
    {
        Interlocked.Increment(ref _configs);
        Console.WriteLine($"[server] mirror config: epoch {config.Epoch}, {config.Codec} {config.WidthPx}x{config.HeightPx} @ {config.Fps} fps, {config.BitrateKbps} kbps");
        lock (_lock)
        {
            _config = config;
            _keyframeDue = true;
            TimeSpan interval = TimeSpan.FromSeconds(1.0 / config.Fps);
            _timer.Change(interval, interval);
        }
    }

    private void OnStopped()
    {
        Console.WriteLine("[server] mirror stopped");
        lock (_lock)
        {
            _config = null;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnKeyframeRequested()
    {
        Interlocked.Increment(ref _keyframeRequests);
        lock (_lock)
        {
            _keyframeDue = true;
        }
    }

    private void OnTick()
    {
        lock (_lock) // a late tick waits instead of overlapping (the buffer is shared)
        {
            if (_config is not { } config)
            {
                return;
            }

            bool keyframe = _keyframeDue;
            _keyframeDue = false;
            int delta = Math.Clamp(config.BitrateKbps * 1000 / 8 / config.Fps, 64, VideoDatagram.MaxFrameLength / 3);
            int length = keyframe ? delta * 3 : delta;
            Fill(_buffer.AsSpan(0, length), config.Codec, keyframe, ++_seed);
            _server.SendVideoFrame(_buffer.AsSpan(0, length), keyframe, NowUs());
            Interlocked.Increment(ref _framesSubmitted);
            if (keyframe)
            {
                Interlocked.Increment(ref _keyframesSubmitted);
            }
        }
    }

    /// <summary>An Annex-B start code, a NAL header of the right type (IDR or non-IDR slice) and a byte pattern.</summary>
    private static void Fill(Span<byte> au, MirrorCodec codec, bool keyframe, int seed)
    {
        for (int i = 0; i < au.Length; i++)
        {
            byte b = (byte)((i * 31) + seed);
            au[i] = b == 0 ? (byte)1 : b; // no zero bytes, so no stray start codes
        }

        au[0] = 0;
        au[1] = 0;
        au[2] = 0;
        au[3] = 1;
        if (codec == MirrorCodec.Hevc)
        {
            au[4] = keyframe ? (byte)(19 << 1) : (byte)(1 << 1); // IDR_W_RADL / TRAIL_R
            au[5] = 1;
        }
        else
        {
            au[4] = keyframe ? (byte)0x65 : (byte)0x41; // IDR / non-IDR slice
        }
    }
}
