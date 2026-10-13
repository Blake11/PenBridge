using System.Buffers.Binary;

namespace PenBridge.Core.Protocol;

/// <summary>
/// The 16-byte UDP datagram header, which is also the AEAD associated data:
/// <c>u8 version(1) ‖ u8 flags ‖ u16 reserved=0 ‖ u32 sessionId ‖ u64 sequence</c>.
/// </summary>
public readonly record struct InputDatagramHeader(InputDatagramOptions Flags, uint SessionId, ulong Sequence)
{
    public const int Size = 16;

    /// <summary>Datagram layout version; anything else is dropped.</summary>
    public const byte CurrentVersion = 1;

    /// <summary>Writes the header (reserved written as zero).</summary>
    public void Write(Span<byte> destination)
    {
        destination = destination[..Size];
        destination[0] = CurrentVersion;
        destination[1] = (byte)Flags;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], SessionId);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], Sequence);
    }

    /// <summary>False when truncated or the version isn't <see cref="CurrentVersion"/>. Flags and reserved are not interpreted.</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out InputDatagramHeader header)
    {
        header = default;
        if (source.Length < Size || source[0] != CurrentVersion)
        {
            return false;
        }

        header = new InputDatagramHeader(
            (InputDatagramOptions)source[1],
            BinaryPrimitives.ReadUInt32BigEndian(source[4..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[8..]));
        return true;
    }
}

/// <summary>
/// Layout of one UDP input datagram: <c>header(16, AAD) ‖ ciphertext(payload length) ‖ tag(16)</c>, at most 512 B.
/// Encryption is done by the Security layer over these spans; this type only slices.
/// </summary>
public static class InputDatagram
{
    public const int MaxSize = 512;
    public const int TagSize = 16;
    public const int MinSize = InputDatagramHeader.Size + InputPayload.MinSize + TagSize;

    /// <summary>Largest ciphertext/payload that fits in <see cref="MaxSize"/>.</summary>
    public const int MaxPayloadSize = MaxSize - InputDatagramHeader.Size - TagSize;

    /// <summary>Offset of the 96-bit AEAD nonce (<c>u32 sessionId ‖ u64 sequence</c>) inside the header.</summary>
    public const int NonceOffset = 4;

    /// <summary>Size of the AEAD nonce.</summary>
    public const int NonceSize = 12;

    /// <summary>The AEAD nonce, <c>header[4..16]</c> (<c>u32 sessionId ‖ u64 sequence</c>), as written by <see cref="InputDatagramHeader.Write"/>.</summary>
    public static ReadOnlySpan<byte> Nonce(ReadOnlySpan<byte> header)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(header.Length, InputDatagramHeader.Size, nameof(header));
        return header.Slice(NonceOffset, NonceSize);
    }

    /// <summary>Total datagram size for a payload of <paramref name="payloadLength"/> bytes.</summary>
    public static int GetSize(int payloadLength) => InputDatagramHeader.Size + payloadLength + TagSize;

    /// <summary>
    /// Slices a datagram buffer for sending: writes nothing, returns the header (AAD), payload/ciphertext and tag regions.
    /// Write the header and the plaintext payload in place, then encrypt the payload region in place.
    /// </summary>
    public static void Slice(Span<byte> datagram, int payloadLength, out Span<byte> header, out Span<byte> payload, out Span<byte> tag)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(payloadLength, InputPayload.MinSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadLength, MaxPayloadSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(datagram.Length, GetSize(payloadLength), nameof(datagram));
        header = datagram[..InputDatagramHeader.Size];
        payload = datagram.Slice(InputDatagramHeader.Size, payloadLength);
        tag = datagram.Slice(InputDatagramHeader.Size + payloadLength, TagSize);
    }

    /// <summary>
    /// Splits a received datagram. False when it's smaller than <see cref="MinSize"/>, larger than
    /// <see cref="MaxSize"/>, or the header is invalid. The header is not yet authenticated.
    /// </summary>
    public static bool TrySplit(
        ReadOnlySpan<byte> datagram,
        out InputDatagramHeader header,
        out ReadOnlySpan<byte> associatedData,
        out ReadOnlySpan<byte> ciphertext,
        out ReadOnlySpan<byte> tag)
    {
        associatedData = default;
        ciphertext = default;
        tag = default;
        if (datagram.Length is < MinSize or > MaxSize || !InputDatagramHeader.TryRead(datagram, out header))
        {
            header = default;
            return false;
        }

        associatedData = datagram[..InputDatagramHeader.Size];
        ciphertext = datagram[InputDatagramHeader.Size..^TagSize];
        tag = datagram[^TagSize..];
        return true;
    }
}
