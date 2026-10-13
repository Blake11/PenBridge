using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace PenBridge.Core.Protocol;

/// <summary>A decoded control frame. <see cref="Payload"/> is only valid until the next read on the same reader.</summary>
public readonly struct ReceivedControlFrame(ControlMessageType type, ReadOnlyMemory<byte> payload)
{
    public ControlMessageType Type { get; } = type;

    public ReadOnlyMemory<byte> Payload { get; } = payload;

    /// <summary>Parses the payload as <typeparamref name="T"/>; throws <see cref="ProtocolException"/> when the type or payload is wrong.</summary>
    public T Read<T>()
        where T : IControlMessage<T>
    {
        if (Type != T.Type)
        {
            throw new ProtocolException(ProtocolErrorCode.UnexpectedMessage, "Unexpected control message type.");
        }

        if (!T.TryRead(Payload.Span, out T message))
        {
            throw new ProtocolException(ProtocolErrorCode.ProtocolViolation, "Malformed control message payload.");
        }

        return message;
    }
}

/// <summary>
/// Reads control frames from a <see cref="Stream"/> (the TLS <c>SslStream</c>). One pooled buffer is reused and
/// grown on demand up to <see cref="ControlFrame.MaxLength"/>, so a peer can't make it allocate more than that.
/// Reserved-type frames are skipped, but at most <see cref="MaxConsecutiveReservedFrames"/> in a row; more is a
/// protocol violation, so reserved frames can't stand in for real traffic. Not thread-safe: one reader per connection.
/// </summary>
/// <remarks>
/// <para>
/// The reader has no timeouts of its own. Callers must pass a per-read deadline
/// (a <see cref="CancellationToken"/> from a source with <c>CancelAfter</c>) and enforce a handshake timeout before
/// authentication, otherwise a silent or trickling peer holds the connection open indefinitely.
/// </para>
/// <para>
/// Any failure is terminal: after cancellation or any exception (including <see cref="ProtocolException"/>) the
/// stream may be positioned mid-frame and is desynchronized. Dispose the reader and close the connection; never
/// read again.
/// </para>
/// </remarks>
public sealed class ControlFrameReader : IDisposable
{
    /// <summary>Most reserved (<c>0xF0–0xFF</c>) frames accepted back to back before the reader gives up.</summary>
    public const int MaxConsecutiveReservedFrames = 16;

    private const int InitialBufferSize = 1024;

    private readonly Stream _stream;
    private byte[] _buffer;
    private bool _disposed;

    public ControlFrameReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
    }

    /// <summary>
    /// Reads the next non-reserved frame. Returns null on a clean end of stream at a frame boundary.
    /// Throws <see cref="ProtocolException"/> on an invalid length, an unknown type, more than
    /// <see cref="MaxConsecutiveReservedFrames"/> reserved frames in a row, or a stream that ends mid-frame.
    /// Any exception or cancellation leaves the stream desynchronized (see remarks on the type).
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<ReceivedControlFrame?> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int reserved = 0;
        while (true)
        {
            int got = await _stream.ReadAtLeastAsync(_buffer.AsMemory(0, ControlFrame.LengthPrefixSize), ControlFrame.LengthPrefixSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                return null;
            }

            if (got < ControlFrame.LengthPrefixSize)
            {
                throw Truncated();
            }

            int length = ControlFrame.ValidateLength(BinaryPrimitives.ReadUInt32BigEndian(_buffer));
            EnsureCapacity(length);
            if (await _stream.ReadAtLeastAsync(_buffer.AsMemory(0, length), length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) < length)
            {
                throw Truncated();
            }

            byte type = _buffer[0];
            if (ControlFrame.ClassifyType(type))
            {
                if (++reserved > MaxConsecutiveReservedFrames)
                {
                    throw new ProtocolException(ProtocolErrorCode.ProtocolViolation, "Too many consecutive reserved control frames.");
                }

                continue;
            }

            return new ReceivedControlFrame((ControlMessageType)type, _buffer.AsMemory(1, length - 1));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = [];
    }

    private void EnsureCapacity(int length)
    {
        if (_buffer.Length >= length)
        {
            return;
        }

        byte[] bigger = ArrayPool<byte>.Shared.Rent(length);
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = bigger;
    }

    private static ProtocolException Truncated() =>
        new(ProtocolErrorCode.ProtocolViolation, "Control stream ended in the middle of a frame.");
}
