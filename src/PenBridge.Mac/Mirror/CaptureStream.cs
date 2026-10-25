using CoreFoundation;
using CoreGraphics;
using CoreMedia;
using CoreVideo;
using Foundation;
using ScreenCaptureKit;

namespace PenBridge.Mac.Mirror;

/// <summary>
/// One ScreenCaptureKit <c>SCStream</c> of one display: NV12, queue depth 6, the cursor shown, scaled to the requested
/// size, at most one frame per <c>1/fps</c> (spec "Heads → Mac → Capture"). ScreenCaptureKit delivers no frames while
/// the captured content doesn't change, so a static screen costs nothing.
/// </summary>
/// <remarks>
/// The sample-buffer callback is exported with raw pointer parameters, so no managed wrapper is created per frame; the
/// frame goes straight to <see cref="FrameHandler"/> on the capture queue. Stop notifications are forwarded to
/// <see cref="StoppedHandler"/> (ScreenCaptureKit's thread).
/// </remarks>
internal sealed class CaptureStream : NSObject, ISCStreamOutput, ISCStreamDelegate
{
    /// <summary>SCStreamOutputTypeScreen.</summary>
    private const nint OutputTypeScreen = 0;

    /// <summary>
    /// Surfaces in the ScreenCaptureKit pool (its maximum is 8). Held at 120 fps on the single-display path: the controller's
    /// retained last frame (1) + frames inside the encoder (1–2 in low-latency mode) + the frame being handled in the
    /// callback (1) + the one ScreenCaptureKit is rendering into (1) = 5, plus one of slack so a late encoder output can't
    /// starve the pool. The All-displays path holds one surface per stream (the compositor's slot), which this covers
    /// too. A 2160x1160 NV12 frame is about 3.8 MB (4K: 12.4 MB), so 6 surfaces are about 23 MB (4K: 75 MB) per stream (live
    /// check: RSS, and no starvation at 120 fps).
    /// </summary>
    private const int QueueDepth = 6;

    /// <summary>How long a stop waits for ScreenCaptureKit's completion before giving up.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The stream background. <c>SCStreamConfiguration.backgroundColor</c> is an <c>assign</c> property (not retained),
    /// and SCStream copies the configuration later: a colour disposed after setting it crashes in <c>CGColorCreateCopy</c>.
    /// So one colour lives for the process.
    /// </summary>
    private static readonly CGColor Black = new(0, 0, 0, 1);

    private SCStream? _stream;
    private volatile bool _active;

    public CaptureStream(int index, uint displayId)
    {
        Index = index;
        DisplayId = displayId;
    }

    /// <summary>Called on the capture queue with a CMSampleBufferRef (valid only during the call).</summary>
    public Action<CaptureStream, IntPtr>? FrameHandler { get; set; }

    /// <summary>Called when the system stopped the stream (display gone, permission revoked, …).</summary>
    public Action<CaptureStream, NSError?>? StoppedHandler { get; set; }

    /// <summary>The position of this stream in the target's display list.</summary>
    public int Index { get; }

    /// <summary>The captured display.</summary>
    public uint DisplayId { get; }

    /// <summary>False once stopping began: frames still queued are ignored.</summary>
    public bool Active => _active;

    /// <summary>A stream configuration for <paramref name="sourceRect"/> (display-local points) scaled to width × height.</summary>
    public static SCStreamConfiguration Configuration(CGRect sourceRect, int width, int height, int fps)
    {
        var config = new SCStreamConfiguration
        {
            Width = (nuint)width,
            Height = (nuint)height,
            SourceRect = sourceRect,
            ScalesToFit = true,
            // Fill the stream exactly (a sub-pixel aspect change at most) so the image lines up with the pen's active area.
            PreservesAspectRatio = false,
            ShowsCursor = true,
            PixelFormat = CVPixelFormatType.CV420YpCbCr8BiPlanarVideoRange,
            QueueDepth = QueueDepth,
            MinimumFrameInterval = new CMTime(1, fps),
            WeakColorMatrix = CGDisplayStreamYCbCrMatrixOptionKeys.Itu_R_709_2,
            WeakColorSpaceName = CGColorSpaceNames.Srgb,
        };
        config.BackgroundColor = Black;
        return config;
    }

    /// <summary>Starts capturing; frames arrive on <paramref name="queue"/>.</summary>
    /// <exception cref="NSErrorException">ScreenCaptureKit refused (permission, display gone).</exception>
    public async Task StartAsync(SCContentFilter filter, SCStreamConfiguration configuration, DispatchQueue queue)
    {
        _stream = new SCStream(filter, configuration, this);
        if (!_stream.AddStreamOutput(this, SCStreamOutputType.Screen, queue, out NSError? error))
        {
            throw new NSErrorException(error!);
        }

        _active = true;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stream.StartCapture(e =>
        {
            if (e is null)
            {
                done.TrySetResult();
            }
            else
            {
                done.TrySetException(new NSErrorException(e));
            }
        });
        try
        {
            await done.Task.ConfigureAwait(false);
        }
        catch
        {
            _active = false;
            throw;
        }
    }

    /// <summary>Applies a new size, source rectangle or frame rate without restarting the stream.</summary>
    public Task UpdateAsync(SCStreamConfiguration configuration)
    {
        if (_stream is null)
        {
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stream.UpdateConfiguration(configuration, e =>
        {
            if (e is null)
            {
                done.TrySetResult();
            }
            else
            {
                done.TrySetException(new NSErrorException(e));
            }
        });
        return done.Task;
    }

    /// <summary>
    /// Stops capturing, detaches the output and drains <paramref name="queue"/> so no callback can still be pending, then
    /// releases the stream (errors are ignored: the stream may already have stopped). After this it is safe to dispose
    /// this object: exported callback targets must outlive their queue's pending blocks.
    /// </summary>
    /// <returns>False when ScreenCaptureKit didn't confirm the stop within the timeout.</returns>
    public async Task<bool> StopAsync(DispatchQueue queue)
    {
        _active = false;
        SCStream? stream = _stream;
        _stream = null;
        if (stream is null)
        {
            return true;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.StopCapture(_ => done.TrySetResult());
        bool confirmed = await Task.WhenAny(done.Task, Task.Delay(StopTimeout)).ConfigureAwait(false) == done.Task;
        _ = stream.RemoveStreamOutput(this, SCStreamOutputType.Screen, out _);
        queue.DispatchSync(static () => { }); // pending frame blocks (they see _active == false) have run
        stream.Dispose();
        return confirmed;
    }

    [Export("stream:didOutputSampleBuffer:ofType:")]
    public void DidOutputSampleBufferRaw(IntPtr stream, IntPtr sampleBuffer, nint type)
    {
        if (type == OutputTypeScreen && _active && sampleBuffer != IntPtr.Zero)
        {
            FrameHandler?.Invoke(this, sampleBuffer);
        }
    }

    [Export("stream:didStopWithError:")]
    public void DidStop(SCStream stream, NSError error)
    {
        if (_active)
        {
            _active = false;
            StoppedHandler?.Invoke(this, error);
        }
    }
}
