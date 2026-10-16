namespace PenBridge.Core.Input;

/// <summary>A point in global display points (macOS global coordinates: top-left origin, y down).</summary>
public readonly record struct DisplayPoint(double X, double Y);

/// <summary>A rectangle in global display points.</summary>
public readonly record struct DisplayRect(double X, double Y, double Width, double Height);

/// <summary>A rectangle in fractions (0..1) of another rectangle.</summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public static NormalizedRect Full => new(0, 0, 1, 1);
}

/// <summary>
/// What the pen maps onto (ADR-0031): one display with an optional region of it, or all displays (their bounding box,
/// one desktop). Immutable.
/// </summary>
public sealed class MappingTarget
{
    /// <summary>Most displays an all-displays target keeps.</summary>
    public const int MaxDisplays = 32;

    private readonly DisplayRect[] _displays;

    private MappingTarget(DisplayRect[] displays, DisplayRect bounds, NormalizedRect region, bool all)
    {
        _displays = displays;
        Bounds = bounds;
        Region = region;
        IsAllDisplays = all;
    }

    /// <summary>True for the all-displays target.</summary>
    public bool IsAllDisplays { get; }

    /// <summary>The region of the display (<see cref="NormalizedRect.Full"/> for all displays).</summary>
    public NormalizedRect Region { get; }

    /// <summary>The rectangle the active area maps onto: the display's region, or the bounding box of all displays.</summary>
    public DisplayRect Bounds { get; }

    /// <summary>The displays a point is kept on: the one display (whole, not just the region), or all of them.</summary>
    public ReadOnlySpan<DisplayRect> Displays => _displays;

    /// <summary>One display, or a region of it (fractions of the display).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The display is empty or not finite, or the region is not inside 0..1.</exception>
    public static MappingTarget ForDisplay(DisplayRect display, NormalizedRect region)
    {
        RequireDisplay(display, nameof(display));
        const double Epsilon = 1e-9;
        if (!(region.X >= 0) || !(region.Y >= 0) || !(region.Width > 0) || !(region.Height > 0)
            || region.X + region.Width > 1 + Epsilon || region.Y + region.Height > 1 + Epsilon)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Region must be a non-empty rectangle inside 0..1.");
        }

        var bounds = new DisplayRect(
            display.X + (region.X * display.Width),
            display.Y + (region.Y * display.Height),
            region.Width * display.Width,
            region.Height * display.Height);
        return new MappingTarget([display], bounds, region, all: false);
    }

    /// <summary>One whole display.</summary>
    public static MappingTarget ForDisplay(DisplayRect display) => ForDisplay(display, NormalizedRect.Full);

    /// <summary>All displays as one desktop: the bounding box; points in a gap go onto the nearest display.</summary>
    /// <exception cref="ArgumentOutOfRangeException">No displays, more than <see cref="MaxDisplays"/>, or an invalid one.</exception>
    public static MappingTarget ForAllDisplays(ReadOnlySpan<DisplayRect> displays)
    {
        if (displays.IsEmpty || displays.Length > MaxDisplays)
        {
            throw new ArgumentOutOfRangeException(nameof(displays), $"Needs 1..{MaxDisplays} displays.");
        }

        double left = double.MaxValue;
        double top = double.MaxValue;
        double right = double.MinValue;
        double bottom = double.MinValue;
        foreach (DisplayRect d in displays)
        {
            RequireDisplay(d, nameof(displays));
            left = Math.Min(left, d.X);
            top = Math.Min(top, d.Y);
            right = Math.Max(right, d.X + d.Width);
            bottom = Math.Max(bottom, d.Y + d.Height);
        }

        return new MappingTarget(displays.ToArray(), new DisplayRect(left, top, right - left, bottom - top), NormalizedRect.Full, all: true);
    }

    /// <summary>
    /// The point itself when it lies on a display (right and bottom edges excluded), else the nearest point on one, so
    /// it never lands off-screen or on a neighbouring display. Allocation-free.
    /// </summary>
    public DisplayPoint ClampToDisplays(DisplayPoint p)
    {
        DisplayRect[] displays = _displays;
        double bestX = p.X;
        double bestY = p.Y;
        double best = double.MaxValue;
        for (int i = 0; i < displays.Length; i++)
        {
            DisplayRect d = displays[i];
            double x = Clamp(p.X, d.X, Math.BitDecrement(d.X + d.Width));
            double y = Clamp(p.Y, d.Y, Math.BitDecrement(d.Y + d.Height));
            double dx = x - p.X;
            double dy = y - p.Y;
            double distance = (dx * dx) + (dy * dy);
            if (distance < best)
            {
                best = distance;
                bestX = x;
                bestY = y;
                if (distance == 0)
                {
                    break;
                }
            }
        }

        return new DisplayPoint(bestX, bestY);
    }

    private static double Clamp(double v, double min, double max) => v > min ? (v < max ? v : max) : min;

    private static void RequireDisplay(DisplayRect display, string name)
    {
        if (!(display.Width > 0) || !(display.Height > 0) || !double.IsFinite(display.Width) || !double.IsFinite(display.Height)
            || !double.IsFinite(display.X) || !double.IsFinite(display.Y))
        {
            throw new ArgumentOutOfRangeException(name, "Display must have a finite origin and a finite positive size.");
        }
    }
}

/// <summary>
/// Maps normalized phone-surface coordinates (0..65535 on each axis, as currently oriented on the phone) onto a
/// <see cref="MappingTarget"/> (ADR-0031). The <see cref="ActiveArea"/> is the largest rectangle with the target's aspect
/// ratio, centred on the surface; it maps onto the whole target, so every point of the target is reachable and strokes
/// keep their shape. A point outside it is pinned to the nearest edge of the target. Results stay on a display: the
/// right/bottom edges map just inside them, and with all displays a point in a gap goes onto the nearest display.
/// </summary>
/// <remarks>
/// Pass the surface size for the orientation the normalized coordinates refer to (portrait: width &lt; height).
/// When the phone rotates, build a new mapper from the new SurfaceInfo. Immutable, allocation-free.
/// </remarks>
public readonly struct CoordinateMapper
{
    /// <summary>The largest normalized coordinate.</summary>
    public const double NormalizedMax = 65535.0;

    private readonly MappingTarget? _target;
    private readonly NormalizedRect _active;
    private readonly DisplayRect _bounds;
    private readonly double _maxX;
    private readonly double _maxY;

    public CoordinateMapper(double surfaceWidth, double surfaceHeight, DisplayRect display)
        : this(surfaceWidth, surfaceHeight, MappingTarget.ForDisplay(display, NormalizedRect.Full))
    {
    }

    /// <param name="surfaceWidth">Phone surface width (pixels or any unit; only the ratio matters).</param>
    /// <param name="surfaceHeight">Phone surface height, same unit.</param>
    /// <param name="display">Target display bounds in global display points.</param>
    /// <param name="region">Sub-region of <paramref name="display"/>, in fractions of it.</param>
    public CoordinateMapper(double surfaceWidth, double surfaceHeight, DisplayRect display, NormalizedRect region)
        : this(surfaceWidth, surfaceHeight, MappingTarget.ForDisplay(display, region))
    {
    }

    /// <param name="surfaceWidth">Phone surface width (pixels or any unit; only the ratio matters).</param>
    /// <param name="surfaceHeight">Phone surface height, same unit.</param>
    /// <param name="target">The display (and region) or all displays.</param>
    public CoordinateMapper(double surfaceWidth, double surfaceHeight, MappingTarget target)
    {
        RequirePositive(surfaceWidth, nameof(surfaceWidth));
        RequirePositive(surfaceHeight, nameof(surfaceHeight));
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _bounds = target.Bounds;
        _active = ActiveAreaFor(surfaceWidth, surfaceHeight, _bounds.Width, _bounds.Height);
        _maxX = Math.BitDecrement(_bounds.X + _bounds.Width);
        _maxY = Math.BitDecrement(_bounds.Y + _bounds.Height);
    }

    /// <summary>Maps normalized coordinates to the unit square (0..1); used until a real surface/display is known.</summary>
    public static CoordinateMapper Identity => new(1, 1, new DisplayRect(0, 0, 1, 1));

    /// <summary>False for <c>default(CoordinateMapper)</c>.</summary>
    public bool IsConfigured => _target is not null;

    /// <summary>The target this mapper maps onto (null for <c>default</c>).</summary>
    public MappingTarget? Target => _target;

    /// <summary>The rectangle the active area maps onto (the display region, or the bounding box of all displays).</summary>
    public DisplayRect TargetRect => _bounds;

    /// <summary>
    /// The active part of the surface, in fractions of it (current orientation): the largest rectangle with the
    /// target's aspect ratio, centred. It maps onto <see cref="TargetRect"/>.
    /// </summary>
    public NormalizedRect ActiveArea => _active;

    /// <summary>
    /// Where the whole surface would lie at the mapping's scale, in display points: <see cref="TargetRect"/> extended
    /// by the margins outside the active area (so it may reach beyond the target).
    /// </summary>
    public DisplayRect ContentRect => new(
        _bounds.X - (_active.X / _active.Width * _bounds.Width),
        _bounds.Y - (_active.Y / _active.Height * _bounds.Height),
        _bounds.Width / _active.Width,
        _bounds.Height / _active.Height);

    /// <summary>Display points per normalized unit on the X axis.</summary>
    public double ScaleX => _bounds.Width / (_active.Width * NormalizedMax);

    /// <summary>Display points per normalized unit on the Y axis.</summary>
    public double ScaleY => _bounds.Height / (_active.Height * NormalizedMax);

    /// <summary>The whole surface's shorter side in display points (<see cref="ContentRect"/>): gesture distances scale with it.</summary>
    public double ShortSide => Math.Min(_bounds.Width / _active.Width, _bounds.Height / _active.Height);

    /// <summary>
    /// The active area of a surface for a target size: the largest rectangle with the target's aspect ratio, centred,
    /// in fractions of the surface.
    /// </summary>
    public static NormalizedRect ActiveAreaFor(double surfaceWidth, double surfaceHeight, double targetWidth, double targetHeight)
    {
        double surfaceAspect = surfaceWidth / surfaceHeight;
        double targetAspect = targetWidth / targetHeight;
        if (surfaceAspect >= targetAspect)
        {
            // The surface is relatively wider: full height, margins left and right.
            double w = targetAspect / surfaceAspect;
            return new NormalizedRect((1 - w) / 2, 0, w, 1);
        }

        // The surface is relatively taller: full width, margins top and bottom.
        double h = surfaceAspect / targetAspect;
        return new NormalizedRect(0, (1 - h) / 2, 1, h);
    }

    public DisplayPoint Map(ushort x, ushort y) => Map((double)x, y);

    /// <summary>
    /// Maps fractional normalized coordinates (0..65535) for the pen: points outside the active area (and out-of-range
    /// or NaN inputs) are pinned to the nearest edge of the target, and the result is kept on a display.
    /// </summary>
    public DisplayPoint Map(double x, double y)
    {
        double u = Clamp01(((x / NormalizedMax) - _active.X) / _active.Width);
        double v = Clamp01(((y / NormalizedMax) - _active.Y) / _active.Height);
        double px = Math.Min(_bounds.X + (u * _bounds.Width), _maxX);
        double py = Math.Min(_bounds.Y + (v * _bounds.Height), _maxY);
        var p = new DisplayPoint(px, py);
        return _target!.IsAllDisplays ? _target.ClampToDisplays(p) : p;
    }

    /// <summary>
    /// Maps without pinning or clamping (linear, extends beyond the target): for gesture measurements, whose deltas
    /// and distances must not stop at the active area's edge. Not a position to post.
    /// </summary>
    public DisplayPoint MapUnclamped(double x, double y) => new(
        _bounds.X + ((((x / NormalizedMax) - _active.X) / _active.Width) * _bounds.Width),
        _bounds.Y + ((((y / NormalizedMax) - _active.Y) / _active.Height) * _bounds.Height));

    /// <summary>Keeps a point on the target's displays (see <see cref="MappingTarget.ClampToDisplays"/>).</summary>
    public DisplayPoint ClampToDisplays(DisplayPoint p) => _target!.ClampToDisplays(p);

    private static double Clamp01(double v) => v > 0 ? (v < 1 ? v : 1) : 0;

    private static void RequirePositive(double value, string name)
    {
        if (!(value > 0) || !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, "Must be a finite positive number.");
        }
    }
}
