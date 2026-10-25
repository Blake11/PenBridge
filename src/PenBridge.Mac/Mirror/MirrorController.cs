using CoreFoundation;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PenBridge.Core.Input;
using ScreenCaptureKit;

namespace PenBridge.Mac.Mirror;

/// <summary>
/// Capture + hardware encode of the mirror stream (ADR-0038, spec "Heads → Mac"): ScreenCaptureKit streams of the
/// target into a VideoToolbox encoder, Annex-B access units out to an <see cref="IMirrorFrameSink"/>. The head wires it
/// to Core's server events (MirrorRequested → <see cref="StartAsync"/>, MirrorConfigChanged → <see cref="ReconfigureAsync"/>,
/// KeyframeRequested → <see cref="RequestKeyframe"/>, MirrorStopped → <see cref="StopAsync"/>) and the sink to
/// <c>SendVideoFrame</c>.
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> Control calls may come from any thread and are serialized (one at a time). Frames are captured
/// and submitted to the encoder on one serial dispatch queue at user-initiated QoS, below the input path's
/// user-interactive priority; the encoder's output callback runs on a VideoToolbox thread. <see cref="Failed"/> is
/// raised on a thread-pool thread, never on the capture queue.</para>
/// <para><b>Idle.</b> Nothing runs while stopped. While running, ScreenCaptureKit delivers no frames for a static screen
/// (frames with a status other than Complete are skipped), so nothing is encoded either. A keyframe request on a static
/// screen re-encodes the last captured frame (kept retained: one of the three queue buffers).</para>
/// <para><b>Per frame.</b> The single-display path creates no managed objects: raw sample buffers in, raw pixel buffers
/// to the encoder, the access unit in a reused buffer. All displays composites through Core Image
/// (<see cref="DisplayCompositor"/>), which allocates a few wrappers per composite.</para>
/// <para>Encoder failures recreate the encoder once (a GPU reset invalidates sessions); a second failure raises
/// <see cref="Failed"/> with <see cref="MirrorFailure.EncoderFailed"/>, after which no frames are produced until the
/// next start or reconfigure.</para>
/// </remarks>
public sealed class MirrorController : IDisposable
{
    private static readonly Lock CodecsLock = new();
    private static MirrorCodecs? _hardwareCodecs;

    private readonly IMirrorFrameSink _sink;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _control = new(1, 1);
    private readonly DispatchQueue _queue;
    private readonly Action _encodeStillAction;
    private readonly IntPtr _statusKey = SCStreamFrameInfoKeys.Status.Handle;

    // Control side (under _control).
    private CaptureStream[] _streams = [];
    private MirrorTarget? _target;
    private MirrorStreamSettings _settings;
    private bool _running;
    private bool _disposed;

    // Capture queue only.
    private HardwareEncoder? _encoder;
    private DisplayCompositor? _compositor;
    private IntPtr _lastFrame;
    private long _lastFrameUs;
    private long _lastComposeUs;
    private bool _composePending;
    private bool _recreatedAfterFailure;

    // Any thread.
    private int _keyframeRequested;

    public MirrorController(IMirrorFrameSink sink, ILogger<MirrorController>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _queue = new DispatchQueue(
            "com.blake11.penbridge.mirror",
            new DispatchQueue.Attributes { QualityOfService = DispatchQualityOfService.UserInitiated },
            null);
        _encodeStillAction = EncodeStillIfRequested;
    }

    /// <summary>Raised (thread pool) when capture or encode stopped by itself; the stream is dead until restarted.</summary>
    public event EventHandler<MirrorFailedEventArgs>? Failed;

    /// <summary>True between a successful <see cref="StartAsync"/> and <see cref="StopAsync"/>.</summary>
    public bool IsRunning => Volatile.Read(ref _running);

    /// <summary>
    /// The codecs this Mac encodes in hardware in real time (for <c>SetMirrorEncoders</c>). Detected once by creating a
    /// test session of each (a few ms); call it off the main thread the first time.
    /// </summary>
    public static MirrorCodecs HardwareCodecs(ILogger? logger = null)
    {
        lock (CodecsLock)
        {
            _hardwareCodecs ??= HardwareEncoder.DetectHardwareCodecs(logger ?? NullLogger.Instance);
            return _hardwareCodecs.Value;
        }
    }

    /// <summary>
    /// The highest frame rate the hardware encoder reports it sustains in real time for this size and codec
    /// (<c>kVTCompressionPropertyKey_MaximumRealTimeFrameRate</c>), or 0 when it doesn't say. Creates a test session.
    /// </summary>
    public static double MaxRealTimeFps(int width, int height, MirrorCodec codec, ILogger? logger = null)
        => HardwareEncoder.MaxRealTimeFps(width, height, codec, logger ?? NullLogger.Instance);

    /// <summary>Starts capturing <paramref name="target"/> and encoding it with <paramref name="settings"/>.</summary>
    /// <exception cref="MirrorException">
    /// <see cref="MirrorFailure.PermissionDenied"/> (Screen Recording), <see cref="MirrorFailure.EncoderFailed"/> (no
    /// hardware session; fall back to H.264 for HEVC), or <see cref="MirrorFailure.CaptureFailed"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">Already running.</exception>
    public async Task StartAsync(MirrorTarget target, MirrorStreamSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        settings.Validate();
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                throw new InvalidOperationException("Mirroring is already running.");
            }

            await StartCoreAsync(target, settings).ConfigureAwait(false);
        }
        finally
        {
            _control.Release();
        }
    }

    /// <summary>
    /// Applies a new target, size, fps, bitrate or codec. Fps and bitrate change in place; a new size or codec makes a
    /// new encoder (its first frame is a keyframe); a new set of displays restarts capture.
    /// </summary>
    /// <exception cref="MirrorException">As <see cref="StartAsync"/>; the stream is stopped then.</exception>
    /// <exception cref="InvalidOperationException">Not running.</exception>
    public async Task ReconfigureAsync(MirrorTarget target, MirrorStreamSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        settings.Validate();
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_running)
            {
                throw new InvalidOperationException("Mirroring is not running.");
            }

            try
            {
                await ReconfigureCoreAsync(target, settings).ConfigureAwait(false);
            }
            catch (MirrorException)
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _control.Release();
        }
    }

    /// <summary>
    /// Encodes the next frame as a keyframe; on a static screen, re-encodes the last frame now. Rate limiting and epoch
    /// checks are the caller's (Core). Any thread, non-blocking.
    /// </summary>
    public void RequestKeyframe()
    {
        Interlocked.Exchange(ref _keyframeRequested, 1);
        if (IsRunning)
        {
            _queue.DispatchAsync(_encodeStillAction);
        }
    }

    /// <summary>Stops capture and releases the encoder. No-op when stopped.</summary>
    public async Task StopAsync()
    {
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _control.Release();
        }
    }

    /// <summary>Stops without waiting for ScreenCaptureKit (best effort; prefer <see cref="StopAsync"/>).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (CaptureStream s in _streams)
        {
            _ = s.StopAsync();
        }

        _streams = [];
        Volatile.Write(ref _running, false);
        _queue.DispatchAsync(ReleaseQueueState);
    }

    private async Task StartCoreAsync(MirrorTarget target, MirrorStreamSettings settings)
    {
        if (!ScreenCapturePermission.IsGranted)
        {
            throw new MirrorException(MirrorFailure.PermissionDenied, "Screen Recording permission is not granted.");
        }

        SCShareableContent content;
        try
        {
            content = await SCShareableContent.GetShareableContentAsync(false, true).ConfigureAwait(false);
        }
        catch (NSErrorException ex)
        {
            throw CaptureError(ex, "Listing the shareable content failed");
        }

        using (content)
        {
            MirrorDisplay[] displays = target.Displays.ToArray();
            var scDisplays = new SCDisplay[displays.Length];
            for (int i = 0; i < displays.Length; i++)
            {
                scDisplays[i] = FindDisplay(content, displays[i].DisplayId)
                    ?? throw new MirrorException(MirrorFailure.CaptureFailed, $"Display {displays[i].DisplayId} is not available for capture.");
            }

            SCRunningApplication[] own = OwnApplication(content);
            HardwareEncoder encoder = HardwareEncoder.Create(settings, _sink, _logger);
            DisplayCompositor? compositor = target.IsAllDisplays ? CreateCompositor(target, settings) : null;
            _queue.DispatchSync(() =>
            {
                _encoder = encoder;
                _compositor = compositor;
                _recreatedAfterFailure = false;
                _lastComposeUs = 0;
                _composePending = false;
            });

            var streams = new CaptureStream[displays.Length];
            try
            {
                for (int i = 0; i < displays.Length; i++)
                {
                    streams[i] = new CaptureStream(i, displays[i].DisplayId)
                    {
                        FrameHandler = OnFrame,
                        StoppedHandler = OnStreamStopped,
                    };
                    using var filter = new SCContentFilter(scDisplays[i], own, [], SCContentFilterOption.Exclude);
                    using SCStreamConfiguration config = ConfigurationFor(target, i, settings);
                    await streams[i].StartAsync(filter, config, _queue).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is NSErrorException or MirrorException)
            {
                foreach (CaptureStream? s in streams)
                {
                    if (s is not null)
                    {
                        await s.StopAsync().ConfigureAwait(false);
                        s.Dispose();
                    }
                }

                _queue.DispatchSync(ReleaseQueueState);
                throw ex is NSErrorException ns ? CaptureError(ns, "Starting the capture stream failed") : ex;
            }

            _streams = streams;
            _target = target;
            _settings = settings;
            Volatile.Write(ref _running, true);
            DisplayRectLog(target, settings);
        }
    }

    private async Task ReconfigureCoreAsync(MirrorTarget target, MirrorStreamSettings settings)
    {
        MirrorTarget current = _target!;
        if (!current.SameDisplays(target))
        {
            await StopCoreAsync().ConfigureAwait(false);
            await StartCoreAsync(target, settings).ConfigureAwait(false);
            MirrorLog.Reconfigured(_logger, settings.Width, settings.Height, settings.Fps, settings.BitrateKbps, settings.Codec.ToString(), "restart");
            return;
        }

        MirrorStreamSettings old = _settings;
        bool captureChanged = old.Width != settings.Width || old.Height != settings.Height || old.Fps != settings.Fps || !current.SameAs(target);

        // Capture first: until the encoder follows, frames of the new size are skipped (size check in Submit).
        if (captureChanged)
        {
            try
            {
                for (int i = 0; i < _streams.Length; i++)
                {
                    using SCStreamConfiguration config = ConfigurationFor(target, i, settings);
                    await _streams[i].UpdateAsync(config).ConfigureAwait(false);
                }
            }
            catch (NSErrorException ex)
            {
                throw CaptureError(ex, "Updating the capture stream failed");
            }
        }

        _target = target;
        ApplyEncoderSettings(settings, target.IsAllDisplays ? target : null, captureChanged);
    }

    /// <summary>A new encoder for a new size or codec (and a new compositor for All displays), else fps/bitrate in place.</summary>
    private void ApplyEncoderSettings(MirrorStreamSettings settings, MirrorTarget? compositeTarget, bool captureChanged)
    {
        MirrorStreamSettings old = _settings;
        bool newEncoder = old.Width != settings.Width || old.Height != settings.Height || old.Codec != settings.Codec;
        if (newEncoder)
        {
            HardwareEncoder encoder = HardwareEncoder.Create(settings, _sink, _logger);
            DisplayCompositor? compositor = compositeTarget is null ? null : CreateCompositor(compositeTarget, settings);
            _queue.DispatchSync(() =>
            {
                HardwareEncoder? previous = _encoder;
                _encoder = encoder;
                _recreatedAfterFailure = false;
                previous?.Dispose();
                if (compositor is not null)
                {
                    _compositor?.Dispose();
                    _compositor = compositor;
                }
            });
        }
        else if (old.Fps != settings.Fps || old.BitrateKbps != settings.BitrateKbps)
        {
            _queue.DispatchSync(() => _encoder?.SetRate(settings.Fps, settings.BitrateKbps));
        }

        _settings = settings;
        MirrorLog.Reconfigured(_logger, settings.Width, settings.Height, settings.Fps, settings.BitrateKbps, settings.Codec.ToString(),
            newEncoder ? "new encoder" : captureChanged ? "capture updated" : "rate");
        if (newEncoder)
        {
            // A static screen sends no new frame: the new epoch's keyframe comes from the last one if it has the size.
            RequestKeyframe();
        }
    }

    private async Task StopCoreAsync()
    {
        if (!_running)
        {
            return;
        }

        Volatile.Write(ref _running, false);
        foreach (CaptureStream s in _streams)
        {
            await s.StopAsync().ConfigureAwait(false);
            s.Dispose();
        }

#if DEBUG
        _synthetic?.Dispose();
        _synthetic = null;
#endif

        _streams = [];
        _target = null;
        _queue.DispatchSync(ReleaseQueueState);
        MirrorLog.Stopped(_logger);
    }

#if DEBUG
    private SyntheticFrameSource? _synthetic;

    /// <summary>Debug: the synthetic source while <see cref="StartSyntheticAsync"/> runs (pause it for a static screen).</summary>
    internal SyntheticFrameSource? Synthetic => _synthetic;

    /// <summary>
    /// Debug: runs the encoder path (keyframes, still re-encode, reconfigure, sink) on <see cref="SyntheticFrameSource"/>
    /// frames instead of ScreenCaptureKit, so it can be checked without the Screen Recording permission.
    /// </summary>
    internal async Task StartSyntheticAsync(MirrorStreamSettings settings)
    {
        settings.Validate();
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                throw new InvalidOperationException("Mirroring is already running.");
            }

            HardwareEncoder encoder = HardwareEncoder.Create(settings, _sink, _logger);
            _queue.DispatchSync(() =>
            {
                _encoder = encoder;
                _recreatedAfterFailure = false;
            });
            _settings = settings;
            _synthetic = new SyntheticFrameSource(settings.Width, settings.Height, settings.Fps, OnSyntheticFrame);
            Volatile.Write(ref _running, true);
            _synthetic.Start();
        }
        finally
        {
            _control.Release();
        }
    }

    /// <summary>Debug: <see cref="ReconfigureAsync"/> for the synthetic source.</summary>
    internal async Task ReconfigureSyntheticAsync(MirrorStreamSettings settings)
    {
        settings.Validate();
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_synthetic is null)
            {
                throw new InvalidOperationException("The synthetic source is not running.");
            }

            MirrorStreamSettings old = _settings;
            _synthetic.Configure(settings.Width, settings.Height, settings.Fps);
            ApplyEncoderSettings(settings, null, old.Width != settings.Width || old.Height != settings.Height || old.Fps != settings.Fps);
        }
        finally
        {
            _control.Release();
        }
    }

    /// <summary>Debug: the current encoder's dropped and failed frame counts.</summary>
    internal (int Dropped, int Errors) EncoderCounters()
    {
        (int, int) result = default;
        _queue.DispatchSync(() => result = _encoder is null ? default : (_encoder.DroppedFrames, _encoder.OutputErrors));
        return result;
    }

    // The synthetic thread hands frames to the capture queue like ScreenCaptureKit does.
    private void OnSyntheticFrame(IntPtr pixels, long captureUs) => _queue.DispatchSync(() =>
    {
        if (_encoder is not null)
        {
            Remember(pixels, captureUs);
            Submit(pixels, captureUs);
        }
    });
#endif

    private void ReleaseQueueState()
    {
        _encoder?.Dispose();
        _encoder = null;
        _compositor?.Dispose();
        _compositor = null;
        if (_lastFrame != IntPtr.Zero)
        {
            MirrorNative.CFRelease(_lastFrame);
            _lastFrame = IntPtr.Zero;
        }
    }

    // Capture queue.
    private unsafe void OnFrame(CaptureStream stream, IntPtr sampleBuffer)
    {
        if (_encoder is null || !IsComplete(sampleBuffer))
        {
            return;
        }

        IntPtr pixels = MirrorNative.CMSampleBufferGetImageBuffer(sampleBuffer);
        if (pixels == IntPtr.Zero)
        {
            return;
        }

        MirrorNative.CMTimeValue pts = MirrorNative.CMSampleBufferGetPresentationTimeStamp(sampleBuffer);
        long captureUs = pts.IsValid ? pts.ToMicroseconds() : MirrorClock.NowUs();
        if (_compositor is { } compositor)
        {
            compositor.Update(stream.Index, pixels);
            ComposeOrDefer(captureUs);
            return;
        }

        Remember(pixels, captureUs);
        Submit(pixels, captureUs);
    }

    private void ComposeOrDefer(long captureUs)
    {
        DisplayCompositor compositor = _compositor!;
        if (!compositor.HasAll)
        {
            return;
        }

        // Several displays each deliver up to fps frames: composite at most once per frame interval, and catch up
        // with a one-shot timer for the last change.
        // The fps comes from the queue-owned encoder (set before capture starts), never from the control-side settings.
        long intervalUs = 1_000_000 / _encoder!.Settings.Fps;
        long now = MirrorClock.NowUs();
        if (now - _lastComposeUs < intervalUs * 3 / 4)
        {
            if (!_composePending)
            {
                _composePending = true;
                long delayNs = (_lastComposeUs + intervalUs - now) * 1000;
                _queue.DispatchAfter(new DispatchTime(DispatchTime.Now, Math.Max(0, delayNs)), () =>
                {
                    _composePending = false;
                    if (_compositor is not null)
                    {
                        Compose(MirrorClock.NowUs());
                    }
                });
            }

            return;
        }

        Compose(captureUs);
    }

    private void Compose(long captureUs)
    {
        _lastComposeUs = MirrorClock.NowUs();
        IntPtr composite = _compositor!.Compose();
        if (composite == IntPtr.Zero)
        {
            return;
        }

        Remember(composite, captureUs);
        Submit(composite, captureUs);
        MirrorNative.CFRelease(composite); // Remember retained it
    }

    private void Remember(IntPtr pixels, long captureUs)
    {
        MirrorNative.CFRetain(pixels);
        if (_lastFrame != IntPtr.Zero)
        {
            MirrorNative.CFRelease(_lastFrame);
        }

        _lastFrame = pixels;
        _lastFrameUs = captureUs;
    }

    private void Submit(IntPtr pixels, long captureUs)
    {
        HardwareEncoder? encoder = _encoder;
        if (encoder is null)
        {
            return;
        }

        MirrorStreamSettings s = encoder.Settings;
        if ((int)MirrorNative.CVPixelBufferGetWidth(pixels) != s.Width || (int)MirrorNative.CVPixelBufferGetHeight(pixels) != s.Height)
        {
            return; // a frame of the old size while a reconfiguration is in progress
        }

        bool keyframe = Interlocked.Exchange(ref _keyframeRequested, 0) == 1;
        int status = encoder.Encode(pixels, captureUs, keyframe);
        if (status == 0)
        {
            return;
        }

        Interlocked.Exchange(ref _keyframeRequested, 1); // the next encoder starts with one anyway
        MirrorLog.EncodeFailed(_logger, status);
        _encoder = null;
        encoder.Dispose();
        if (!_recreatedAfterFailure)
        {
            _recreatedAfterFailure = true;
            try
            {
                _encoder = HardwareEncoder.Create(s, _sink, _logger);
                return;
            }
            catch (MirrorException)
            {
                // reported below
            }
        }

        RaiseFailed(MirrorFailure.EncoderFailed, $"The {s.Codec} encoder failed (OSStatus {status}).");
    }

    private void EncodeStillIfRequested()
    {
        if (Volatile.Read(ref _keyframeRequested) == 0 || _lastFrame == IntPtr.Zero || _encoder is null)
        {
            return;
        }

        // A still frame shows the screen as it is now: stamp it now, so latency figures stay meaningful.
        Submit(_lastFrame, Math.Max(_lastFrameUs, MirrorClock.NowUs()));
    }

    private unsafe bool IsComplete(IntPtr sampleBuffer)
    {
        IntPtr attachments = MirrorNative.CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, 0);
        if (attachments == IntPtr.Zero || MirrorNative.CFArrayGetCount(attachments) == 0)
        {
            return false;
        }

        IntPtr status = MirrorNative.CFDictionaryGetValue(MirrorNative.CFArrayGetValueAtIndex(attachments, 0), _statusKey);
        long value;
        return status != IntPtr.Zero
            && MirrorNative.CFNumberGetValue(status, MirrorNative.CFNumberSInt64Type, &value) != 0
            && value == (long)SCFrameStatus.Complete;
    }

    private void OnStreamStopped(CaptureStream stream, NSError? error)
    {
        bool denied = error is not null && error.Code == (nint)(long)SCStreamErrorCode.UserDeclined;
        RaiseFailed(denied ? MirrorFailure.PermissionDenied : MirrorFailure.CaptureFailed,
            $"Capture of display {stream.DisplayId} stopped: {error?.LocalizedDescription ?? "no reason given"}");
    }

    private void RaiseFailed(MirrorFailure failure, string message)
    {
        MirrorLog.Failed(_logger, failure.ToString(), message);
        EventHandler<MirrorFailedEventArgs>? handler = Failed;
        if (handler is not null)
        {
            ThreadPool.QueueUserWorkItem(_ => handler(this, new MirrorFailedEventArgs(failure, message)));
        }
    }

    private static SCStreamConfiguration ConfigurationFor(MirrorTarget target, int index, MirrorStreamSettings settings)
    {
        MirrorDisplay display = target.Displays[index];
        if (!target.IsAllDisplays)
        {
            DisplayRect r = target.Rect;
            var source = new CGRect(r.X - display.Bounds.X, r.Y - display.Bounds.Y, r.Width, r.Height);
            return CaptureStream.Configuration(source, settings.Width, settings.Height, settings.Fps);
        }

        CGRect slot = SlotFor(target, index, settings);
        var whole = new CGRect(0, 0, display.Bounds.Width, display.Bounds.Height);
        return CaptureStream.Configuration(whole, EvenAtLeast2(slot.Width), EvenAtLeast2(slot.Height), settings.Fps);
    }

    private static DisplayCompositor CreateCompositor(MirrorTarget target, MirrorStreamSettings settings)
    {
        var slots = new CGRect[target.Displays.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = SlotFor(target, i, settings);
        }

        return new DisplayCompositor(settings.Width, settings.Height, slots);
    }

    /// <summary>Display <paramref name="index"/>'s rectangle in the composite, in stream pixels, top-left origin.</summary>
    private static CGRect SlotFor(MirrorTarget target, int index, MirrorStreamSettings settings)
    {
        DisplayRect box = target.Rect;
        DisplayRect d = target.Displays[index].Bounds;
        double sx = settings.Width / box.Width;
        double sy = settings.Height / box.Height;
        return new CGRect((d.X - box.X) * sx, (d.Y - box.Y) * sy, d.Width * sx, d.Height * sy);
    }

    private static int EvenAtLeast2(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    private static SCDisplay? FindDisplay(SCShareableContent content, uint displayId)
    {
        foreach (SCDisplay d in content.Displays)
        {
            if (d.DisplayId == displayId)
            {
                return d;
            }
        }

        return null;
    }

    private SCRunningApplication[] OwnApplication(SCShareableContent content)
    {
        int pid = Environment.ProcessId;
        foreach (SCRunningApplication app in content.Applications)
        {
            if (app.ProcessId == pid)
            {
                return [app];
            }
        }

        MirrorLog.OwnAppNotFound(_logger);
        return [];
    }

    private static MirrorException CaptureError(NSErrorException ex, string what)
    {
        bool denied = ex.Code == (nint)(long)SCStreamErrorCode.UserDeclined;
        return new MirrorException(denied ? MirrorFailure.PermissionDenied : MirrorFailure.CaptureFailed,
            $"{what}: {ex.Error.LocalizedDescription}", ex);
    }

    private void DisplayRectLog(MirrorTarget target, MirrorStreamSettings settings)
    {
        DisplayRect r = target.Rect;
        MirrorLog.Started(_logger, target.Displays.Length, r.X, r.Y, r.Width, r.Height, settings.Width, settings.Height, settings.Fps, settings.Codec.ToString());
    }
}
