using PenBridge.Core.Input;

namespace PenBridge.Mac.Input;

/// <summary>
/// Constants for CGEvent tablet fields. Values come from IOKit's <c>IOLLEvent.h</c> (<c>NX_TABLET_*</c>), which the
/// CGEvent tablet fields reuse; <see cref="CoreGraphics.CGEventField"/> itself is bound by .NET for macOS.
/// </summary>
internal static class TabletConstants
{
    /// <summary><c>NX_TABLET_POINTER_PEN</c>: <c>kCGTabletProximityEventPointerType</c> for the pen tip.</summary>
    public const long PointerTypePen = 1;

    /// <summary><c>NX_TABLET_POINTER_ERASER</c>: the eraser end (apps switch to their eraser tool).</summary>
    public const long PointerTypeEraser = 3;

    /// <summary><c>kCGTabletEventPointButtons</c> bit for the tip (<c>NX_TABLET_BUTTON_PENTIPMASK</c>).</summary>
    public const long ButtonTip = 0x0001;

    /// <summary>Lower side switch (<c>NX_TABLET_BUTTON_PENLOWERSIDEMASK</c>), set while the side button is down.</summary>
    public const long ButtonLowerSide = 0x0002;

    /// <summary>
    /// <c>kCGTabletProximityEventCapabilityMask</c>: device id, absolute X/Y, buttons, tilt X/Y, pressure and rotation
    /// (<c>NX_TABLET_CAPABILITY_*MASK</c> 0x0001 | 0x0002 | 0x0004 | 0x0040 | 0x0080 | 0x0100 | 0x0400 | 0x2000).
    /// </summary>
    public const long CapabilityMask = 0x0001 | 0x0002 | 0x0004 | 0x0040 | 0x0080 | 0x0100 | 0x0400 | 0x2000;

    /// <summary>
    /// Stable device id: <c>kCGTabletProximityEventDeviceId</c> on proximity events and <c>kCGTabletEventDeviceId</c>
    /// on tablet-point events must match so apps tie points to the proximity that announced the pointer.
    /// </summary>
    public const long DeviceId = 0x5042; // "PB"

    /// <summary>Stable pointer id (<c>kCGTabletProximityEventPointerId</c>); one virtual pen.</summary>
    public const long PointerId = 1;

    /// <summary>System/vendor tablet id (<c>kCGTabletProximityEventSystemTabletId</c>, <c>…TabletId</c>).</summary>
    public const long TabletId = 1;

    /// <summary>
    /// <c>kCGTabletProximityEventVendorPointerType</c>: Wacom's "general stylus" code (0x0802). Toolkits such as Qt
    /// (Krita) classify the device from this field; the pen and eraser end share it, as on a real pen.
    /// </summary>
    public const long VendorPointerType = 0x0802;

    /// <summary>Stable non-zero <c>kCGTabletProximityEventVendorUniqueId</c>; Qt keys its tablet devices on it.</summary>
    public const long VendorUniqueId = 0x50656E4272696467; // "PenBridg"
}

/// <summary>CGEvent-specific mapping; the platform-free math lives in Core's <see cref="TabletEventMath"/>.</summary>
internal static class TabletMath
{
    /// <summary>Proximity pointer type: the eraser end is reported as eraser; pen and finger fallback as pen.</summary>
    public static long PointerType(PenTool tool) =>
        tool == PenTool.Eraser ? TabletConstants.PointerTypeEraser : TabletConstants.PointerTypePen;
}
