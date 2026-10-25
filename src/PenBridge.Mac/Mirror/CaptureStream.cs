using CoreFoundation;
using CoreGraphics;
using CoreMedia;
using CoreVideo;
using Foundation;
using ScreenCaptureKit;

namespace PenBridge.Mac.Mirror;

/// <summary>
/// One ScreenCaptureKit <c>SCStream</c> of one display: NV12, queue depth 3, the cursor shown, scaled to the requested
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
            QueueDepth = 3,
            MinimumFrameInterval = new CMTime(1, fps),
            WeakColorMatrix = CGDisplayStreamYCbCrMatrixOptionKeys.Itu_R_709_2,
            WeakColorSpaceName = CGColorSpaceNames.Srgb,
        };
        using CGColor black = new(0, 0, 0, 1);
        config.BackgroundColor = black;
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

    /// <summary>Stops capturing (errors are ignored: the stream may already have stopped).</summary>
    public Task StopAsync()
    {
        _active = false;
        SCStream? stream = _stream;
        _stream = null;
        if (stream is null)
        {
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stream.StopCapture(_ =>
        {
            stream.Dispose();
            done.TrySetResult();
        });
        return done.Task;
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
