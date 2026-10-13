using System.Buffers.Binary;
using PenBridge.Core.Input;

namespace PenBridge.Core.Protocol;

/// <summary>
/// Wire codec for <see cref="PenSample"/> (16 B) and <see cref="TouchSample"/> (6 B). Big-endian, allocation-free.
/// Readers reject out-of-range values and unknown tool/state/button bits; writers throw on them (a caller bug).
/// </summary>
public static class InputSampleCodec
{
    public const int PenSampleSize = 16;
    public const int TouchSampleSize = 6;
    public const short MaxTilt = 9000;
    public const ushort MaxRotation = 35999;

    private const byte TouchContact = 2;
    private const PenState KnownPenStates = PenState.InRange | PenState.Contact;
    private const PenButtons KnownButtons = PenButtons.Primary | PenButtons.Secondary;

    /// <summary>
    /// <c>u16 x ‖ u16 y ‖ u16 pressure ‖ i16 tiltX ‖ i16 tiltY ‖ u16 rotation ‖ u8 tool ‖ u8 state ‖ u8 buttons ‖ u8 reserved=0</c>.
    /// </summary>
    public static void WritePen(Span<byte> destination, in PenSample sample)
    {
        if (!IsValid(sample))
        {
            throw new ArgumentException("Pen sample has out-of-range values.", nameof(sample));
        }

        destination = destination[..PenSampleSize];
        BinaryPrimitives.WriteUInt16BigEndian(destination, sample.X);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], sample.Y);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], sample.Pressure);
        BinaryPrimitives.WriteInt16BigEndian(destination[6..], sample.TiltX);
        BinaryPrimitives.WriteInt16BigEndian(destination[8..], sample.TiltY);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], sample.Rotation);
        destination[12] = (byte)sample.Tool;
        destination[13] = (byte)sample.State;
        destination[14] = (byte)sample.Buttons;
        destination[15] = 0;
    }

    /// <summary>Reads 16 bytes; false when truncated or invalid. The reserved byte is ignored.</summary>
    public static bool TryReadPen(ReadOnlySpan<byte> source, out PenSample sample)
    {
        sample = default;
        if (source.Length < PenSampleSize)
        {
            return false;
        }

        var candidate = new PenSample(
            BinaryPrimitives.ReadUInt16BigEndian(source),
            BinaryPrimitives.ReadUInt16BigEndian(source[2..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[4..]),
            BinaryPrimitives.ReadInt16BigEndian(source[6..]),
            BinaryPrimitives.ReadInt16BigEndian(source[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[10..]),
            (PenTool)source[12],
            (PenState)source[13],
            (PenButtons)source[14]);
        if (!IsValid(candidate))
        {
            return false;
        }

        sample = candidate;
        return true;
    }

    /// <summary><c>u8 pointerId ‖ u8 state (Contact = 2) ‖ u16 x ‖ u16 y</c>.</summary>
    public static void WriteTouch(Span<byte> destination, in TouchSample sample)
    {
        destination = destination[..TouchSampleSize];
        destination[0] = sample.PointerId;
        destination[1] = sample.Contact ? TouchContact : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], sample.X);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], sample.Y);
    }

    /// <summary>Reads 6 bytes; false when truncated or the state has unknown bits.</summary>
    public static bool TryReadTouch(ReadOnlySpan<byte> source, out TouchSample sample)
    {
        sample = default;
        if (source.Length < TouchSampleSize || (source[1] & ~TouchContact) != 0)
        {
            return false;
        }

        sample = new TouchSample(
            source[0],
            source[1] == TouchContact,
            BinaryPrimitives.ReadUInt16BigEndian(source[2..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[4..]));
        return true;
    }

    /// <summary>True when every field is in its documented range and the tool is Pen, Eraser or FingerFallback.</summary>
    public static bool IsValid(in PenSample sample) =>
        sample.Tool is PenTool.Pen or PenTool.Eraser or PenTool.FingerFallback
        && (sample.State & ~KnownPenStates) == 0
        && (sample.Buttons & ~KnownButtons) == 0
        && sample.TiltX is >= -MaxTilt and <= MaxTilt
        && sample.TiltY is >= -MaxTilt and <= MaxTilt
        && sample.Rotation <= MaxRotation;
}
