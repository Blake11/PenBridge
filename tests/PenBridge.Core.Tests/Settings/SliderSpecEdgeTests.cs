using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Settings;

/// <summary>Slider boundaries (#279): a range that doesn't start at 0, the snap's exact edge, and a zero step.</summary>
public class SliderSpecEdgeTests
{
    [Theory]
    [InlineData(0.0, 2.0)]
    [InlineData(0.2, 4.0)]
    [InlineData(0.5, 7.0)]
    [InlineData(1.0, 12.0)]
    public void A_linear_range_not_starting_at_zero_maps_both_ways(double position, double value)
    {
        var s = new SliderSpec(2, 12, 7, 1, false);
        Assert.Equal(value, s.FromPosition(position), 9);
        Assert.Equal(position, s.ToPosition(value), 9);
    }

    [Theory]
    [InlineData(0.25, -0.5)]
    [InlineData(0.75, 0.5)]
    public void A_symmetric_range_maps_through_its_minimum(double position, double value)
    {
        var s = new SliderSpec(-1, 1, 0, 0.05, false);
        Assert.Equal(value, s.FromPosition(position), 9);
        Assert.Equal(position, s.ToPosition(value), 9);
    }

    [Theory]
    [InlineData(0.03, 0.0)] // exactly SnapWidth from the default's position 0: snapped
    [InlineData(0.0300001, 0.3)] // just past: the value of the position
    public void The_snap_edge_is_inclusive(double position, double expected) =>
        Assert.Equal(expected, new SliderSpec(0, 10, 0, 0.1, false).FromPosition(position), 9);

    [Fact]
    public void A_zero_step_leaves_the_value_unrounded()
    {
        var s = new SliderSpec(0, 10, 5, 0, false);
        Assert.Equal(3.14159, s.Coerce(3.14159), 12);
        Assert.Equal(1.23, s.FromPosition(0.123), 9);
        Assert.Equal(3.14159, s.StepUp(3.14159), 12);
        Assert.Equal(10, s.Coerce(11));
    }

    [Theory]
    [InlineData(2.5, 3.0)]
    [InlineData(2.4999, 2.0)]
    [InlineData(-2.5, -3.0)]
    public void Midpoints_round_away_from_zero(double typed, double expected) =>
        Assert.Equal(expected, new SliderSpec(-10, 10, 0, 1, false).Coerce(typed));
}
