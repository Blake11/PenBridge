using System.Buffers.Binary;

namespace PenBridge.Core.Protocol;

#pragma warning disable CA1028 // Wire enums: storage matches the u8 wire field.

/// <summary><c>u8 kind</c> of a video datagram. Any other value is dropped.</summary>
public enum VideoDatagramKind : byte
{
    /// <summary>Mac → phone under <c>K_vs</c>: one fragment of an access unit.</summary>
    VideoFragment = 1,

    /// <summary>Phone → Mac under <c>K_vc</c>: pins the phone's video endpoint; empty payload.</summary>
    Probe = 2,
}

/// <summary><c>u8 flags</c> of a video datagram. Unknown bits are ignored.</summary>
[Flags]
public enum VideoDatagramOptions : byte
{
    None = 0,

    /// <summary>On every fragment of a keyframe.</summary>
    Keyframe = 1,
}

#pragma warning restore CA1028

/// <summary>
/// The 24-byte video datagram header (protocol 1.3, <c>docs/specs/screen-mirroring.md</c>), which is also the AEAD
/// associated data: <c>u8 version(1) ‖ u8 kind ‖ u8 epoch ‖ u8 flags ‖ u32 sessionId ‖ u64 sequence ‖ u32 frameNumber ‖
/// u16 fragmentIndex ‖ u16 fragmentCount</c>. Bytes 4..16 (<c>sessionId ‖ sequence</c>) are the 96-bit nonce, as in the
/// input datagram.
/// </summary>
public readonly record struct VideoDatagramHeader(
    VideoDatagramKind Kind,
    byte Epoch,
    VideoDatagramOptions Flags,
    uint SessionId,
    ulong Sequence,
    uint FrameNumber,
    ushort FragmentIndex,
    ushort FragmentCount)
{
    public const int Size = 24;

    /// <summary>Datagram layout version; anything else is dropped.</summary>
    public const byte CurrentVersion = 1;

    /// <summary>Offset of <c>u32 sessionId ‖ u64 sequence</c>, the AEAD nonce.</summary>
    public const int NonceOffset = 4;

    /// <summary>Size of the AEAD nonce.</summary>
    public const int NonceSize = 12;

    /// <summary>A fragment header; <see cref="IsConsistent"/> checks the fields.</summary>
    public static VideoDatagramHeader Fragment(byte epoch, bool keyframe, uint sessionId, ulong sequence, uint frameNumber, ushort fragmentIndex, ushort fragmentCount) =>
        new(VideoDatagramKind.VideoFragment, epoch, keyframe ? VideoDatagramOptions.Keyframe : VideoDatagramOptions.None, sessionId, sequence, frameNumber, fragmentIndex, fragmentCount);

    /// <summary>A Probe header (frame fields zero).</summary>
    public static VideoDatagramHeader Probe(byte epoch, uint sessionId, ulong sequence) =>
        new(VideoDatagramKind.Probe, epoch, VideoDatagramOptions.None, sessionId, sequence, 0, 0, 0);

    /// <summary>True when this fragment carries the keyframe flag.</summary>
    public bool IsKeyframe => (Flags & VideoDatagramOptions.Keyframe) != 0;

    /// <summary>
    /// The header rules: a known kind; a fragment has <c>frameNumber</c> ≥ 1, <c>fragmentCount</c> 1..<see cref="VideoDatagram.MaxFragments"/>
    /// and <c>fragmentIndex &lt; fragmentCount</c>; a Probe has frameNumber, fragmentIndex and fragmentCount 0.
    /// </summary>
    public bool IsConsistent => Kind switch
    {
        VideoDatagramKind.VideoFragment => FrameNumber >= VideoDatagram.FirstFrameNumber && FragmentCount is >= 1 and <= VideoDatagram.MaxFragments && FragmentIndex < FragmentCount,
        VideoDatagramKind.Probe => FrameNumber == 0 && FragmentIndex == 0 && FragmentCount == 0,
        _ => false,
    };

    /// <summary>Writes the header. Throws <see cref="ArgumentException"/> for an inconsistent header or a short destination.</summary>
    public void Write(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Size, nameof(destination));
        if (!IsConsistent)
        {
            throw new ArgumentException("Inconsistent video datagram header.", nameof(destination));
        }

        destination[0] = CurrentVersion;
        destination[1] = (byte)Kind;
        destination[2] = Epoch;
        destination[3] = (byte)Flags;
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], SessionId);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], FrameNumber);
        BinaryPrimitives.WriteUInt16BigEndian(destination[20..], FragmentIndex);
        BinaryPrimitives.WriteUInt16BigEndian(destination[22..], FragmentCount);
    }

    /// <summary>False when truncated, of an unknown version or kind, or inconsistent (<see cref="IsConsistent"/>). Never throws. Not authenticated yet.</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out VideoDatagramHeader header)
    {
        header = default;
        if (source.Length < Size || source[0] != CurrentVersion)
        {
            return false;
        }

        var parsed = new VideoDatagramHeader(
            (VideoDatagramKind)source[1],
            source[2],
            (VideoDatagramOptions)source[3],
            BinaryPrimitives.ReadUInt32BigEndian(source[4..]),
            BinaryPrimitives.ReadUInt64BigEndian(source[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[16..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[20..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[22..]));
        if (!parsed.IsConsistent)
        {
            return false;
        }

        header = parsed;
        return true;
    }
}

/// <summary>
/// Layout of one video datagram: <c>header(24, AAD) ‖ ciphertext ‖ tag(16)</c>, at most <see cref="MaxSize"/> bytes.
/// The first fragment's plaintext starts with <c>u32 captureTimeUs ‖ u32 frameLength</c>
/// (<see cref="FirstFragmentPrefixSize"/>); later fragments are access-unit bytes only.
/// </summary>
public static class VideoDatagram
{
    /// <summary>Largest datagram on the wire.</summary>
    public const int MaxSize = 1200;

    public const int TagSize = 16;

    /// <summary>Smallest valid datagram (a Probe: header and tag).</summary>
    public const int MinSize = VideoDatagramHeader.Size + TagSize;

    /// <summary>Largest plaintext payload per datagram.</summary>
    public const int MaxPayloadSize = MaxSize - VideoDatagramHeader.Size - TagSize;

    /// <summary>Most fragments per frame.</summary>
    /// <remarks>
    /// The most a <see cref="MaxFrameLength"/> frame needs with full fragments (904): a larger count can't carry a valid
    /// frame and would only reserve reassembly memory, so the header rejects it.
    /// </remarks>
    public const int MaxFragments = 1 + ((MaxFrameLength - FirstFragmentDataSize + MaxPayloadSize - 1) / MaxPayloadSize);

    /// <summary>Largest access unit (<c>frameLength</c>).</summary>
    public const int MaxFrameLength = 1024 * 1024;

    /// <summary><c>u32 captureTimeUs ‖ u32 frameLength</c> at the start of fragment 0.</summary>
    public const int FirstFragmentPrefixSize = 8;

    /// <summary>Access-unit bytes the first fragment carries at most.</summary>
    public const int FirstFragmentDataSize = MaxPayloadSize - FirstFragmentPrefixSize;

    /// <summary>The first frame number of every epoch (always a keyframe); 0 means "no frame".</summary>
    public const uint FirstFrameNumber = 1;

    /// <summary>
    /// After a new MirrorConfig the Mac holds the epoch's video, starting from its first keyframe (frame 1, never
    /// dropped), until an authenticated Probe carrying that epoch arrives, at most this long; then it sends anyway.
    /// </summary>
    public static readonly TimeSpan EpochProbeWait = TimeSpan.FromMilliseconds(250);

    /// <summary>Total datagram size for a payload of <paramref name="payloadLength"/> bytes.</summary>
    public static int GetSize(int payloadLength) => VideoDatagramHeader.Size + payloadLength + TagSize;
}
