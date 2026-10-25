using System.Buffers.Binary;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Core.Mirror;

/// <summary>One access unit to send: the per-frame header fields of its fragments.</summary>
/// <param name="Epoch">The current MirrorConfig epoch.</param>
/// <param name="FrameNumber">
/// Frame number in the epoch: <see cref="VideoDatagram.FirstFrameNumber"/> for the epoch's first access unit (a
/// keyframe), then one more than the previous access unit's, also when the sender dropped that one unsent, so the
/// phone sees a gap and waits for a keyframe.
/// </param>
/// <param name="Keyframe">The access unit is a keyframe (starts with its parameter sets).</param>
/// <param name="CaptureTimeUs">Capture time on the Mac's session clock (the server's Ping clock), µs, wraps.</param>
public readonly record struct VideoFrameInfo(byte Epoch, uint FrameNumber, bool Keyframe, uint CaptureTimeUs);

/// <summary>
/// Splits an access unit into sealed VideoFragment datagrams (Mac side), one call per fragment so the caller can pace
/// each send (<see cref="VideoPacer"/>). Fragments are filled greedily: every fragment but the last carries
/// <see cref="VideoDatagram.MaxPayloadSize"/> bytes of payload (fragment 0: the 8-byte prefix and
/// <see cref="VideoDatagram.FirstFragmentDataSize"/> bytes), which lets the reassembler place data without moving it.
/// Allocation-free; the cipher assigns the sequence (<see cref="VideoCipher.LastSealedSequence"/> + 1).
/// </summary>
public static class VideoFragmenter
{
    /// <summary>Fragments for an access unit of <paramref name="accessUnitLength"/> bytes (1..1 MiB), 1..1024.</summary>
    public static int GetFragmentCount(int accessUnitLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(accessUnitLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(accessUnitLength, VideoDatagram.MaxFrameLength);
        int rest = Math.Max(0, accessUnitLength - VideoDatagram.FirstFragmentDataSize);
        return 1 + ((rest + VideoDatagram.MaxPayloadSize - 1) / VideoDatagram.MaxPayloadSize);
    }

    /// <summary>
    /// Writes fragment <paramref name="fragmentIndex"/> of <paramref name="accessUnit"/> into
    /// <paramref name="datagram"/> (at least <see cref="VideoDatagram.MaxSize"/> bytes, or the exact size) and seals it
    /// with <paramref name="cipher"/> (<c>K_vs</c>). Returns the datagram length.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An empty or oversized access unit, or an index past the count.</exception>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public static int WriteFragment(VideoCipher cipher, ReadOnlySpan<byte> accessUnit, in VideoFrameInfo frame, int fragmentIndex, Span<byte> datagram)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        int count = GetFragmentCount(accessUnit.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(fragmentIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fragmentIndex, count);

        int start = DataOffset(fragmentIndex);
        int dataLength = Math.Min(accessUnit.Length - start, fragmentIndex == 0 ? VideoDatagram.FirstFragmentDataSize : VideoDatagram.MaxPayloadSize);
        int payloadLength = dataLength + (fragmentIndex == 0 ? VideoDatagram.FirstFragmentPrefixSize : 0);
        int total = VideoDatagram.GetSize(payloadLength);
        if (datagram.Length < total)
        {
            throw new ArgumentException("Datagram buffer too small.", nameof(datagram));
        }

        VideoDatagramHeader.Fragment(frame.Epoch, frame.Keyframe, cipher.SessionId, cipher.LastSealedSequence + 1, frame.FrameNumber, (ushort)fragmentIndex, (ushort)count)
            .Write(datagram);
        Span<byte> payload = datagram.Slice(VideoDatagramHeader.Size, payloadLength);
        if (fragmentIndex == 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload, frame.CaptureTimeUs);
            BinaryPrimitives.WriteUInt32BigEndian(payload[4..], (uint)accessUnit.Length);
            payload = payload[VideoDatagram.FirstFragmentPrefixSize..];
        }

        accessUnit.Slice(start, dataLength).CopyTo(payload);
        return cipher.Seal(datagram, payloadLength);
    }

    /// <summary>
    /// Writes and seals a Probe (phone side, <c>K_vc</c>) for <paramref name="epoch"/> into <paramref name="datagram"/>.
    /// Returns its length (<see cref="VideoDatagram.MinSize"/>).
    /// </summary>
    public static int WriteProbe(VideoCipher cipher, byte epoch, Span<byte> datagram)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        if (datagram.Length < VideoDatagram.MinSize)
        {
            throw new ArgumentException("Datagram buffer too small.", nameof(datagram));
        }

        VideoDatagramHeader.Probe(epoch, cipher.SessionId, cipher.LastSealedSequence + 1).Write(datagram);
        return cipher.Seal(datagram, 0);
    }

    /// <summary>Offset in the access unit of fragment <paramref name="index"/>'s data (greedy layout).</summary>
    internal static int DataOffset(int index) =>
        index == 0 ? 0 : VideoDatagram.FirstFragmentDataSize + ((index - 1) * VideoDatagram.MaxPayloadSize);
}
