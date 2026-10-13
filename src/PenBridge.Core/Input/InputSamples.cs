using System.Runtime.InteropServices;

namespace PenBridge.Core.Input;

/// <summary>Pen tool reported by the phone.</summary>
public enum PenTool : byte
{
    None = 0,
    Pen = 1,
    Eraser = 2,
    /// <summary>Finger used as a pen when no stylus is present (synthetic pressure).</summary>
    FingerFallback = 3,
}

[Flags]
public enum PenState : byte
{
    None = 0,
    InRange = 1,
    Contact = 2,
}

[Flags]
public enum PenButtons : byte
{
    None = 0,
    Primary = 1,
    Secondary = 2,
}

/// <summary>
/// One pen sample. Positions are normalized 0..65535 over the phone surface; tilt is centi-degrees
/// (-9000..9000); rotation is centi-degrees (0..35999). Wire size is 16 bytes (see docs/specs/phase-1-core.md).
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct PenSample(
    ushort X,
    ushort Y,
    ushort Pressure,
    short TiltX,
    short TiltY,
    ushort Rotation,
    PenTool Tool,
    PenState State,
    PenButtons Buttons);

/// <summary>One active touch pointer. Positions are normalized 0..65535. Wire size is 6 bytes.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct TouchSample(byte PointerId, bool Contact, ushort X, ushort Y);
