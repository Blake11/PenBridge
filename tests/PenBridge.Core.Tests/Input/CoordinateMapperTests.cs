using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>ADR-0031: the active area has the target's aspect ratio, is centred on the surface and maps onto the whole target.</summary>
[Collection(AllocationGroup.Name)]
public class CoordinateMapperTests
{
    private const double Tol = 1e-6;
    private const double Max = CoordinateMapper.NormalizedMax;

    [Fact]
    public void Tall_surface_on_wide_display_uses_full_width_with_margins_top_and_bottom()
    {
        // S23 Ultra portrait 1440x3088 onto a 1920x1080 display.
        var m = new CoordinateMapper(1440, 3088, new DisplayRect(0, 0, 1920, 1080));
        double h = (1440.0 / 3088) / (1920.0 / 1080);
        AssertRect(new NormalizedRect(0, (1 - h) / 2, 1, h), m.ActiveArea);
        AssertPoint(new DisplayPoint(960, 540), m.Map(Max / 2, Max / 2));
        AssertPoint(new DisplayPoint(0, 0), m.Map(0, m.ActiveArea.Y * Max));
        DisplayPoint far = m.Map(Max, (m.ActiveArea.Y + h) * Max);
        Assert.Multiple(
            () => Assert.InRange(far.X, Math.BitIncrement(1920 - Tol), Math.BitDecrement(1920.0)),
            () => Assert.InRange(far.Y, Math.BitIncrement(1080 - Tol), Math.BitDecrement(1080.0)));
    }

    [Fact]
    public void Wide_surface_on_less_wide_display_uses_full_height_with_margins_left_and_right()
    {
        // S23 Ultra landscape 3088x1440 (2.14) onto a 16:10 display (#127: top and bottom were unreachable).
        var m = new CoordinateMapper(3088, 1440, new DisplayRect(0, 0, 2560, 1600));
        double w = 1.6 / (3088.0 / 1440);
        AssertRect(new NormalizedRect((1 - w) / 2, 0, w, 1), m.ActiveArea);
        AssertPoint(new DisplayPoint(1280, 0), m.Map(Max / 2, 0));
        DisplayPoint bottom = m.Map(Max / 2, Max);
        Assert.InRange(bottom.Y, Math.BitIncrement(1600 - Tol), Math.BitDecrement(1600.0));
        AssertPoint(new DisplayPoint(0, 800), m.Map(m.ActiveArea.X * Max, Max / 2));
    }

    [Theory]
    [InlineData(1440, 3088)]
    [InlineData(3088, 1440)]
    public void Both_orientations_keep_the_shape(double w, double h)
    {
        var m = new CoordinateMapper(w, h, new DisplayRect(0, 0, 1920, 1080));
        // Equal surface distances give equal display distances on both axes (no squash).
        double surfaceUnitX = w / Max;
        double surfaceUnitY = h / Max;
        Assert.Equal(m.ScaleX / surfaceUnitX, m.ScaleY / surfaceUnitY, 9);
        // The active area has the display's aspect ratio in surface units.
        Assert.Equal(1920.0 / 1080, m.ActiveArea.Width * w / (m.ActiveArea.Height * h), 9);
    }

    [Fact]
    public void Points_in_the_margins_are_pinned_to_the_nearest_edge()
    {
        var m = new CoordinateMapper(3088, 1440, new DisplayRect(100, 50, 1920, 1080));
        NormalizedRect a = m.ActiveArea;
        double midY = Max / 2;
        AssertPoint(new DisplayPoint(100, 50 + 540), m.Map(a.X * Max / 2, midY)); // left margin
        DisplayPoint right = m.Map(Max, midY); // right margin
        Assert.InRange(right.X, Math.BitIncrement(2020 - Tol), Math.BitDecrement(2020.0));
        Assert.Equal(590, right.Y, Tol);
        AssertPoint(new DisplayPoint(100, 50), m.Map(0, 0)); // corner
    }

    [Fact]
    public void Matching_aspect_fills_the_display_exactly()
    {
        var m = new CoordinateMapper(1600, 1000, new DisplayRect(0, 0, 1440, 900));
        AssertRect(NormalizedRect.Full, m.ActiveArea);
        AssertRect(new DisplayRect(0, 0, 1440, 900), m.ContentRect);
        AssertPoint(new DisplayPoint(720, 450), m.Map(Max / 2, Max / 2));
    }

    [Fact]
    public void Display_origin_offsets_are_honoured_for_secondary_displays()
    {
        var m = new CoordinateMapper(1600, 1000, new DisplayRect(-1440, -200, 1440, 900));
        AssertPoint(new DisplayPoint(-1440, -200), m.Map(0, 0));
        AssertPoint(new DisplayPoint(-720, 250), m.Map(Max / 2, Max / 2));
    }

    [Fact]
    public void Out_of_range_and_nan_inputs_are_clamped()
    {
        var m = new CoordinateMapper(1600, 1000, new DisplayRect(100, 100, 1440, 900));
        AssertPoint(new DisplayPoint(100, 100), m.Map(-5000, double.NaN));
        DisplayPoint p = m.Map(1e9, 1e9);
        Assert.Multiple(
            () => Assert.InRange(p.X, double.NegativeInfinity, Math.BitDecrement(1540.0)),
            () => Assert.InRange(p.Y, double.NegativeInfinity, Math.BitDecrement(1000.0)));
    }

    [Fact]
    public void Region_aspect_decides_the_active_area_and_maps_onto_the_region()
    {
        // Right half of a 1920x1080 display: target 960x1080 at x=960 (0.889); surface 1:2 (0.5) => full width.
        var m = new CoordinateMapper(1000, 2000, new DisplayRect(0, 0, 1920, 1080), new NormalizedRect(0.5, 0, 0.5, 1));
        AssertRect(new DisplayRect(960, 0, 960, 1080), m.TargetRect);
        double h = 0.5 / (960.0 / 1080);
        AssertRect(new NormalizedRect(0, (1 - h) / 2, 1, h), m.ActiveArea);
        AssertPoint(new DisplayPoint(1440, 540), m.Map(Max / 2, Max / 2));
        AssertPoint(new DisplayPoint(960, 0), m.Map(0, 0));
    }

    [Fact]
    public void Content_rect_scale_and_short_side_describe_the_whole_surface()
    {
        var m = new CoordinateMapper(3088, 1440, new DisplayRect(0, 0, 1920, 1080));
        double w = (1920.0 / 1080) / (3088.0 / 1440);
        double fullWidth = 1920 / w;
        AssertRect(new DisplayRect(-(fullWidth - 1920) / 2, 0, fullWidth, 1080), m.ContentRect);
        Assert.Equal(fullWidth / Max, m.ScaleX, 12);
        Assert.Equal(1080 / Max, m.ScaleY, 12);
    }

    [Fact]
    public void Gesture_rect_is_the_phase_4_letterbox_on_the_whole_display()
    {
        // Wide surface on 16:9: full width, bars top and bottom.
        var m = new CoordinateMapper(3088, 1440, new DisplayRect(0, 0, 1920, 1080));
        double h = 1920 * 1440.0 / 3088;
        AssertRect(new DisplayRect(0, (1080 - h) / 2, 1920, h), m.GestureRect);
        Assert.Equal(h, m.GestureShortSide, 9);
        AssertPoint(new DisplayPoint(0, (1080 - h) / 2), m.MapGesture(0, 0));
        AssertPoint(new DisplayPoint(960, 540), m.MapGesture(Max / 2, Max / 2));

        // Tall surface: full height, bars left and right.
        var tall = new CoordinateMapper(1440, 3088, new DisplayRect(100, 50, 1920, 1080));
        double w = 1080 * 1440.0 / 3088;
        AssertRect(new DisplayRect(100 + ((1920 - w) / 2), 50, w, 1080), tall.GestureRect);
        Assert.Equal(w, tall.GestureShortSide, 9);
    }

    [Fact]
    public void Gesture_rect_ignores_the_region()
    {
        var display = new DisplayRect(0, 0, 1920, 1080);
        var full = new CoordinateMapper(3088, 1440, display);
        var region = new CoordinateMapper(3088, 1440, display, new NormalizedRect(0.6, 0.1, 0.2, 0.5));
        Assert.Equal(full.GestureRect, region.GestureRect);
        Assert.Equal(full.MapGesture(1234, 5678), region.MapGesture(1234, 5678));
    }

    [Fact]
    public void Gesture_rect_with_all_displays_uses_the_display_at_the_origin()
    {
        var main = new DisplayRect(0, 0, 1512, 982);
        MappingTarget all = MappingTarget.ForAllDisplays([new DisplayRect(-2560, -200, 2560, 1440), main]);
        Assert.Equal(main, all.GestureDisplay);
        Assert.Equal(new CoordinateMapper(3088, 1440, main).GestureRect, new CoordinateMapper(3088, 1440, all).GestureRect);

        MappingTarget noOrigin = MappingTarget.ForAllDisplays([new DisplayRect(10, 0, 800, 600), new DisplayRect(900, 0, 800, 600)]);
        Assert.Equal(new DisplayRect(10, 0, 800, 600), noOrigin.GestureDisplay);
    }

    [Fact]
    public void Gesture_mapping_clamps_to_the_gesture_rect()
    {
        var m = new CoordinateMapper(3088, 1440, new DisplayRect(0, 0, 1920, 1080));
        DisplayPoint p = m.MapGesture(-10, double.NaN);
        Assert.Equal(m.GestureRect.X, p.X);
        Assert.Equal(m.GestureRect.Y, p.Y);
        Assert.InRange(m.MapGesture(Max, Max).X, double.NegativeInfinity, Math.BitDecrement(m.GestureRect.X + m.GestureRect.Width));
    }

    [Fact]
    public void All_displays_map_the_bounding_box_and_put_gap_points_on_the_nearest_display()
    {
        // A 1000x1000 at the origin, B 1000x500 to its right after a 500 pt gap: bounding box 2500x1000.
        MappingTarget target = MappingTarget.ForAllDisplays([new DisplayRect(0, 0, 1000, 1000), new DisplayRect(1500, 0, 1000, 500)]);
        Assert.True(target.IsAllDisplays);
        AssertRect(new DisplayRect(0, 0, 2500, 1000), target.Bounds);
        var m = new CoordinateMapper(2500, 1000, target);
        AssertRect(NormalizedRect.Full, m.ActiveArea);

        AssertPoint(new DisplayPoint(500, 500), m.Map(500 / 2500.0 * Max, Max / 2)); // on A
        AssertPoint(new DisplayPoint(2000, 250), m.Map(2000 / 2500.0 * Max, Max / 4)); // on B

        DisplayPoint gapNearA = m.Map(1200 / 2500.0 * Max, Max / 2);
        Assert.InRange(gapNearA.X, Math.BitIncrement(1000 - Tol), Math.BitDecrement(1000.0));
        Assert.Equal(500, gapNearA.Y, Tol);

        AssertPoint(new DisplayPoint(1500, 200), m.Map(1300 / 2500.0 * Max, 200 / 1000.0 * Max)); // nearer B

        DisplayPoint belowB = m.Map(2000 / 2500.0 * Max, 800 / 1000.0 * Max);
        Assert.Equal(2000, belowB.X, Tol);
        Assert.InRange(belowB.Y, Math.BitIncrement(500 - Tol), Math.BitDecrement(500.0));
    }

    [Fact]
    public void All_displays_active_area_follows_the_bounding_box_aspect()
    {
        MappingTarget target = MappingTarget.ForAllDisplays([new DisplayRect(0, 0, 1920, 1080), new DisplayRect(1920, 0, 1920, 1080)]);
        var m = new CoordinateMapper(3088, 1440, target); // 2.14 surface, 3.56 target => full width
        double h = (3088.0 / 1440) / (3840.0 / 1080);
        AssertRect(new NormalizedRect(0, (1 - h) / 2, 1, h), m.ActiveArea);
        AssertPoint(new DisplayPoint(1920, 540), m.Map(Max / 2, Max / 2));
    }

    [Fact]
    public void Single_display_clamp_keeps_points_on_the_whole_display()
    {
        var m = new CoordinateMapper(1, 1, new DisplayRect(0, 0, 1920, 1080), new NormalizedRect(0.5, 0.5, 0.5, 0.5));
        AssertPoint(new DisplayPoint(0, 0), m.ClampToDisplays(new DisplayPoint(-50, -10)));
        AssertPoint(new DisplayPoint(100, 100), m.ClampToDisplays(new DisplayPoint(100, 100))); // outside the region is fine
        DisplayPoint p = m.ClampToDisplays(new DisplayPoint(5000, 5000));
        Assert.Multiple(
            () => Assert.InRange(p.X, Math.BitIncrement(1920 - Tol), Math.BitDecrement(1920.0)),
            () => Assert.InRange(p.Y, double.NegativeInfinity, Math.BitDecrement(1080.0)));
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
        Assert.Throws<ArgumentOutOfRangeException>(() => MappingTarget.ForAllDisplays([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MappingTarget.ForAllDisplays([new DisplayRect(0, 0, 10, double.PositiveInfinity)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MappingTarget.ForAllDisplays(new DisplayRect[MappingTarget.MaxDisplays + 1]));
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
        var single = new CoordinateMapper(1440, 3088, new DisplayRect(0, 0, 1920, 1080));
        var all = new CoordinateMapper(2500, 1000, MappingTarget.ForAllDisplays([new DisplayRect(0, 0, 1000, 1000), new DisplayRect(1500, 0, 1000, 500)]));
        long mapped = 0;
        Alloc.AssertZero(10_000, i =>
        {
            DisplayPoint p = single.Map((ushort)i, (ushort)(i * 3));
            DisplayPoint q = all.Map((ushort)(i * 7), (ushort)(i * 5));
            DisplayPoint r = all.ClampToDisplays(new DisplayPoint(i % 3000, i % 1200));
            if (p.X >= 0 && q.X >= 0 && r.X >= 0)
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

    private static void AssertRect(NormalizedRect expected, NormalizedRect actual)
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
