using CsCheck;
using PenBridge.Core.Input;
using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Properties;

/// <summary>
/// Shared CsCheck generators and settings for the property tests. CsCheck picks a random seed per run and prints it on
/// failure ("Set seed: ..."); pass that string as <c>seed:</c> to <c>Sample</c> (or set the <c>CsCheck_Seed</c>
/// environment variable) to replay the shrunk case.
/// </summary>
internal static class PropertyGens
{
    /// <summary>Iterations for cheap pure-math properties.</summary>
    public const int GeometryIterations = 2_000;

    /// <summary>Iterations for codec properties (each one encodes, decodes and slices several buffers).</summary>
    public const int CodecIterations = 500;

    /// <summary>
    /// One thread per property: xUnit already runs test classes in parallel, and CsCheck's default (all cores) would
    /// starve the real-time network tests running beside it.
    /// </summary>
    public const int Threads = 1;

    /// <summary>A display: an origin anywhere on a large desktop and a size from tiny to 8K.</summary>
    public static readonly Gen<DisplayRect> Display =
        Gen.Select(Gen.Double[-8000, 8000], Gen.Double[-8000, 8000], Gen.Double[50, 8000], Gen.Double[50, 8000])
            .Select((x, y, w, h) => new DisplayRect(x, y, w, h));

    /// <summary>A region inside 0..1 (what <see cref="MappingTarget.ForDisplay(DisplayRect, NormalizedRect)"/> accepts), including the full one.</summary>
    public static readonly Gen<NormalizedRect> Region = Gen.Frequency(
        (1, Gen.Const(NormalizedRect.Full)),
        (6, Gen.Select(Gen.Double[0, 0.95], Gen.Double[0, 0.95])
            .SelectMany((x, y) => Gen.Select(Gen.Double[0.05, 1 - x], Gen.Double[0.05, 1 - y])
                .Select((w, h) => new NormalizedRect(x, y, w, h)))));

    /// <summary>A phone surface size in pixels, portrait or landscape.</summary>
    public static readonly Gen<(double Width, double Height)> Surface =
        Gen.Select(Gen.Double[100, 4000], Gen.Double[100, 4000]);

    /// <summary>A normalized coordinate, mostly on the 0..65535 surface but also past its edges.</summary>
    public static readonly Gen<double> Coordinate = Gen.Frequency(
        (8, Gen.Double[0, CoordinateMapper.NormalizedMax]),
        (2, Gen.Double[-20_000, 90_000]),
        (1, Gen.OneOfConst(0, CoordinateMapper.NormalizedMax, double.NaN, double.PositiveInfinity, double.NegativeInfinity)));

    /// <summary>A view size in points (a display the region editor covers).</summary>
    public static readonly Gen<(double Width, double Height)> View =
        Gen.Select(Gen.Double[100, 8000], Gen.Double[100, 8000]);

    /// <summary>Every handle, including the ones a resize ignores.</summary>
    public static readonly Gen<RegionHandle> Handle = Gen.Enum<RegionHandle>();
}
