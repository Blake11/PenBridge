using System.Diagnostics;
using System.Runtime.InteropServices;
using Foundation;
using Microsoft.Extensions.Logging;
using VideoToolbox;

namespace PenBridge.Mac.Mirror;

/// <summary>
/// One VideoToolbox <c>VTCompressionSession</c>: hardware only, real time, no frame reordering, keyframes only on request,
/// and Annex-B output with the parameter sets in front of every keyframe (ADR-0038 "Codec", spec "Heads → Mac").
/// </summary>
/// <remarks>
/// <para>The session is created through raw handles with an <see cref="UnmanagedCallersOnlyAttribute"/> output callback,
/// so neither encoding nor the callback creates a managed object per frame; the access unit is built in a reused
/// <see cref="AnnexBBuffer"/>.</para>
/// <para><see cref="Encode"/> is called from the capture queue (one thread at a time); the output callback runs on a
/// VideoToolbox thread and calls the sink. The size and codec are fixed per session: a change needs a new encoder.
/// Fps and bitrate change in place (<see cref="SetRate"/>).</para>
/// <para>Low-latency rate control (<c>kVTVideoEncoderSpecification_EnableLowLatencyRateControl</c>) is asked for first; if
/// the encoder refuses it, the session is created without it.</para>
/// </remarks>
internal sealed unsafe class HardwareEncoder : IDisposable
{
    /// <summary>Keyframes come only on request; the interval is set this high so the encoder never adds its own.</summary>
    private const int NoAutomaticKeyframes = 1_000_000;

    /// <summary>The peak rate the encoder may hit over one second, relative to the average (the pacer's 1.5 × budget).</summary>
    private const double PeakToAverage = 1.5;

    private static readonly Lazy<NSDictionary> ForceKeyframeOptions = new(() =>
        NSDictionary.FromObjectAndKey(NSNumber.FromBoolean(true), VTEncodeFrameOptionKey.ForceKeyFrame));

    private static readonly Lazy<IntPtr> NotSyncKey = new(() => MirrorNative.CoreMediaConstant("kCMSampleAttachmentKey_NotSync"));

    private readonly ILogger _logger;
    private readonly IMirrorFrameSink _sink;
    private readonly AnnexBBuffer _annexB = new();
    private readonly Lock _outputLock = new();
    private IntPtr _session;
    private GCHandle _self;
    private byte[] _scratch = new byte[64 * 1024];
    private int _nalLengthSize;
    private long _lastPtsUs = long.MinValue;
    private int _outputErrors;
    private int _droppedFrames;
    private bool _disposed;

    // Sustained-drop detection (under _outputLock).
    private long _windowStart;
    private int _windowTotal;
    private int _windowBad;
    private bool _overloadReported;

    private HardwareEncoder(MirrorStreamSettings settings, IMirrorFrameSink sink, ILogger logger)
    {
        Settings = settings;
        _sink = sink;
        _logger = logger;
    }

    /// <summary>The size, fps, bitrate and codec in effect.</summary>
    public MirrorStreamSettings Settings { get; private set; }

    /// <summary>
    /// Raised once (VideoToolbox thread) when the encoder drops or fails more than half of its frames over a window of at
    /// least <see cref="OverloadWindowSeconds"/> second(s) holding at least <see cref="OverloadMinFrames"/> frames: it can't
    /// sustain the size and fps (arguments: bad frames, frames in the window). Don't dispose the encoder from the handler.
    /// </summary>
    public Action<HardwareEncoder, int, int>? Overloaded { get; set; }

    /// <summary>The length of the window sustained drops are judged over, in seconds.</summary>
    public const int OverloadWindowSeconds = 1;

    /// <summary>Fewest frames a window needs before it can count as overloaded.</summary>
    public const int OverloadMinFrames = 20;

    /// <summary>True when the session runs with low-latency rate control.</summary>
    public bool LowLatency { get; private set; }

    /// <summary>Frames the encoder reported as failed since creation (output callback status ≠ 0).</summary>
    public int OutputErrors => Volatile.Read(ref _outputErrors);

    /// <summary>Frames the encoder dropped since creation (rate control over budget).</summary>
    public int DroppedFrames => Volatile.Read(ref _droppedFrames);

    /// <summary>Creates and prepares a hardware session for <paramref name="settings"/>.</summary>
    /// <exception cref="MirrorException"><see cref="MirrorFailure.EncoderFailed"/>: no hardware encoder for the codec at that size.</exception>
    public static HardwareEncoder Create(MirrorStreamSettings settings, IMirrorFrameSink sink, ILogger logger)
    {
        settings.Validate();
        var encoder = new HardwareEncoder(settings, sink, logger);
        try
        {
            encoder.Open();
            return encoder;
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The codecs this Mac can encode in hardware with the stream's settings (real time, no reordering), found by
    /// creating a small session of each. Takes a few milliseconds; call it off the main thread and cache it.
    /// </summary>
    public static MirrorCodecs DetectHardwareCodecs(ILogger logger)
    {
        MirrorCodecs result = MirrorCodecs.None;
        foreach (MirrorCodec codec in (ReadOnlySpan<MirrorCodec>)[MirrorCodec.Hevc, MirrorCodec.H264])
        {
            try
            {
                using HardwareEncoder probe = Create(new MirrorStreamSettings(1920, 1080, 60, 8000, codec), NullSink.Instance, logger);
                result |= codec == MirrorCodec.Hevc ? MirrorCodecs.Hevc : MirrorCodecs.H264;
            }
            catch (MirrorException)
            {
                // Not available in hardware: left out.
            }
        }

        MirrorLog.HardwareEncoders(logger, result.ToString());
        return result;
    }

    /// <summary>
    /// The highest real-time frame rate the hardware encoder reports for this size and codec
    /// (<c>kVTCompressionPropertyKey_MaximumRealTimeFrameRate</c>), or 0 when it doesn't report one or there is no
    /// hardware encoder.
    /// </summary>
    public static double MaxRealTimeFps(int width, int height, MirrorCodec codec, ILogger logger)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(15))
        {
            return 0;
        }

        try
        {
            using HardwareEncoder probe = Create(new MirrorStreamSettings(width, height, 120, 20000, codec), NullSink.Instance, logger);
            IntPtr value;
            if (MirrorNative.VTSessionCopyProperty(probe._session, VTCompressionPropertyKey.MaximumRealTimeFrameRate.Handle, IntPtr.Zero, &value) != 0
                || value == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                double fps;
                return MirrorNative.CFNumberGetDouble(value, MirrorNative.CFNumberDoubleType, &fps) != 0 ? fps : 0;
            }
            finally
            {
                MirrorNative.CFRelease(value);
            }
        }
        catch (MirrorException)
        {
            return 0;
        }
    }

    /// <summary>Changes fps and bitrate in place (no keyframe).</summary>
    public void SetRate(int fps, int bitrateKbps)
    {
        MirrorStreamSettings next = Settings with { Fps = fps, BitrateKbps = bitrateKbps };
        next.Validate();
        ObjectDisposedException.ThrowIf(_disposed, this);
        Settings = next;
        ApplyRate();
    }

    /// <summary>
    /// Encodes one NV12 pixel buffer of the session's size. Allocation-free.
    /// </summary>
    /// <param name="pixelBuffer">A CVPixelBufferRef (not consumed; VideoToolbox retains it while encoding).</param>
    /// <param name="captureTimeUs">The capture time, handed back to the sink with the frame.</param>
    /// <param name="keyframe">Force an IDR frame.</param>
    /// <returns>The OSStatus: 0, or the error (kVTInvalidSessionErr after a GPU reset: recreate the encoder).</returns>
    public int Encode(IntPtr pixelBuffer, long captureTimeUs, bool keyframe)
    {
        if (_session == IntPtr.Zero)
        {
            return -12903; // kVTInvalidSessionErr
        }

        // Presentation times must increase strictly; a re-encoded still frame reuses an old capture time.
        long pts = captureTimeUs > _lastPtsUs ? captureTimeUs : _lastPtsUs + 1;
        _lastPtsUs = pts;
        IntPtr options = keyframe ? ForceKeyframeOptions.Value.Handle : IntPtr.Zero;
        uint info;
        return MirrorNative.VTCompressionSessionEncodeFrame(
            _session, pixelBuffer, MirrorNative.CMTimeValue.FromMicroseconds(pts), MirrorNative.CMTimeValue.Invalid,
            options, new IntPtr(captureTimeUs), &info);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_session != IntPtr.Zero)
        {
            // Delivers (or drops) pending frames, so no callback can run after the handle is freed.
            _ = MirrorNative.VTCompressionSessionCompleteFrames(_session, MirrorNative.CMTimeValue.Invalid);
            MirrorNative.VTCompressionSessionInvalidate(_session);
            MirrorNative.CFRelease(_session);
            _session = IntPtr.Zero;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private void Open()
    {
        _self = GCHandle.Alloc(this);
        int status = CreateSession(lowLatency: true);
        if (status != 0)
        {
            MirrorLog.LowLatencyRefused(_logger, Settings.Codec.ToString(), status);
            status = CreateSession(lowLatency: false);
        }

        if (status != 0)
        {
            throw new MirrorException(MirrorFailure.EncoderFailed,
                $"No hardware {Settings.Codec} encoder for {Settings.Width}x{Settings.Height} (OSStatus {status}).");
        }

        // Tag the stream BT.709 like the capture's colour matrix. The range follows the NV12 420v input buffers (video
        // range, limited): VideoToolbox has no range property and writes video_full_range_flag = 0 for them.
        SetConstant("kVTCompressionPropertyKey_ColorPrimaries", "kCMFormatDescriptionColorPrimaries_ITU_R_709_2");
        SetConstant("kVTCompressionPropertyKey_TransferFunction", "kCMFormatDescriptionTransferFunction_ITU_R_709_2");
        SetConstant("kVTCompressionPropertyKey_YCbCrMatrix", "kCMFormatDescriptionYCbCrMatrix_ITU_R_709_2");
        SetBool(VTCompressionPropertyKey.RealTime, true);
        SetBool(VTCompressionPropertyKey.AllowFrameReordering, false);
        SetBool(VTCompressionPropertyKey.PrioritizeEncodingSpeedOverQuality, true);
        SetObject(VTCompressionPropertyKey.ProfileLevel, Settings.Codec == MirrorCodec.Hevc
            ? VTProfileLevelKeys.Hevc_Main_AutoLevel
            : VTProfileLevelKeys.H264_High_AutoLevel);
        SetInt(VTCompressionPropertyKey.MaxKeyFrameInterval, NoAutomaticKeyframes);
        SetDouble(VTCompressionPropertyKey.MaxKeyFrameIntervalDuration, NoAutomaticKeyframes);
        ApplyRate();

        status = MirrorNative.VTCompressionSessionPrepareToEncodeFrames(_session);
        if (status != 0)
        {
            throw new MirrorException(MirrorFailure.EncoderFailed, $"Preparing the {Settings.Codec} encoder failed (OSStatus {status}).");
        }

        MirrorLog.EncoderCreated(_logger, Settings.Codec.ToString(), Settings.Width, Settings.Height, Settings.Fps, Settings.BitrateKbps, LowLatency);
    }

    private int CreateSession(bool lowLatency)
    {
        using var spec = new NSMutableDictionary();
        spec[VTVideoEncoderSpecificationKeys.RequireHardwareAcceleratedVideoEncoder] = NSNumber.FromBoolean(true);
        IntPtr lowLatencyKey = MirrorNative.VideoToolboxConstant("kVTVideoEncoderSpecification_EnableLowLatencyRateControl");
        if (lowLatency)
        {
            if (lowLatencyKey == IntPtr.Zero)
            {
                return -12902; // kVTParameterErr: not on this macOS
            }

            spec.LowlevelSetObject(NSNumber.FromBoolean(true), lowLatencyKey);
        }

        IntPtr session;
        int status = MirrorNative.VTCompressionSessionCreate(
            IntPtr.Zero, Settings.Width, Settings.Height,
            Settings.Codec == MirrorCodec.Hevc ? MirrorNative.CodecHevc : MirrorNative.CodecH264,
            spec.Handle, IntPtr.Zero, IntPtr.Zero, &OnOutput, GCHandle.ToIntPtr(_self), &session);
        if (status == 0)
        {
            _session = session;
            LowLatency = lowLatency;
        }

        return status;
    }

    private void ApplyRate()
    {
        long bitsPerSecond = Settings.BitrateKbps * 1000L;
        SetInt(VTCompressionPropertyKey.AverageBitRate, (int)Math.Min(int.MaxValue, bitsPerSecond));
        SetInt(VTCompressionPropertyKey.ExpectedFrameRate, Settings.Fps);
        using var limits = NSArray.FromNSObjects(
            NSNumber.FromInt64((long)(bitsPerSecond * PeakToAverage / 8)), NSNumber.FromDouble(1.0));
        Set(VTCompressionPropertyKey.DataRateLimits, limits.Handle);
    }

    private void SetConstant(string keyName, string valueName)
    {
        IntPtr key = MirrorNative.VideoToolboxConstant(keyName);
        IntPtr value = MirrorNative.CoreMediaConstant(valueName);
        if (key == IntPtr.Zero || value == IntPtr.Zero)
        {
            MirrorLog.PropertyRefused(_logger, keyName, -12902);
            return;
        }

        int status = MirrorNative.VTSessionSetProperty(_session, key, value);
        if (status != 0)
        {
            MirrorLog.PropertyRefused(_logger, keyName, status);
        }
    }

    private void SetBool(NSString key, bool value) => Set(key, value ? MirrorNative.True : MirrorNative.False);

    private void SetInt(NSString key, int value)
    {
        using var n = NSNumber.FromInt32(value);
        Set(key, n.Handle);
    }

    private void SetDouble(NSString key, double value)
    {
        using var n = NSNumber.FromDouble(value);
        Set(key, n.Handle);
    }

    private void SetObject(NSString key, NSObject value) => Set(key, value.Handle);

    private void Set(NSString key, IntPtr value)
    {
        int status = MirrorNative.VTSessionSetProperty(_session, key.Handle, value);
        if (status != 0)
        {
            MirrorLog.PropertyRefused(_logger, key.ToString(), status);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnOutput(IntPtr refCon, IntPtr sourceFrameRefCon, int status, uint infoFlags, IntPtr sampleBuffer)
    {
        try
        {
            if (GCHandle.FromIntPtr(refCon).Target is HardwareEncoder encoder)
            {
                encoder.HandleOutput((long)sourceFrameRefCon, status, infoFlags, sampleBuffer);
            }
        }
#pragma warning disable CA1031 // Nothing may unwind into VideoToolbox; a sink exception loses only this frame.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void HandleOutput(long captureTimeUs, int status, uint infoFlags, IntPtr sampleBuffer)
    {
        if (status != 0)
        {
            Interlocked.Increment(ref _outputErrors);
            CountFrame(bad: true);
            return;
        }

        if (sampleBuffer == IntPtr.Zero || (infoFlags & MirrorNative.EncodeInfoFrameDropped) != 0)
        {
            Interlocked.Increment(ref _droppedFrames);
            CountFrame(bad: true);
            return;
        }

        CountFrame(bad: false);

        bool keyframe = IsKeyframe(sampleBuffer);
        lock (_outputLock)
        {
            _annexB.Reset();
            IntPtr format = MirrorNative.CMSampleBufferGetFormatDescription(sampleBuffer);
            if (format == IntPtr.Zero || !ReadParameterSets(format, keyframe))
            {
                Interlocked.Increment(ref _outputErrors);
                return;
            }

            IntPtr block = MirrorNative.CMSampleBufferGetDataBuffer(sampleBuffer);
            nuint lengthAtOffset, total;
            byte* data;
            if (block == IntPtr.Zero || MirrorNative.CMBlockBufferGetDataPointer(block, 0, &lengthAtOffset, &total, &data) != 0)
            {
                Interlocked.Increment(ref _outputErrors);
                return;
            }

            ReadOnlySpan<byte> payload;
            if (lengthAtOffset >= total)
            {
                payload = new ReadOnlySpan<byte>(data, checked((int)total));
            }
            else
            {
                // Not contiguous: copy into the reused scratch buffer.
                int length = checked((int)total);
                if (_scratch.Length < length)
                {
                    _scratch = new byte[Math.Max(length, _scratch.Length * 2)];
                }

                fixed (byte* dst = _scratch)
                {
                    if (MirrorNative.CMBlockBufferCopyDataBytes(block, 0, total, dst) != 0)
                    {
                        Interlocked.Increment(ref _outputErrors);
                        return;
                    }
                }

                payload = _scratch.AsSpan(0, length);
            }

            if (!_annexB.AppendLengthPrefixed(payload, _nalLengthSize))
            {
                Interlocked.Increment(ref _outputErrors);
                return;
            }

            _sink.OnFrame(_annexB.Written, keyframe, captureTimeUs);
        }
    }

    /// <summary>Judges the last second of output; nothing happens (and nothing is counted) while no frames come.</summary>
    private void CountFrame(bool bad)
    {
        int badFrames = 0, total = 0;
        lock (_outputLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (_windowTotal == 0)
            {
                _windowStart = now;
            }

            _windowTotal++;
            if (bad)
            {
                _windowBad++;
            }

            if (now - _windowStart < OverloadWindowSeconds * Stopwatch.Frequency)
            {
                return;
            }

            if (_windowTotal >= OverloadMinFrames && _windowBad * 2 > _windowTotal && !_overloadReported)
            {
                _overloadReported = true;
                badFrames = _windowBad;
                total = _windowTotal;
            }

            _windowTotal = 0;
            _windowBad = 0;
        }

        if (total != 0)
        {
            Overloaded?.Invoke(this, badFrames, total);
        }
    }

    private static bool IsKeyframe(IntPtr sampleBuffer)
    {
        IntPtr attachments = MirrorNative.CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, 0);
        if (attachments == IntPtr.Zero || MirrorNative.CFArrayGetCount(attachments) == 0)
        {
            return true; // no attachments: a sync sample
        }

        IntPtr dict = MirrorNative.CFArrayGetValueAtIndex(attachments, 0);
        IntPtr notSync = MirrorNative.CFDictionaryGetValue(dict, NotSyncKey.Value);
        return notSync == IntPtr.Zero || MirrorNative.CFBooleanGetValue(notSync) == 0;
    }

    /// <summary>Learns the NAL length size and, for a keyframe, appends every parameter set (VPS/SPS/PPS or SPS/PPS).</summary>
    private bool ReadParameterSets(IntPtr format, bool keyframe)
    {
        if (!keyframe && _nalLengthSize != 0)
        {
            return true;
        }

        bool hevc = Settings.Codec == MirrorCodec.Hevc;
        nuint count;
        int nalLength;
        int status = hevc
            ? MirrorNative.CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(format, 0, null, null, &count, &nalLength)
            : MirrorNative.CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, 0, null, null, &count, &nalLength);
        if (status != 0)
        {
            return false;
        }

        _nalLengthSize = nalLength;
        if (!keyframe)
        {
            return true;
        }

        for (nuint i = 0; i < count; i++)
        {
            byte* set;
            nuint size;
            status = hevc
                ? MirrorNative.CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(format, i, &set, &size, null, null)
                : MirrorNative.CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, i, &set, &size, null, null);
            if (status != 0)
            {
                return false;
            }

            _annexB.AppendNal(new ReadOnlySpan<byte>(set, checked((int)size)));
        }

        return true;
    }

    /// <summary>Discards frames (capability probing).</summary>
    private sealed class NullSink : IMirrorFrameSink
    {
        public static readonly NullSink Instance = new();

        public void OnFrame(ReadOnlySpan<byte> accessUnit, bool keyframe, long captureTimeUs)
        {
        }
    }
}

/// <summary>The host monotonic clock in microseconds, the time base of capture times.</summary>
internal static class MirrorClock
{
    private static readonly double UsPerTick = 1_000_000.0 / Stopwatch.Frequency;

    /// <summary>Now, in the same microseconds as ScreenCaptureKit presentation times and <see cref="IMirrorFrameSink"/> capture times.</summary>
    public static long NowUs() => (long)(Stopwatch.GetTimestamp() * UsPerTick);
}
