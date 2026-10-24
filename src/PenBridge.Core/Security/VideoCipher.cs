using System.Buffers.Binary;
using System.Security.Cryptography;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Security;

/// <summary>
/// AES-256-GCM protection of video datagrams (protocol 1.3) for one session, one key and one direction: <c>K_vs</c>
/// for the Mac's video fragments, <c>K_vc</c> for the phone's Probes (<see cref="KeySchedule.DeriveVideoKey"/>).
/// </summary>
/// <remarks>
/// <para>Datagram = 24-byte <see cref="VideoDatagramHeader"/> ‖ ciphertext ‖ 16-byte tag, at most
/// <see cref="VideoDatagram.MaxSize"/> bytes. The header is the associated data; its bytes 4..16
/// (<c>u32 sessionId ‖ u64 sequence</c>) are the 96-bit nonce. The sender writes the header (with
/// <c>sequence = <see cref="LastSealedSequence"/> + 1</c>) and the plaintext, then calls <see cref="Seal"/>.</para>
/// <para>Sealing enforces one strictly increasing sequence for the whole session starting at 1, never reset across
/// epochs, so a nonce is never reused under the key. Opening tolerates reordering: it accepts each sequence once
/// within a sliding window of <see cref="ReplayWindow"/> sequences below the highest authenticated one, and drops
/// replays and anything older. The replay state changes only after the tag verifies.</para>
/// <para>Both operations work in place on a caller-owned buffer and do not allocate. Not thread-safe: use one instance
/// per thread (the Mac's send thread seals, the phone's receive thread opens).</para>
/// </remarks>
public sealed class VideoCipher : IDisposable
{
    /// <summary>Sequences this far below the highest authenticated one are still accepted once (reordering).</summary>
    public const int ReplayWindow = 1024;

    private const int TagLength = VideoDatagram.TagSize;
    private const int HeaderLength = VideoDatagramHeader.Size;
    private const int WindowWords = ReplayWindow / 64;

    private readonly AesGcm _aes;
    private readonly uint _sessionId;
    private readonly ulong[] _window = new ulong[WindowWords];
    private ulong _lastSealed;
    private ulong _highestOpened;
    private bool _disposed;

    /// <summary>Creates a cipher for <paramref name="sessionId"/> with a 32-byte video key.</summary>
    public VideoCipher(ReadOnlySpan<byte> videoKey, uint sessionId)
    {
        KeySchedule.RequireLength(videoKey, KeySchedule.KeyLength, nameof(videoKey));
        _aes = new AesGcm(videoKey, TagLength);
        _sessionId = sessionId;
    }

    /// <summary>The session this cipher is bound to.</summary>
    public uint SessionId => _sessionId;

    /// <summary>The last sequence sealed (0 before the first); the next datagram uses this + 1.</summary>
    public ulong LastSealedSequence => _lastSealed;

    /// <summary>
    /// The highest sequence authenticated so far (0 before the first). Endpoint re-pinning compares a new Probe's
    /// sequence with this value read before <see cref="TryOpen"/>.
    /// </summary>
    public ulong HighestSequence => _highestOpened;

    /// <summary>
    /// Encrypts in place. On entry <paramref name="datagram"/> holds the written header followed by
    /// <paramref name="payloadLength"/> plaintext bytes; on return the payload is ciphertext and the tag follows it.
    /// Returns the total datagram length.
    /// </summary>
    /// <exception cref="ArgumentException">Bad lengths, an invalid header, or the header's sessionId is not this session's.</exception>
    /// <exception cref="InvalidOperationException">The header's sequence is 0 or not above the last sealed one.</exception>
    public int Seal(Span<byte> datagram, int payloadLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)payloadLength > VideoDatagram.MaxPayloadSize || datagram.Length < HeaderLength + payloadLength + TagLength)
        {
            throw new ArgumentException("Datagram buffer too small or payload too large.", nameof(datagram));
        }

        if (!VideoDatagramHeader.TryRead(datagram, out VideoDatagramHeader header) || header.SessionId != _sessionId)
        {
            throw new ArgumentException("Header invalid or its sessionId does not match this cipher.", nameof(datagram));
        }

        if (header.Sequence <= _lastSealed)
        {
            throw new InvalidOperationException("Sequence must start at 1 and strictly increase.");
        }

        ReadOnlySpan<byte> aad = datagram[..HeaderLength];
        Span<byte> body = datagram.Slice(HeaderLength, payloadLength);
        Span<byte> tag = datagram.Slice(HeaderLength + payloadLength, TagLength);
        _aes.Encrypt(aad.Slice(VideoDatagramHeader.NonceOffset, VideoDatagramHeader.NonceSize), body, body, tag, aad);
        _lastSealed = header.Sequence;
        return HeaderLength + payloadLength + TagLength;
    }

    /// <summary>
    /// Parses, authenticates and decrypts a received datagram in place. On success the plaintext payload is at
    /// <c>datagram[24..(24 + payloadLength)]</c>. Returns <see langword="false"/> (never throws) for anything
    /// truncated, oversized, malformed (<see cref="VideoDatagramHeader.TryRead"/>), foreign-session, replayed, older
    /// than the replay window or tampered. The epoch is not checked here (the caller drops other epochs).
    /// </summary>
    public bool TryOpen(Span<byte> datagram, out VideoDatagramHeader header, out int payloadLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        payloadLength = 0;
        if (datagram.Length is < VideoDatagram.MinSize or > VideoDatagram.MaxSize
            || !VideoDatagramHeader.TryRead(datagram, out header))
        {
            header = default;
            return false;
        }

        if (header.SessionId != _sessionId || header.Sequence == 0 || !IsFresh(header.Sequence))
        {
            header = default;
            return false;
        }

        int length = datagram.Length - HeaderLength - TagLength;
        ReadOnlySpan<byte> aad = datagram[..HeaderLength];
        Span<byte> body = datagram.Slice(HeaderLength, length);
        ReadOnlySpan<byte> tag = datagram.Slice(HeaderLength + length, TagLength);
        try
        {
            _aes.Decrypt(aad.Slice(VideoDatagramHeader.NonceOffset, VideoDatagramHeader.NonceSize), body, tag, body, aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            header = default;
            return false;
        }

        MarkSeen(header.Sequence);
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

    private bool IsFresh(ulong sequence)
    {
        if (sequence > _highestOpened)
        {
            return true;
        }

        if (_highestOpened - sequence >= ReplayWindow)
        {
            return false;
        }

        int bit = (int)(sequence % ReplayWindow);
        return (_window[bit >> 6] & (1UL << (bit & 63))) == 0;
    }

    private void MarkSeen(ulong sequence)
    {
        if (sequence > _highestOpened)
        {
            ulong advance = sequence - _highestOpened;
            if (advance >= ReplayWindow)
            {
                Array.Clear(_window);
            }
            else
            {
                for (ulong s = _highestOpened + 1; s < sequence; s++)
                {
                    Clear((int)(s % ReplayWindow));
                }
            }

            _highestOpened = sequence;
        }

        int bit = (int)(sequence % ReplayWindow);
        _window[bit >> 6] |= 1UL << (bit & 63);
    }

    private void Clear(int bit) => _window[bit >> 6] &= ~(1UL << (bit & 63));
}
