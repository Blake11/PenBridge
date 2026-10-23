using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>A window as the target (ADR-0036): the display holding most of it, clipped and normalized to it.</summary>
public class WindowTargetGeometryTests
{
    private static readonly DisplayRect Main = new(0, 0, 1000, 500);
    private static readonly DisplayRect Right = new(1000, 0, 800, 600);
    private static readonly DisplayRect Above = new(0, -400, 1000, 400);

    [Fact]
    public void A_window_inside_one_display_is_normalized_to_it()
    {
        Assert.True(WindowTargetGeometry.TryFit(new DisplayRect(100, 50, 500, 200), [Main, Right], out int index, out NormalizedRect region));
        Assert.Equal(0, index);
        Assert.Equal(new NormalizedRect(0.1, 0.1, 0.5, 0.4), region);
    }

    [Fact]
    public void A_window_on_a_second_display_is_relative_to_that_display()
    {
        Assert.True(WindowTargetGeometry.TryFit(new DisplayRect(1200, 150, 400, 300), [Main, Right], out int index, out NormalizedRect region));
        Assert.Equal(1, index);
        Assert.Equal(new NormalizedRect(0.25, 0.25, 0.5, 0.5), region);
    }

    [Fact]
    public void A_window_spanning_two_displays_maps_its_part_on_the_larger_share()
    {
        // 300 pt on Main, 100 pt on Right: Main wins and the window is clipped at its right edge.
        Assert.True(WindowTargetGeometry.TryFit(new DisplayRect(700, 100, 400, 200), [Main, Right], out int index, out NormalizedRect region));
        Assert.Equal(0, index);
        Assert.Equal(0.7, region.X, 9);
        Assert.Equal(0.2, region.Y, 9);
        Assert.Equal(0.3, region.Width, 9);
        Assert.Equal(0.4, region.Height, 9);

        // Mostly on Right: clipped at Right's left edge.
        Assert.True(WindowTargetGeometry.TryFit(new DisplayRect(900, 0, 500, 200), [Main, Right], out index, out region));
        Assert.Equal(1, index);
        Assert.Equal(new NormalizedRect(0, 0, 0.5, 200.0 / 600), region);
    }

    [Fact]
    public void A_window_above_the_main_display_uses_negative_coordinates()
    {
        Assert.True(WindowTargetGeometry.TryFit(new DisplayRect(0, -300, 500, 200), [Main, Above], out int index, out NormalizedRect region));
        Assert.Equal(1, index);
        Assert.Equal(new NormalizedRect(0, 0.25, 0.5, 0.5), region);
    }

    [Fact]
    public void A_window_larger_than_its_display_is_clipped_to_the_full_display()
    {
        Assert.True(WindowTargetGeometry.TryFit(new DisplayRect(-50, -20, 1100, 600), [Main], out int index, out NormalizedRect region));
        Assert.Equal(0, index);
        Assert.Equal(NormalizedRect.Full, region);
    }

    [Fact]
    public void A_tie_goes_to_the_earlier_display()
    {
        var left = new DisplayRect(0, 0, 100, 100);
        var right = new DisplayRect(100, 0, 100, 100);
        Assert.Equal(0, WindowTargetGeometry.DisplayIndexFor(new DisplayRect(50, 0, 100, 100), [left, right]));
        Assert.Equal(0, WindowTargetGeometry.DisplayIndexFor(new DisplayRect(50, 0, 100, 100), [right, left])); // right, listed first
    }

    [Theory]
    [InlineData(5000, 0, 100, 100)] // off every display
    [InlineData(999.5, 10, 100, 100)] // only half a point on Main (and none on another display)
    [InlineData(10, 10, 0, 100)] // empty
    [InlineData(double.NaN, 0, 100, 100)]
    [InlineData(0, 0, double.PositiveInfinity, double.NaN)]
    public void A_window_on_no_display_does_not_fit(double x, double y, double width, double height)
    {
        Assert.False(WindowTargetGeometry.TryFit(new DisplayRect(x, y, width, height), [Main], out int index, out NormalizedRect region));
        Assert.Equal(-1, index);
        Assert.Equal(NormalizedRect.Full, region);
    }

    [Fact]
    public void No_displays_does_not_fit()
    {
        Assert.False(WindowTargetGeometry.TryFit(new DisplayRect(0, 0, 10, 10), [], out int index, out _));
        Assert.Equal(-1, index);
    }

    [Fact]
    public void An_empty_display_does_not_fit()
    {
        Assert.False(WindowTargetGeometry.TryRegion(new DisplayRect(0, 0, 10, 10), new DisplayRect(0, 0, 0, 10), out _));
    }

    [Fact]
    public void The_result_is_a_valid_mapping_region()
    {
        // Edges that don't divide evenly must stay inside 0..1 so MappingTarget accepts them.
        var display = new DisplayRect(0.1, 0.3, 1512.7, 982.3);
        Assert.True(WindowTargetGeometry.TryRegion(new DisplayRect(33.3, 17.7, 2000, 2000), display, out NormalizedRect region));
        Assert.InRange(region.X + region.Width, double.NegativeInfinity, 1);
        Assert.InRange(region.Y + region.Height, double.NegativeInfinity, 1);
        MappingTarget target = MappingTarget.ForDisplay(display, region);
        Assert.Equal(region, target.Region);
    }

    [Fact]
    public void A_window_whose_visible_part_is_exactly_the_minimum_side_fits()
    {
        var display = new DisplayRect(0, 0, 100, 100);
        double min = WindowTargetGeometry.MinVisibleSide;
        // Overlaps by exactly MinVisibleSide on each axis (at the right/bottom corner).
        var corner = new DisplayRect(100 - min, 100 - min, 50, 50);
        Assert.True(WindowTargetGeometry.TryRegion(corner, display, out NormalizedRect region));
        Assert.Equal(new NormalizedRect(0.99, 0.99, 0.01, 0.01), region);
        Assert.True(WindowTargetGeometry.TryFit(corner, [display], out int index, out _));
        Assert.Equal(0, index);
        Assert.Equal(0, WindowTargetGeometry.DisplayIndexFor(corner, [display]));

        // Just under it on one axis does not, on either axis.
        double under = min - 1e-6;
        Assert.False(WindowTargetGeometry.TryRegion(new DisplayRect(100 - under, 10, 50, 50), display, out _));
        Assert.False(WindowTargetGeometry.TryRegion(new DisplayRect(10, 100 - under, 50, 50), display, out _));
        Assert.Equal(-1, WindowTargetGeometry.DisplayIndexFor(new DisplayRect(100 - under, 10, 50, 50), [display]));
        Assert.Equal(-1, WindowTargetGeometry.DisplayIndexFor(new DisplayRect(10, 100 - under, 50, 50), [display]));

        // A window of exactly the minimum size inside the display.
        Assert.True(WindowTargetGeometry.TryRegion(new DisplayRect(10, 20, min, min), display, out region));
        Assert.Equal(new NormalizedRect(0.1, 0.2, 0.01, 0.01), region);
        Assert.False(WindowTargetGeometry.TryRegion(new DisplayRect(10, 20, under, min), display, out _));
        Assert.False(WindowTargetGeometry.TryRegion(new DisplayRect(10, 20, min, under), display, out _));
    }

    [Fact]
    public void A_zero_or_negative_size_display_never_fits_and_leaves_the_full_region()
    {
        var window = new DisplayRect(0, 0, 10, 10);
        foreach (DisplayRect display in new[] { new DisplayRect(0, 0, 0, 10), new DisplayRect(0, 0, 10, 0), new DisplayRect(0, 0, -10, 10), new DisplayRect(0, 0, 10, -10), new DisplayRect(0, 0, double.NaN, 10), new DisplayRect(0, 0, 10, double.NaN) })
        {
            Assert.False(WindowTargetGeometry.TryRegion(window, display, out NormalizedRect region));
            Assert.Equal(NormalizedRect.Full, region);
        }
    }

    [Fact]
    public void A_window_clipped_to_a_sliver_on_one_axis_does_not_fit()
    {
        var display = new DisplayRect(0, 0, 100, 100);
        Assert.False(WindowTargetGeometry.TryRegion(new DisplayRect(99.5, 0, 50, 50), display, out _));
        Assert.False(WindowTargetGeometry.TryRegion(new DisplayRect(0, 99.5, 50, 50), display, out _));
        Assert.False(WindowTargetGeometry.TryRegion(new DisplayRect(-49.5, 0, 50, 50), display, out _));
        Assert.False(WindowTargetGeometry.TryRegion(new DisplayRect(0, -49.5, 50, 50), display, out _));
        Assert.False(WindowTargetGeometry.TryRegion(new DisplayRect(10, 10, 0, 50), display, out _));
        Assert.False(WindowTargetGeometry.TryRegion(new DisplayRect(10, 10, 50, 0), display, out _));
    }

    [Fact]
    public void A_region_on_a_display_with_an_origin_is_relative_to_it_on_both_axes()
    {
        var display = new DisplayRect(-500, -200, 400, 100);
        Assert.True(WindowTargetGeometry.TryRegion(new DisplayRect(-400, -175, 100, 50), display, out NormalizedRect region));
        Assert.Equal(new NormalizedRect(0.25, 0.25, 0.25, 0.5), region);
        // Clipped on the left and top: starts at 0, width and height are the visible part only.
        Assert.True(WindowTargetGeometry.TryRegion(new DisplayRect(-600, -250, 200, 100), display, out region));
        Assert.Equal(new NormalizedRect(0, 0, 0.25, 0.5), region);
    }

    [Fact]
    public void Display_index_picks_the_largest_part_and_ignores_slivers()
    {
        var a = new DisplayRect(0, 0, 100, 100);
        var b = new DisplayRect(100, 0, 100, 100);
        // 30 x 50 on A, 20 x 50 on B.
        Assert.Equal(0, WindowTargetGeometry.DisplayIndexFor(new DisplayRect(70, 0, 50, 50), [a, b]));
        Assert.Equal(1, WindowTargetGeometry.DisplayIndexFor(new DisplayRect(70, 0, 50, 50), [b, a])); // listed order does not change the winner
        Assert.Equal(1, WindowTargetGeometry.DisplayIndexFor(new DisplayRect(80, 0, 50, 50), [a, b]));
        // Wide and short on a short A (60 x 10 = 600) versus narrow and tall on B (10 x 100 = 1000): areas, not widths.
        var shortA = new DisplayRect(0, 0, 100, 10);
        Assert.Equal(1, WindowTargetGeometry.DisplayIndexFor(new DisplayRect(40, 0, 70, 100), [shortA, b]));
        Assert.Equal(-1, WindowTargetGeometry.DisplayIndexFor(new DisplayRect(10, 10, 0.5, 0.5), [a, b]));
    }
}
