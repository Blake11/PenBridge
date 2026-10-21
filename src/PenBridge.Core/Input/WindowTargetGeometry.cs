namespace PenBridge.Core.Input;

/// <summary>
/// A window as the mapping target (ADR-0036): the window's frame is clipped to the display that holds most of it and
/// normalized to that display, so it maps like a region of it (<see cref="MappingTarget.ForDisplay(DisplayRect, NormalizedRect)"/>).
/// </summary>
/// <remarks>All rectangles are in global display points with a top-left origin, y down (CoreGraphics' and the mapper's
/// convention). A window spanning two displays keeps only its part on the display with the larger share.</remarks>
public static class WindowTargetGeometry
{
    /// <summary>Smallest visible part of a window, in points per side, that still counts as on a display.</summary>
    public const double MinVisibleSide = 1;

    /// <summary>
    /// The index of the display holding the largest part of <paramref name="window"/>, or -1 when no display holds a
    /// part of at least <see cref="MinVisibleSide"/> points per side. On a tie the earlier display wins.
    /// </summary>
    public static int DisplayIndexFor(DisplayRect window, ReadOnlySpan<DisplayRect> displays)
    {
        int best = -1;
        double bestArea = 0;
        for (int i = 0; i < displays.Length; i++)
        {
            if (Intersect(window, displays[i], out DisplayRect part))
            {
                double area = part.Width * part.Height;
                if (area > bestArea)
                {
                    best = i;
                    bestArea = area;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The part of <paramref name="window"/> on <paramref name="display"/>, as fractions of the display (inside 0..1).
    /// False when that part is smaller than <see cref="MinVisibleSide"/> points per side or the display is empty.
    /// </summary>
    public static bool TryRegion(DisplayRect window, DisplayRect display, out NormalizedRect region)
    {
        region = NormalizedRect.Full;
        if (!(display.Width > 0) || !(display.Height > 0) || !Intersect(window, display, out DisplayRect part))
        {
            return false;
        }

        double x = Math.Clamp((part.X - display.X) / display.Width, 0, 1);
        double y = Math.Clamp((part.Y - display.Y) / display.Height, 0, 1);
        double w = Math.Min(part.Width / display.Width, 1 - x);
        double h = Math.Min(part.Height / display.Height, 1 - y);
        if (!(w > 0) || !(h > 0))
        {
            return false;
        }

        region = new NormalizedRect(x, y, w, h);
        return true;
    }

    /// <summary>
    /// <see cref="DisplayIndexFor"/> and <see cref="TryRegion"/> together: the display holding most of the window and
    /// the window's part on it. False (index -1) when the window is on no display.
    /// </summary>
    public static bool TryFit(DisplayRect window, ReadOnlySpan<DisplayRect> displays, out int displayIndex, out NormalizedRect region)
    {
        displayIndex = DisplayIndexFor(window, displays);
        if (displayIndex < 0 || !TryRegion(window, displays[displayIndex], out region))
        {
            displayIndex = -1;
            region = NormalizedRect.Full;
            return false;
        }

        return true;
    }

    private static bool Intersect(DisplayRect a, DisplayRect b, out DisplayRect part)
    {
        double left = Math.Max(a.X, b.X);
        double top = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.X + a.Width, b.X + b.Width);
        double bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        part = new DisplayRect(left, top, right - left, bottom - top);

        // NaN-safe: a non-finite rectangle never intersects.
        return right - left >= MinVisibleSide && bottom - top >= MinVisibleSide;
    }
}
