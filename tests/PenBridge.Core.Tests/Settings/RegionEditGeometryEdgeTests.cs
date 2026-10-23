using PenBridge.Core.Input;
using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Settings;

/// <summary>
/// The region editor's boundaries (#279): hit-test edges and tolerance, locked resizes from every handle with the
/// room limit binding, the bar's exact placement limits and the normalization clamps.
/// </summary>
public class RegionEditGeometryEdgeTests
{
    private const double W = 1000;
    private const double H = 600;
    private const double Tol = 6;
    private const double Precision = 9;
    private const double Ratio = 2;

    private static readonly ViewRect Sel = new(200, 100, 400, 300); // left 200, right 600, bottom 100, top 400

    private static void Equal(ViewRect expected, ViewRect actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Width, actual.Width, Precision);
        Assert.Equal(expected.Height, actual.Height, Precision);
    }

    // ---------------------------------------------------------------- hit-testing

    [Theory]
    // exactly the tolerance away from an edge is still the edge handle; just past it is not
    [InlineData(194, 250, RegionHandle.Left)]
    [InlineData(193.9, 250, RegionHandle.None)]
    [InlineData(606, 250, RegionHandle.Right)]
    [InlineData(606.1, 250, RegionHandle.None)]
    [InlineData(400, 94, RegionHandle.Bottom)]
    [InlineData(400, 93.9, RegionHandle.None)]
    [InlineData(400, 406, RegionHandle.Top)]
    [InlineData(400, 406.1, RegionHandle.None)]
    // the inner side of the tolerance band
    [InlineData(206, 250, RegionHandle.Left)]
    [InlineData(206.1, 250, RegionHandle.Inside)]
    [InlineData(594, 250, RegionHandle.Right)]
    [InlineData(593.9, 250, RegionHandle.Inside)]
    [InlineData(400, 106, RegionHandle.Bottom)]
    [InlineData(400, 106.1, RegionHandle.Inside)]
    [InlineData(400, 394, RegionHandle.Top)]
    [InlineData(400, 393.9, RegionHandle.Inside)]
    // corners at the tolerance
    [InlineData(194, 94, RegionHandle.BottomLeft)]
    [InlineData(606, 94, RegionHandle.BottomRight)]
    [InlineData(606, 406, RegionHandle.TopRight)]
    [InlineData(194, 406, RegionHandle.TopLeft)]
    // on the line of an edge but beyond its ends: nothing (edges are only as long as the selection plus the tolerance)
    [InlineData(100, 100, RegionHandle.None)]
    [InlineData(700, 100, RegionHandle.None)]
    [InlineData(100, 400, RegionHandle.None)]
    [InlineData(700, 400, RegionHandle.None)]
    [InlineData(200, 500, RegionHandle.None)]
    [InlineData(200, 0, RegionHandle.None)]
    [InlineData(600, 500, RegionHandle.None)]
    [InlineData(600, 0, RegionHandle.None)]
    [InlineData(100, 250, RegionHandle.None)]
    [InlineData(700, 250, RegionHandle.None)]
    [InlineData(400, 0, RegionHandle.None)]
    [InlineData(400, 500, RegionHandle.None)]
    public void Hit_test_edges_and_tolerance_are_exact(double x, double y, RegionHandle expected) =>
        Assert.Equal(expected, RegionEditGeometry.HitTest(Sel, x, y, Tol));

    // ---------------------------------------------------------------- locked corner, the vertical movement wins

    [Theory]
    [InlineData(RegionHandle.TopRight, 200, 100, 600, 300)]
    [InlineData(RegionHandle.TopLeft, 0, 100, 600, 300)]
    [InlineData(RegionHandle.BottomRight, 200, 0, 600, 300)]
    [InlineData(RegionHandle.BottomLeft, 0, 0, 600, 300)]
    public void A_locked_corner_follows_a_vertical_drag(RegionHandle handle, double x, double y, double w, double h)
    {
        // Locked is 400 × 200 at (200, 100): dragging the top (+100) or the bottom (−100) by 100 makes the height 300,
        // so the width (2 × 300) beats the unmoved 400.
        var locked = new ViewRect(200, 100, 400, 200);
        double dy = handle is RegionHandle.TopRight or RegionHandle.TopLeft ? 100 : -100;
        Equal(new ViewRect(x, y, w, h), RegionEditGeometry.Resize(locked, handle, 0, dy, W, H, Ratio));
    }

    [Theory]
    [InlineData(RegionHandle.BottomRight, 200, 0, 500, 250)]
    [InlineData(RegionHandle.BottomLeft, 100, 0, 500, 250)]
    public void A_locked_corner_from_a_bottom_handle_is_limited_by_the_room_below(RegionHandle handle, double x, double y, double w, double h)
    {
        // Selection (200, 50, 400, 200): the top is at 250, so only 250 of height fit below the fixed top corner.
        var locked = new ViewRect(200, 50, 400, 200);
        Equal(new ViewRect(x, y, w, h), RegionEditGeometry.Resize(locked, handle, 0, -1000, W, H, Ratio));
    }

    [Fact]
    public void A_locked_corner_from_a_top_handle_is_limited_by_the_room_above()
    {
        // Bottom at 50: 550 of height fit above it, 1100 of width by the ratio, but only 800 to the right of x = 200.
        Equal(new ViewRect(200, 50, 800, 400), RegionEditGeometry.Resize(new ViewRect(200, 50, 400, 200), RegionHandle.TopRight, 0, 1000, W, H, Ratio));

        // And the room above limits first on a tall ratio: 0.5 → 275 wide on the 550 high room.
        Equal(new ViewRect(200, 50, 275, 550), RegionEditGeometry.Resize(new ViewRect(200, 50, 100, 200), RegionHandle.TopRight, 0, 1000, W, H, 0.5));
    }

    // ---------------------------------------------------------------- locked edges

    [Fact]
    public void A_locked_left_edge_is_limited_by_the_room_to_its_left()
    {
        // Right edge fixed at 600: 600 of width fit; the 300 height is centred on the middle y 200 → y 50.
        Equal(new ViewRect(0, 50, 600, 300), RegionEditGeometry.Resize(new ViewRect(200, 100, 400, 200), RegionHandle.Left, -1000, 0, W, H, Ratio));
    }

    [Fact]
    public void A_locked_bottom_edge_sets_the_height_and_keeps_the_top()
    {
        var locked = new ViewRect(200, 100, 400, 200); // top 300
        Equal(new ViewRect(150, 50, 500, 250), RegionEditGeometry.Resize(locked, RegionHandle.Bottom, 999, -50, W, H, Ratio));
        Equal(new ViewRect(250, 150, 300, 150), RegionEditGeometry.Resize(locked, RegionHandle.Bottom, 999, 50, W, H, Ratio));
    }

    [Fact]
    public void A_locked_bottom_edge_is_limited_by_the_room_below()
    {
        // Top fixed at 300: 300 of height fit below it → 600 wide, centred on x 400.
        Equal(new ViewRect(100, 0, 600, 300), RegionEditGeometry.Resize(new ViewRect(200, 100, 400, 200), RegionHandle.Bottom, 0, -1000, W, H, Ratio));
    }

    [Fact]
    public void A_locked_top_edge_is_limited_by_the_room_above()
    {
        // Bottom fixed at 100: 500 of height fit above it (the display's 600 less 100) → 1000 wide, the whole display.
        Equal(new ViewRect(0, 100, 1000, 500), RegionEditGeometry.Resize(new ViewRect(200, 100, 400, 200), RegionHandle.Top, 0, 1000, W, H, Ratio));
    }

    [Fact]
    public void A_zero_or_negative_ratio_is_a_free_resize()
    {
        Equal(new ViewRect(200, 100, 450, 300), RegionEditGeometry.Resize(Sel, RegionHandle.Right, 50, 20, W, H, aspect: 0));
        Equal(new ViewRect(200, 100, 450, 300), RegionEditGeometry.Resize(Sel, RegionHandle.Right, 50, 20, W, H, aspect: -2));
        Equal(new ViewRect(200, 100, 450, 300), RegionEditGeometry.Resize(Sel, RegionHandle.Right, 50, 20, W, H, aspect: double.PositiveInfinity));
        Equal(new ViewRect(200, 100, 450, 300), RegionEditGeometry.Resize(Sel, RegionHandle.Right, 50, 20, W, H, aspect: double.NaN));
    }

    // ---------------------------------------------------------------- an unusable display

    [Theory]
    [InlineData(W, 0)]
    [InlineData(0, 0)]
    [InlineData(W, double.NaN)]
    [InlineData(double.NegativeInfinity, H)]
    public void A_display_with_a_zero_side_leaves_the_selection_unchanged(double width, double height)
    {
        Equal(Sel, RegionEditGeometry.Move(Sel, 10, 10, width, height));
        Equal(Sel, RegionEditGeometry.Resize(Sel, RegionHandle.Top, 10, 10, width, height, null));
        Equal(default, RegionEditGeometry.Draw(10, 10, 100, 100, width, height, null));
    }

    // ---------------------------------------------------------------- applying the aspect

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    public void An_unusable_aspect_leaves_the_selection_alone(double aspect) =>
        Equal(Sel, RegionEditGeometry.ApplyAspect(Sel, aspect, W, H));

    [Fact]
    public void Applying_a_very_tall_aspect_is_capped_by_the_display_height_times_the_ratio()
    {
        // 0.05: the selection fits 15 wide, the minimum (51) is above the display's 600 × 0.05 = 30 cap → 30 × 600.
        Equal(new ViewRect(385, 0, 30, 600), RegionEditGeometry.ApplyAspect(Sel, 0.05, W, H));
    }

    // ---------------------------------------------------------------- the bar

    private const double BarW = 300;
    private const double BarH = 40;
    private const double Gap = 12;

    [Fact]
    public void The_bar_is_below_when_exactly_a_gap_is_left_under_it()
    {
        // selection bottom 64 = gap + bar + gap: the bar's bottom lands exactly at the gap.
        Equal(new ViewRect(250, Gap, BarW, BarH), RegionEditGeometry.PlaceBar(new ViewRect(200, 64, 400, 200), BarW, BarH, W, H, Gap));

        // 0.1 less: no room, so it goes above the selection.
        Equal(new ViewRect(250, 63.9 + 200 + Gap, BarW, BarH), RegionEditGeometry.PlaceBar(new ViewRect(200, 63.9, 400, 200), BarW, BarH, W, H, Gap));
    }

    [Fact]
    public void The_bar_is_above_when_exactly_a_gap_is_left_over_it()
    {
        // top 536 + gap 12 + bar 40 = 588 = height − gap
        Equal(new ViewRect(250, 548, BarW, BarH), RegionEditGeometry.PlaceBar(new ViewRect(200, 30, 400, 506), BarW, BarH, W, H, Gap));
    }

    [Fact]
    public void The_bar_goes_inside_when_it_does_not_fit_above_by_a_little()
    {
        // top 540 + 12 + 40 = 592 > 588: not above; inside at the selection's bottom + gap.
        Equal(new ViewRect(250, 42, BarW, BarH), RegionEditGeometry.PlaceBar(new ViewRect(200, 30, 400, 510), BarW, BarH, W, H, Gap));
    }

    [Fact]
    public void The_bar_inside_stays_on_the_display_vertically()
    {
        // A 100 high display: neither below nor above fits; bottom + gap = 67 would push the 40 high bar past 100.
        Equal(new ViewRect(250, 60, BarW, BarH), RegionEditGeometry.PlaceBar(new ViewRect(200, 55, 400, 40), BarW, BarH, W, 100, Gap));
    }

    [Fact]
    public void A_bar_wider_than_the_display_minus_the_gap_starts_at_the_smaller_margin()
    {
        // 990 wide on 1000: the gap would be 12 but only 10 are free.
        Assert.Equal(10, RegionEditGeometry.PlaceBar(Sel, 990, BarH, W, H, Gap).X, Precision);
        Assert.Equal(-100, RegionEditGeometry.PlaceBar(Sel, 1100, BarH, W, H, Gap).X, Precision);
    }

    // ---------------------------------------------------------------- to a region

    [Fact]
    public void To_region_clamps_the_size_to_what_is_left_of_the_display()
    {
        // x 0.8: only 0.2 of width is left of the 0.4 asked for.
        NormalizedRect wide = RegionEditGeometry.ToRegion(new ViewRect(800, 100, 400, 300), W, H);
        Assert.Equal(0.8, wide.X, Precision);
        Assert.Equal(0.2, wide.Width, Precision);

        // The selection hangs off the display's bottom: top-left y 0.5 (600 − (−100) − 400 = 300), 0.5 of height left.
        NormalizedRect tall = RegionEditGeometry.ToRegion(new ViewRect(200, -100, 400, 400), W, H);
        Assert.Equal(0.5, tall.Y, Precision);
        Assert.Equal(0.5, tall.Height, Precision);
    }
}
