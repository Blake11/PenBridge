using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

[Collection(AllocationGroup.Name)]
public class CoordinateMapperTests
{
    private const double Tol = 1e-6;

    [Fact]
    public void Portrait_phone_on_16x9_display_is_pillarboxed_and_centered()
    {
        // S23 Ultra portrait 1440x3088 onto a 1920x1080 display.
        var m = new CoordinateMapper(1440, 3088, new DisplayRect(0, 0, 1920, 1080));
        double width = 1080 * 1440.0 / 3088;
        AssertRect(new DisplayRect((1920 - width) / 2, 0, width, 1080), m.ContentRect);
        AssertPoint(new DisplayPoint(960, 540), m.Map(32767.5, 32767.5));
    }

    [Fact]
    public void Landscape_phone_on_16x9_display_is_letterboxed()
    {
        var m = new CoordinateMapper(3088, 1440, new DisplayRect(0, 0, 1920, 1080));
        double height = 1920 * 1440.0 / 3088; // 895.3 < 1080
        AssertRect(new DisplayRect(0, (1080 - height) / 2, 1920, height), m.ContentRect);
        AssertPoint(new DisplayPoint(0, (1080 - height) / 2), m.Map(0, 0));
    }

    [Fact]
    public void Portrait_phone_on_16x10_display_is_pillarboxed()
    {
        var m = new CoordinateMapper(1440, 3088, new DisplayRect(0, 0, 1728, 1080));
        double width = 1080 * 1440.0 / 3088;
        AssertRect(new DisplayRect((1728 - width) / 2, 0, width, 1080), m.ContentRect);
    }

    [Fact]
    public void Landscape_phone_on_16x10_display_is_letterboxed()
    {
        var m = new CoordinateMapper(3088, 1440, new DisplayRect(0, 0, 2560, 1600));
        double height = 2560 * 1440.0 / 3088;
        AssertRect(new DisplayRect(0, (1600 - height) / 2, 2560, height), m.ContentRect);
    }

    [Fact]
    public void Matching_aspect_fills_display_exactly()
    {
        var m = new CoordinateMapper(1600, 1000, new DisplayRect(0, 0, 1440, 900));
        AssertRect(new DisplayRect(0, 0, 1440, 900), m.ContentRect);
    }

    [Fact]
    public void Display_origin_offsets_are_honoured_for_secondary_displays()
    {
        var m = new CoordinateMapper(1600, 1000, new DisplayRect(-1440, -200, 1440, 900));
        AssertPoint(new DisplayPoint(-1440, -200), m.Map(0, 0));
        AssertPoint(new DisplayPoint(-720, 250), m.Map(32767.5, 32767.5));
    }

    [Fact]
    public void Far_edges_stay_strictly_inside_the_content_rect()
    {
        var m = new CoordinateMapper(1600, 1000, new DisplayRect(0, 0, 1440, 900));
        DisplayPoint p = m.Map(ushort.MaxValue, ushort.MaxValue);
        Assert.True(p.X < 1440 && p.X > 1440 - Tol);
        Assert.True(p.Y < 900 && p.Y > 900 - Tol);
    }

    [Fact]
    public void Out_of_range_and_nan_inputs_are_clamped()
    {
        var m = new CoordinateMapper(1600, 1000, new DisplayRect(100, 100, 1440, 900));
        AssertPoint(new DisplayPoint(100, 100), m.Map(-5000, double.NaN));
        DisplayPoint p = m.Map(1e9, 1e9);
        Assert.True(p.X < 1540 && p.Y < 1000);
    }

    [Fact]
    public void Sub_region_is_fitted_with_aspect_preserved()
    {
        // Right half of a 1920x1080 display: target 960x1080 at x=960; portrait surface 1:2 => 540x1080 centered.
        var m = new CoordinateMapper(1000, 2000, new DisplayRect(0, 0, 1920, 1080), new NormalizedRect(0.5, 0, 0.5, 1));
        AssertRect(new DisplayRect(960, 0, 960, 1080), m.TargetRect);
        AssertRect(new DisplayRect(960 + 210, 0, 540, 1080), m.ContentRect);
    }

    [Fact]
    public void Scale_and_short_side_reflect_content_rect()
    {
        var m = new CoordinateMapper(3088, 1440, new DisplayRect(0, 0, 1920, 1080));
        Assert.Equal(1920 / 65535.0, m.ScaleX, 12);
        Assert.Equal(m.ContentRect.Height, m.ShortSide, 12);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, -1)]
    [InlineData(double.NaN, 100)]
    [InlineData(double.PositiveInfinity, 100)]
    public void Invalid_surface_size_throws(double w, double h) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoordinateMapper(w, h, new DisplayRect(0, 0, 10, 10)));

    [Fact]
    public void Invalid_display_and_region_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoordinateMapper(1, 1, new DisplayRect(0, 0, 0, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoordinateMapper(1, 1, new DisplayRect(double.NaN, 0, 10, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoordinateMapper(1, 1, new DisplayRect(0, 0, 10, 10), new NormalizedRect(0.6, 0, 0.5, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoordinateMapper(1, 1, new DisplayRect(0, 0, 10, 10), new NormalizedRect(0, 0, 0, 1)));
    }

    [Fact]
    public void Default_mapper_is_not_configured()
    {
        Assert.False(default(CoordinateMapper).IsConfigured);
        Assert.True(CoordinateMapper.Identity.IsConfigured);
    }

    [Fact]
    public void Map_is_allocation_free()
    {
        var m = new CoordinateMapper(1440, 3088, new DisplayRect(0, 0, 1920, 1080));
        long mapped = 0;
        Alloc.AssertZero(10_000, i =>
        {
            DisplayPoint p = m.Map((ushort)i, (ushort)(i * 3));
            if (p.X >= 0 && p.Y >= 0)
            {
                mapped++;
            }
        }, () => mapped);
    }

    private static void AssertRect(DisplayRect expected, DisplayRect actual)
    {
        Assert.Equal(expected.X, actual.X, Tol);
        Assert.Equal(expected.Y, actual.Y, Tol);
        Assert.Equal(expected.Width, actual.Width, Tol);
        Assert.Equal(expected.Height, actual.Height, Tol);
    }

    private static void AssertPoint(DisplayPoint expected, DisplayPoint actual)
    {
        Assert.Equal(expected.X, actual.X, Tol);
        Assert.Equal(expected.Y, actual.Y, Tol);
    }
}
