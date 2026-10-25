using PenBridge.Core.Input;

namespace PenBridge.Android.Mirror;

/// <summary>
/// The active area in whole surface pixels. The video surface and the dimmed margins around it both use this one
/// rounding, so the image edge and the margin edge are the same pixel (ADR-0038: within 1 phone pixel of the pen).
/// </summary>
internal static class MirrorGeometry
{
    /// <summary>
    /// Pixel rect (left, top, right, bottom; right/bottom exclusive) of <paramref name="area"/> on a
    /// <paramref name="width"/>×<paramref name="height"/> surface. Empty when the surface has no size.
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom) ActivePixels(NormalizedRect area, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return (0, 0, 0, 0);
        }

        int left = Clamp(area.X * width, width);
        int top = Clamp(area.Y * height, height);
        int right = Clamp((area.X + area.Width) * width, width);
        int bottom = Clamp((area.Y + area.Height) * height, height);
        return right <= left || bottom <= top ? (0, 0, width, height) : (left, top, right, bottom);
    }

    private static int Clamp(double v, int max) =>
        double.IsFinite(v) ? Math.Clamp((int)Math.Round(v, MidpointRounding.AwayFromZero), 0, max) : 0;
}
