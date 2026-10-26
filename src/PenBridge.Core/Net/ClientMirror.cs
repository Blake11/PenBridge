using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Mirror;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Core.Net;

/// <summary>
/// Screen mirroring of one client connection (protocol 1.3, <c>docs/specs/screen-mirroring.md</c>): MirrorStart/Stop,
/// the video socket and its receive thread (one per run), reassembly, and what the phone owes the Mac on its own:
/// Probes (after each MirrorConfig, then every second), KeyframeRequests (every 250 ms until a keyframe) and
/// MirrorReports (every 500 ms).
/// </summary>
/// <remarks>
/// <para>Video is accepted only from the Mac's address and the <c>videoUdpPort</c> of its latest MirrorConfig, and
/// only when it authenticates under <c>K_vs</c>. Allocation-free per datagram. In TLS fallback there is no video: the
/// client sends MirrorStop, closes the run and reports Unavailable/TlsFallback, and asks again once UDP is back.</para>
/// <para>Control writes go through one coalescing writer (latest MirrorStart-or-Stop, KeyframeRequest, MirrorReport),
/// so they keep their order and never block a caller. The keys live for the connection (one sequence per direction
/// for the whole session).</para>
/// </remarks>
internal sealed class ClientMirror : IDisposable
{
    /// <summary>Probes repeat this often while streaming.</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);

    /// <summary>MirrorReports go out this often while streaming.</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(500);

    private const int WriteStartStop = 1;
    private const int WriteKeyframe = 2;
    private const int WriteReport = 4;

    private static readonly TimerCallback TickCallback = static s => ((ClientMirror)s!).OnTick();
    private static readonly Action<ClientMirror> OpenRunCallback = static m => m.OpenRun();

    private readonly PenBridgeClient _owner;
    private readonly ControlChannel _channel;
    private readonly IPAddress _server;
    private readonly PenBridgeClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly Lock _openLock = new();
    private readonly Lock _probeLock = new();
    private readonly Lock _renderLock = new();
    private readonly VideoCipher _opener;
    private readonly VideoCipher _sealer;
    private readonly FrameReassembler _reassembler;
    private readonly ITimer _tick;
    private readonly CoalescingWriter _writer;
    private readonly byte[] _probe = new byte[VideoDatagram.MinSize];
    private bool _ciphersDisposed; // under _openLock and _probeLock

    // Guarded by _lock.
    private MirrorRequest? _request;
    private bool _fallback;
    private Socket? _socket;
    private int _port;
    private MirrorStreamConfig? _config;
    private bool _streaming;
    private long _nextProbe;
    private long _nextReport;
    private bool _startSent; // the Mac was told to start (and not told to stop)
    private bool _wantStart; // the latest intent to write: start (true) or stop
    private bool _intentPending;
    private KeyframeRequestMessage? _keyframeOut;
    private MirrorReportMessage? _reportOut;
    private bool _disposed;
    private int _qosWarned;
    private int _openWarned;
    private bool _opening; // an OpenRun is queued or running (under _lock)

    private SocketAddress? _source; // the Mac's video endpoint; read by the receive thread
    private long _armedDue = long.MaxValue; // tick due (timestamp), read without the lock to skip re-arms
    private byte _renderedEpoch; // the render fields: under _renderLock; _lastRendered 0 = none
    private uint _lastRendered;
    private ulong _lastRenderedAt;

    // Counters (Interlocked).
    private long _frames;
    private long _authFailures;
    private long _malformed;
    private long _foreign;
    private long _datagrams;
    private long _keyframesSent;
    private long _probesSent;
    private long _reportsSent;

    // Totals of finished epochs (guarded by _lock); the running epoch's come from the reassembler.
    private long _epochs;
    private long _droppedBefore;
    private long _lostBefore;

    public ClientMirror(
        PenBridgeClient owner,
        ControlChannel channel,
        IPAddress server,
        PenBridgeClientOptions options,
        ILogger log,
        ReadOnlySpan<byte> serverToClientKey,
        ReadOnlySpan<byte> clientToServerKey,
        uint sessionId)
    {
        _owner = owner;
        _channel = channel;
        _server = server;
        _options = options;
        _time = options.TimeProvider;
        _log = log;
        _opener = new VideoCipher(serverToClientKey, sessionId);
        _sealer = new VideoCipher(clientToServerKey, sessionId);
        _reassembler = new FrameReassembler(_time);
        _tick = _time.CreateTimer(TickCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _writer = new CoalescingWriter(DrainAsync);
        _ = _writer.Enable();
    }

    /// <summary>The video socket's port of the current run (0 without one; tests).</summary>
    internal int Port
    {
        get
        {
            lock (_lock)
            {
                return _port;
            }
        }
    }

    /// <summary>The reassembler (tests).</summary>
    internal FrameReassembler Reassembler => _reassembler;

    /// <summary>The last MirrorConfig of this run, or null.</summary>
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

    /// <summary>Video-path counters (frames handed out, auth failures, malformed, datagrams from another source, datagrams from the Mac).</summary>
    public MirrorCounters Counters => new(
        Interlocked.Read(ref _frames),
        Interlocked.Read(ref _authFailures),
        Interlocked.Read(ref _malformed),
        0,
        0,
        0,
        Interlocked.Read(ref _frames),
        Interlocked.Read(ref _foreign),
        Interlocked.Read(ref _datagrams));

    /// <summary>Connection-wide mirroring counters (see <see cref="MirrorClientStats"/>).</summary>
    public MirrorClientStats Stats
    {
        get
        {
            long dropped;
            long lost;
            long epochs;
            lock (_lock)
            {
                dropped = _droppedBefore;
                lost = _lostBefore;
                epochs = _epochs;
                if (_reassembler.IsActive)
                {
                    dropped += _reassembler.FramesDropped;
                    lost += _reassembler.FragmentsLost;
                }
            }

            return new MirrorClientStats(
                Interlocked.Read(ref _datagrams),
                Interlocked.Read(ref _frames),
                dropped,
                lost,
                Interlocked.Read(ref _keyframesSent),
                Interlocked.Read(ref _probesSent),
                Interlocked.Read(ref _reportsSent),
                epochs,
                Interlocked.Read(ref _authFailures),
                Interlocked.Read(ref _malformed),
                Interlocked.Read(ref _foreign));
        }
    }

    /// <summary>
    /// Asks for video with <paramref name="request"/> (a second call sends a new MirrorStart). Opens the video socket
    /// and its receive thread (on the thread pool) unless the session is in TLS fallback (then it asks once UDP is back).
    /// </summary>
    public void Start(MirrorRequest request)
    {
        _ = request.ToMessage(1); // validates before anything changes
        bool fallback;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _request = request;
            fallback = _fallback;
            if (!fallback)
            {
                WantRunLocked();
            }
        }

        if (fallback)
        {
            _owner.RaiseMirrorState(MirrorState.Unavailable, MirrorReason.TlsFallback);
        }
    }

    /// <summary>No more video: MirrorStop (if a MirrorStart went out), the run's socket and thread close.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_disposed || _request is null)
            {
                return;
            }

            _request = null;
            CloseRunLocked();
            QueueIntentLocked(start: false);
        }
    }

    /// <summary>
    /// The head rendered <paramref name="frameNumber"/> of <paramref name="epoch"/> at <paramref name="renderedAtUs"/>
    /// (client Ping clock); goes into the next MirrorReport of that epoch only.
    /// </summary>
    public void ReportFrameRendered(byte epoch, uint frameNumber, ulong renderedAtUs)
    {
        lock (_renderLock)
        {
            _renderedEpoch = epoch;
            _lastRendered = frameNumber;
            _lastRenderedAt = renderedAtUs;
        }
    }

    /// <summary>The same for the current epoch (none: ignored).</summary>
    public void ReportFrameRendered(uint frameNumber, ulong renderedAtUs)
    {
        byte epoch;
        lock (_lock)
        {
            if (_config is not { } config)
            {
                return;
            }

            epoch = config.Epoch;
        }

        ReportFrameRendered(epoch, frameNumber, renderedAtUs);
    }

    /// <summary>
    /// The head's decoder needs a keyframe: the reassembler gates and requests one now and every 250 ms until it
    /// arrives (the same path as a loss). Ignored while no epoch streams.
    /// </summary>
    public void RequestKeyframe()
    {
        lock (_lock)
        {
            if (_disposed || !_streaming)
            {
                return;
            }

            _reassembler.RequestKeyframe();
            TakeKeyframeRequestLocked();
            ArmLocked(_time.GetTimestamp());
        }
    }

    /// <summary>
    /// Read loop: a MirrorConfig. Starts the epoch in the reassembler, raises MirrorConfigReceived, then sends the
    /// epoch's Probe ("I accept this epoch"). Ignored without a running request.
    /// </summary>
    public void OnConfig(in MirrorConfigMessage message)
    {
        MirrorStreamConfig config = MirrorStreamConfig.FromMessage(message);
        lock (_lock)
        {
            if (_disposed || _request is null || _socket is null)
            {
                return;
            }

            if (_source is null || PortOf(_source) != message.VideoUdpPort)
            {
                Volatile.Write(ref _source, new IPEndPoint(_server, message.VideoUdpPort).Serialize());
            }

            _config = config;
            lock (_renderLock)
            {
                _lastRendered = 0; // a frame number of the previous epoch means nothing in this one
            }

            FoldEpochLocked();
            _epochs++;
            _reassembler.Start(config.Epoch);
            _streaming = true;
            long now = _time.GetTimestamp();
            _nextProbe = now + Ticks(ProbeInterval);
            _nextReport = now + Ticks(ReportInterval);
            ArmLocked(now);
        }

        _owner.RaiseMirrorConfig(config);
        SendProbe(config.Epoch);
    }

    /// <summary>Read loop: a MirrorState. Anything but Streaming ends the epoch here (the Mac starts a new one to resume).</summary>
    public void OnState(MirrorState state, MirrorReason reason)
    {
        if (state != MirrorState.Streaming)
        {
            lock (_lock)
            {
                StopEpochLocked();
            }
        }

        _owner.RaiseMirrorState(state, reason);
    }

    /// <summary>
    /// The input transport changed (called under the client's send lock on the read loop: only records and posts, never
    /// opens a socket or starts a thread here). Fallback: MirrorStop, close the run, Unavailable/TlsFallback. Back on
    /// UDP: the run opens on the thread pool, then MirrorStart goes out.
    /// </summary>
    public void OnTransport(bool tls)
    {
        lock (_lock)
        {
            if (_disposed || tls == _fallback)
            {
                return;
            }

            _fallback = tls;
            if (_request is null)
            {
                return;
            }

            if (tls)
            {
                CloseRunLocked();
                QueueIntentLocked(start: false);
            }
            else
            {
                WantRunLocked();
            }
        }

        if (tls)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static owner => owner.RaiseMirrorState(MirrorState.Unavailable, MirrorReason.TlsFallback), _owner, preferLocal: false);
        }
    }

    /// <summary>The connection ended: the run stops and the keys are discarded. Frames the head still holds stay valid until disposed.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _request = null;
            CloseRunLocked();
            _writer.Stop();
            _tick.Dispose();
        }

        lock (_openLock)
        {
            lock (_probeLock)
            {
                _ciphersDisposed = true;
                _opener.Dispose();
                _sealer.Dispose();
            }
        }
    }

    private static int PortOf(SocketAddress address) => BinaryPrimitives.ReadUInt16BigEndian(address.Buffer.Span[2..]);

    /// <summary>Same family, port and address (the fields every platform's sockaddr keeps at the same offsets).</summary>
    private static bool SameEndpoint(SocketAddress expected, SocketAddress actual)
    {
        if (expected.Family != actual.Family || expected.Size > actual.Size)
        {
            return false;
        }

        ReadOnlySpan<byte> a = expected.Buffer.Span;
        ReadOnlySpan<byte> b = actual.Buffer.Span;
        return expected.Family == AddressFamily.InterNetwork
            ? a.Slice(2, 6).SequenceEqual(b.Slice(2, 6)) // port, IPv4 address
            : a.Slice(2, 2).SequenceEqual(b.Slice(2, 2)) && a.Slice(8, 16).SequenceEqual(b.Slice(8, 16)); // port, IPv6 address
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    /// <summary>
    /// Under <c>_lock</c>, with a request and not in fallback: with a run open, MirrorStart (new limits) goes out now;
    /// otherwise the run opens on the thread pool and MirrorStart follows once it has a port.
    /// </summary>
    private void WantRunLocked()
    {
        if (_socket is not null)
        {
            QueueIntentLocked(start: true);
            return;
        }

        if (!_opening)
        {
            _opening = true;
            ThreadPool.UnsafeQueueUserWorkItem(OpenRunCallback, this, preferLocal: false);
        }
    }

    private bool RunWantedLocked() => !_disposed && _request is not null && !_fallback;

    /// <summary>
    /// Thread pool: opens the run's socket (bind, TOS, the head's hook) and starts its receive thread, both off every
    /// lock. A failure makes mirroring Unavailable (logged once) and never touches the session.
    /// </summary>
    private void OpenRun()
    {
        lock (_lock)
        {
            if (!RunWantedLocked() || _socket is not null)
            {
                _opening = false;
                return;
            }
        }

        Socket? socket = null;
        int port = 0;
        Exception? failure = null;
        try
        {
            socket = new Socket(_server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(_server.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
            VideoSockets.PrepareForReceiveThread(socket);
            SocketQos.Configure(socket, SocketQos.Video, _options.ConfigureVideoSocket, _log, ref _qosWarned);
            port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
#pragma warning disable CA1031 // A video socket that can't open (bind, a broken hook) costs mirroring, never the session.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
            socket?.Dispose();
            socket = null;
        }

        bool published = false;
        bool unavailable = false;
        lock (_lock)
        {
            _opening = false;
            if (RunWantedLocked() && _socket is null)
            {
                if (socket is not null)
                {
                    _socket = socket;
                    _port = port;
                    published = true;
                }
                else
                {
                    unavailable = true;
                }
            }
        }

        if (published)
        {
            try
            {
                MirrorThreads.Start("PenBridge video receive", MirrorThreadRole.VideoReceive, _options.MirrorThreadStarted, _log, () => ReceiveLoop(socket!));
                lock (_lock)
                {
                    if (ReferenceEquals(_socket, socket))
                    {
                        QueueIntentLocked(start: true);
                    }
                }

                return;
            }
#pragma warning disable CA1031 // No receive thread (out of threads): no mirroring, the session goes on.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
                lock (_lock)
                {
                    if (ReferenceEquals(_socket, socket))
                    {
                        Volatile.Write(ref _socket, null);
                        _port = 0;
                        unavailable = RunWantedLocked();
                    }
                }
            }
        }

        if (!published || failure is not null)
        {
            socket?.Dispose(); // never handed to a receive thread
        }

        if (failure is not null && Interlocked.Exchange(ref _openWarned, 1) == 0)
        {
            NetLog.VideoSocketFailed(_log, failure);
        }

        if (unavailable)
        {
            _owner.RaiseMirrorState(MirrorState.Unavailable, MirrorReason.None);
        }
    }

    private void CloseRunLocked()
    {
        StopEpochLocked();
        Volatile.Write(ref _source, null);
        _config = null;
        if (_socket is { } socket)
        {
            Volatile.Write(ref _socket, null);
            _port = 0;
            VideoSockets.Wake(socket); // its receive thread sees the run closed and disposes it
        }
    }

    private void StopEpochLocked()
    {
        _streaming = false;
        FoldEpochLocked();
        _reassembler.Stop();
        _keyframeOut = null;
        _reportOut = null;
        Volatile.Write(ref _armedDue, long.MaxValue);
        if (!_disposed)
        {
            _tick.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Adds the running epoch's drop counters to the totals before the reassembler restarts or stops.</summary>
    private void FoldEpochLocked()
    {
        if (_reassembler.IsActive)
        {
            _droppedBefore += _reassembler.FramesDropped;
            _lostBefore += _reassembler.FragmentsLost;
        }
    }

    private void QueueIntentLocked(bool start)
    {
        _wantStart = start;
        _intentPending = true;
        _ = _writer.Request(WriteStartStop);
    }

    private void ArmLocked(long now)
    {
        if (!_streaming || _disposed)
        {
            return;
        }

        long due = Math.Min(_nextProbe, _nextReport);
        if (_reassembler.NextDeadline is { } deadline)
        {
            due = Math.Min(due, now + Ticks(deadline));
        }

        Volatile.Write(ref _armedDue, due);
        _tick.Change(TimeSpan.FromSeconds(Math.Max(0, due - now) / (double)_time.TimestampFrequency), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Timer: frame timeouts, due KeyframeRequests, the periodic Probe and MirrorReport.</summary>
    private void OnTick()
    {
        byte probeEpoch = 0;
        bool probe = false;
        try
        {
            lock (_lock)
            {
                if (_disposed || !_streaming || _config is not { } config)
                {
                    return;
                }

                long now = _time.GetTimestamp();
                _reassembler.Poll();
                TakeKeyframeRequestLocked();
                if (now >= _nextProbe)
                {
                    probe = true;
                    probeEpoch = config.Epoch;
                    _nextProbe = now + Ticks(ProbeInterval);
                }

                if (now >= _nextReport)
                {
                    // Only a render of this epoch, of a frame this epoch handed out (not a late one of an older epoch).
                    uint delivered = _reassembler.LastCompleteFrame;
                    uint rendered = 0;
                    ulong renderedAt = 0;
                    lock (_renderLock)
                    {
                        if (_renderedEpoch == config.Epoch && _lastRendered != 0 && _lastRendered <= delivered)
                        {
                            rendered = _lastRendered;
                            renderedAt = _lastRenderedAt;
                        }
                    }

                    _reportOut = _reassembler.CreateReport(rendered, renderedAt);
                    _ = _writer.Request(WriteReport);
                    _nextReport = now + Ticks(ReportInterval);
                }

                ArmLocked(now);
            }

            if (probe)
            {
                SendProbe(probeEpoch);
            }
        }
#pragma warning disable CA1031 // Timer thread: an escaping exception would crash the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            NetLog.Unexpected(_log, null, ex);
        }
    }

    private void TakeKeyframeRequestLocked()
    {
        if (_reassembler.TryTakeKeyframeRequest(out KeyframeRequestMessage request))
        {
            _keyframeOut = request;
            _ = _writer.Request(WriteKeyframe);
        }
    }

    /// <summary>Probes sent so far (tests).</summary>
    internal long ProbesSent => Interlocked.Read(ref _probesSent);

    /// <summary>Seals and sends one Probe of <paramref name="epoch"/> (the timer's thread; tests call it directly).</summary>
    internal void SendProbe(byte epoch)
    {
        SocketAddress? target = Volatile.Read(ref _source);
        Socket? socket;
        lock (_lock)
        {
            socket = _socket;
        }

        if (target is null || socket is null)
        {
            return;
        }

        lock (_probeLock)
        {
            if (_ciphersDisposed)
            {
                return;
            }

            int length = VideoFragmenter.WriteProbe(_sealer, epoch, _probe);
            try
            {
                if (_options.VideoDatagramFilter is not { } filter || filter(_probe.AsSpan(0, length)))
                {
                    socket.SendTo(_probe.AsSpan(0, length), SocketFlags.None, target);
                }

                Interlocked.Increment(ref _probesSent); // a filtered Probe counts as sent and lost
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Lossy by design; the next Probe follows in a second.
            }
        }
    }

    /// <summary>Tests only: sends raw bytes from the run's video socket to the Mac's video endpoint.</summary>
    internal void SendRaw(ReadOnlySpan<byte> datagram)
    {
        Socket socket;
        lock (_lock)
        {
            socket = _socket ?? throw new InvalidOperationException("No video run.");
        }

        socket.SendTo(datagram, SocketFlags.None, Volatile.Read(ref _source) ?? throw new InvalidOperationException("No MirrorConfig yet."));
    }

    /// <summary>The run's receive thread: source check, authentication, reassembly, frames to the head.</summary>
    private void ReceiveLoop(Socket socket)
    {
        byte[] buffer = new byte[VideoDatagram.MaxSize + 1]; // one more: an oversized datagram shows as such
        var address = new SocketAddress(socket.AddressFamily);
        try
        {
            while (ReferenceEquals(Volatile.Read(ref _socket), socket))
            {
                int received;
                try
                {
                    received = socket.ReceiveFrom(buffer, SocketFlags.None, address);
                }
                catch (SocketException)
                {
                    continue; // the receive timeout, or an ICMP error reported on the next receive
                }

                if (!ReferenceEquals(Volatile.Read(ref _socket), socket))
                {
                    break; // the run closed (this was the wake datagram, or late video)
                }

                if (Volatile.Read(ref _source) is not { } source || !SameEndpoint(source, address))
                {
                    Interlocked.Increment(ref _foreign);
                    continue;
                }

                Interlocked.Increment(ref _datagrams);
                Process(buffer.AsSpan(0, received));
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            socket.Dispose(); // this thread owns the socket (see VideoSockets)
        }
    }

    /// <summary>One datagram from the Mac's endpoint (receive thread; tests call it directly). Allocation-free.</summary>
    internal void Process(Span<byte> datagram)
    {
        bool ok;
        VideoDatagramHeader header;
        int payloadLength;
        lock (_openLock)
        {
            if (_ciphersDisposed)
            {
                return;
            }

            ok = _opener.TryOpen(datagram, out header, out payloadLength);
        }

        if (!ok)
        {
            if (VideoDatagramHeader.TryRead(datagram, out _))
            {
                Interlocked.Increment(ref _authFailures);
            }
            else
            {
                Interlocked.Increment(ref _malformed);
            }

            return;
        }

        ReassemblyResult result = _reassembler.Accept(header, datagram.Slice(VideoDatagramHeader.Size, payloadLength), out VideoFrame frame);
        if (result == ReassemblyResult.FrameReady)
        {
            Interlocked.Increment(ref _frames);
            _owner.RaiseVideoFrame(frame);
        }

        if (_reassembler.KeyframeNeeded || (_reassembler.NextDeadline is { } d && _time.GetTimestamp() + Ticks(d) < Volatile.Read(ref _armedDue)))
        {
            lock (_lock)
            {
                if (_streaming && !_disposed)
                {
                    TakeKeyframeRequestLocked();
                    long now = _time.GetTimestamp();
                    if (_reassembler.NextDeadline is { } next && now + Ticks(next) < Volatile.Read(ref _armedDue))
                    {
                        ArmLocked(now);
                    }
                }
            }
        }
    }

    /// <summary>The single writer: the latest start-or-stop intent, then the latest KeyframeRequest and MirrorReport.</summary>
    private async Task<bool> DrainAsync(int bits)
    {
        MirrorStartMessage? start = null;
        bool stop = false;
        KeyframeRequestMessage? keyframe;
        MirrorReportMessage? report;
        lock (_lock)
        {
            if (_intentPending)
            {
                _intentPending = false;
                if (_wantStart && _request is { } request && _port != 0)
                {
                    start = request.ToMessage((ushort)_port);
                    _startSent = true;
                }
                else if (!_wantStart && _startSent)
                {
                    stop = true;
                    _startSent = false;
                }
            }

            keyframe = _keyframeOut;
            _keyframeOut = null;
            report = _reportOut;
            _reportOut = null;
        }

        try
        {
            if (start is { } s && !await _channel.TryWriteAsync(s).ConfigureAwait(false))
            {
                return false;
            }

            if (stop && !await _channel.TryWriteAsync(default(MirrorStopMessage)).ConfigureAwait(false))
            {
                return false;
            }

            if (keyframe is { } k)
            {
                if (!await _channel.TryWriteAsync(k).ConfigureAwait(false))
                {
                    return false;
                }

                Interlocked.Increment(ref _keyframesSent);
            }

            if (report is { } r)
            {
                if (!await _channel.TryWriteAsync(r).ConfigureAwait(false))
                {
                    return false;
                }

                Interlocked.Increment(ref _reportsSent);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException or NotSupportedException)
        {
            _channel.Abort();
            return false;
        }
    }
}
