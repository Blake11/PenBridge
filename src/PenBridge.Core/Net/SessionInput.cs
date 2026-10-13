using System.Net;
using System.Net.Sockets;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Core.Net;

/// <summary>
/// Server-side input path of one session: forged-datagram budget, decryption, endpoint pinning, payload validation,
/// the <see cref="PenStateTracker"/> and its one-shot watchdog timer.
/// </summary>
/// <remarks>
/// <para>Every tracker/recognizer call (datagrams, TLS InputFrames, the watchdog, surface changes, shutdown) runs under
/// one lock shared by all sessions of a server, so the <see cref="IInputSink"/> is never called concurrently, even while
/// an old session is releasing and a new one starts. The watchdog is a one-shot timer re-armed from
/// <see cref="PenStateTracker.WatchdogDueIn"/> after each frame; with nothing active it stays disarmed (no polling).</para>
/// <para>Endpoint: the session's UDP source is pinned by the first authenticated datagram and re-pinned only by a
/// datagram from a new source that authenticates (AEAD) <i>and</i> carries a sequence above the highest seen, so
/// replays and forgeries can't move it, while a phone that roams (new IP or NAT port) keeps working.</para>
/// <para>Accepted DoS limitation: any source can make the server try one AEAD decryption per datagram with a
/// plausible header, bounded by a token bucket shared by the whole session. A LAN attacker who floods forged datagrams
/// exhausts that budget and so also delays the genuine phone's datagrams; it cannot inject input.</para>
/// <para>Frames from both transports are ordered by their <c>timeUs</c>: a frame older than the last accepted one is
/// dropped as stale, so a late TLS fallback frame can't overtake UDP input after the client switched back.</para>
/// <para>A sink or tracker exception faults the session (reported once through the fault callback) instead of escaping
/// into the UDP loop or a timer thread. Allocation-free per datagram except when the endpoint changes.</para>
/// </remarks>
internal sealed class SessionInput : IDisposable
{
    private static readonly TimerCallback WatchdogCallback = static state => ((SessionInput)state!).OnWatchdog();

    /// <summary>Re-arm delay when the tracker reports "due now" but did not fire (clock granularity).</summary>
    private static readonly TimeSpan MinWatchdogDelay = TimeSpan.FromMilliseconds(1);

    /// <summary>UdpAck is re-sent at most this often while datagrams arrive.</summary>
    private static readonly TimeSpan AckInterval = TimeSpan.FromSeconds(1);

    private readonly Lock _lock;
    private readonly TimeProvider _time;
    private readonly PenStateTracker _tracker;
    private readonly InputCipher _cipher;
    private readonly ITimer _watchdog;
    private readonly Action<ulong>? _onAck;
    private readonly Action<Exception>? _onFault;
    private readonly double _budgetCapacity;
    private readonly double _budgetPerSecond;

    private SocketAddress? _pinned;
    private double _budget;
    private long _budgetTimestamp;
    private bool _budgetExhaustedReported;
    private bool _closed;
    private bool _disposed;
    private bool _faulted;
    private InputTransport _transport;
    private bool _acked;
    private long _lastAckTimestamp;
    private bool _hasTime;
    private uint _lastTimeUs;
    private DisplayRect _display;
    private NormalizedRect _region;
    private ushort _surfaceWidth;
    private ushort _surfaceHeight;

    private long _accepted;
    private long _stale;
    private long _authFailures;
    private long _malformed;
    private long _invalid;
    private long _endpointChanges;
    private long _rateLimited;

    public SessionInput(
        Lock inputLock,
        IInputSink sink,
        TimeProvider time,
        PenStateTrackerOptions? trackerOptions,
        ReadOnlySpan<byte> inputKey,
        uint sessionId,
        DisplayRect display,
        NormalizedRect region,
        int maxAuthFailuresPerSecond,
        Action<ulong>? onAck,
        Action<Exception>? onFault)
    {
        _lock = inputLock;
        _time = time;
        _tracker = new PenStateTracker(sink, time, trackerOptions);
        _cipher = new InputCipher(inputKey, sessionId);
        _display = display;
        _region = region;
        _onAck = onAck;
        _onFault = onFault;
        _budgetPerSecond = Math.Max(1, maxAuthFailuresPerSecond);
        _budgetCapacity = _budgetPerSecond * 2;
        _budget = _budgetCapacity;
        _budgetTimestamp = time.GetTimestamp();
        _watchdog = time.CreateTimer(WatchdogCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised (under the input lock) the first time the forged-datagram budget runs out.</summary>
    public event Action? ForgedDatagramFlood;

    public uint SessionId => _cipher.SessionId;

    /// <summary>The transport of the last accepted frame.</summary>
    public InputTransport Transport
    {
        get
        {
            lock (_lock)
            {
                return _transport;
            }
        }
    }

    public InputCounters Counters
    {
        get
        {
            lock (_lock)
            {
                return new InputCounters(_accepted, _stale, _authFailures, _malformed, _invalid, _endpointChanges, _rateLimited);
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
    /// Handles one received UDP datagram in place (it is decrypted in the buffer). Allocation-free unless the source
    /// endpoint changes. Never throws.
    /// </summary>
    public void ProcessDatagram(Span<byte> datagram, SocketAddress source)
    {
        bool ack = false;
        ulong highest = 0;
        Exception? fault = null;
        lock (_lock)
        {
            if (_closed)
            {
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

            // Authenticated and above the highest sequence (TryOpen guarantees both): safe to (re)pin.
            if (_pinned is null || !_pinned.Equals(source))
            {
                if (_pinned is not null)
                {
                    _endpointChanges++;
                }

                _pinned = Copy(source);
            }

            long now = _time.GetTimestamp();
            if (!_acked || _time.GetElapsedTime(_lastAckTimestamp, now) >= AckInterval)
            {
                _acked = true;
                _lastAckTimestamp = now;
                ack = true;
                highest = _cipher.HighestSequence;
            }

            fault = HandlePayloadLocked(datagram.Slice(InputDatagramHeader.Size, payloadLength), InputTransport.Udp);
        }

        if (ack)
        {
            _onAck?.Invoke(highest);
        }

        Report(fault);
    }

    /// <summary>Handles a TLS-fallback InputFrame payload.</summary>
    public void ProcessInputFrame(ReadOnlySpan<byte> payload)
    {
        Exception? fault;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            fault = HandlePayloadLocked(payload, InputTransport.Tls);
        }

        Report(fault);
    }

    /// <summary>Ends the session input: synthesizes the full release (unless faulted), stops the watchdog, drops the key.</summary>
    public void Shutdown()
    {
        Exception? fault = null;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            bool release = !_closed && _tracker.IsMapperConfigured;
            _closed = true;
            _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (release)
            {
                try
                {
                    _tracker.Reset();
                }
#pragma warning disable CA1031 // A faulty sink must not stop the session teardown.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    fault = FaultLocked(ex);
                }
            }

            _cipher.Dispose();
        }

        _watchdog.Dispose();
        Report(fault);
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

    private Exception? HandlePayloadLocked(ReadOnlySpan<byte> payload, InputTransport transport)
    {
        if (!InputPayload.TryRead(payload, out InputPayload frame))
        {
            _malformed++;
            return null;
        }

        if (!IsValid(frame))
        {
            _invalid++;
            return null;
        }

        // Cross-transport ordering: never apply a frame older than the last one (timeUs wraps; compare as a delta).
        if (_hasTime && unchecked((int)(frame.TimeUs - _lastTimeUs)) < 0)
        {
            _stale++;
            return null;
        }

        _hasTime = true;
        _lastTimeUs = frame.TimeUs;
        _transport = transport;
        PenSample? pen = frame.Pen;
        try
        {
            _tracker.ProcessFrame(frame.TimeUs, pen, frame.History, frame.Touches);
        }
#pragma warning disable CA1031 // Sink/tracker failures fault the session; they must not escape into the UDP loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return FaultLocked(ex);
        }

        _accepted++;
        RearmLocked();
        return null;
    }

    private Exception? FaultLocked(Exception ex)
    {
        _closed = true;
        _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_faulted)
        {
            return null;
        }

        _faulted = true;
        return ex;
    }

    private void Report(Exception? fault)
    {
        if (fault is not null)
        {
            _onFault?.Invoke(fault);
        }
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
        Exception? fault = null;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            try
            {
                _tracker.CheckWatchdog();
                RearmLocked();
            }
#pragma warning disable CA1031 // Never let a sink failure escape onto a timer thread.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                fault = FaultLocked(ex);
            }
        }

        Report(fault);
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

    private static SocketAddress Copy(SocketAddress source)
    {
        var copy = new SocketAddress(source.Family, source.Size);
        source.Buffer.Span[..source.Size].CopyTo(copy.Buffer.Span);
        return copy;
    }
}
