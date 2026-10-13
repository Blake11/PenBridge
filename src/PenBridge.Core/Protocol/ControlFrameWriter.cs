using System.Buffers;

namespace PenBridge.Core.Protocol;

/// <summary>Writes control frames to a <see cref="Stream"/> using a pooled buffer per write. Callers serialize writes per connection.</summary>
public static class ControlFrameWriter
{
    /// <summary>Encodes <paramref name="message"/> as one frame and writes it with a single stream write.</summary>
    public static async ValueTask WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken = default)
        where T : IControlMessage<T>
    {
        ArgumentNullException.ThrowIfNull(stream);
        int size = ControlFrame.GetFrameSize(message);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            int written = ControlFrame.Write(buffer, message);
            await stream.WriteAsync(buffer.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
