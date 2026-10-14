using System.Net.Security;
using System.Net.Sockets;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Net;

/// <summary>
/// One TLS control connection: the socket, the <see cref="SslStream"/>, a <see cref="ControlFrameReader"/>, a
/// write lock, and deadlines. Every read has a deadline; extra deadlines (handshake, pairing) can be started with
/// <see cref="StartDeadline"/>. Any deadline cancels <see cref="Token"/> and the channel is then dead: a cancelled
/// or failed read desynchronizes the stream, so the owner must dispose it and never read again.
/// </summary>
internal sealed class ControlChannel : IAsyncDisposable
{
    /// <summary>Wall-clock bound for best-effort farewell writes (Error/Bye) on a channel that is closing.</summary>
    private static readonly TimeSpan FarewellTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Wall-clock bound for waiting, after a farewell and a half-close, for the peer to close its side. Real time like
    /// <see cref="FarewellTimeout"/>: a peer that never closes must not hold up a takeover or shutdown.
    /// </summary>
    public static readonly TimeSpan LingerTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Backstop for any synchronous socket send: all control writes are async and cancellable, so this only bounds a
    /// write that bypasses them.
    /// </summary>
    public static readonly TimeSpan SyncSendTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimerCallback DeadlineCallback = static state => ((ControlChannel)state!).OnDeadline();

    // A stuck write may not observe cancellation (the bytes are already in the socket layer): close the socket too.
    private static readonly TimerCallback WriteDeadlineCallback = static state => ((ControlChannel)state!).OnWriteDeadline();

    private readonly Socket _socket;
    private readonly SslStream _ssl;
    private readonly TimeProvider _time;
    private readonly ControlFrameReader _reader;
    private readonly CancellationTokenSource _cts = new();
    private readonly ITimer _readDeadline;
    private readonly ITimer _writeDeadline;
    private long _writeStarted;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _timedOut;
    private int _disposed;

    public ControlChannel(Socket socket, TimeProvider time)
    {
        _socket = socket;
        _time = time;
        _ssl = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
        _reader = new ControlFrameReader(_ssl);
        _readDeadline = time.CreateTimer(DeadlineCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _writeDeadline = time.CreateTimer(WriteDeadlineCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Deadline for each control write (writes are serialized, so one timer covers them). A peer that stops reading
    /// but keeps pinging can't wedge a writer: the deadline kills the connection, which the owner reports as a timeout.
    /// </summary>
    public static TimeSpan WriteTimeout { get; } = TimeSpan.FromSeconds(2);

    public SslStream Ssl => _ssl;

    public Socket Socket => _socket;

    /// <summary>Cancelled when a deadline passes or the channel is aborted.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>True when a deadline (rather than <see cref="Abort"/>) cancelled the channel.</summary>
    public bool TimedOut => Volatile.Read(ref _timedOut) != 0;

    public bool IsAborted => _cts.IsCancellationRequested;

    /// <summary>Wall-clock age of the write in progress, or null (tests: detect a really stalled write).</summary>
    internal TimeSpan? WriteInFlightFor
    {
        get
        {
            long started = Volatile.Read(ref _writeStarted);
            return started == 0 ? null : System.Diagnostics.Stopwatch.GetElapsedTime(started);
        }
    }

    /// <summary>Starts a one-shot deadline; dispose the returned timer to cancel it.</summary>
    public ITimer StartDeadline(TimeSpan due) => _time.CreateTimer(DeadlineCallback, this, due, Timeout.InfiniteTimeSpan);

    /// <summary>Reads the next frame within <paramref name="timeout"/>; null on a clean end of stream.</summary>
    public async ValueTask<ReceivedControlFrame?> ReadAsync(TimeSpan timeout)
    {
        _readDeadline.Change(timeout, Timeout.InfiniteTimeSpan);
        try
        {
            return await _reader.ReadAsync(_cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _readDeadline.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Reads the next frame; throws <see cref="IOException"/> on end of stream.</summary>
    public async ValueTask<ReceivedControlFrame> ReadRequiredAsync(TimeSpan timeout) =>
        await ReadAsync(timeout).ConfigureAwait(false) ?? throw new IOException("The peer closed the control connection.");

    /// <summary>Writes one message (serialized with other writes).</summary>
    public async ValueTask WriteAsync<T>(T message)
        where T : IControlMessage<T>
    {
        await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        _writeDeadline.Change(WriteTimeout, Timeout.InfiniteTimeSpan);
        try
        {
            Volatile.Write(ref _writeStarted, System.Diagnostics.Stopwatch.GetTimestamp());
            await ControlFrameWriter.WriteAsync(_ssl, message, _cts.Token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _writeStarted, 0);
            _writeDeadline.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _writeLock.Release();
        }
    }

    /// <summary>Writes an already-encoded frame (serialized with other writes). Used by the client's fallback writer.</summary>
    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame)
    {
        await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        _writeDeadline.Change(WriteTimeout, Timeout.InfiniteTimeSpan);
        try
        {
            Volatile.Write(ref _writeStarted, System.Diagnostics.Stopwatch.GetTimestamp());
            await _ssl.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _writeStarted, 0);
            _writeDeadline.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Best-effort write of a final message (Error, Bye, Busy) bounded by a short wall-clock timeout. Never throws.
    /// True when the whole frame was handed to the socket (and, with <paramref name="shutdownSend"/>, the send side
    /// was half-closed right behind it).
    /// </summary>
    /// <param name="message">The farewell.</param>
    /// <param name="shutdownSend">
    /// Half-close after the frame, still holding the write lock: the FIN is queued behind the farewell, so the peer
    /// reads it before the end of the stream and no other write can slip in between. Reads keep working, so the owner
    /// can wait for the peer to close its side (a close with unread input would reset the connection, and a reset can
    /// discard the farewell before the peer reads it).
    /// </param>
    public async ValueTask<bool> TryWriteFarewellAsync<T>(T message, bool shutdownSend = false)
        where T : IControlMessage<T>
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        using var cts = new CancellationTokenSource(FarewellTimeout);
        bool locked = false;
        try
        {
            locked = await _writeLock.WaitAsync(FarewellTimeout, cts.Token).ConfigureAwait(false);
            if (locked)
            {
                await ControlFrameWriter.WriteAsync(_ssl, message, cts.Token).ConfigureAwait(false);
                if (shutdownSend)
                {
                    _socket.Shutdown(SocketShutdown.Send);
                }

                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            // Best effort: the channel is being closed anyway.
        }
        finally
        {
            if (locked)
            {
                _writeLock.Release();
            }
        }

        return false;
    }

    /// <summary>Cancels all pending reads and writes; the channel is dead afterwards.</summary>
    public void Abort()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Cancels everything and closes the socket at once, without taking any lock: unblocks a write stuck on a peer
    /// that stopped reading. No farewell can be sent afterwards.
    /// </summary>
    public void AbortHard()
    {
        Abort();
        try
        {
            _socket.Close(0);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Abort();
        await _readDeadline.DisposeAsync().ConfigureAwait(false);
        await _writeDeadline.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _ssl.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        _socket.Dispose();

        // Wait for an in-flight write to observe the cancellation before returning its buffer to the pool.
        bool locked = await _writeLock.WaitAsync(FarewellTimeout).ConfigureAwait(false);
        _reader.Dispose();
        if (locked)
        {
            _writeLock.Release();
        }

        _cts.Dispose();
    }

    private void OnWriteDeadline()
    {
        Volatile.Write(ref _timedOut, 1);
        AbortHard();
    }

    private void OnDeadline()
    {
        Volatile.Write(ref _timedOut, 1);
        Abort();
    }
}
