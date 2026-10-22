using PenBridge.Core.Input;
using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Settings;

/// <summary>The region editor's geometry (ADR-0035 amendment, #243): view points, bottom-left origin, y up.</summary>
public class RegionEditGeometryTests
{
    private const double W = 1000;
    private const double H = 600;
    private const double Tol = 6;
    private const double Precision = 9;

    // MinSide(1000) = max(24, ceil(50.000…)) = 51; MinSide(600) = max(24, ceil(30.000…)) = 31.
    private const double MinW = 51;
    private const double MinH = 31;

    private static readonly ViewRect Sel = new(200, 100, 400, 300); // left 200, right 600, bottom 100, top 400

    private static void Equal(ViewRect expected, ViewRect actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Width, actual.Width, Precision);
        Assert.Equal(expected.Height, actual.Height, Precision);
    }

    private static void Inside(ViewRect r)
    {
        Assert.Multiple(
            () => Assert.InRange(r.X, -1e-9, double.PositiveInfinity),
            () => Assert.InRange(r.Y, -1e-9, double.PositiveInfinity),
            () => Assert.InRange(r.X + r.Width, double.NegativeInfinity, W + 1e-9),
            () => Assert.InRange(r.Y + r.Height, double.NegativeInfinity, H + 1e-9));
    }

    // ---------------------------------------------------------------- min size

    [Fact]
    public void Min_side_is_the_larger_of_the_points_floor_and_the_key_tables_fraction()
    {
        Assert.Equal(MinW, RegionEditGeometry.MinSide(W));
        Assert.Equal(MinH, RegionEditGeometry.MinSide(H));
        Assert.Equal(RegionEditGeometry.MinSidePoints, RegionEditGeometry.MinSide(200));
    }

    [Fact]
    public void A_min_size_selection_normalizes_to_a_valid_region()
    {
        foreach (double side in new[] { 1440.0, 1512, 1728, 1920, 2560, 3008, 5120, 1000 })
        {
            double min = RegionEditGeometry.MinSide(side);
            NormalizedRect r = RegionEditGeometry.ToRegion(new ViewRect(0, 0, min, min), side, side);
            Assert.True(r.Width >= MacSettings.MinRegionSize && r.Height >= MacSettings.MinRegionSize, $"side {side}");
        }
    }

    [Fact]
    public void Big_enough_needs_both_sides_at_the_minimum()
    {
        Assert.True(RegionEditGeometry.IsBigEnough(new ViewRect(0, 0, MinW, MinH), W, H));
        Assert.False(RegionEditGeometry.IsBigEnough(new ViewRect(0, 0, MinW - 1, MinH), W, H));
        Assert.False(RegionEditGeometry.IsBigEnough(new ViewRect(0, 0, MinW, MinH - 1), W, H));
        Assert.False(RegionEditGeometry.IsBigEnough(new ViewRect(5, 5, 0, 0), W, H)); // a click
    }

    // ---------------------------------------------------------------- hit-testing

    [Theory]
    [InlineData(200, 100, RegionHandle.BottomLeft)]
    [InlineData(600, 100, RegionHandle.BottomRight)]
    [InlineData(600, 400, RegionHandle.TopRight)]
    [InlineData(200, 400, RegionHandle.TopLeft)]
    [InlineData(195, 405, RegionHandle.TopLeft)] // just outside, within the tolerance
    [InlineData(400, 100, RegionHandle.Bottom)]
    [InlineData(400, 404, RegionHandle.Top)]
    [InlineData(197, 250, RegionHandle.Left)]
    [InlineData(603, 250, RegionHandle.Right)]
    [InlineData(250, 380, RegionHandle.Inside)]
    [InlineData(400, 250, RegionHandle.Inside)]
    [InlineData(100, 250, RegionHandle.None)]
    [InlineData(400, 500, RegionHandle.None)]
    [InlineData(190, 90, RegionHandle.None)] // beyond the tolerance diagonally
    public void Hit_test_finds_handles_inside_and_outside(double x, double y, RegionHandle expected) =>
        Assert.Equal(expected, RegionEditGeometry.HitTest(Sel, x, y, Tol));

    [Fact]
    public void Hit_test_prefers_corners_over_edges() =>
        Assert.Equal(RegionHandle.TopRight, RegionEditGeometry.HitTest(Sel, 600, 398, Tol));

    // ---------------------------------------------------------------- move

    [Fact]
    public void Move_shifts_the_selection() => Equal(new ViewRect(250, 80, 400, 300), RegionEditGeometry.Move(Sel, 50, -20, W, H));

    [Theory]
    [InlineData(-500, 0, 0, 100)]
    [InlineData(900, 0, 600, 100)]
    [InlineData(0, -500, 200, 0)]
    [InlineData(0, 900, 200, 300)]
    [InlineData(-900, 900, 0, 300)]
    public void Move_is_clamped_inside_the_display(double dx, double dy, double x, double y) =>
        Equal(new ViewRect(x, y, 400, 300), RegionEditGeometry.Move(Sel, dx, dy, W, H));

    [Fact]
    public void Moving_the_full_display_keeps_it_full()
    {
        var full = new ViewRect(0, 0, W, H);
        Equal(full, RegionEditGeometry.Move(full, 30, -30, W, H));
    }

    // ---------------------------------------------------------------- resize, free

    [Theory]
    [InlineData(RegionHandle.Left, -50, 20, 150, 100, 450, 300)]
    [InlineData(RegionHandle.Right, 50, 20, 200, 100, 450, 300)]
    [InlineData(RegionHandle.Bottom, 20, -50, 200, 50, 400, 350)]
    [InlineData(RegionHandle.Top, 20, 50, 200, 100, 400, 350)]
    [InlineData(RegionHandle.BottomLeft, -50, -50, 150, 50, 450, 350)]
    [InlineData(RegionHandle.BottomRight, 50, -50, 200, 50, 450, 350)]
    [InlineData(RegionHandle.TopRight, 50, 50, 200, 100, 450, 350)]
    [InlineData(RegionHandle.TopLeft, -50, 50, 150, 100, 450, 350)]
    public void Each_handle_moves_only_its_own_sides(RegionHandle handle, double dx, double dy, double x, double y, double w, double h) =>
        Equal(new ViewRect(x, y, w, h), RegionEditGeometry.Resize(Sel, handle, dx, dy, W, H, aspect: null));

    [Theory]
    [InlineData(RegionHandle.Left, 1000, 0, 549, 100, MinW, 300)]
    [InlineData(RegionHandle.Right, -1000, 0, 200, 100, MinW, 300)]
    [InlineData(RegionHandle.Bottom, 0, 1000, 200, 369, 400, MinH)]
    [InlineData(RegionHandle.Top, 0, -1000, 200, 100, 400, MinH)]
    [InlineData(RegionHandle.TopRight, -1000, -1000, 200, 100, MinW, MinH)]
    [InlineData(RegionHandle.BottomLeft, 1000, 1000, 549, 369, MinW, MinH)]
    public void Resize_stops_at_the_min_size_without_flipping(RegionHandle handle, double dx, double dy, double x, double y, double w, double h) =>
        Equal(new ViewRect(x, y, w, h), RegionEditGeometry.Resize(Sel, handle, dx, dy, W, H, aspect: null));

    [Theory]
    [InlineData(RegionHandle.TopLeft, -1000, 1000)]
    [InlineData(RegionHandle.TopRight, 1000, 1000)]
    [InlineData(RegionHandle.BottomLeft, -1000, -1000)]
    [InlineData(RegionHandle.BottomRight, 1000, -1000)]
    public void Resize_stops_at_the_display_edges(RegionHandle handle, double dx, double dy)
    {
        ViewRect r = RegionEditGeometry.Resize(Sel, handle, dx, dy, W, H, aspect: null);
        Inside(r);
        Assert.Multiple(
            () => Assert.InRange(r.Width, Math.BitIncrement(Sel.Width), double.PositiveInfinity),
            () => Assert.InRange(r.Height, Math.BitIncrement(Sel.Height), double.PositiveInfinity));
    }

    [Fact]
    public void Inside_and_none_do_not_resize()
    {
        Equal(Sel, RegionEditGeometry.Resize(Sel, RegionHandle.Inside, 10, 10, W, H, aspect: null));
        Equal(Sel, RegionEditGeometry.Resize(Sel, RegionHandle.None, 10, 10, W, H, aspect: 2));
    }

    // ---------------------------------------------------------------- resize, aspect locked

    private const double Ratio = 2; // width / height

    private static readonly ViewRect Locked = new(200, 100, 400, 200); // already 2:1; middle y = 200, middle x = 400

    [Theory]
    [InlineData(RegionHandle.TopLeft)]
    [InlineData(RegionHandle.Top)]
    [InlineData(RegionHandle.TopRight)]
    [InlineData(RegionHandle.Right)]
    [InlineData(RegionHandle.BottomRight)]
    [InlineData(RegionHandle.Bottom)]
    [InlineData(RegionHandle.BottomLeft)]
    [InlineData(RegionHandle.Left)]
    public void Every_handle_keeps_the_aspect_ratio(RegionHandle handle)
    {
        foreach ((double dx, double dy) in new[] { (37.0, 11.0), (-23.0, 41.0), (500.0, -500.0), (-1000.0, 1000.0), (3.0, -2.0) })
        {
            ViewRect r = RegionEditGeometry.Resize(Locked, handle, dx, dy, W, H, Ratio);
            Assert.Equal(Ratio, r.Width / r.Height, Precision);
            Inside(r);
            Assert.True(r.Width >= MinW - 1e-9 && r.Height >= MinH - 1e-9, $"{handle} {dx},{dy}: {r} below the min");
        }
    }

    [Fact]
    public void A_locked_corner_keeps_the_opposite_corner_and_follows_the_larger_movement()
    {
        // TopRight dragged by (+100, +10): width 500 wins over height 210 × 2 = 420; the bottom-left corner stays.
        Equal(new ViewRect(200, 100, 500, 250), RegionEditGeometry.Resize(Locked, RegionHandle.TopRight, 100, 10, W, H, Ratio));

        // BottomLeft dragged by (-10, -100): height 300 × 2 = 600 wins; the top-right corner (600, 300) stays,
        // and the 600-wide rect would start at x = 0: fits exactly.
        Equal(new ViewRect(0, 0, 600, 300), RegionEditGeometry.Resize(Locked, RegionHandle.BottomLeft, -10, -100, W, H, Ratio));
    }

    [Fact]
    public void A_locked_corner_shrinks_to_fit_the_room_left_on_the_display()
    {
        // TopRight from the bottom-left anchor (200, 100): room 800 × 500 → width ≤ min(800, 1000) = 800.
        Equal(new ViewRect(200, 100, 800, 400), RegionEditGeometry.Resize(Locked, RegionHandle.TopRight, 2000, 0, W, H, Ratio));
    }

    [Fact]
    public void A_locked_edge_sets_its_side_and_centres_the_other()
    {
        // Right edge +100: width 500, height 250 centred on the middle y = 200 → y 75.
        Equal(new ViewRect(200, 75, 500, 250), RegionEditGeometry.Resize(Locked, RegionHandle.Right, 100, 999, W, H, Ratio));

        // Top edge +50: height 250, width 500 centred on the middle x = 400 → x 150; the bottom stays.
        Equal(new ViewRect(150, 100, 500, 250), RegionEditGeometry.Resize(Locked, RegionHandle.Top, 999, 50, W, H, Ratio));
    }

    [Fact]
    public void A_locked_edge_is_shifted_back_onto_the_display()
    {
        // Left edge −300 from a selection near the top: 600 × 300 centred on its middle y 475 would start at y 325
        // and end at 625, so it is shifted down to end at the display's top.
        var nearTop = new ViewRect(400, 400, 300, 150);
        Equal(new ViewRect(100, 300, 600, 300), RegionEditGeometry.Resize(nearTop, RegionHandle.Left, -300, 0, W, H, Ratio));
    }

    [Fact]
    public void A_locked_edge_stops_at_the_display_height()
    {
        // Right edge far out: width capped by the room (800) and by the display height × ratio (1200) → 800 × 400.
        ViewRect r = RegionEditGeometry.Resize(Locked, RegionHandle.Right, 5000, 0, W, H, Ratio);
        Assert.Equal(800, r.Width, Precision);
        Assert.Equal(400, r.Height, Precision);
        Inside(r);
    }

    [Fact]
    public void A_locked_resize_stops_at_the_min_size()
    {
        // The min width with the lock is max(51, 31 × 2) = 62.
        ViewRect r = RegionEditGeometry.Resize(Locked, RegionHandle.BottomLeft, 1000, 1000, W, H, Ratio);
        Equal(new ViewRect(600 - 62, 300 - 31, 62, 31), r);
    }

    [Theory]
    [InlineData(RegionHandle.TopRight, 10, 10)]
    [InlineData(RegionHandle.TopLeft, -10, 10)]
    [InlineData(RegionHandle.Top, 0, 10)]
    [InlineData(RegionHandle.Right, 10, 0)]
    public void A_locked_resize_from_an_off_ratio_selection_at_the_edge_never_gets_under_the_min(RegionHandle handle, double dx, double dy)
    {
        // Review case: 1920 × 1080, ratio 0.5, a 400 × 100 selection touching the top. Shrinking to the room above the
        // fixed corner gave 50 × 100; the min width is max(97, 55 × 0.5) = 97, so it is shifted down instead.
        const double dw = 1920;
        const double dh = 1080;
        var start = new ViewRect(500, 980, 400, 100);
        ViewRect r = RegionEditGeometry.Resize(start, handle, dx, dy, dw, dh, aspect: 0.5);
        Assert.Equal(0.5, r.Width / r.Height, Precision);
        Assert.InRange(r.Width, RegionEditGeometry.MinSide(dw) - 1e-9, double.PositiveInfinity);
        Assert.InRange(r.Height, RegionEditGeometry.MinSide(dh) - 1e-9, double.PositiveInfinity);
        Assert.True(RegionEditGeometry.IsBigEnough(r, dw, dh));
        Assert.Multiple(
            () => Assert.InRange(r.X, 0, double.PositiveInfinity),
            () => Assert.InRange(r.Y, 0, double.PositiveInfinity),
            () => Assert.InRange(r.X + r.Width, double.NegativeInfinity, dw + 1e-9),
            () => Assert.InRange(r.Y + r.Height, double.NegativeInfinity, dh + 1e-9));
    }

    [Fact]
    public void A_locked_top_right_drag_at_the_top_shifts_the_fixed_corner_down()
    {
        ViewRect r = RegionEditGeometry.Resize(new ViewRect(500, 980, 400, 100), RegionHandle.TopRight, 10, 10, 1920, 1080, aspect: 0.5);
        Equal(new ViewRect(500, 1080 - 194, 97, 194), r);
    }

    [Fact]
    public void A_locked_edge_without_room_is_shifted_back_at_the_min()
    {
        // Right edge of a 40-wide selection at the display's right side: no room, so the 62-wide minimum moves left.
        ViewRect r = RegionEditGeometry.Resize(new ViewRect(960, 100, 40, 20), RegionHandle.Right, -30, 0, W, H, Ratio);
        Equal(new ViewRect(W - 62, 100 + ((20 - 31) / 2.0), 62, 31), r);
    }

    // ---------------------------------------------------------------- draw a new selection

    [Fact]
    public void Draw_spans_the_anchor_and_the_point_in_any_direction()
    {
        Equal(new ViewRect(100, 100, 200, 50), RegionEditGeometry.Draw(100, 100, 300, 150, W, H, aspect: null));
        Equal(new ViewRect(50, 20, 50, 80), RegionEditGeometry.Draw(100, 100, 50, 20, W, H, aspect: null));
    }

    [Fact]
    public void Draw_is_clamped_onto_the_display() =>
        Equal(new ViewRect(900, 0, 100, 100), RegionEditGeometry.Draw(900, 100, 2000, -50, W, H, aspect: null));

    [Fact]
    public void Draw_with_aspect_takes_the_larger_side_and_fits_the_room()
    {
        Equal(new ViewRect(100, 100, 200, 100), RegionEditGeometry.Draw(100, 100, 150, 200, W, H, Ratio));
        ViewRect r = RegionEditGeometry.Draw(800, 500, 1000, 600, W, H, Ratio); // room 200 × 100 from the anchor
        Equal(new ViewRect(800, 500, 200, 100), r);
    }

    // ---------------------------------------------------------------- aspect lock turned on

    [Fact]
    public void Applying_the_aspect_fits_inside_the_selection_centred()
    {
        // 400 × 300 at 2:1 → 400 × 200, centred vertically (y 100 + 50).
        Equal(new ViewRect(200, 150, 400, 200), RegionEditGeometry.ApplyAspect(Sel, Ratio, W, H));
    }

    [Fact]
    public void Opening_with_the_lock_on_fits_a_full_display_region_to_the_ratio()
    {
        // The editor applies ApplyAspect at open: a full 1000 × 600 display at 2:1 → 1000 × 500, centred.
        Equal(new ViewRect(0, 50, 1000, 500), RegionEditGeometry.ApplyAspect(new ViewRect(0, 0, W, H), Ratio, W, H));
        NormalizedRect region = RegionEditGeometry.ToRegion(RegionEditGeometry.ApplyAspect(new ViewRect(0, 0, W, H), 0.5, W, H), W, H);
        Assert.Equal(0.3, region.Width, Precision); // 300 × 600 at 1:2
        Assert.Equal(1, region.Height, Precision);
    }

    [Fact]
    public void Applying_the_aspect_to_a_tiny_selection_grows_it_to_the_min_on_the_display()
    {
        ViewRect r = RegionEditGeometry.ApplyAspect(new ViewRect(990, 595, 10, 5), Ratio, W, H);
        Assert.Equal(Ratio, r.Width / r.Height, Precision);
        Assert.InRange(r.Width, 62 - 1e-9, double.PositiveInfinity);
        Inside(r);
    }

    // ---------------------------------------------------------------- the bar

    private const double BarW = 300;
    private const double BarH = 40;
    private const double Gap = 12;

    [Fact]
    public void The_bar_goes_centred_below_the_selection() =>
        Equal(new ViewRect(250, 100 - Gap - BarH, BarW, BarH), RegionEditGeometry.PlaceBar(Sel, BarW, BarH, W, H, Gap));

    [Fact]
    public void The_bar_flips_above_without_room_below() =>
        Equal(new ViewRect(250, 30 + 200 + Gap, BarW, BarH), RegionEditGeometry.PlaceBar(new ViewRect(200, 30, 400, 200), BarW, BarH, W, H, Gap));

    [Fact]
    public void The_bar_goes_inside_without_room_above_or_below() =>
        Equal(new ViewRect(350, Gap, BarW, BarH), RegionEditGeometry.PlaceBar(new ViewRect(0, 0, W, H), BarW, BarH, W, H, Gap));

    [Theory]
    [InlineData(0, 12)]
    [InlineData(900, W - Gap - BarW)]
    public void The_bar_stays_on_the_display_horizontally(double selectionX, double barX) =>
        Assert.Equal(barX, RegionEditGeometry.PlaceBar(new ViewRect(selectionX, 300, 100, 100), BarW, BarH, W, H, Gap).X, Precision);

    // ---------------------------------------------------------------- to a region

    [Fact]
    public void To_region_flips_to_a_top_left_origin_and_inverts_the_tint_hole()
    {
        NormalizedRect region = RegionEditGeometry.ToRegion(Sel, W, H);
        Assert.Equal(0.2, region.X, Precision);
        Assert.Equal(1 - (400 / H), region.Y, Precision);
        Assert.Equal(0.4, region.Width, Precision);
        Assert.Equal(0.5, region.Height, Precision);
        ViewRect back = RegionTintRule.HoleInView(region, W, H);
        Equal(Sel, back);
    }

    [Fact]
    public void The_full_display_is_the_full_region() =>
        Assert.True(MacSettings.IsFullRegion(RegionEditGeometry.ToRegion(new ViewRect(0, 0, W, H), W, H)));
}
