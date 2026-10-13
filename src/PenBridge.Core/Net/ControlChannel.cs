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

    private static readonly TimerCallback DeadlineCallback = static state => ((ControlChannel)state!).OnDeadline();

    private readonly Socket _socket;
    private readonly SslStream _ssl;
    private readonly TimeProvider _time;
    private readonly ControlFrameReader _reader;
    private readonly CancellationTokenSource _cts = new();
    private readonly ITimer _readDeadline;
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
    }

    public SslStream Ssl => _ssl;

    public Socket Socket => _socket;

    /// <summary>Cancelled when a deadline passes or the channel is aborted.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>True when a deadline (rather than <see cref="Abort"/>) cancelled the channel.</summary>
    public bool TimedOut => Volatile.Read(ref _timedOut) != 0;

    public bool IsAborted => _cts.IsCancellationRequested;

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
        try
        {
            await ControlFrameWriter.WriteAsync(_ssl, message, _cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes an already-encoded frame synchronously (fallback InputFrames from the send path). Returns false and
    /// aborts the channel on failure.
    /// </summary>
    public bool TryWriteFrame(ReadOnlySpan<byte> frame)
    {
        try
        {
            _writeLock.Wait(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            _ssl.Write(frame);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            Abort();
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Best-effort write of a final message (Error, Bye, Busy) bounded by a short wall-clock timeout. Never throws.</summary>
    public async ValueTask TryWriteFarewellAsync<T>(T message)
        where T : IControlMessage<T>
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        using var cts = new CancellationTokenSource(FarewellTimeout);
        bool locked = false;
        try
        {
            locked = await _writeLock.WaitAsync(FarewellTimeout, cts.Token).ConfigureAwait(false);
            if (locked)
            {
                await ControlFrameWriter.WriteAsync(_ssl, message, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Abort();
        await _readDeadline.DisposeAsync().ConfigureAwait(false);
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

    private void OnDeadline()
    {
        Volatile.Write(ref _timedOut, 1);
        Abort();
    }
}
