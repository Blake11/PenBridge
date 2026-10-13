using System.Net;
using System.Net.Sockets;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Core.Net;

/// <summary>
/// Server-side input path of one session: endpoint pinning, forged-datagram budget, decryption, payload validation,
/// the <see cref="PenStateTracker"/> and its one-shot watchdog timer.
/// </summary>
/// <remarks>
/// Every tracker/recognizer call (datagrams, TLS InputFrames, the watchdog, surface changes, shutdown) runs under one
/// lock shared by all sessions of a server, so the <see cref="IInputSink"/> is never called concurrently, even while an
/// old session is releasing and a new one starts. The watchdog is a one-shot timer re-armed from
/// <see cref="PenStateTracker.WatchdogDueIn"/> after each frame; with nothing active it stays disarmed (no polling).
/// The datagram path is allocation-free once the source endpoint is pinned.
/// </remarks>
internal sealed class SessionInput : IDisposable
{
    private static readonly TimerCallback WatchdogCallback = static state => ((SessionInput)state!).OnWatchdog();

    /// <summary>Re-arm delay when the tracker reports "due now" but did not fire (clock granularity).</summary>
    private static readonly TimeSpan MinWatchdogDelay = TimeSpan.FromMilliseconds(1);

    private readonly Lock _lock;
    private readonly TimeProvider _time;
    private readonly PenStateTracker _tracker;
    private readonly InputCipher _cipher;
    private readonly ITimer _watchdog;
    private readonly IPAddress _peerAddress;
    private readonly Action<ulong>? _onFirstDatagram;
    private readonly double _budgetCapacity;
    private readonly double _budgetPerSecond;

    private SocketAddress? _pinned;
    private double _budget;
    private long _budgetTimestamp;
    private bool _budgetExhaustedReported;
    private bool _closed;
    private bool _tlsMode;
    private bool _firstDatagramSeen;
    private DisplayRect _display;
    private NormalizedRect _region;
    private ushort _surfaceWidth;
    private ushort _surfaceHeight;

    private long _accepted;
    private long _stale;
    private long _authFailures;
    private long _malformed;
    private long _invalid;
    private long _foreignSource;
    private long _rateLimited;

    public SessionInput(
        Lock inputLock,
        IInputSink sink,
        TimeProvider time,
        PenStateTrackerOptions? trackerOptions,
        ReadOnlySpan<byte> inputKey,
        uint sessionId,
        IPAddress peerAddress,
        DisplayRect display,
        NormalizedRect region,
        int maxAuthFailuresPerSecond,
        Action<ulong>? onFirstDatagram)
    {
        _lock = inputLock;
        _time = time;
        _tracker = new PenStateTracker(sink, time, trackerOptions);
        _cipher = new InputCipher(inputKey, sessionId);
        _peerAddress = Normalize(peerAddress);
        _display = display;
        _region = region;
        _onFirstDatagram = onFirstDatagram;
        _budgetPerSecond = Math.Max(1, maxAuthFailuresPerSecond);
        _budgetCapacity = _budgetPerSecond * 2;
        _budget = _budgetCapacity;
        _budgetTimestamp = time.GetTimestamp();
        _watchdog = time.CreateTimer(WatchdogCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised (under the input lock) the first time the forged-datagram budget runs out.</summary>
    public event Action? ForgedDatagramFlood;

    public uint SessionId => _cipher.SessionId;

    public InputTransport Transport
    {
        get
        {
            lock (_lock)
            {
                return _tlsMode ? InputTransport.Tls : _firstDatagramSeen ? InputTransport.Udp : InputTransport.None;
            }
        }
    }

    public InputCounters Counters
    {
        get
        {
            lock (_lock)
            {
                return new InputCounters(_accepted, _stale, _authFailures, _malformed, _invalid, _foreignSource, _rateLimited);
            }
        }
    }

    /// <summary>True once SurfaceInfo configured the mapper (frames are dropped before).</summary>
    public bool IsSurfaceConfigured
    {
        get
        {
            lock (_lock)
            {
                return _surfaceWidth > 0;
            }
        }
    }

    /// <summary>Accepted frames so far (for packets/s).</summary>
    public long Accepted
    {
        get
        {
            lock (_lock)
            {
                return _accepted;
            }
        }
    }

    /// <summary>Tracker access for tests (call only under the input lock).</summary>
    internal PenStateTracker Tracker => _tracker;

    /// <summary>Applies SurfaceInfo: (re)configures the mapper. Frames are dropped until this is called.</summary>
    public void ConfigureSurface(ushort widthPx, ushort heightPx)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _surfaceWidth = widthPx;
            _surfaceHeight = heightPx;
            ApplyMapperLocked();
        }
    }

    /// <summary>Changes the target display region.</summary>
    public void SetTarget(DisplayRect display, NormalizedRect region)
    {
        lock (_lock)
        {
            _display = display;
            _region = region;
            if (!_closed && _surfaceWidth > 0)
            {
                ApplyMapperLocked();
            }
        }
    }

    /// <summary>
    /// Handles one received UDP datagram in place (it is decrypted in the buffer). Allocation-free once the peer's
    /// source endpoint is pinned. Never throws for bad input.
    /// </summary>
    public void ProcessDatagram(Span<byte> datagram, SocketAddress source)
    {
        bool first = false;
        ulong highest = 0;
        lock (_lock)
        {
            if (_closed || _tlsMode)
            {
                return;
            }

            if (_pinned is not null)
            {
                if (!_pinned.Equals(source))
                {
                    _foreignSource++;
                    return;
                }
            }
            else if (!IsPeerAddress(source))
            {
                _foreignSource++;
                return;
            }

            if (!InputDatagram.TrySplit(datagram, out InputDatagramHeader header, out _, out _, out _)
                || header.SessionId != _cipher.SessionId)
            {
                _malformed++;
                return;
            }

            if (header.Sequence <= _cipher.HighestSequence)
            {
                _stale++;
                return;
            }

            if (!TryTakeBudget())
            {
                _rateLimited++;
                return;
            }

            if (!_cipher.TryOpen(datagram, out int payloadLength))
            {
                _authFailures++;
                return;
            }

            _budget += 1; // Refund: only failed decryptions consume the budget.
            if (_pinned is null)
            {
                _pinned = Copy(source);
            }

            if (!_firstDatagramSeen)
            {
                _firstDatagramSeen = true;
                first = true;
                highest = _cipher.HighestSequence;
            }

            HandlePayloadLocked(datagram.Slice(InputDatagramHeader.Size, payloadLength));
        }

        if (first)
        {
            _onFirstDatagram?.Invoke(highest);
        }
    }

    /// <summary>Handles a TLS-fallback InputFrame payload. From now on UDP datagrams for this session are ignored.</summary>
    public void ProcessInputFrame(ReadOnlySpan<byte> payload)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _tlsMode = true;
            HandlePayloadLocked(payload);
        }
    }

    /// <summary>Ends the session input: synthesizes the full release, stops the watchdog and drops the key.</summary>
    public void Shutdown()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_tracker.IsMapperConfigured)
            {
                _tracker.Reset();
            }

            _cipher.Dispose();
        }

        _watchdog.Dispose();
    }

    public void Dispose() => Shutdown();

    /// <summary>
    /// Rejects frames that are well-formed but semantically wrong: an in-range pen without a known tool, history
    /// without a current pen, or history samples that are not in range.
    /// </summary>
    internal static bool IsValid(in InputPayload payload)
    {
        PenSample? pen = payload.Pen;
        if (pen.HasValue)
        {
            PenSample p = pen.GetValueOrDefault();
            if (IsInRange(p) && !IsKnownTool(p.Tool))
            {
                return false;
            }
        }
        else if (payload.History.Length > 0)
        {
            return false;
        }

        foreach (ref readonly PenSample h in payload.History)
        {
            if (!IsInRange(h) || !IsKnownTool(h.Tool))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsInRange(in PenSample s) => (s.State & (PenState.InRange | PenState.Contact)) != 0;

    private static bool IsKnownTool(PenTool tool) => tool is PenTool.Pen or PenTool.Eraser or PenTool.FingerFallback;

    private void HandlePayloadLocked(ReadOnlySpan<byte> payload)
    {
        if (!InputPayload.TryRead(payload, out InputPayload frame))
        {
            _malformed++;
            return;
        }

        if (!IsValid(frame))
        {
            _invalid++;
            return;
        }

        PenSample? pen = frame.Pen;
        _tracker.ProcessFrame(frame.TimeUs, pen, frame.History, frame.Touches);
        _accepted++;
        RearmLocked();
    }

    private void ApplyMapperLocked()
    {
        _tracker.Mapper = new CoordinateMapper(_surfaceWidth, _surfaceHeight, _display, _region);
    }

    private void RearmLocked()
    {
        TimeSpan? due = _tracker.WatchdogDueIn;
        TimeSpan delay = due is { } d ? (d > TimeSpan.Zero ? d : MinWatchdogDelay) : Timeout.InfiniteTimeSpan;
        _watchdog.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnWatchdog()
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _tracker.CheckWatchdog();
            RearmLocked();
        }
    }

    private bool TryTakeBudget()
    {
        long now = _time.GetTimestamp();
        double elapsed = _time.GetElapsedTime(_budgetTimestamp, now).TotalSeconds;
        _budgetTimestamp = now;
        if (elapsed > 0)
        {
            _budget = Math.Min(_budgetCapacity, _budget + (elapsed * _budgetPerSecond));
        }

        if (_budget >= 1)
        {
            _budget -= 1;
            if (_budget >= _budgetPerSecond)
            {
                _budgetExhaustedReported = false;
            }

            return true;
        }

        if (!_budgetExhaustedReported)
        {
            _budgetExhaustedReported = true;
            ForgedDatagramFlood?.Invoke();
        }

        return false;
    }

    private bool IsPeerAddress(SocketAddress source)
    {
        // Runs only until the first authenticated datagram pins the endpoint; allocating here is fine.
        try
        {
            IPEndPoint template = source.Family == AddressFamily.InterNetworkV6
                ? new IPEndPoint(IPAddress.IPv6Any, 0)
                : new IPEndPoint(IPAddress.Any, 0);
            var endPoint = (IPEndPoint)template.Create(source);
            return Normalize(endPoint.Address).Equals(_peerAddress);
        }
        catch (Exception ex) when (ex is ArgumentException or SocketException or InvalidCastException)
        {
            return false;
        }
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static SocketAddress Copy(SocketAddress source)
    {
        var copy = new SocketAddress(source.Family, source.Size);
        source.Buffer.Span[..source.Size].CopyTo(copy.Buffer.Span);
        return copy;
    }
}
