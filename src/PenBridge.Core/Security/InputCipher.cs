using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PenBridge.Core.Security;

/// <summary>
/// AES-256-GCM protection of UDP input datagrams for one session and one direction.
/// </summary>
/// <remarks>
/// <para>Datagram = 16-byte header ‖ ciphertext ‖ 16-byte tag. The header is the associated data;
/// its bytes 4..16 (<c>u32 sessionId ‖ u64 sequence</c>, big-endian) are the 96-bit nonce. The
/// header itself (version, flags, reserved) is written by the protocol layer; this type only reads
/// sessionId and sequence from it.</para>
/// <para>Sealing enforces a strictly increasing sequence starting at 1, so a nonce is never reused
/// under one key. Opening keeps the replay state ("latest wins"): anything at or below the highest
/// authenticated sequence is dropped. The highest sequence advances only after the tag verifies.</para>
/// <para>Both operations work in place on a caller-owned buffer and do not allocate. Not thread-safe.</para>
/// </remarks>
public sealed class InputCipher : IDisposable
{
    /// <summary>Header (associated data) length.</summary>
    public const int HeaderLength = 16;

    /// <summary>GCM tag length.</summary>
    public const int TagLength = 16;

    /// <summary>Maximum datagram size on the wire.</summary>
    public const int MaxDatagramLength = 512;

    /// <summary>Maximum plaintext payload length.</summary>
    public const int MaxPayloadLength = MaxDatagramLength - HeaderLength - TagLength;

    private const int SessionIdOffset = 4;
    private const int SequenceOffset = 8;
    private const int NonceLength = 12;

    private readonly AesGcm _aes;
    private readonly uint _sessionId;
    private ulong _lastSealed;
    private ulong _highestOpened;
    private bool _disposed;

    /// <summary>Creates a cipher for <paramref name="sessionId"/> with the 32-byte <c>K_in</c>.</summary>
    public InputCipher(ReadOnlySpan<byte> inputKey, uint sessionId)
    {
        KeySchedule.RequireLength(inputKey, KeySchedule.KeyLength, nameof(inputKey));
        _aes = new AesGcm(inputKey, TagLength);
        _sessionId = sessionId;
    }

    /// <summary>The session this cipher is bound to.</summary>
    public uint SessionId => _sessionId;

    /// <summary>Highest sequence successfully opened so far (0 before the first), for <c>UdpAck</c>.</summary>
    public ulong HighestSequence => _highestOpened;

    /// <summary>
    /// Encrypts in place. On entry <paramref name="datagram"/> holds the header followed by
    /// <paramref name="payloadLength"/> plaintext bytes; on return the payload is ciphertext and the
    /// tag follows it. Returns the total datagram length.
    /// </summary>
    /// <exception cref="ArgumentException">Bad lengths, or the header's sessionId is not this session's.</exception>
    /// <exception cref="InvalidOperationException">The header's sequence is 0 or not above the last sealed one.</exception>
    public int Seal(Span<byte> datagram, int payloadLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)payloadLength > MaxPayloadLength || datagram.Length < HeaderLength + payloadLength + TagLength)
        {
            throw new ArgumentException("Datagram buffer too small or payload too large.", nameof(datagram));
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(datagram[SessionIdOffset..]) != _sessionId)
        {
            throw new ArgumentException("Header sessionId does not match this cipher.", nameof(datagram));
        }

        ulong sequence = BinaryPrimitives.ReadUInt64BigEndian(datagram[SequenceOffset..]);
        if (sequence <= _lastSealed)
        {
            throw new InvalidOperationException("Sequence must start at 1 and strictly increase.");
        }

        ReadOnlySpan<byte> header = datagram[..HeaderLength];
        Span<byte> body = datagram.Slice(HeaderLength, payloadLength);
        Span<byte> tag = datagram.Slice(HeaderLength + payloadLength, TagLength);
        _aes.Encrypt(header[SessionIdOffset..], body, body, tag, header);
        _lastSealed = sequence;
        return HeaderLength + payloadLength + TagLength;
    }

    /// <summary>
    /// Authenticates and decrypts a received datagram in place. On success the plaintext payload is
    /// at <c>datagram[16..(16 + payloadLength)]</c>. Returns <see langword="false"/> (never throws)
    /// for anything malformed, foreign-session, replayed, old or tampered.
    /// </summary>
    public bool TryOpen(Span<byte> datagram, out int payloadLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        payloadLength = 0;
        if (datagram.Length < HeaderLength + TagLength || datagram.Length > MaxDatagramLength)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(datagram[SessionIdOffset..]) != _sessionId)
        {
            return false;
        }

        ulong sequence = BinaryPrimitives.ReadUInt64BigEndian(datagram[SequenceOffset..]);
        if (sequence <= _highestOpened)
        {
            return false;
        }

        int length = datagram.Length - HeaderLength - TagLength;
        ReadOnlySpan<byte> header = datagram[..HeaderLength];
        Span<byte> body = datagram.Slice(HeaderLength, length);
        ReadOnlySpan<byte> tag = datagram.Slice(HeaderLength + length, TagLength);
        try
        {
            _aes.Decrypt(header[SessionIdOffset..(SessionIdOffset + NonceLength)], body, tag, body, header);
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }

        _highestOpened = sequence;
        payloadLength = length;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _aes.Dispose();
        }
    }
}
