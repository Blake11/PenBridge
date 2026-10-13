using System.Buffers;
using System.Buffers.Binary;

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
/// Reserved-type frames are skipped. Not thread-safe: one reader per connection.
/// </summary>
public sealed class ControlFrameReader : IDisposable
{
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
    /// Throws <see cref="ProtocolException"/> on an invalid length, an unknown type, or a stream that ends mid-frame.
    /// </summary>
    public async ValueTask<ReceivedControlFrame?> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            int got = await ReadAtLeastAsync(_buffer.AsMemory(0, ControlFrame.LengthPrefixSize), cancellationToken).ConfigureAwait(false);
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
            if (await ReadAtLeastAsync(_buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false) < length)
            {
                throw Truncated();
            }

            byte type = _buffer[0];
            if (ControlFrame.ClassifyType(type))
            {
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

    private async ValueTask<int> ReadAtLeastAsync(Memory<byte> destination, CancellationToken cancellationToken) =>
        await _stream.ReadAtLeastAsync(destination, destination.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);

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
