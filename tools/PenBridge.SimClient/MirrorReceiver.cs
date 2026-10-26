using System.Threading.Channels;
using PenBridge.Core.Mirror;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;

namespace PenBridge.SimClient;

/// <summary>
/// The SimClient's "decoder" for <c>mirror</c>: counts frames, keyframes and frame-number gaps per epoch, reports
/// each frame as rendered on arrival (so the Mac's video latency spans capture → reassembled here), and optionally
/// appends the Annex-B bytes to a file on its own writer task (the receive thread never waits for the disk).
/// </summary>
internal sealed class MirrorReceiver : IAsyncDisposable
{
    private readonly Channel<byte[]>? _queue;
    private readonly Task _writer = Task.CompletedTask;
    private readonly Lock _lock = new();
    private PenBridgeClient? _client;
    private byte _epoch;
    private uint _lastNumber;
    private long _frames;
    private long _keyframes;
    private long _gaps;
    private long _bytes;

    public MirrorReceiver(string? outPath)
    {
        if (outPath is null)
        {
            return;
        }

        var stream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
        _queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _writer = WriteAsync(stream, _queue.Reader);
    }

    public long Frames => Interlocked.Read(ref _frames);

    public long Keyframes => Interlocked.Read(ref _keyframes);

    /// <summary>Frame numbers skipped within an epoch: frames the Mac numbered that never reached this "decoder".</summary>
    public long Gaps => Interlocked.Read(ref _gaps);

    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Subscribes to <paramref name="client"/>'s mirroring events (before it connects).</summary>
    public void Attach(PenBridgeClient client)
    {
        _client = client;
        client.MirrorConfigReceived += (_, c) =>
            Console.WriteLine($"Mirror config: epoch {c.Epoch}, {c.Codec} {c.WidthPx}x{c.HeightPx} @ {c.Fps} fps, {c.BitrateKbps} kbps");
        client.MirrorStateChanged += (_, e) =>
            Console.WriteLine($"Mirror state: {e.State}{(e.Reason == MirrorReason.None ? string.Empty : " (" + e.Reason + ")")}");
        client.VideoFrameReady += (_, f) => OnFrame(f);
    }

    /// <summary>Flushes and closes the output file.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue?.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private static async Task WriteAsync(FileStream stream, ChannelReader<byte[]> reader)
    {
        await using (stream.ConfigureAwait(false))
        {
            await foreach (byte[] au in reader.ReadAllAsync().ConfigureAwait(false))
            {
                await stream.WriteAsync(au).ConfigureAwait(false);
            }
        }
    }

    private void OnFrame(VideoFrame frame)
    {
        using (frame)
        {
            lock (_lock)
            {
                if (frame.Epoch != _epoch || _lastNumber == 0)
                {
                    _epoch = frame.Epoch;
                }
                else if (frame.FrameNumber > _lastNumber + 1)
                {
                    _gaps += frame.FrameNumber - _lastNumber - 1;
                }

                _lastNumber = frame.FrameNumber;
            }

            Interlocked.Increment(ref _frames);
            Interlocked.Add(ref _bytes, frame.Length);
            if (frame.IsKeyframe)
            {
                Interlocked.Increment(ref _keyframes);
            }

            _queue?.Writer.TryWrite(frame.Span.ToArray());
            if (_client is { } client)
            {
                client.ReportFrameRendered(frame.FrameNumber, client.NowUs);
            }
        }
    }
}
