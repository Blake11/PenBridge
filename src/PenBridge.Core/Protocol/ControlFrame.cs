using System.Buffers.Binary;

namespace PenBridge.Core.Protocol;

/// <summary>Outcome of <see cref="ControlFrame.TryRead"/>.</summary>
public enum ControlFrameStatus
{
    /// <summary>The buffer doesn't hold a complete frame yet; nothing was consumed.</summary>
    NeedMoreData,

    /// <summary>A complete frame of a known type.</summary>
    Frame,

    /// <summary>A complete frame of a reserved type (<c>0xF0–0xFF</c>); skip <c>consumed</c> bytes.</summary>
    Ignored,
}

/// <summary>
/// Control-channel framing: <c>u32 BE length</c> (type byte + payload, 1..65 536) ‖ <c>u8 type</c> ‖ payload.
/// This is the transport-independent buffer codec; <see cref="ControlFrameReader"/> and
/// <see cref="ControlFrameWriter"/> apply it to a <see cref="Stream"/>.
/// </summary>
public static class ControlFrame
{
    /// <summary>Size of the <c>u32</c> length prefix.</summary>
    public const int LengthPrefixSize = 4;

    /// <summary>Size of the length prefix plus the type byte.</summary>
    public const int HeaderSize = LengthPrefixSize + 1;

    /// <summary>Maximum value of the length field (type byte + payload).</summary>
    public const int MaxLength = 65_536;

    /// <summary>Maximum payload size.</summary>
    public const int MaxPayloadSize = MaxLength - 1;

    /// <summary>Total size of the frame for <paramref name="message"/>. Throws when the payload exceeds <see cref="MaxPayloadSize"/>.</summary>
    public static int GetFrameSize<T>(in T message)
        where T : IControlMessage<T>
    {
        int payload = message.PayloadSize;
        if (payload > MaxPayloadSize)
        {
            throw new ArgumentException($"Payload exceeds {MaxPayloadSize} bytes.", nameof(message));
        }

        return HeaderSize + payload;
    }

    /// <summary>Writes a complete frame. Returns bytes written.</summary>
    public static int Write<T>(Span<byte> destination, in T message)
        where T : IControlMessage<T>
    {
        int size = GetFrameSize(message);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, size, nameof(destination));
        WriteHeader(destination, T.Type, size - HeaderSize);
        int written = message.WritePayload(destination[HeaderSize..]);
        if (written != size - HeaderSize)
        {
            throw new InvalidOperationException("Message wrote a different size than it declared.");
        }

        return size;
    }

    /// <summary>Writes a frame whose payload is already encoded (e.g. an InputFrame built in place). Returns bytes written.</summary>
    public static int WriteRaw(Span<byte> destination, ControlMessageType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
        {
            throw new ArgumentException($"Payload exceeds {MaxPayloadSize} bytes.", nameof(payload));
        }

        int size = HeaderSize + payload.Length;
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, size, nameof(destination));
        WriteHeader(destination, type, payload.Length);
        payload.CopyTo(destination[HeaderSize..]);
        return size;
    }

    /// <summary>Writes the 5-byte header (length + type) for a payload of <paramref name="payloadLength"/> bytes.</summary>
    public static void WriteHeader(Span<byte> destination, ControlMessageType type, int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadLength, MaxPayloadSize);
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)(payloadLength + 1));
        destination[LengthPrefixSize] = (byte)type;
    }

    /// <summary>
    /// Decodes one frame from the start of <paramref name="buffer"/>.
    /// Throws <see cref="ProtocolException"/> for a zero length (<see cref="ProtocolErrorCode.ProtocolViolation"/>),
    /// an oversized length (<see cref="ProtocolErrorCode.FrameTooLarge"/>) and an unknown non-reserved type (<see cref="ProtocolErrorCode.UnknownMessageType"/>); the caller must disconnect.
    /// </summary>
    public static ControlFrameStatus TryRead(
        ReadOnlySpan<byte> buffer,
        out ControlMessageType type,
        out ReadOnlySpan<byte> payload,
        out int consumed)
    {
        type = default;
        payload = default;
        consumed = 0;
        if (buffer.Length < LengthPrefixSize)
        {
            return ControlFrameStatus.NeedMoreData;
        }

        int length = ValidateLength(BinaryPrimitives.ReadUInt32BigEndian(buffer));
        if (buffer.Length < LengthPrefixSize + length)
        {
            return ControlFrameStatus.NeedMoreData;
        }

        byte rawType = buffer[LengthPrefixSize];
        bool ignored = ClassifyType(rawType);
        consumed = LengthPrefixSize + length;
        type = (ControlMessageType)rawType;
        payload = buffer.Slice(HeaderSize, length - 1);
        return ignored ? ControlFrameStatus.Ignored : ControlFrameStatus.Frame;
    }

    /// <summary>Validates the length field; returns it as an int.</summary>
    internal static int ValidateLength(uint length)
    {
        if (length == 0)
        {
            throw new ProtocolException(ProtocolErrorCode.ProtocolViolation, "Control frame has zero length.");
        }

        if (length > MaxLength)
        {
            throw new ProtocolException(ProtocolErrorCode.FrameTooLarge, "Control frame exceeds the maximum length.");
        }

        return (int)length;
    }

    /// <summary>Returns true for reserved (ignored) types; throws for unknown non-reserved types.</summary>
    internal static bool ClassifyType(byte type)
    {
        if (ControlMessageTypes.IsReserved(type))
        {
            return true;
        }

        if (!ControlMessageTypes.IsKnown(type))
        {
            throw new ProtocolException(ProtocolErrorCode.UnknownMessageType, "Unknown control message type.");
        }

        return false;
    }
}
