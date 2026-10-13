using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using PenBridge.Core.Input;

namespace PenBridge.Core.Protocol;

/// <summary>
/// The plaintext input payload carried (encrypted) in every UDP datagram and, in fallback mode, in InputFrame:
/// <c>u32 timeUs ‖ u8 penCount(0|1) ‖ u8 touchCount(0..10) ‖ pen? ‖ touches… ‖ u8 historyCount(0..8) ‖ history…</c>.
/// History holds earlier pen samples, oldest first. It is a self-contained value type (inline buffers, no heap
/// references), so writing and reading it never allocates. Bytes after the history are ignored.
/// </summary>
public struct InputPayload
{
    public const int MaxTouches = 10;
    public const int MaxHistory = 8;

    /// <summary>Smallest valid payload: time + three counts.</summary>
    public const int MinSize = 4 + 1 + 1 + 1;

    /// <summary>Largest valid payload (211 B).</summary>
    public const int MaxSize = MinSize + InputSampleCodec.PenSampleSize
        + (MaxTouches * InputSampleCodec.TouchSampleSize)
        + (MaxHistory * InputSampleCodec.PenSampleSize);

    private TouchBuffer _touches;
    private HistoryBuffer _history;
    private byte _touchCount;
    private byte _historyCount;

    /// <summary>Microseconds since session start; wraps.</summary>
    public uint TimeUs { readonly get; set; }

    /// <summary>The current pen sample, or null when the pen is out of range.</summary>
    public PenSample? Pen { readonly get; set; }

    /// <summary>All active touch pointers.</summary>
    [UnscopedRef]
    public readonly ReadOnlySpan<TouchSample> Touches => ((ReadOnlySpan<TouchSample>)_touches)[.._touchCount];

    /// <summary>Earlier pen samples batched into this datagram, oldest first.</summary>
    [UnscopedRef]
    public readonly ReadOnlySpan<PenSample> History => ((ReadOnlySpan<PenSample>)_history)[.._historyCount];

    /// <summary>Encoded size in bytes.</summary>
    public readonly int Size => GetSize(Pen.HasValue, _touchCount, _historyCount);

    /// <summary>Replaces the touches. Throws when there are more than <see cref="MaxTouches"/>.</summary>
    public void SetTouches(ReadOnlySpan<TouchSample> touches)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(touches.Length, MaxTouches, nameof(touches));
        touches.CopyTo(_touches);
        _touchCount = (byte)touches.Length;
    }

    /// <summary>Replaces the history. Throws when there are more than <see cref="MaxHistory"/>.</summary>
    public void SetHistory(ReadOnlySpan<PenSample> history)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(history.Length, MaxHistory, nameof(history));
        history.CopyTo(_history);
        _historyCount = (byte)history.Length;
    }

    /// <summary>Encoded size for the given counts.</summary>
    public static int GetSize(bool hasPen, int touchCount, int historyCount) =>
        MinSize
        + (hasPen ? InputSampleCodec.PenSampleSize : 0)
        + (touchCount * InputSampleCodec.TouchSampleSize)
        + (historyCount * InputSampleCodec.PenSampleSize);

    /// <summary>Writes this payload. Returns bytes written.</summary>
    public readonly int Write(Span<byte> destination)
    {
        PenSample? pen = Pen;
        return Write(destination, TimeUs, pen, Touches, History);
    }

    /// <summary>
    /// Writes a payload straight from the caller's state without building an <see cref="InputPayload"/>.
    /// Throws when counts exceed their caps, a sample is invalid, or <paramref name="destination"/> is too small.
    /// Duplicate touch pointer ids are rejected.
    /// </summary>
    public static int Write(
        Span<byte> destination,
        uint timeUs,
        in PenSample? pen,
        ReadOnlySpan<TouchSample> touches,
        ReadOnlySpan<PenSample> history)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(touches.Length, MaxTouches, nameof(touches));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(history.Length, MaxHistory, nameof(history));
        if (HasDuplicatePointer(touches))
        {
            throw new ArgumentException("Duplicate touch pointer id.", nameof(touches));
        }

        int size = GetSize(pen.HasValue, touches.Length, history.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, size, nameof(destination));

        BinaryPrimitives.WriteUInt32BigEndian(destination, timeUs);
        destination[4] = pen.HasValue ? (byte)1 : (byte)0;
        destination[5] = (byte)touches.Length;
        int offset = 6;
        if (pen.HasValue)
        {
            InputSampleCodec.WritePen(destination[offset..], pen.GetValueOrDefault());
            offset += InputSampleCodec.PenSampleSize;
        }

        foreach (ref readonly TouchSample touch in touches)
        {
            InputSampleCodec.WriteTouch(destination[offset..], touch);
            offset += InputSampleCodec.TouchSampleSize;
        }

        destination[offset++] = (byte)history.Length;
        foreach (ref readonly PenSample sample in history)
        {
            InputSampleCodec.WritePen(destination[offset..], sample);
            offset += InputSampleCodec.PenSampleSize;
        }

        return offset;
    }

    /// <summary>
    /// Parses an untrusted (already authenticated) payload. False when truncated, a count exceeds its cap,
    /// a sample is invalid, or two touches share a pointer id. Never throws, never allocates.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out InputPayload payload)
    {
        payload = default;
        if (source.Length < MinSize)
        {
            return false;
        }

        uint timeUs = BinaryPrimitives.ReadUInt32BigEndian(source);
        byte penCount = source[4];
        byte touchCount = source[5];
        if (penCount > 1 || touchCount > MaxTouches)
        {
            return false;
        }

        int offset = 6;
        int needed = GetSize(penCount == 1, touchCount, 0);
        if (source.Length < needed)
        {
            return false;
        }

        PenSample? pen = null;
        if (penCount == 1)
        {
            if (!InputSampleCodec.TryReadPen(source[offset..], out PenSample sample))
            {
                return false;
            }

            pen = sample;
            offset += InputSampleCodec.PenSampleSize;
        }

        Span<TouchSample> touches = payload._touches;
        for (int i = 0; i < touchCount; i++)
        {
            if (!InputSampleCodec.TryReadTouch(source[offset..], out touches[i]))
            {
                payload = default;
                return false;
            }

            offset += InputSampleCodec.TouchSampleSize;
        }

        if (HasDuplicatePointer(touches[..touchCount]))
        {
            payload = default;
            return false;
        }

        byte historyCount = source[offset++];
        if (historyCount > MaxHistory || source.Length < offset + (historyCount * InputSampleCodec.PenSampleSize))
        {
            payload = default;
            return false;
        }

        Span<PenSample> history = payload._history;
        for (int i = 0; i < historyCount; i++)
        {
            if (!InputSampleCodec.TryReadPen(source[offset..], out history[i]))
            {
                payload = default;
                return false;
            }

            offset += InputSampleCodec.PenSampleSize;
        }

        payload.TimeUs = timeUs;
        payload.Pen = pen;
        payload._touchCount = touchCount;
        payload._historyCount = historyCount;
        return true;
    }

    private static bool HasDuplicatePointer(ReadOnlySpan<TouchSample> touches)
    {
        for (int i = 1; i < touches.Length; i++)
        {
            for (int j = 0; j < i; j++)
            {
                if (touches[i].PointerId == touches[j].PointerId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [InlineArray(MaxTouches)]
    private struct TouchBuffer
    {
        private TouchSample _element;
    }

    [InlineArray(MaxHistory)]
    private struct HistoryBuffer
    {
        private PenSample _element;
    }
}
