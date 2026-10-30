using System.Net;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Input;
using PenBridge.Core.Mirror;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Core.Net;

/// <summary>
/// Screen mirroring of one server session (protocol 1.3, <c>docs/specs/screen-mirroring.md</c>): the phone's requests,
/// the stream plan, the input guard, epochs, the MirrorConfig/MirrorState it owes the phone, KeyframeRequest folding,
/// MirrorReports, and the per-run <see cref="VideoSender"/> (video socket, send and probe threads).
/// </summary>
/// <remarks>
/// <para><b>State.</b> What the phone sees is composed here: no request → Off/PhoneStopped; TLS fallback →
/// Unavailable/TlsFallback; MirrorEnabled off in the settings snapshot → Off/SettingOff; otherwise the head's
/// availability (<see cref="PenBridgeServer.SetMirrorState"/>), and once it is Streaming the plan: Starting while an
/// input is missing (SurfaceInfo, source, encoders), Unavailable/NoCommonCodec when no codec fits, Throttled while the
/// guard pauses, else Streaming with a MirrorConfig. Every change that alters the configuration (codec, size, planned
/// fps or target bitrate; or a new MirrorStart, or a return to Streaming from another state) starts a new epoch.</para>
/// <para><b>Guard.</b> The input guard's steps never start an epoch: the MirrorConfig keeps the planned fps and
/// bitrate, and the running rate goes to the head (<see cref="PenBridgeServer.MirrorRateChanged"/>) and the sender's
/// pacing. A guard pause (Throttled) holds the epoch; the resume continues it with a forced keyframe (the next frame
/// number, no MirrorConfig) when nothing else changed meanwhile, else a new epoch starts.</para>
/// <para><b>Keys.</b> <c>K_vs</c> and <c>K_vc</c> live for the session: each direction has one sequence for the whole
/// session, so a MirrorStop only stops the socket and threads, and the keys are discarded with the session
/// (<see cref="Dispose"/>). Sealing and opening run under their own locks, so a send thread of an ended run can never
/// seal concurrently with a newer one.</para>
/// <para><b>Locks.</b> <c>_lock</c> guards the state; under it this class may take the server's <c>_gate</c>, the input
/// lock (guard attach), the push writer's lock and the sender's lock, never the other way round. Head events go
/// through <see cref="MirrorEvents"/> (thread pool, in order), so no head code runs under a lock, on the input path or
/// on the control read loop.</para>
/// </remarks>
internal sealed class ServerMirror : IDisposable
{
    /// <summary>KeyframeRequests are honoured at most this often; later ones within the window fold into one.</summary>
    public static readonly TimeSpan KeyframeInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>MirrorReports accepted per second (burst the same); more are dropped.</summary>
    public const int ReportsPerSecond = 4;

    private static readonly TimerCallback GuardCallback = static s => ((ServerMirror)s!).RunGuard();
    private static readonly TimerCallback KeyframeCallback = static s => ((ServerMirror)s!).OnKeyframeTimer();
    private static readonly TimerCallback EpochCallback = static s => ((ServerMirror)s!).OnEpochTimer();

    private readonly ServerConnection _connection;
    private readonly PenBridgeServer _server;
    private readonly PenBridgeServerOptions _options;
    private readonly SessionInput _input;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly uint _sessionId;
    private readonly long _sessionEpochUs;
    private readonly Lock _lock = new();
    private readonly Lock _sealLock = new();
    private readonly Lock _openLock = new();
    private readonly VideoCipher _sealer;
    private readonly VideoCipher _opener;
    private readonly MirrorRateController _guard;
    private readonly ITimer _guardTimer;
    private readonly ITimer _keyframeTimer;
    private readonly ITimer _epochTimer;
    private readonly TokenBucket _reports; // control read loop only
    private readonly Action _wakeGuard;
    private bool _ciphersDisposed; // under both crypto locks

    // Guarded by _lock.
    private MirrorRequest? _request;
    private bool _tlsFallback;
    private ushort _surfaceWidth;
    private ushort _surfaceHeight;
    private NormalizedRect _area = NormalizedRect.Full;
    private MirrorStreamPlan? _plan;
    private MirrorStreamConfig? _config; // the epoch being streamed; null while no video flows
    private MirrorStreamConfig? _pausedConfig; // the epoch held by a guard pause (resumable), else null
    private MirrorRateChange _rate; // the rate the current epoch runs at (head and pacing)
    private byte _epoch;
    private bool _guardRunning;
    private VideoSender? _sender;
    private MirrorState _state = MirrorState.Off;
    private MirrorReason _reason = MirrorReason.None;
    private MirrorState _stateSent;
    private MirrorReason _reasonSent;
    private bool _stateUnsent;
    private MirrorConfigMessage? _configOut;
    private long _lastKeyframeAt;
    private bool _haveKeyframeAt;
    private bool _keyframePending;
    private bool _fastPing;
    private uint _lastLatencyFrame;
    private byte _lastLatencyEpoch;
    private bool _started; // a MirrorStart arrived in this session: from then on the phone gets every state change
    private bool _disposed;
    private int _qosWarned;

    // Counters (Interlocked).
    private long _probes;
    private long _authFailures;
    private long _malformed;
    private long _stale;
    private long _rateLimited;
    private long _reportsDropped;

    public ServerMirror(
        ServerConnection connection,
        PenBridgeServer server,
        SessionInput input,
        ReadOnlySpan<byte> serverToClientKey,
        ReadOnlySpan<byte> clientToServerKey,
        uint sessionId,
        long sessionEpoch)
    {
        _connection = connection;
        _server = server;
        _options = server.Options;
        _input = input;
        _time = _options.TimeProvider;
        _log = server.Log;
        _sessionId = sessionId;
        _sessionEpochUs = (long)(sessionEpoch * (1e6 / _time.TimestampFrequency));
        _sealer = new VideoCipher(serverToClientKey, sessionId);
        _opener = new VideoCipher(clientToServerKey, sessionId);
        _guard = new MirrorRateController(_time);
        _guardTimer = _time.CreateTimer(GuardCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _keyframeTimer = _time.CreateTimer(KeyframeCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _epochTimer = _time.CreateTimer(EpochCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _reports = new TokenBucket(_time, ReportsPerSecond, ReportsPerSecond);
        _wakeGuard = WakeGuard;
    }

    /// <summary>The state the phone was last told, or is about to be told (tests, UI).</summary>
    public (MirrorState State, MirrorReason Reason) State
    {
        get
        {
            lock (_lock)
            {
                return (_state, _reason);
            }
        }
    }

    /// <summary>The configuration of the epoch being streamed, or null (tests).</summary>
    public MirrorStreamConfig? Config
    {
        get
        {
            lock (_lock)
            {
                return _config;
            }
        }
    }

    /// <summary>True while the session's input runs over TLS (no video; tests).</summary>
    internal bool InTlsFallback
    {
        get
        {
            lock (_lock)
            {
                return _tlsFallback;
            }
        }
    }

    /// <summary>The guard of this session (tests).</summary>
    internal MirrorRateController Guard => _guard;

    /// <summary>The video sender of the current run, or null (tests).</summary>
    internal VideoSender? Sender => Volatile.Read(ref _sender);

    /// <summary>MirrorReports dropped by the 4/s cap (tests).</summary>
    internal long ReportsDropped => Interlocked.Read(ref _reportsDropped);

    /// <summary>Counters of this session's video path.</summary>
    public MirrorCounters Counters
    {
        get
        {
            VideoSender? sender = Sender;
            return new MirrorCounters(
                Interlocked.Read(ref _probes),
                Interlocked.Read(ref _authFailures),
                Interlocked.Read(ref _malformed),
                Interlocked.Read(ref _stale),
                Interlocked.Read(ref _rateLimited),
                sender?.EndpointChanges ?? 0,
                sender?.FramesSent ?? 0,
                sender?.FramesDropped ?? 0,
                sender?.FragmentsSent ?? 0);
        }
    }

    /// <summary>The Mac's video port of the current run (0 without one).</summary>
    internal int VideoPort => Sender?.Port ?? 0;

    /// <summary>MirrorStart: the phone wants video (again, possibly with other limits or a new video socket).</summary>
    public void OnMirrorStart(in MirrorStartMessage message)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _request = MirrorRequest.FromMessage(message);
            _started = true;
            _tlsFallback = _input.Transport == InputTransport.Tls;
            _stateUnsent = true; // every MirrorStart gets a MirrorState, changed or not
            if (!_tlsFallback)
            {
                _server.MirrorEvents.Requested(_request.Value);
            }

            EvaluateLocked(newEpoch: true);
        }
    }

    /// <summary>MirrorStop: the phone no longer wants video.</summary>
    public void OnMirrorStop()
    {
        lock (_lock)
        {
            if (_disposed || _request is null)
            {
                return;
            }

            _request = null;
            if (!_tlsFallback)
            {
                _server.MirrorEvents.Stopped();
            }

            EvaluateLocked(newEpoch: false);
        }
    }

    /// <summary>The session switched input to TLS fallback: no video until the phone asks again over UDP.</summary>
    public void OnTlsFallback()
    {
        lock (_lock)
        {
            if (_disposed || _tlsFallback)
            {
                return;
            }

            _tlsFallback = true;
            if (_request is not null)
            {
                _server.MirrorEvents.Stopped();
                EvaluateLocked(newEpoch: false);
            }
        }
    }

    /// <summary>
    /// UDP input works again after a fallback (input path: only posts). A request still standing streams again (the
    /// head is asked again, a new epoch starts) unless a TLS frame read since the post put the input back on TLS: then
    /// the session stays in fallback, and the next accepted UDP datagram restores it again.
    /// </summary>
    public void OnUdpRestored() =>
        ThreadPool.UnsafeQueueUserWorkItem(static mirror => mirror.RestoreUdp(), this, preferLocal: false);

    /// <summary>The posted half of <see cref="OnUdpRestored"/> (thread pool; tests call it directly).</summary>
    internal void RestoreUdp()
    {
        lock (_lock)
        {
            // Re-check the transport: a TLS frame between the post and now flipped it back, and its OnTlsFallback was a
            // no-op (still in fallback), so resuming here would stream during fallback.
            if (_disposed || !_tlsFallback || _input.Transport != InputTransport.Udp)
            {
                return;
            }

            _tlsFallback = false;
            if (_request is { } request)
            {
                _server.MirrorEvents.Requested(request);
                EvaluateLocked(newEpoch: true);
            }
        }
    }

    /// <summary>A SurfaceInfo: the phone's surface in pixels.</summary>
    public void OnSurface(ushort widthPx, ushort heightPx)
    {
        lock (_lock)
        {
            _surfaceWidth = widthPx;
            _surfaceHeight = heightPx;
            EvaluateLocked(newEpoch: false);
        }
    }

    /// <summary>The session's active area changed (SessionInput.ActiveAreaChanged; any thread, no locks held).</summary>
    public void OnActiveArea(NormalizedRect area)
    {
        lock (_lock)
        {
            _area = area;
            EvaluateLocked(newEpoch: false);
        }
    }

    /// <summary>A server-wide mirroring input changed (head state, source, encoders, settings).</summary>
    public void Reevaluate()
    {
        lock (_lock)
        {
            EvaluateLocked(newEpoch: false);
        }
    }

    /// <summary>KeyframeRequest: at most one honoured per <see cref="KeyframeInterval"/>, later ones folded; other epochs ignored.</summary>
    public void OnKeyframeRequest(in KeyframeRequestMessage message)
    {
        lock (_lock)
        {
            if (_disposed || _config is not { } config || message.Epoch != config.Epoch)
            {
                return;
            }

            long now = _time.GetTimestamp();
            TimeSpan since = _haveKeyframeAt ? _time.GetElapsedTime(_lastKeyframeAt, now) : TimeSpan.MaxValue;
            if (since >= KeyframeInterval)
            {
                HonourKeyframeLocked(now);
            }
            else if (!_keyframePending)
            {
                _keyframePending = true;
                _keyframeTimer.Change(KeyframeInterval - since, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>MirrorReport: at most <see cref="ReportsPerSecond"/> per second; feeds the guard and the latency log.</summary>
    public void OnMirrorReport(in MirrorReportMessage report)
    {
        if (!_reports.TryTake())
        {
            Interlocked.Increment(ref _reportsDropped);
            return;
        }

        lock (_lock)
        {
            if (_disposed || _config is not { } config || report.Epoch != config.Epoch || _sender is not { } sender)
            {
                return;
            }

            _guard.OnMirrorReport(report, config.Epoch, (ulong)sender.FragmentsInEpoch);
            RecordVideoLatencyLocked(report, sender, config.Epoch);
        }

        RunGuard();
    }

    /// <summary>One Ping round trip of the session.</summary>
    public void OnRtt(TimeSpan rtt)
    {
        if (!Volatile.Read(ref _guardRunning))
        {
            return;
        }

        _guard.OnRttSample(rtt);
        RunGuard();
    }

    /// <summary>
    /// An access unit from the head (thread-safe, non-blocking): copied into the run's send queue or dropped (see
    /// <see cref="VideoSender.Enqueue"/>). <paramref name="captureTimeUs"/> is host-monotonic µs on the server
    /// <see cref="TimeProvider"/>'s timestamp scale; it is converted to the session (Ping) clock here.
    /// </summary>
    public void SendVideoFrame(ReadOnlySpan<byte> accessUnit, bool keyframe, long captureTimeUs) =>
        Volatile.Read(ref _sender)?.Enqueue(accessUnit, keyframe, captureTimeUs);

    /// <summary>Microseconds of the session (Ping) clock for a host-monotonic capture time.</summary>
    internal uint ToSessionUs(long captureTimeUs) => unchecked((uint)(captureTimeUs - _sessionEpochUs));

    /// <summary>For the push writer: the MirrorConfig and MirrorState not yet sent (config first).</summary>
    public void TakeOutbound(out MirrorConfigMessage? config, out MirrorStateMessage? state)
    {
        lock (_lock)
        {
            config = _configOut;
            _configOut = null;
            state = null;
            if (_stateUnsent || _state != _stateSent || _reason != _reasonSent)
            {
                state = new MirrorStateMessage(_state, MirrorWire.HasReason(_state) ? _reason : MirrorReason.None);
                _stateSent = _state;
                _reasonSent = _reason;
                _stateUnsent = false;
            }
        }
    }

    /// <summary>Seals fragment <paramref name="index"/> of a frame (send thread); 0 once the session's keys are gone.</summary>
    internal int SealFragment(ReadOnlySpan<byte> accessUnit, in VideoFrameInfo frame, int index, Span<byte> datagram)
    {
        lock (_sealLock)
        {
            return _ciphersDisposed ? 0 : VideoFragmenter.WriteFragment(_sealer, accessUnit, frame, index, datagram);
        }
    }

    /// <summary>
    /// One datagram on the video socket (probe thread): header checks and the stale check before decryption, one
    /// attempt from the session's forged-datagram budget per decryption, then pinning (only an authenticated Probe
    /// with a sequence above every earlier one moves the endpoint) and the epoch release. Allocation-free unless the
    /// endpoint moves.
    /// </summary>
    internal void OnVideoDatagram(Span<byte> datagram, SocketAddress source, VideoSender sender)
    {
        if (datagram.Length is < VideoDatagram.MinSize or > VideoDatagram.MaxSize
            || !VideoDatagramHeader.TryRead(datagram, out VideoDatagramHeader header)
            || header.Kind != VideoDatagramKind.Probe
            || header.SessionId != _sessionId)
        {
            Interlocked.Increment(ref _malformed);
            return;
        }

        lock (_openLock)
        {
            if (_ciphersDisposed)
            {
                return;
            }

            if (header.Sequence <= _opener.HighestSequence)
            {
                Interlocked.Increment(ref _stale);
                return;
            }
        }

        if (!_input.TryTakeAuthBudget())
        {
            Interlocked.Increment(ref _rateLimited);
            return;
        }

        bool ok;
        ulong before;
        lock (_openLock)
        {
            if (_ciphersDisposed)
            {
                return;
            }

            before = _opener.HighestSequence;
            ok = _opener.TryOpen(datagram, out header, out _);
        }

        if (!ok)
        {
            Interlocked.Increment(ref _authFailures);
            return;
        }

        _input.RefundAuthBudget();
        Interlocked.Increment(ref _probes);
        if (header.Sequence > before)
        {
            sender.Pin(source);
        }

        lock (_lock)
        {
            if (!_disposed && ReferenceEquals(_sender, sender) && _config is { } config && header.Epoch == config.Epoch)
            {
                sender.Release(config.Epoch);
            }
        }
    }

    /// <summary>Ends mirroring for good (session end, takeover, Bye): stops the run, the guard and the timers, discards the keys.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_request is not null && !_tlsFallback)
            {
                _server.MirrorEvents.Stopped();
            }

            _request = null;
            _disposed = true;
            StopVideoLocked(disposeSender: true);
            StopGuardLocked();
            _guardTimer.Dispose();
            _keyframeTimer.Dispose();
            _epochTimer.Dispose();
        }

        lock (_sealLock)
        {
            lock (_openLock)
            {
                _ciphersDisposed = true;
                _sealer.Dispose();
                _opener.Dispose();
            }
        }
    }

    /// <summary>
    /// Recomputes what the phone should get from every input and applies it: the state, a new epoch when the
    /// configuration changed (or <paramref name="newEpoch"/> while streaming), the guard, the run's socket and threads,
    /// and the ping rate.
    /// </summary>
    private void EvaluateLocked(bool newEpoch)
    {
        if (_disposed || !_started)
        {
            return;
        }

        (MirrorState state, MirrorReason reason) = PlanLocked(out MirrorStreamPlan plan);
        if (state == MirrorState.Streaming)
        {
            bool codecChanged = _plan is { } previous && previous.Codec != plan.Codec;
            if (!_guardRunning || codecChanged)
            {
                _guard.Start(plan);
                if (!_guardRunning)
                {
                    _guardRunning = true;
                    _input.AttachGuard(_guard, _wakeGuard);
                }
            }
            else if (_plan != plan)
            {
                _guard.Retarget(plan);
            }

            _plan = plan;
            MirrorGuardLevel level = _guard.Level;
            MirrorStreamConfig wanted = plan.ToConfig(0);
            if (newEpoch)
            {
                _pausedConfig = null; // a MirrorStart (or a return from fallback): the phone starts over too
            }

            if (level.Paused)
            {
                PauseVideoLocked();
                state = MirrorState.Throttled;
            }
            else if (!newEpoch && _config is null && _pausedConfig is { } held && held with { Epoch = 0 } == wanted && _sender is not null)
            {
                ResumeEpochLocked(held, level);
            }
            else if (newEpoch || _config is not { } current || current with { Epoch = 0 } != wanted)
            {
                if (!BeginEpochLocked(wanted, level))
                {
                    state = MirrorState.Unavailable;
                    reason = MirrorReason.CaptureFailed;
                }
            }
            else
            {
                ApplyRateLocked(level);
            }

            ArmGuardLocked();
        }
        else
        {
            StopVideoLocked(disposeSender: _request is null || _tlsFallback);
            StopGuardLocked();
        }

        SetStateLocked(state, reason);
        SetFastPingLocked(_guardRunning);
    }

    /// <summary>The state the inputs call for and, when it is Streaming, the plan.</summary>
    private (MirrorState, MirrorReason) PlanLocked(out MirrorStreamPlan plan)
    {
        plan = default;
        if (_request is not { } request)
        {
            return (MirrorState.Off, MirrorReason.PhoneStopped);
        }

        if (_tlsFallback)
        {
            return (MirrorState.Unavailable, MirrorReason.TlsFallback);
        }

        MirrorHostState host = _server.MirrorHost;
        if (host.Settings.Known && !host.Settings.Enabled)
        {
            return (MirrorState.Off, MirrorReason.SettingOff);
        }

        if (host.State != MirrorState.Streaming)
        {
            return (host.State, host.Reason);
        }

        if (_surfaceWidth == 0 || host.Source is not { } source || !host.EncodersSet)
        {
            return (MirrorState.Starting, MirrorReason.None);
        }

        (int activeWidth, int activeHeight) = MirrorStreamPlanner.ActiveAreaPixels(_surfaceWidth, _surfaceHeight, _area);
        var inputs = new MirrorPlanInputs
        {
            Request = request,
            Encoders = host.Encoders,
            Source = source,
            ActiveWidthPx = Math.Max(1, activeWidth),
            ActiveHeightPx = Math.Max(1, activeHeight),
            Quality = host.Settings.Quality,
            FpsLimit = host.Settings.FpsLimit,
            MaxPixelRate = host.MaxPixelRate > 0 ? host.MaxPixelRate : MirrorStreamPlanner.DefaultMaxPixelRate,
        };
        return MirrorStreamPlanner.TryPlan(inputs, out plan, out MirrorReason failure)
            ? (MirrorState.Streaming, MirrorReason.None)
            : (MirrorState.Unavailable, failure);
    }

    /// <summary>
    /// Starts a new epoch with <paramref name="wanted"/> (planned values) running at the guard's <paramref name="level"/>:
    /// the run's socket and threads if needed, the MirrorConfig, the head events, the probe wait.
    /// </summary>
    private bool BeginEpochLocked(MirrorStreamConfig wanted, MirrorGuardLevel level)
    {
        if (_sender is null)
        {
            try
            {
                Volatile.Write(ref _sender, new VideoSender(this, _options, _log, ref _qosWarned));
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                NetLog.Unexpected(_log, null, ex);
                StopVideoLocked(disposeSender: false);
                return false;
            }
        }

        _epoch = unchecked((byte)(_epoch + 1));
        MirrorStreamConfig config = wanted with { Epoch = _epoch };
        _config = config;
        _pausedConfig = null;
        _rate = new MirrorRateChange(config.Epoch, level.Fps, level.BitrateKbps);
        _sender!.BeginEpoch(config, level.BitrateKbps, level.Fps);
        _configOut = config.ToMessage((ushort)_sender.Port);
        _haveKeyframeAt = false;
        _keyframePending = false;
        _keyframeTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _epochTimer.Change(VideoDatagram.EpochProbeWait, Timeout.InfiniteTimeSpan);
        _server.MirrorEvents.ConfigChanged(config);
        NetLog.MirrorConfig(_log, config.Epoch, config.Codec, config.WidthPx, config.HeightPx, config.Fps, config.BitrateKbps);
        if (level.Fps != config.Fps || level.BitrateKbps != config.BitrateKbps)
        {
            RaiseRateLocked(); // the guard holds the stream below the plan: the head starts the epoch at that rate
        }

        _ = _connection.RequestPush(ServerConnection.PushMirror);
        return true;
    }

    /// <summary>A guard step within the epoch: the new rate to the sender's pacing and the head; nothing to the phone.</summary>
    private void ApplyRateLocked(MirrorGuardLevel level)
    {
        if (_config is not { } config || _sender is not { } sender)
        {
            return;
        }

        var rate = new MirrorRateChange(config.Epoch, level.Fps, level.BitrateKbps);
        if (rate == _rate)
        {
            return;
        }

        _rate = rate;
        sender.SetRate(rate.BitrateKbps, rate.Fps);
        RaiseRateLocked();
    }

    private void RaiseRateLocked()
    {
        _server.MirrorEvents.RateChanged(_rate);
        NetLog.MirrorRate(_log, _rate.Epoch, _rate.Fps, _rate.BitrateKbps);
    }

    /// <summary>The guard paused: no video, but the epoch is held so the resume can continue it.</summary>
    private void PauseVideoLocked()
    {
        if (_config is { } config)
        {
            StopVideoLocked(disposeSender: false);
            _pausedConfig = config;
        }
    }

    /// <summary>
    /// The guard resumed and the plan is the held epoch's: the same epoch goes on (no MirrorConfig, so the phone keeps
    /// its decoder) at the guard's rate, from a forced keyframe that takes the next frame number.
    /// </summary>
    private void ResumeEpochLocked(MirrorStreamConfig held, MirrorGuardLevel level)
    {
        _config = held;
        _pausedConfig = null;
        VideoSender sender = _sender!;
        sender.Resume();
        ApplyRateLocked(level);
        if (!sender.IsReleased)
        {
            _epochTimer.Change(VideoDatagram.EpochProbeWait, Timeout.InfiniteTimeSpan); // paused before its Probe came
        }

        _keyframePending = false;
        _keyframeTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        HonourKeyframeLocked(_time.GetTimestamp());
    }

    /// <summary>No video flows: the epoch ends (frames are dropped); with <paramref name="disposeSender"/> the run's socket and threads stop too.</summary>
    private void StopVideoLocked(bool disposeSender)
    {
        _config = null;
        _pausedConfig = null;
        _configOut = null;
        _keyframePending = false;
        _keyframeTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _epochTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (_sender is { } sender)
        {
            if (disposeSender)
            {
                Volatile.Write(ref _sender, null);
                sender.Dispose();
            }
            else
            {
                sender.Deactivate();
            }
        }
    }

    private void StopGuardLocked()
    {
        _plan = null;
        if (!_guardRunning)
        {
            return;
        }

        Volatile.Write(ref _guardRunning, false);
        _input.AttachGuard(null, null);
        _guard.Stop();
        _guardTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void SetStateLocked(MirrorState state, MirrorReason reason)
    {
        reason = MirrorWire.HasReason(state) ? reason : MirrorReason.None;
        if (state == _state && reason == _reason && !_stateUnsent)
        {
            return;
        }

        if (state != _state || reason != _reason)
        {
            NetLog.MirrorState(_log, state, reason);
        }

        _state = state;
        _reason = reason;
        _ = _connection.RequestPush(ServerConnection.PushMirror);
    }

    private void SetFastPingLocked(bool fast)
    {
        if (fast != _fastPing)
        {
            _fastPing = fast;
            _connection.SetMirrorPing(fast);
        }
    }

    private void HonourKeyframeLocked(long now)
    {
        _lastKeyframeAt = now;
        _haveKeyframeAt = true;
        _server.MirrorEvents.Keyframe();
    }

    private void OnKeyframeTimer()
    {
        lock (_lock)
        {
            if (_disposed || !_keyframePending)
            {
                return;
            }

            _keyframePending = false;
            if (_config is not null)
            {
                HonourKeyframeLocked(_time.GetTimestamp());
            }
        }
    }

    /// <summary><see cref="VideoDatagram.EpochProbeWait"/> after a MirrorConfig: send the epoch even without its Probe (to the pinned endpoint).</summary>
    private void OnEpochTimer()
    {
        lock (_lock)
        {
            if (!_disposed && _config is { } config)
            {
                _sender?.Release(config.Epoch);
            }
        }
    }

    /// <summary>Evaluates the guard (timer, RTT sample, report, input wake) and applies a step.</summary>
    private void RunGuard()
    {
        try
        {
            lock (_lock)
            {
                if (_disposed || !_guardRunning)
                {
                    return;
                }

                if (_guard.Update())
                {
                    EvaluateLocked(newEpoch: false);
                }

                ArmGuardLocked();
            }
        }
#pragma warning disable CA1031 // Runs on timer threads: an escaping exception would crash the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            NetLog.Unexpected(_log, null, ex);
        }
    }

    private void ArmGuardLocked()
    {
        if (!_guardRunning)
        {
            return;
        }

        TimeSpan? due = _guard.NextUpdateIn;
        _guardTimer.Change(due is { } d ? (d > TimeSpan.Zero ? d : TimeSpan.FromMilliseconds(1)) : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Input path: a new input degradation. Only fires the guard timer now (no lock, no allocation).</summary>
    private void WakeGuard()
    {
        try
        {
            _guardTimer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RecordVideoLatencyLocked(in MirrorReportMessage report, VideoSender sender, byte epoch)
    {
        if (_options.LatencyRecorder is not { } latency || report.LastRenderedFrame == 0
            || (report.LastRenderedFrame == _lastLatencyFrame && epoch == _lastLatencyEpoch))
        {
            return;
        }

        if (sender.TryGetCaptureTimestamp(epoch, report.LastRenderedFrame, out long captured))
        {
            _lastLatencyFrame = report.LastRenderedFrame;
            _lastLatencyEpoch = epoch;
            latency.RecordVideo(_sessionId, report.LastRenderedAtUs, captured, _time.GetTimestamp());
        }
    }
}

/// <summary>
/// The server's mirroring events, raised in order on the thread pool by one drainer (no head code runs under a
/// lock, on the input path or on a control read loop, and an event raised while another is being handled waits for
/// it). Server-wide, so an old session's MirrorStopped precedes a new session's MirrorRequested.
/// </summary>
internal sealed class MirrorEvents(PenBridgeServer server)
{
    private readonly Lock _lock = new();
    private readonly Queue<Item> _queue = new();
    private bool _draining;

    internal enum Kind
    {
        Requested,
        Stopped,
        ConfigChanged,
        RateChanged,
        Keyframe,
    }

    public void Requested(MirrorRequest request) => Enqueue(new Item(Kind.Requested, request, default, default));

    public void Stopped() => Enqueue(new Item(Kind.Stopped, default, default, default));

    public void ConfigChanged(MirrorStreamConfig config) => Enqueue(new Item(Kind.ConfigChanged, default, config, default));

    public void RateChanged(MirrorRateChange rate) => Enqueue(new Item(Kind.RateChanged, default, default, rate));

    public void Keyframe() => Enqueue(new Item(Kind.Keyframe, default, default, default));

    private void Enqueue(Item item)
    {
        lock (_lock)
        {
            _queue.Enqueue(item);
            if (_draining)
            {
                return;
            }

            _draining = true;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static s => s.Drain(), this, preferLocal: false);
    }

    private void Drain()
    {
        while (true)
        {
            Item item;
            lock (_lock)
            {
                if (!_queue.TryDequeue(out item))
                {
                    _draining = false;
                    return;
                }
            }

            server.RaiseMirrorEvent(item.What, item.Request, item.Config, item.Rate);
        }
    }

    private readonly record struct Item(Kind What, MirrorRequest Request, MirrorStreamConfig Config, MirrorRateChange Rate);
}

/// <summary>The head's mirroring inputs, server-wide (<see cref="PenBridgeServer.SetMirrorState"/> and friends).</summary>
internal readonly record struct MirrorHostState(
    MirrorState State,
    MirrorReason Reason,
    MirrorCodecs Encoders,
    bool EncodersSet,
    MirrorSource? Source,
    long MaxPixelRate,
    MirrorSettingsState Settings);

/// <summary>Settings 30–32 from the latest snapshot; <see cref="Known"/> false without a snapshot that carries key 30.</summary>
internal readonly record struct MirrorSettingsState(bool Known, bool Enabled, MirrorQuality Quality, int FpsLimit)
{
    public static MirrorSettingsState Default { get; } = new(false, true, MirrorQuality.Balanced, Settings.MacSettings.MirrorFpsAuto);

    public static MirrorSettingsState From(IReadOnlyList<SettingEntry> entries)
    {
        var values = Settings.MacSettingValues.FromEntries(entries);
        return new MirrorSettingsState(
            values.Carries(Settings.MacSettingKey.MirrorEnabled),
            values.MirrorEnabled,
            values.MirrorQuality,
            values.MirrorFps);
    }
}
