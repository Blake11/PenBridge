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
/// Maps normalized phone-surface coordinates (0..65535 on each axis, as currently oriented on the phone) onto a
/// display or display sub-region, preserving the surface aspect ratio with a centered letterbox (wide surface) or
/// pillarbox (tall surface). Results are clamped to the content rectangle; the right/bottom edges map just inside
/// it so a point never lands on the neighbouring display.
/// </summary>
/// <remarks>
/// Pass the surface size for the orientation the normalized coordinates refer to (portrait: width &lt; height).
/// When the phone rotates, build a new mapper from the new SurfaceInfo. Immutable, allocation-free.
/// </remarks>
public readonly struct CoordinateMapper
{
    /// <summary>The largest normalized coordinate.</summary>
    public const double NormalizedMax = 65535.0;

    private readonly double _left;
    private readonly double _top;
    private readonly double _width;
    private readonly double _height;
    private readonly double _maxX;
    private readonly double _maxY;

    public CoordinateMapper(double surfaceWidth, double surfaceHeight, DisplayRect display)
        : this(surfaceWidth, surfaceHeight, display, NormalizedRect.Full)
    {
    }

    /// <param name="surfaceWidth">Phone surface width (pixels or any unit; only the ratio matters).</param>
    /// <param name="surfaceHeight">Phone surface height, same unit.</param>
    /// <param name="display">Target display bounds in global display points.</param>
    /// <param name="region">Optional sub-region of <paramref name="display"/>, in fractions of it.</param>
    public CoordinateMapper(double surfaceWidth, double surfaceHeight, DisplayRect display, NormalizedRect region)
    {
        RequirePositive(surfaceWidth, nameof(surfaceWidth));
        RequirePositive(surfaceHeight, nameof(surfaceHeight));
        RequirePositive(display.Width, nameof(display));
        RequirePositive(display.Height, nameof(display));
        if (!double.IsFinite(display.X) || !double.IsFinite(display.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(display), "Display origin must be finite.");
        }

        const double Epsilon = 1e-9;
        if (!(region.X >= 0) || !(region.Y >= 0) || !(region.Width > 0) || !(region.Height > 0)
            || region.X + region.Width > 1 + Epsilon || region.Y + region.Height > 1 + Epsilon)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Region must be a non-empty rectangle inside 0..1.");
        }

        var target = new DisplayRect(
            display.X + (region.X * display.Width),
            display.Y + (region.Y * display.Height),
            region.Width * display.Width,
            region.Height * display.Height);
        TargetRect = target;

        double surfaceAspect = surfaceWidth / surfaceHeight;
        double targetAspect = target.Width / target.Height;
        if (surfaceAspect >= targetAspect)
        {
            // Surface is relatively wider: full width, bars top and bottom (letterbox).
            _width = target.Width;
            _height = target.Width / surfaceAspect;
            _left = target.X;
            _top = target.Y + ((target.Height - _height) / 2);
        }
        else
        {
            // Surface is relatively taller: full height, bars left and right (pillarbox).
            _height = target.Height;
            _width = target.Height * surfaceAspect;
            _top = target.Y;
            _left = target.X + ((target.Width - _width) / 2);
        }

        _maxX = Math.BitDecrement(_left + _width);
        _maxY = Math.BitDecrement(_top + _height);
    }

    /// <summary>Maps normalized coordinates to the unit square (0..1); used until a real surface/display is known.</summary>
    public static CoordinateMapper Identity => new(1, 1, new DisplayRect(0, 0, 1, 1));

    /// <summary>False for <c>default(CoordinateMapper)</c>.</summary>
    public bool IsConfigured => _width > 0;

    /// <summary>The display region the surface is fitted into.</summary>
    public DisplayRect TargetRect { get; }

    /// <summary>The aspect-preserving area the surface actually covers (inside <see cref="TargetRect"/>).</summary>
    public DisplayRect ContentRect => new(_left, _top, _width, _height);

    /// <summary>Display points per normalized unit on the X axis.</summary>
    public double ScaleX => _width / NormalizedMax;

    /// <summary>Display points per normalized unit on the Y axis.</summary>
    public double ScaleY => _height / NormalizedMax;

    /// <summary>The shorter side of <see cref="ContentRect"/>, in display points.</summary>
    public double ShortSide => Math.Min(_width, _height);

    public DisplayPoint Map(ushort x, ushort y) => Map((double)x, y);

    /// <summary>Maps fractional normalized coordinates (0..65535); out-of-range and NaN inputs are clamped.</summary>
    public DisplayPoint Map(double x, double y)
    {
        double px = _left + (Clamp01(x / NormalizedMax) * _width);
        double py = _top + (Clamp01(y / NormalizedMax) * _height);
        return new DisplayPoint(Math.Min(px, _maxX), Math.Min(py, _maxY));
    }

    private static double Clamp01(double v) => v > 0 ? (v < 1 ? v : 1) : 0;

    private static void RequirePositive(double value, string name)
    {
        if (!(value > 0) || !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, "Must be a finite positive number.");
        }
    }
}
