namespace PenBridge.Core.Protocol;

/// <summary>
/// A control message with a hand-written big-endian payload codec. The frame header (length + type) is added
/// by <see cref="ControlFrame"/>.
/// </summary>
/// <typeparam name="TSelf">The implementing message type.</typeparam>
public interface IControlMessage<TSelf>
    where TSelf : IControlMessage<TSelf>
{
    /// <summary>Wire type byte.</summary>
    static abstract ControlMessageType Type { get; }

    /// <summary>Payload size in bytes (excluding length and type). Throws when the message is invalid.</summary>
    int PayloadSize { get; }

    /// <summary>Writes the payload; <paramref name="destination"/> must hold at least <see cref="PayloadSize"/> bytes. Returns bytes written.</summary>
    int WritePayload(Span<byte> destination);

    /// <summary>
    /// Parses an untrusted payload. Returns false on truncation or an invalid field; never throws.
    /// Bytes after the known fields are ignored (a newer minor may append fields).
    /// Variable-size fields are copied, so the result doesn't alias <paramref name="payload"/>.
    /// </summary>
    static abstract bool TryRead(ReadOnlySpan<byte> payload, out TSelf message);
}
