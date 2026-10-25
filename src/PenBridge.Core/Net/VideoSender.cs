using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Mirror;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Net;

/// <summary>
/// One mirroring run of a server session: the video socket (AF41), the send thread that fragments, seals and paces
/// access units (<see cref="VideoPacer"/>), and the probe thread that receives the phone's Probes.
/// </summary>
/// <remarks>
/// <para><b>Queue.</b> <see cref="Enqueue"/> copies an access unit into a pooled buffer and queues it, or drops it.
/// Numbering (<c>docs/specs/screen-mirroring.md</c> "Video datagram"): access units before the epoch's first keyframe
/// belong to the previous configuration and are discarded unnumbered; that keyframe is frame 1, and every later access
/// unit takes the next number, also one dropped unsent, so the phone sees the gap and waits for a keyframe. In steady
/// state one frame may wait behind the one on the wire (a keyframe takes up to one interval, so the next frame arrives
/// as it finishes); a frame that finds another one still waiting is dropped.</para>
/// <para><b>Epoch start.</b> Frame 1 and its followers (up to <see cref="QueueCapacity"/>) are held until the epoch is
/// released: by an authenticated Probe of the epoch or by the <see cref="VideoDatagram.EpochProbeWait"/> timer, and
/// only once an endpoint is pinned (before the first Probe nothing is sent). The backlog then goes out paced, each frame
/// within its window, so it drains faster than frames arrive; new frames queue behind it until it is gone.</para>
/// <para><b>Pacing</b> uses the real clock (<see cref="TimeProvider.System"/>): it is about wire timing, not protocol
/// deadlines. A wait shorter than 1 ms is skipped (the pacer catches up within its slack).</para>
/// <para>Allocation-free per frame and per fragment once the pool is warm; the endpoint copy on a re-pin allocates.</para>
/// </remarks>
internal sealed class VideoSender : IDisposable
{
    /// <summary>Most frames queued (held at an epoch start, or the one on the wire and one waiting).</summary>
    public const int QueueCapacity = 8;

    private const int CaptureRing = 256;
    private static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(1);

    private readonly ServerMirror _owner;
    private readonly DatagramFilter? _filter;
    private readonly TimeProvider _time;
    private readonly Socket _socket;
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create(VideoDatagram.MaxFrameLength, QueueCapacity + 2);
    private readonly Lock _lock = new();
    private readonly ManualResetEventSlim _wake = new(false, 0);
    private readonly VideoPacer _pacer = new(TimeProvider.System);
    private readonly QueuedFrame[] _queue = new QueuedFrame[QueueCapacity];
    private readonly byte[] _captureEpoch = new byte[CaptureRing];
    private readonly uint[] _captureFrame = new uint[CaptureRing];
    private readonly long[] _captureTimestamp = new long[CaptureRing];
    private readonly double _timestampsPerUs;

    // Guarded by _lock.
    private int _head;
    private int _count;
    private bool _sending;
    private bool _accepting;
    private byte _epoch;
    private int _generation;
    private bool _epochStarted;
    private uint _lastNumber;
    private bool _released;
    private bool _backlog;
    private int _bitrateKbps;
    private int _fps;
    private bool _pacerDirty;

    private SocketAddress? _target; // pinned endpoint; written under _lock, read by the send thread
    private volatile bool _stopped;
    private long _framesSent;
    private long _framesDropped;
    private long _fragmentsSent;
    private long _fragmentsInEpoch;
    private long _endpointChanges;

    public VideoSender(ServerMirror owner, PenBridgeServerOptions options, ILogger log, ref int qosWarned)
    {
        _owner = owner;
        _filter = options.VideoDatagramFilter;
        _time = options.TimeProvider;
        _timestampsPerUs = _time.TimestampFrequency / 1e6;
        IPAddress bind = options.BindAddress;
        _socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (bind.AddressFamily == AddressFamily.InterNetworkV6 && bind.Equals(IPAddress.IPv6Any))
            {
                _socket.DualMode = true;
            }

            _socket.Bind(new IPEndPoint(bind, 0));
            VideoSockets.PrepareForReceiveThread(_socket);
            SocketQos.Configure(_socket, SocketQos.Video, options.ConfigureVideoSocket, log, ref qosWarned);
        }
        catch
        {
            _socket.Dispose();
            throw;
        }

        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        MirrorThreads.Start("PenBridge video send", MirrorThreadRole.VideoSend, options.MirrorThreadStarted, log, SendLoop);
        MirrorThreads.Start("PenBridge video probes", MirrorThreadRole.VideoReceive, options.MirrorThreadStarted, log, ReceiveLoop);
    }

    /// <summary>The video socket's port (MirrorConfig <c>videoUdpPort</c>).</summary>
    public int Port { get; }

    public long FramesSent => Interlocked.Read(ref _framesSent);

    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    public long FragmentsSent => Interlocked.Read(ref _fragmentsSent);

    /// <summary>Fragments sent in the current epoch (the guard's loss base).</summary>
    public long FragmentsInEpoch => Interlocked.Read(ref _fragmentsInEpoch);

    public long EndpointChanges => Interlocked.Read(ref _endpointChanges);

    /// <summary>True once the run stopped (tests).</summary>
    internal bool IsStopped => _stopped;

    /// <summary>The pinned endpoint, or null before the first authenticated Probe (tests).</summary>
    internal SocketAddress? Target => Volatile.Read(ref _target);

    /// <summary>True once the current epoch may go out (its Probe or the wait passed) (tests).</summary>
    internal bool IsReleased
    {
        get
        {
            lock (_lock)
            {
                return _released;
            }
        }
    }

    /// <summary>Starts an epoch: drops queued frames of the previous one and waits for the epoch's first keyframe.</summary>
    public void BeginEpoch(MirrorStreamConfig config)
    {
        lock (_lock)
        {
            _epoch = config.Epoch;
            _generation++;
            _accepting = true;
            _epochStarted = false;
            _lastNumber = 0;
            _released = false;
            _backlog = false;
            _bitrateKbps = config.BitrateKbps;
            _fps = config.Fps;
            _pacerDirty = true;
            Interlocked.Exchange(ref _fragmentsInEpoch, 0);
            DropQueuedLocked();
        }

        _wake.Set();
    }

    /// <summary>No video until the next epoch (paused, throttled, head not streaming): queued frames are dropped.</summary>
    public void Deactivate()
    {
        lock (_lock)
        {
            _accepting = false;
            _generation++;
            DropQueuedLocked();
        }

        _wake.Set();
    }

    /// <summary>The epoch may go out (its Probe arrived, or the wait passed); sending still needs a pinned endpoint.</summary>
    public void Release(byte epoch)
    {
        lock (_lock)
        {
            if (!_accepting || epoch != _epoch || _released)
            {
                return;
            }

            _released = true;
            _backlog = _count > 1;
        }

        _wake.Set();
    }

    /// <summary>Pins (or re-pins) the endpoint video goes to: the source of an authenticated, newer Probe.</summary>
    public void Pin(SocketAddress source)
    {
        lock (_lock)
        {
            if (_target is { } current && current.Equals(source))
            {
                return;
            }

            bool moved = _target is not null;
            var copy = new SocketAddress(source.Family, source.Size);
            source.Buffer.Span[..source.Size].CopyTo(copy.Buffer.Span);
            Volatile.Write(ref _target, copy);
            if (moved)
            {
                Interlocked.Increment(ref _endpointChanges); // after the write: a reader that sees the count sees the endpoint
            }
        }

        _wake.Set();
    }

    /// <summary>
    /// Queues an access unit or drops it (see the class remarks). Thread-safe and non-blocking; allocation-free once the
    /// pool is warm. False when it was dropped or discarded.
    /// </summary>
    public bool Enqueue(ReadOnlySpan<byte> accessUnit, bool keyframe, long captureTimeUs)
    {
        if (_stopped || accessUnit.IsEmpty || accessUnit.Length > VideoDatagram.MaxFrameLength || !Volatile.Read(ref _accepting))
        {
            return false;
        }

        byte[] buffer = _pool.Rent(accessUnit.Length);
        accessUnit.CopyTo(buffer);
        bool queued = false;
        lock (_lock)
        {
            if (_accepting && !_stopped && (_epochStarted || keyframe))
            {
                uint number = _epochStarted ? unchecked(_lastNumber + 1) : VideoDatagram.FirstFrameNumber;
                _epochStarted = true;
                _lastNumber = number;
                int slot = (int)(number % CaptureRing);
                _captureEpoch[slot] = _epoch;
                _captureFrame[slot] = number;
                _captureTimestamp[slot] = (long)(captureTimeUs * _timestampsPerUs);
                int waiting = _count - (_sending ? 1 : 0);
                if (_count < QueueCapacity && (waiting == 0 || !_released || _backlog))
                {
                    _queue[(_head + _count) % QueueCapacity] = new QueuedFrame(
                        buffer, accessUnit.Length, keyframe, _epoch, number, _owner.ToSessionUs(captureTimeUs), _generation);
                    _count++;
                    queued = true;
                }
                else
                {
                    Interlocked.Increment(ref _framesDropped);
                }
            }
        }

        if (!queued)
        {
            _pool.Return(buffer);
            return false;
        }

        _wake.Set();
        return true;
    }

    /// <summary>The capture time (server timestamp) of a frame of <paramref name="epoch"/> still in the recent ring.</summary>
    public bool TryGetCaptureTimestamp(byte epoch, uint frameNumber, out long timestamp)
    {
        lock (_lock)
        {
            int slot = (int)(frameNumber % CaptureRing);
            bool found = _captureEpoch[slot] == epoch && _captureFrame[slot] == frameNumber;
            timestamp = found ? _captureTimestamp[slot] : 0;
            return found;
        }
    }

    /// <summary>Stops the run: no more sends, queued frames are dropped, the probe thread wakes, closes the socket and ends.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _accepting = false;
            _generation++;
            DropQueuedLocked();
        }

        _wake.Set();
        VideoSockets.Wake(_socket);
    }

    private void DropQueuedLocked()
    {
        // The frame on the wire stays at the head: the send thread aborts it and returns its buffer.
        int keep = _sending ? 1 : 0;
        for (int i = keep; i < _count; i++)
        {
            int index = (_head + i) % QueueCapacity;
            _pool.Return(_queue[index].Buffer);
            _queue[index] = default;
        }

        _count = keep;
        if (keep == 0)
        {
            _head = 0;
        }
    }

    private void DequeueHeadLocked()
    {
        _pool.Return(_queue[_head].Buffer);
        _queue[_head] = default;
        _head = (_head + 1) % QueueCapacity;
        _count--;
    }

    private void SendLoop()
    {
        byte[] datagram = new byte[VideoDatagram.MaxSize];
        while (true)
        {
            QueuedFrame frame;
            bool configure;
            int bitrate;
            int fps;
            lock (_lock)
            {
                _wake.Reset();
                if (_stopped)
                {
                    while (_count > 0)
                    {
                        DequeueHeadLocked();
                    }

                    return;
                }

                if (_count == 0)
                {
                    _backlog = false;
                    frame = default;
                }
                else
                {
                    frame = _queue[_head];
                    if (frame.Generation != _generation)
                    {
                        DequeueHeadLocked();
                        continue;
                    }

                    if (!_released || Volatile.Read(ref _target) is null)
                    {
                        frame = default; // hold the epoch's start until it is released and an endpoint is pinned
                    }
                }

                configure = _pacerDirty;
                _pacerDirty = false;
                bitrate = _bitrateKbps;
                fps = _fps;
                _sending = frame.Buffer is not null;
            }

            if (configure && bitrate > 0 && fps > 0)
            {
                _pacer.Configure(bitrate, fps);
            }

            if (frame.Buffer is null)
            {
                _wake.Wait();
                continue;
            }

            bool complete = SendFrame(frame, datagram);
            lock (_lock)
            {
                _sending = false;
                if (_count > 0 && ReferenceEquals(_queue[_head].Buffer, frame.Buffer))
                {
                    DequeueHeadLocked();
                }
            }

            if (complete)
            {
                Interlocked.Increment(ref _framesSent);
            }
        }
    }

    /// <summary>Sends one frame's fragments, paced; false when the epoch changed, the run stopped or the keys are gone.</summary>
    private bool SendFrame(in QueuedFrame frame, byte[] datagram)
    {
        ReadOnlySpan<byte> accessUnit = frame.Buffer.AsSpan(0, frame.Length);
        int count = VideoFragmenter.GetFragmentCount(frame.Length);
        int total = (count * (VideoDatagramHeader.Size + VideoDatagram.TagSize)) + VideoDatagram.FirstFragmentPrefixSize + frame.Length;
        _pacer.BeginFrame(total, frame.Keyframe);
        var info = new VideoFrameInfo(frame.Epoch, frame.Number, frame.Keyframe, frame.CaptureUs);
        for (int i = 0; i < count; i++)
        {
            while (true)
            {
                if (Interrupted(frame.Generation))
                {
                    return false;
                }

                TimeSpan delay = _pacer.GetDelay();
                if (delay < MinWait)
                {
                    break;
                }

                // A frame enqueued meanwhile may wake this early; the loop just waits again.
                _wake.Reset();
                if (Interrupted(frame.Generation))
                {
                    return false;
                }

                _wake.Wait(delay);
            }

            int length = _owner.SealFragment(accessUnit, info, i, datagram);
            if (length == 0 || Volatile.Read(ref _target) is not { } target)
            {
                return false;
            }

            try
            {
                if (_filter is null || _filter(datagram.AsSpan(0, length)))
                {
                    _socket.SendTo(datagram.AsSpan(0, length), SocketFlags.None, target);
                }
            }
            catch (SocketException)
            {
                // Lossy by design (a full send buffer, a route change): the phone asks for a keyframe.
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            _pacer.OnSent(length);
            Interlocked.Increment(ref _fragmentsSent);
            Interlocked.Increment(ref _fragmentsInEpoch);
        }

        return true;
    }

    private bool Interrupted(int generation) => _stopped || Volatile.Read(ref _generation) != generation;

    private void ReceiveLoop()
    {
        byte[] buffer = new byte[VideoDatagram.MaxSize + 1]; // one more: an oversized datagram shows as such
        var address = new SocketAddress(_socket.AddressFamily);
        try
        {
            while (!_stopped)
            {
                int received;
                try
                {
                    received = _socket.ReceiveFrom(buffer, SocketFlags.None, address);
                }
                catch (SocketException)
                {
                    continue; // the receive timeout, or an ICMP error reported on the next receive
                }

                if (!_stopped)
                {
                    _owner.OnVideoDatagram(buffer.AsSpan(0, received), address, this);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _socket.Dispose(); // this thread owns the socket (see VideoSockets)
        }
    }

    private readonly record struct QueuedFrame(byte[] Buffer, int Length, bool Keyframe, byte Epoch, uint Number, uint CaptureUs, int Generation);
}
