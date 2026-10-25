using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Android.Media;
using Android.OS;
using Android.Util;
using Android.Views;
using PenBridge.Core.Protocol;

namespace PenBridge.Android.Mirror;

/// <summary>
/// Hardware video decoder for the mirror stream (ADR-0038, spec "Heads → Android"): <see cref="MediaCodec"/> in async
/// mode, low-latency where the decoder supports it, rendering each frame to the surface as soon as it is decoded.
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> Every codec call runs on one decoder <see cref="HandlerThread"/>, created on the first
/// <see cref="Configure"/> (mirroring shown), with its Linux nice set explicitly to the default (0). Managed
/// <c>Thread.Priority</c> is a no-op on Android, so the nice is the only priority that counts; the pad's input sender
/// gets its own explicitly (#311). The UI thread, which delivers pen input, only posts to the decoder thread.
/// <see cref="Feed"/> may be called from any thread: it copies the access unit into a reused slot and wakes the
/// decoder thread. <see cref="FrameRendered"/>, <see cref="KeyframeNeeded"/> and <see cref="Error"/> are raised on the
/// decoder thread (except <see cref="KeyframeNeeded"/>, which can also come from the <see cref="Feed"/> caller).</para>
/// <para><b>Keyframe gate.</b> After a configure (every new epoch, even with the same codec and size), a codec
/// restart, a frame-number gap or a frame that couldn't be queued, nothing is fed until a keyframe arrives, and
/// <see cref="KeyframeNeeded"/> is raised once per closing (the sender of KeyframeRequests repeats them itself). A gap
/// that lands on a keyframe needs nothing: the keyframe is fed. Access units that arrive before the codec runs (its
/// surface is still being created) or while it restarts wait in the queue, and the codec starts from the first keyframe
/// among them, so the epoch's first keyframe isn't lost to the start-up.</para>
/// <para><b>Allocation.</b> The access-unit bytes are never reallocated: <see cref="Feed"/> copies into reused slots
/// (a slot grows only when a larger access unit than ever before arrives) and the decoder thread copies them straight
/// into the codec's direct input buffers. The Java interop is not allocation-free: each frame creates small managed
/// peers (the input <c>ByteBuffer</c> from <c>GetInputBuffer</c>, the <c>BufferInfo</c> of the output callback),
/// short-lived and disposed at once, on the decoder thread, never on the pen input path.</para>
/// </remarks>
internal sealed class MirrorDecoder : IDisposable
{
    private const string Tag = "PenBridge";

    /// <summary>The spec's frameLength limit (1 MiB): no access unit is larger.</summary>
    private const int MaxAccessUnit = 1 << 20;

    /// <summary>
    /// Access units waiting for a codec input buffer (≈ 130 ms at 60 fps: a decoder's first frame can take that long).
    /// More than this behind → the decoder is too slow; drop and wait for a keyframe.
    /// </summary>
    private const int SlotCount = 8;

    /// <summary>Codec restarts in a row without a rendered frame before the decoder gives up.</summary>
    private const int MaxRestarts = 3;

    /// <summary>Feed-time ring that maps a presentation timestamp back to its frame number.</summary>
    private const int RingSize = 32;

    // Qualcomm (Snapdragon) vendor keys for low-latency decode: output in decode order, no reorder buffering. Other
    // decoders ignore unknown vendor keys.
    private const string QtiLowLatency = "vendor.qti-ext-dec-low-latency.enable";
    private const string QtiPictureOrder = "vendor.qti-ext-dec-picture-order.enable";

    private readonly global::Android.Content.Context _context;
    private readonly CodecCallback _callback;
    private readonly Java.Lang.Runnable _drainRunnable;
    private HandlerThread? _thread; // created on the first Configure (under _lock)
    private Handler? _handler;
    private readonly Lock _lock = new();

    // Pending access units (under _lock).
    private readonly byte[][] _slotData = new byte[SlotCount][];
    private readonly int[] _slotLength = new int[SlotCount];
    private readonly uint[] _slotFrame = new uint[SlotCount];
    private readonly bool[] _slotKey = new bool[SlotCount];
    private int _slotHead;
    private int _slotCount;
    private bool _drainPosted;
    private bool _configured; // between Configure and Stop (or giving up): Feed queues, even before the codec runs
    private bool _needKeyframe = true;
    private bool _signalOnGated; // a configure closed the gate: the first gated frame raises KeyframeNeeded
    private bool _haveLast;
    private uint _lastFrame;
    private long _framesFed;
    private long _framesGated;

    // Decoder thread only.
    private readonly int[] _freeInputs = new int[64];
    private int _freeHead;
    private int _freeCount;
    private readonly long[] _ringPts = new long[RingSize];
    private readonly uint[] _ringFrame = new uint[RingSize];
    private int _ringNext;
    private long _lastPts;
    private MediaCodec? _codec;
    private MirrorVideoConfig? _running;
    private MirrorVideoConfig? _config;
    private Surface? _surface;
    private long _nanoToStopwatchNs;
    private int _restartsWithoutOutput;
    private bool _disposed;

    /// <param name="context">For the capability probe (<see cref="MirrorDecoderCapabilities.Get"/>, run on the decoder thread).</param>
    public MirrorDecoder(global::Android.Content.Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        for (int i = 0; i < SlotCount; i++)
        {
            _slotData[i] = [];
        }

        _callback = new CodecCallback(this);
        _drainRunnable = new Java.Lang.Runnable(Drain);
    }

    /// <summary>A frame reached the screen: (frameNumber, renderedAtUs). Decoder thread.</summary>
    /// <remarks>renderedAtUs is microseconds since <see cref="ClockOrigin"/> on the <see cref="Stopwatch"/> clock.</remarks>
    public Action<uint, long>? FrameRendered { get; set; }

    /// <summary>
    /// The decoder needs a keyframe (new epoch, gap, lost frame, restart). Raised once each time the gate closes; decoder
    /// or <see cref="Feed"/> thread.
    /// </summary>
    public Action? KeyframeNeeded { get; set; }

    /// <summary>The decoder can't run (no decoder for the codec, creation failed). Decoder thread.</summary>
    public Action<string>? Error { get; set; }

    /// <summary>
    /// <see cref="Stopwatch.GetTimestamp"/> value that <see cref="FrameRendered"/>'s time counts from (the client's
    /// Ping clock origin). 0 = raw Stopwatch microseconds.
    /// </summary>
    public long ClockOrigin { get; set; }

    /// <summary>Queued to the codec → output buffer available (decode latency), µs.</summary>
    public MirrorLatencyStats DecodeLatency { get; } = new();

    /// <summary>Queued to the codec → frame on screen (decode + composition), µs.</summary>
    public MirrorLatencyStats RenderLatency { get; } = new();

    /// <summary>Access units queued to the codec since creation.</summary>
    public long FramesFed => Interlocked.Read(ref _framesFed);

    /// <summary>Access units dropped by the keyframe gate or a full queue.</summary>
    public long FramesGated => Interlocked.Read(ref _framesGated);

    /// <summary>True while the gate waits for a keyframe.</summary>
    public bool NeedsKeyframe
    {
        get
        {
            lock (_lock)
            {
                return _needKeyframe;
            }
        }
    }

    /// <summary>
    /// Applies a stream configuration (a new epoch). The same codec and size keep the running decoder (a bitrate or
    /// fps step); anything else reconfigures it once a surface exists. Either way the keyframe gate closes and
    /// frame-number continuity restarts: the new epoch is fed from its first keyframe, and the first non-key frame that
    /// arrives before it raises <see cref="KeyframeNeeded"/>. Starts the decoder thread on first use. Any thread.
    /// </summary>
    public void Configure(MirrorVideoConfig config)
    {
        if (config.Width < 2 || config.Height < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(config));
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_thread is null)
            {
                _thread = new HandlerThread("PenBridge.MirrorDecode", (int)global::Android.OS.ThreadPriority.Default);
                _thread.Start();
                _handler = new Handler(_thread.Looper!);
            }

            _haveLast = false;
            _needKeyframe = true;
            _signalOnGated = true;
            _configured = true;
            _framesGated += _slotCount; // the previous epoch's backlog: the new one starts at its own keyframe
            _slotCount = 0;
        }

        Post(() =>
        {
            _config = config;
            _restartsWithoutOutput = 0;
            ApplyState();
        });
    }

    /// <summary>The output surface was created or changed (UI thread, SurfaceHolder callback).</summary>
    public void SetSurface(Surface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        Post(() =>
        {
            if (!ReferenceEquals(_surface, surface))
            {
                ReleaseCodec(); // a codec can't switch to another Surface object on API 29 reliably; start fresh
            }

            _surface = surface;
            ApplyState();
        });
    }

    /// <summary>
    /// The surface is going away (UI thread, <c>surfaceDestroyed</c>): stops the codec and waits (bounded) until it no
    /// longer draws to it, as the SurfaceHolder contract requires.
    /// </summary>
    public void ClearSurface()
    {
        using var done = new ManualResetEventSlim();
        if (!Post(() =>
        {
            ReleaseCodec();
            _surface = null;
            done.Set();
        }))
        {
            return;
        }

        done.Wait(TimeSpan.FromSeconds(1));
    }

    /// <summary>Stops decoding and forgets the configuration (mirroring hidden). Any thread.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _configured = false;
            _slotCount = 0;
        }

        Post(() =>
        {
            _config = null;
            ReleaseCodec();
        });
    }

    /// <summary>
    /// Queues one Annex-B access unit. False when it was dropped: not configured, the keyframe gate (waiting for a
    /// keyframe), too large, or the decoder is too far behind (which closes the gate). Any thread; never blocks on the
    /// codec. The bytes are copied, so the caller may reuse the buffer right away.
    /// </summary>
    public bool Feed(ReadOnlySpan<byte> accessUnit, bool keyframe, uint frameNumber)
    {
        bool signal = false;
        bool post = false;
        bool queued = false;
        Handler? handler;
        lock (_lock)
        {
            if (!_configured || _disposed)
            {
                return false;
            }

            if (_haveLast && frameNumber != unchecked(_lastFrame + 1) && !keyframe)
            {
                signal |= CloseGate(); // a frame went missing (or came out of order) before it reached us
            }

            _haveLast = true;
            _lastFrame = frameNumber;
            if (accessUnit.IsEmpty || accessUnit.Length > MaxAccessUnit)
            {
                signal |= CloseGate();
            }
            else if (_needKeyframe && !keyframe)
            {
                // Gated: keep waiting for the keyframe. After a configure the first one asks for it.
                signal |= _signalOnGated;
                _signalOnGated = false;
            }
            else if (_slotCount == SlotCount)
            {
                signal |= CloseGate(); // the decoder is behind: this frame is lost, so the next ones can't decode
            }
            else
            {
                _needKeyframe = false;
                _signalOnGated = false;
                int slot = (_slotHead + _slotCount) % SlotCount;
                if (_slotData[slot].Length < accessUnit.Length)
                {
                    _slotData[slot] = new byte[(int)Math.Min(MaxAccessUnit, BitOperations.RoundUpToPowerOf2((uint)accessUnit.Length))];
                }

                accessUnit.CopyTo(_slotData[slot]);
                _slotLength[slot] = accessUnit.Length;
                _slotFrame[slot] = frameNumber;
                _slotKey[slot] = keyframe;
                _slotCount++;
                queued = true;
                if (!_drainPosted)
                {
                    _drainPosted = true;
                    post = true;
                }
            }

            if (!queued)
            {
                _framesGated++;
            }

            handler = post ? _handler : null;
        }

        handler?.Post(_drainRunnable);

        if (signal)
        {
            KeyframeNeeded?.Invoke();
        }

        return queued;
    }

    public void Dispose()
    {
        Handler? handler;
        HandlerThread? thread;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _configured = false;
            handler = _handler;
            thread = _thread;
        }

        if (handler is null || thread is null)
        {
            _callback.Dispose();
            return; // never shown: no thread, no codec
        }

        using var done = new ManualResetEventSlim();
        handler.Post(() =>
        {
            _config = null;
            ReleaseCodec();
            _surface = null;
            done.Set();
        });
        done.Wait(TimeSpan.FromSeconds(1));
        thread.QuitSafely();
        thread.Join(1000);
        handler.RemoveCallbacks(_drainRunnable);
        _callback.Dispose();
        handler.Dispose();
        thread.Dispose();
    }

    private static long StopwatchUs(long timestamp) => (long)(timestamp * (1_000_000.0 / Stopwatch.Frequency));

    /// <summary>Under _lock. Returns true if the gate was open (so the caller signals once per gap).</summary>
    private bool CloseGate()
    {
        bool wasOpen = !_needKeyframe;
        _needKeyframe = true;
        return wasOpen;
    }

    /// <summary>False when disposed or never configured (no decoder thread yet: nothing to do).</summary>
    private bool Post(Action action)
    {
        Handler? handler;
        lock (_lock)
        {
            if (_disposed)
            {
                return false;
            }

            handler = _handler;
        }

        return handler is not null && handler.Post(action);
    }

    /// <summary>Decoder thread: starts, keeps or replaces the codec to match the config and surface.</summary>
    private void ApplyState()
    {
        if (_config is not { } config || _surface is not { IsValid: true } surface)
        {
            ReleaseCodec();
            return;
        }

        if (_codec is not null && _running is { } running
            && running.Codec == config.Codec && running.Width == config.Width && running.Height == config.Height)
        {
            _running = config;
            return; // same stream shape: the encoder's stream continues, keep decoding it
        }

        ReleaseCodec();
        StartCodec(config, surface);
    }

    private void StartCodec(MirrorVideoConfig config, Surface surface)
    {
        if (MirrorDecoderCapabilities.Get(_context).For(config.Codec) is not { } info)
        {
            Error?.Invoke("No " + config.Codec.Name() + " decoder");
            return;
        }

        MediaCodec? codec = null;
        try
        {
            codec = MediaCodec.CreateByCodecName(info.Name);
            using MediaFormat format = MediaFormat.CreateVideoFormat(config.Codec.Mime(), config.Width, config.Height);
            format.SetInteger(MediaFormat.KeyMaxInputSize, MaxAccessUnit);
            format.SetInteger(MediaFormat.KeyPriority, 0); // real time
            if (config.Fps > 0)
            {
                format.SetInteger(MediaFormat.KeyOperatingRate, config.Fps);
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(30) && info.LowLatency)
            {
                format.SetInteger(MediaFormat.KeyLowLatency, 1);
            }

            bool qti = info.Name.StartsWith("c2.qti.", StringComparison.Ordinal) || info.Name.StartsWith("OMX.qcom.", StringComparison.Ordinal);
            if (qti)
            {
                format.SetInteger(QtiLowLatency, 1);
                format.SetInteger(QtiPictureOrder, 1);
            }

            codec.SetCallback(_callback, _handler!); // decoder thread: the handler exists
            codec.SetOnFrameRenderedListener(_callback, _handler!);
            codec.Configure(format, surface, null, MediaCodecConfigFlags.None);
            codec.SetVideoScalingMode(VideoScalingMode.ScaleToFit);
            LogStart(codec, info, config, qti);
            codec.Start();
        }
        catch (Java.Lang.Exception e)
        {
            Log.Warn(Tag, "Mirror decoder start failed (" + info.Name + "): " + e.Message);
            if (codec is not null)
            {
                SafeRelease(codec);
            }

            Error?.Invoke("Decoder failed to start");
            return;
        }

        _codec = codec;
        _running = config;
        _freeHead = 0;
        _freeCount = 0;
        _lastPts = 0;
        _nanoToStopwatchNs = (long)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency)) - Java.Lang.JavaSystem.NanoTime();
        DecodeLatency.Reset();
        RenderLatency.Reset();
        bool signal;
        lock (_lock)
        {
            // A fresh decoder can only start at a keyframe. Access units queued while no codec ran (the surface was
            // still being created, a shape change, a restart) are kept from the first keyframe on, so a start right
            // after a new epoch needs no second keyframe from the Mac. The gate keeps its state: closed when frames
            // after the queued ones were lost (it signalled then), open when the stream continues. Without a queued
            // keyframe the gate closes; it asks only if it was open (a closed gate signalled, or will on its first
            // gated frame after a configure).
            while (_slotCount > 0 && !_slotKey[_slotHead])
            {
                _slotHead = (_slotHead + 1) % SlotCount;
                _slotCount--;
                _framesGated++;
            }

            signal = _slotCount == 0 && CloseGate();
            if (signal)
            {
                _signalOnGated = false; // signalled just below
            }
        }

        if (signal)
        {
            KeyframeNeeded?.Invoke();
        }
    }

    private static void LogStart(MediaCodec codec, MirrorDecoderInfo info, MirrorVideoConfig config, bool qti)
    {
        string vendor = string.Empty;
        if (qti && OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            try
            {
                IList<string>? names = codec.SupportedVendorParameters;
                vendor = names is not null && names.Contains(QtiLowLatency) ? ", qti low-latency key supported" : ", qti low-latency key not listed";
            }
            catch (Java.Lang.Exception)
            {
                vendor = ", vendor parameters unavailable";
            }
        }

        Log.Info(Tag, $"Mirror decoder {info.Name} ({(info.Hardware ? "hardware" : "SOFTWARE, debug only")}) {config.Codec.Name()} {config.Width}x{config.Height}@{config.Fps}, low-latency {(info.LowLatency ? "on" : "unsupported")}{vendor}");
    }

    /// <summary>Decoder thread. Pending access units stay queued: the next <see cref="StartCodec"/> resumes from a keyframe among them.</summary>
    private void ReleaseCodec()
    {
        if (_codec is not { } codec)
        {
            return;
        }

        _codec = null;
        _running = null;
        SafeRelease(codec);
    }

    private static void SafeRelease(MediaCodec codec)
    {
        try
        {
            codec.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
        }

        try
        {
            codec.Release();
        }
        catch (Java.Lang.Exception)
        {
        }

        codec.Dispose();
    }

    private bool IsCurrent(MediaCodec codec) =>
        _codec is { } current && (ReferenceEquals(current, codec) || current.Equals(codec));

    /// <summary>Decoder thread: pairs pending access units with free input buffers.</summary>
    private void Drain()
    {
        lock (_lock)
        {
            _drainPosted = false;
        }

        if (_codec is not { } codec)
        {
            return;
        }

        while (_freeCount > 0)
        {
            int slot;
            int length;
            uint frame;
            lock (_lock)
            {
                if (_slotCount == 0)
                {
                    return;
                }

                slot = _slotHead;
                length = _slotLength[slot];
                frame = _slotFrame[slot];
            }

            int index = _freeInputs[_freeHead];
            bool ok = Queue(codec, index, slot, length, frame);
            if (!IsCurrent(codec))
            {
                return; // the codec failed and was released or restarted: that emptied the queue and closed the gate
            }

            if (ok)
            {
                _freeHead = (_freeHead + 1) % _freeInputs.Length; // the codec owns that input buffer now
                _freeCount--;
            }

            bool signal = false;
            lock (_lock)
            {
                if (_slotCount == 0)
                {
                    return; // Stop emptied the queue meanwhile
                }

                if (ok)
                {
                    _slotHead = (_slotHead + 1) % SlotCount;
                    _slotCount--;
                }
                else
                {
                    // This frame is lost, so the ones queued behind it can't decode: drop them too, wait for a keyframe.
                    _framesGated += _slotCount;
                    _slotCount = 0;
                    signal = CloseGate();
                }
            }

            if (!ok)
            {
                if (signal)
                {
                    KeyframeNeeded?.Invoke();
                }

                return;
            }
        }
    }

    private bool Queue(MediaCodec codec, int index, int slot, int length, uint frame)
    {
        try
        {
            using Java.Nio.ByteBuffer? buffer = codec.GetInputBuffer(index);
            if (buffer is null || buffer.Capacity() < length)
            {
                return false;
            }

            IntPtr address = buffer.GetDirectBufferAddress();
            if (address == IntPtr.Zero)
            {
                return false;
            }

            Marshal.Copy(_slotData[slot], 0, address, length);
            long pts = Math.Max(StopwatchUs(Stopwatch.GetTimestamp()), _lastPts + 1); // strictly increasing
            _lastPts = pts;
            _ringPts[_ringNext] = pts;
            _ringFrame[_ringNext] = frame;
            _ringNext = (_ringNext + 1) % RingSize;
            codec.QueueInputBuffer(index, 0, length, pts, MediaCodecBufferFlags.None);
            Interlocked.Increment(ref _framesFed);
            return true;
        }
        catch (Java.Lang.IllegalStateException e)
        {
            Log.Warn(Tag, "Mirror decoder input failed: " + e.Message);
            Restart();
            return false;
        }
    }

    private bool TryFrame(long pts, out uint frame)
    {
        for (int i = 0; i < RingSize; i++)
        {
            if (_ringPts[i] == pts)
            {
                frame = _ringFrame[i];
                return true;
            }
        }

        frame = 0;
        return false;
    }

    /// <summary>Decoder thread: a codec error that isn't transient → a fresh codec (gate closes until a keyframe).</summary>
    private void Restart()
    {
        ReleaseCodec();
        if (++_restartsWithoutOutput > MaxRestarts)
        {
            _config = null; // a decoder that fails again and again without a frame: give up until the next Configure
            lock (_lock)
            {
                _configured = false;
                _slotCount = 0;
            }

            Error?.Invoke("Decoder keeps failing");
            return;
        }

        ApplyState();
    }

    private void OnInputAvailable(MediaCodec codec, int index)
    {
        if (!IsCurrent(codec) || _freeCount == _freeInputs.Length)
        {
            return;
        }

        _freeInputs[(_freeHead + _freeCount) % _freeInputs.Length] = index;
        _freeCount++;
        Drain();
    }

    private void OnOutputAvailable(MediaCodec codec, int index, MediaCodec.BufferInfo info)
    {
        long pts = info.PresentationTimeUs;
        bool render = info.Size > 0 && (info.Flags & MediaCodecBufferFlags.CodecConfig) == 0;
        info.Dispose();
        if (!IsCurrent(codec))
        {
            return;
        }

        if (render)
        {
            DecodeLatency.Record(StopwatchUs(Stopwatch.GetTimestamp()) - pts);
        }

        try
        {
            if (render)
            {
                // An explicit "now" timestamp: shown at the next vsync, never scheduled by presentation time.
                codec.ReleaseOutputBuffer(index, Java.Lang.JavaSystem.NanoTime());
            }
            else
            {
                codec.ReleaseOutputBuffer(index, false);
            }
        }
        catch (Java.Lang.IllegalStateException)
        {
            Restart();
        }
    }

    private void OnRendered(MediaCodec codec, long pts, long nanoTime)
    {
        if (!IsCurrent(codec))
        {
            return;
        }

        _restartsWithoutOutput = 0;
        long renderedNs = nanoTime + _nanoToStopwatchNs;
        long renderedUs = renderedNs / 1000;
        RenderLatency.Record(renderedUs - pts);
        if (FrameRendered is { } rendered && TryFrame(pts, out uint frame))
        {
            rendered(frame, renderedUs - StopwatchUs(ClockOrigin));
        }
    }

    private void OnCodecError(MediaCodec codec, MediaCodec.CodecException e)
    {
        if (!IsCurrent(codec) || e.IsTransient)
        {
            return;
        }

        Log.Warn(Tag, "Mirror decoder error (" + e.DiagnosticInfo + "), restarting");
        Restart();
    }

    private sealed class CodecCallback(MirrorDecoder owner) : MediaCodec.Callback, MediaCodec.IOnFrameRenderedListener
    {
        public override void OnInputBufferAvailable(MediaCodec codec, int index) => owner.OnInputAvailable(codec, index);

        public override void OnOutputBufferAvailable(MediaCodec codec, int index, MediaCodec.BufferInfo info) =>
            owner.OnOutputAvailable(codec, index, info);

        public override void OnError(MediaCodec codec, MediaCodec.CodecException e) => owner.OnCodecError(codec, e);

        public override void OnOutputFormatChanged(MediaCodec codec, MediaFormat format)
        {
        }

        public void OnFrameRendered(MediaCodec codec, long presentationTimeUs, long nanoTime) =>
            owner.OnRendered(codec, presentationTimeUs, nanoTime);
    }
}
