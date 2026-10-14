using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

public class GestureOutputTests
{
    [Fact]
    public void Natural_scrolling_follows_the_fingers()
    {
        Assert.Equal(1, GestureOutput.ScrollDirectionSign(naturalScrolling: true));
        GestureOutput.ToScrollWheel(3, 10, naturalScrolling: true, out double wx, out double wy);
        Assert.Equal(3, wx);
        Assert.Equal(10, wy); // fingers down -> positive wheel (content moves down)
    }

    [Fact]
    public void Classic_scrolling_inverts_the_fingers()
    {
        Assert.Equal(-1, GestureOutput.ScrollDirectionSign(naturalScrolling: false));
        GestureOutput.ToScrollWheel(3, 10, naturalScrolling: false, out double wx, out double wy);
        Assert.Equal(-3, wx);
        Assert.Equal(-10, wy);
    }

    [Fact]
    public void TakeWhole_keeps_the_fraction_for_later()
    {
        double r = 0;
        Assert.Equal(0, GestureOutput.TakeWhole(0.4, ref r));
        Assert.Equal(0, GestureOutput.TakeWhole(0.4, ref r));
        Assert.Equal(1, GestureOutput.TakeWhole(0.4, ref r));
        Assert.Equal(0.2, r, 1e-9);
        Assert.Equal(2, GestureOutput.TakeWhole(2.0, ref r));
        Assert.Equal(0.2, r, 1e-9);
    }

    [Fact]
    public void TakeWhole_rounds_toward_zero_for_negative_deltas()
    {
        double r = 0;
        Assert.Equal(-1, GestureOutput.TakeWhole(-1.7, ref r));
        Assert.Equal(-0.7, r, 1e-9);
        Assert.Equal(-1, GestureOutput.TakeWhole(-0.5, ref r));
        Assert.Equal(-0.2, r, 1e-9);
        Assert.Equal(0, GestureOutput.TakeWhole(0.3, ref r));
        Assert.Equal(0.1, r, 1e-9);
    }

    [Fact]
    public void TakeWhole_ignores_non_finite_deltas()
    {
        double r = 0.5;
        Assert.Equal(0, GestureOutput.TakeWhole(double.NaN, ref r));
        Assert.Equal(0.5, r);
    }

    [Fact]
    public void Scale_and_rotation_map_to_trackpad_conventions()
    {
        Assert.Equal(0.25, GestureOutput.ToMagnification(1.25), 1e-12);
        Assert.Equal(-0.2, GestureOutput.ToMagnification(0.8), 1e-12);
        Assert.Equal(-15, GestureOutput.ToCounterClockwiseDegrees(15));
    }
}
