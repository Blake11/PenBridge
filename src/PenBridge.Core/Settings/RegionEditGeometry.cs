using PenBridge.Core.Input;

namespace PenBridge.Core.Settings;

/// <summary>What a point over the region editor hits (ADR-0035 amendment, #243).</summary>
public enum RegionHandle
{
    /// <summary>Outside the selection: a drag there draws a new one.</summary>
    None,

    /// <summary>Inside the selection, away from its edges: a drag moves it.</summary>
    Inside,

    BottomLeft,
    Bottom,
    BottomRight,
    Right,
    TopRight,
    Top,
    TopLeft,
    Left,
}

/// <summary>
/// The geometry of the region editor (ADR-0035 amendment, #243): moving, resizing by 8 handles, drawing a new
/// selection, the aspect lock, hit-testing and where the floating bar goes. Platform-free; the Mac only draws.
/// </summary>
/// <remarks>
/// Every rect is a <see cref="ViewRect"/> of a view covering the display (bottom-left origin, y up, points), and every
/// result stays inside the display of <c>width</c> × <c>height</c>.
/// </remarks>
public static class RegionEditGeometry
{
    /// <summary>Smallest selection side in points, whatever the display's size.</summary>
    public const double MinSidePoints = 24;

    /// <summary>
    /// The smallest selection side, in points, on a display side of <paramref name="side"/> points: at least
    /// <see cref="MinSidePoints"/> and, rounded up, the key table's <see cref="MacSettings.MinRegionSize"/>, so a
    /// selection this big always normalizes to a valid region.
    /// </summary>
    public static double MinSide(double side) => Math.Max(MinSidePoints, Math.Ceiling(MacSettings.MinRegionSize * side));

    /// <summary>
    /// What (<paramref name="x"/>, <paramref name="y"/>) hits: a corner handle within <paramref name="tolerance"/> of
    /// a corner, an edge handle within it of an edge (along its whole length), the inside, or nothing. Corners win over
    /// edges and edges over the inside.
    /// </summary>
    public static RegionHandle HitTest(ViewRect selection, double x, double y, double tolerance)
    {
        double left = selection.X;
        double right = selection.X + selection.Width;
        double bottom = selection.Y;
        double top = selection.Y + selection.Height;
        if (x < left - tolerance || x > right + tolerance || y < bottom - tolerance || y > top + tolerance)
        {
            return RegionHandle.None;
        }

        bool nearLeft = Math.Abs(x - left) <= tolerance;
        bool nearRight = !nearLeft && Math.Abs(x - right) <= tolerance;
        bool nearBottom = Math.Abs(y - bottom) <= tolerance;
        bool nearTop = !nearBottom && Math.Abs(y - top) <= tolerance;
        return (nearLeft, nearRight, nearBottom, nearTop) switch
        {
            (true, _, true, _) => RegionHandle.BottomLeft,
            (true, _, _, true) => RegionHandle.TopLeft,
            (_, true, true, _) => RegionHandle.BottomRight,
            (_, true, _, true) => RegionHandle.TopRight,
            (true, _, _, _) => RegionHandle.Left,
            (_, true, _, _) => RegionHandle.Right,
            (_, _, true, _) => RegionHandle.Bottom,
            (_, _, _, true) => RegionHandle.Top,
            _ => x > left && x < right && y > bottom && y < top ? RegionHandle.Inside : RegionHandle.None,
        };
    }

    /// <summary>The selection moved by (<paramref name="dx"/>, <paramref name="dy"/>), its size kept, clamped onto the display.</summary>
    public static ViewRect Move(ViewRect start, double dx, double dy, double width, double height)
    {
        double w = Math.Min(start.Width, width);
        double h = Math.Min(start.Height, height);
        return new ViewRect(Math.Clamp(start.X + dx, 0, width - w), Math.Clamp(start.Y + dy, 0, height - h), w, h);
    }

    /// <summary>
    /// The selection resized by dragging <paramref name="handle"/> by (<paramref name="dx"/>, <paramref name="dy"/>)
    /// from where it was at the start of the drag. The opposite corner (or edge) stays put, the sides never get below
    /// <see cref="MinSide"/> (no flipping), and the result stays on the display.
    /// </summary>
    /// <param name="aspect">
    /// Width / height to keep, or null for a free resize. A corner keeps the opposite corner and follows the larger
    /// movement; an edge sets that side and centres the other on the selection's middle.
    /// </param>
    public static ViewRect Resize(ViewRect start, RegionHandle handle, double dx, double dy, double width, double height, double? aspect)
    {
        double minW = Math.Min(MinSide(width), width);
        double minH = Math.Min(MinSide(height), height);
        double left = start.X;
        double right = start.X + start.Width;
        double bottom = start.Y;
        double top = start.Y + start.Height;
        bool movesLeft = handle is RegionHandle.Left or RegionHandle.TopLeft or RegionHandle.BottomLeft;
        bool movesRight = handle is RegionHandle.Right or RegionHandle.TopRight or RegionHandle.BottomRight;
        bool movesBottom = handle is RegionHandle.Bottom or RegionHandle.BottomLeft or RegionHandle.BottomRight;
        bool movesTop = handle is RegionHandle.Top or RegionHandle.TopLeft or RegionHandle.TopRight;
        if (!(movesLeft || movesRight || movesBottom || movesTop))
        {
            return start;
        }

        if (aspect is not { } ratio || !(ratio > 0) || !double.IsFinite(ratio))
        {
            if (movesLeft)
            {
                left = Math.Clamp(left + dx, 0, Math.Max(0, right - minW));
            }
            else if (movesRight)
            {
                right = Math.Clamp(right + dx, Math.Min(width, left + minW), width);
            }

            if (movesBottom)
            {
                bottom = Math.Clamp(bottom + dy, 0, Math.Max(0, top - minH));
            }
            else if (movesTop)
            {
                top = Math.Clamp(top + dy, Math.Min(height, bottom + minH), height);
            }

            return new ViewRect(left, bottom, right - left, top - bottom);
        }

        // Aspect locked: the smallest width that keeps both sides at their minimum, and the largest that fits the
        // display. Room past the fixed corner or edge only limits the size down to the minimum; below that the rect is
        // shifted back onto the display instead, so the result never gets under the minimum.
        double maxWidth = Math.Min(width, height * ratio);
        double minWidth = Math.Min(Math.Max(minW, minH * ratio), maxWidth);
        bool corner = (movesLeft || movesRight) && (movesBottom || movesTop);
        if (corner)
        {
            // The opposite corner is the anchor.
            double anchorX = movesLeft ? right : left;
            double anchorY = movesBottom ? top : bottom;
            double w = movesLeft ? right - (left + dx) : right + dx - left;
            double h = movesBottom ? top - (bottom + dy) : top + dy - bottom;
            double roomX = movesLeft ? anchorX : width - anchorX;
            double roomY = movesBottom ? anchorY : height - anchorY;
            w = Math.Clamp(Math.Min(Math.Max(w, h * ratio), Math.Min(roomX, roomY * ratio)), minWidth, maxWidth);
            h = w / ratio;
            return OnDisplay(movesLeft ? anchorX - w : anchorX, movesBottom ? anchorY - h : anchorY, w, h, width, height);
        }

        if (movesLeft || movesRight)
        {
            // The opposite edge stays; the height follows, centred on the selection's middle.
            double w = movesLeft ? right - (left + dx) : right + dx - left;
            double room = movesLeft ? right : width - left;
            w = Math.Clamp(Math.Min(w, room), minWidth, maxWidth);
            double h = w / ratio;
            return OnDisplay(movesLeft ? right - w : left, start.Y + ((start.Height - h) / 2), w, h, width, height);
        }

        {
            double h = movesBottom ? top - (bottom + dy) : top + dy - bottom;
            double room = movesBottom ? top : height - bottom;
            h = Math.Clamp(Math.Min(h, room), minWidth / ratio, maxWidth / ratio);
            double w = h * ratio;
            return OnDisplay(start.X + ((start.Width - w) / 2), movesBottom ? top - h : bottom, w, h, width, height);
        }
    }

    // A rect no larger than the display, shifted onto it.
    private static ViewRect OnDisplay(double x, double y, double w, double h, double width, double height) =>
        new(Math.Clamp(x, 0, width - w), Math.Clamp(y, 0, height - h), w, h);

    /// <summary>
    /// A new selection dragged from the anchor (<paramref name="anchorX"/>, <paramref name="anchorY"/>) to
    /// (<paramref name="x"/>, <paramref name="y"/>), both clamped onto the display. With <paramref name="aspect"/> the
    /// larger movement wins, then the rect shrinks to stay on the display, keeping the anchor corner. It may be smaller
    /// than <see cref="MinSide"/> (check <see cref="IsBigEnough"/> when the drag ends).
    /// </summary>
    public static ViewRect Draw(double anchorX, double anchorY, double x, double y, double width, double height, double? aspect)
    {
        anchorX = Math.Clamp(anchorX, 0, width);
        anchorY = Math.Clamp(anchorY, 0, height);
        double dx = Math.Clamp(x, 0, width) - anchorX;
        double dy = Math.Clamp(y, 0, height) - anchorY;
        double w = Math.Abs(dx);
        double h = Math.Abs(dy);
        if (aspect is { } ratio && ratio > 0 && double.IsFinite(ratio))
        {
            w = Math.Max(w, h * ratio);
            h = w / ratio;
            double roomX = dx >= 0 ? width - anchorX : anchorX;
            double roomY = dy >= 0 ? height - anchorY : anchorY;
            double scale = Math.Min(1, Math.Min(w > 0 ? roomX / w : 1, h > 0 ? roomY / h : 1));
            w *= scale;
            h *= scale;
        }

        return new ViewRect(dx >= 0 ? anchorX : anchorX - w, dy >= 0 ? anchorY : anchorY - h, w, h);
    }

    /// <summary>True when both sides reach <see cref="MinSide"/> (or the whole display side, if that is smaller).</summary>
    public static bool IsBigEnough(ViewRect selection, double width, double height) =>
        selection.Width >= Math.Min(MinSide(width), width) && selection.Height >= Math.Min(MinSide(height), height);

    /// <summary>
    /// The selection given the aspect ratio when the lock is turned on: the largest rect of that ratio inside it,
    /// centred, grown to <see cref="MinSide"/> if needed and kept on the display.
    /// </summary>
    public static ViewRect ApplyAspect(ViewRect selection, double aspect, double width, double height)
    {
        if (!(aspect > 0) || !double.IsFinite(aspect))
        {
            return selection;
        }

        double w = Math.Min(selection.Width, selection.Height * aspect);
        double minWidth = Math.Max(Math.Min(MinSide(width), width), Math.Min(MinSide(height), height) * aspect);
        w = Math.Min(Math.Max(w, minWidth), Math.Min(width, height * aspect));
        double h = w / aspect;
        double x = Math.Clamp(selection.X + ((selection.Width - w) / 2), 0, width - w);
        double y = Math.Clamp(selection.Y + ((selection.Height - h) / 2), 0, height - h);
        return new ViewRect(x, y, w, h);
    }

    /// <summary>
    /// Where the floating bar of <paramref name="barWidth"/> × <paramref name="barHeight"/> goes: centred under the
    /// selection, <paramref name="gap"/> below it; above it when there's no room below; else inside it at its bottom.
    /// Horizontally it is kept <paramref name="gap"/> inside the display.
    /// </summary>
    public static ViewRect PlaceBar(ViewRect selection, double barWidth, double barHeight, double width, double height, double gap)
    {
        double x = selection.X + ((selection.Width - barWidth) / 2);
        x = Math.Max(Math.Min(x, width - gap - barWidth), Math.Min(gap, width - barWidth));
        double below = selection.Y - gap - barHeight;
        double above = selection.Y + selection.Height + gap;
        double y;
        if (below >= gap)
        {
            y = below;
        }
        else if (above + barHeight <= height - gap)
        {
            y = above;
        }
        else
        {
            y = Math.Clamp(selection.Y + gap, 0, Math.Max(0, height - barHeight));
        }

        return new ViewRect(x, y, barWidth, barHeight);
    }

    /// <summary>
    /// The selection as a region: normalized to the display, flipped to a top-left origin (y down), clamped into 0..1.
    /// The inverse of <see cref="RegionTintRule.HoleInView"/>.
    /// </summary>
    public static NormalizedRect ToRegion(ViewRect selection, double width, double height)
    {
        double x = Math.Clamp(selection.X / width, 0, 1);
        double w = Math.Clamp(selection.Width / width, 0, 1 - x);
        double y = Math.Clamp((height - selection.Y - selection.Height) / height, 0, 1);
        double h = Math.Clamp(selection.Height / height, 0, 1 - y);
        return new NormalizedRect(x, y, w, h);
    }
}
