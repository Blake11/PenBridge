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

/// <summary>
/// Side-effect free conversions from Core's wire units to CGEvent units. Kept static and pure so they can move to a
/// test project unchanged.
/// </summary>
internal static class TabletMath
{
    /// <summary>Normalized pressure 0..65535 to the CGEvent 0..1 range.</summary>
    public static double Pressure(ushort pressure) => pressure / 65535.0;

    /// <summary>
    /// Tilt in centi-degrees (−9000..9000) to the CGEvent −1..1 range (±90° = ±1), clamped. Core's tilt axes follow
    /// the surface (x right, y down), which is what <c>kCGTabletEventTiltX/Y</c> expect (y positive toward the user).
    /// </summary>
    public static double Tilt(short centiDegrees)
    {
        double t = centiDegrees / 9000.0;
        return t > 1 ? 1 : t < -1 ? -1 : t;
    }

    /// <summary>Barrel rotation in centi-degrees (0..35999) to degrees, the unit of <c>kCGTabletEventRotation</c>.</summary>
    public static double RotationDegrees(ushort centiDegrees) => centiDegrees / 100.0;

    /// <summary>Proximity pointer type: the eraser end is reported as eraser; pen and finger fallback as pen.</summary>
    public static long PointerType(PenTool tool) =>
        tool == PenTool.Eraser ? TabletConstants.PointerTypeEraser : TabletConstants.PointerTypePen;

    /// <summary>Converts <see cref="System.Diagnostics.Stopwatch"/> ticks to nanoseconds.</summary>
    /// <remarks>
    /// CGEvent timestamps are nanoseconds on the uptime clock (CLOCK_UPTIME_RAW, the one NSEvent.timestamp reads in
    /// seconds); on macOS <see cref="System.Diagnostics.Stopwatch"/> reads that same clock, usually at 1 GHz.
    /// </remarks>
    public static long StopwatchToNanoseconds(long ticks, long frequency) =>
        frequency == 1_000_000_000 ? ticks : (long)(ticks * (1e9 / frequency));

    /// <summary>
    /// Maps a session timestamp (µs, phone clock) onto the CGEvent clock (ns, Mac uptime) and updates the anchor.
    /// </summary>
    /// <remarks>
    /// The phone and Mac clocks are unrelated, so the mapping is an offset learned from arrivals:
    /// <list type="bullet">
    /// <item>The first sample (or one that would land in the future, i.e. arrived faster than before) re-anchors so it
    /// maps to <paramref name="nowNs"/>. The offset therefore converges on the lowest observed latency, and sample
    /// spacing within a batch (history samples) is preserved.</item>
    /// <item>A sample older than <paramref name="maxLagNs"/> re-anchors too: a stall, a new session (its clock restarts
    /// at 0) or clock drift must not leave events far in the past.</item>
    /// <item>The result never goes backwards.</item>
    /// </list>
    /// </remarks>
    public static long MapTimestamp(ref ClockAnchor anchor, long sampleUs, long nowNs, long maxLagNs)
    {
        long sampleNs = sampleUs * 1000;
        long mapped = sampleNs + anchor.OffsetNs;
        if (!anchor.IsSet || mapped > nowNs || mapped < nowNs - maxLagNs)
        {
            anchor.OffsetNs = nowNs - sampleNs;
            anchor.IsSet = true;
            mapped = nowNs;
        }

        if (mapped < anchor.LastNs)
        {
            mapped = anchor.LastNs;
        }

        anchor.LastNs = mapped;
        return mapped;
    }

    /// <summary>
    /// The click count (<c>kCGMouseEventClickState</c>) for a new button press: one more than the previous press when it
    /// follows within <paramref name="intervalNs"/> (the system double-click interval) and <paramref name="slop"/>
    /// points, else 1. Without it, a pen double-tap never registers as a double-click.
    /// </summary>
    public static long NextClickCount(ref ClickState state, long nowNs, DisplayPoint at, long intervalNs, double slop)
    {
        double dx = at.X - state.Position.X;
        double dy = at.Y - state.Position.Y;
        bool repeat = state.Count > 0 && nowNs - state.TimeNs <= intervalNs && (dx * dx) + (dy * dy) <= slop * slop;
        state.Count = repeat ? state.Count + 1 : 1;
        state.TimeNs = nowNs;
        state.Position = at;
        return state.Count;
    }
}

/// <summary>State of <see cref="TabletMath.MapTimestamp"/>.</summary>
internal struct ClockAnchor
{
    public bool IsSet;
    public long OffsetNs;
    public long LastNs;
}

/// <summary>State of <see cref="TabletMath.NextClickCount"/> for one button.</summary>
internal struct ClickState
{
    public long Count;
    public long TimeNs;
    public DisplayPoint Position;
}
