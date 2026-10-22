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
}
