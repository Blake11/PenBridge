namespace PenBridge.Core.Input;

/// <summary>
/// Converts <see cref="GestureEvent"/> values (display coordinates, y down) to the conventions of host scroll and
/// gesture events, so a head only converts units and posts. Pure functions, allocation-free.
/// </summary>
public static class GestureOutput
{
    /// <summary>
    /// Sign that turns finger movement into a scroll-wheel delta: +1 with natural scrolling (content follows the
    /// fingers), −1 without (content moves against them, like a classic wheel).
    /// </summary>
    public static int ScrollDirectionSign(bool naturalScrolling) => naturalScrolling ? 1 : -1;

    /// <summary>
    /// Scroll-wheel deltas for a finger movement (a scroll event's DeltaX/DeltaY or a <see cref="ScrollMomentum"/>
    /// step). Result convention, as for macOS scroll-wheel events: a positive <paramref name="wheelY"/> scrolls up, so
    /// the content moves down; a positive <paramref name="wheelX"/> scrolls left, so the content moves right.
    /// With natural scrolling, fingers moving down (positive DeltaY) give a positive wheel delta.
    /// </summary>
    public static void ToScrollWheel(double fingerDx, double fingerDy, bool naturalScrolling, out double wheelX, out double wheelY)
    {
        int sign = ScrollDirectionSign(naturalScrolling);
        wheelX = sign * fingerDx;
        wheelY = sign * fingerDy;
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to <paramref name="remainder"/> and takes out the whole units (rounded toward
    /// zero), so integer-only event fields lose no sub-unit movement across a gesture. Reset the remainder to 0 at
    /// each gesture start. Non-finite deltas are ignored.
    /// </summary>
    public static long TakeWhole(double delta, ref double remainder)
    {
        if (!double.IsFinite(delta))
        {
            return 0;
        }

        remainder += delta;
        double whole = Math.Truncate(remainder);
        remainder -= whole;
        return (long)whole;
    }

    /// <summary>
    /// Additive magnification for a multiplicative <see cref="GestureEvent.ScaleDelta"/> (the trackpad convention:
    /// 0 is no change, positive zooms in).
    /// </summary>
    public static double ToMagnification(double scaleDelta) => scaleDelta - 1;

    /// <summary>
    /// Counterclockwise-positive degrees (the trackpad rotate convention) for a <see cref="GestureEvent.RotationDelta"/>,
    /// which is clockwise-positive on screen.
    /// </summary>
    public static double ToCounterClockwiseDegrees(double rotationDelta) => -rotationDelta;
}
