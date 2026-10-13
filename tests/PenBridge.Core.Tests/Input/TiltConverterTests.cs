using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

public class TiltConverterTests
{
    private static float Rad(double degrees) => (float)(degrees * Math.PI / 180);

    /// <summary>Android's input reader: per-axis HID tilt (radians) → (AXIS_TILT, AXIS_ORIENTATION).</summary>
    private static (float Tilt, float Orientation) AndroidAxes(double tiltXDeg, double tiltYDeg)
    {
        double x = tiltXDeg * Math.PI / 180;
        double y = tiltYDeg * Math.PI / 180;
        return ((float)Math.Acos(Math.Cos(x) * Math.Cos(y)), (float)Math.Atan2(-Math.Sin(x), Math.Sin(y)));
    }

    [Fact]
    public void Vertical_pen_has_no_tilt_whatever_the_orientation()
    {
        foreach (double o in new[] { -180.0, -90, 0, 45, 90, 180 })
        {
            TiltConverter.ToTilt(0, Rad(o), out short x, out short y);
            Assert.Equal(0, x);
            Assert.Equal(0, y);
        }
    }

    // Orientation is where the tip points; the pen top leans the opposite way.
    [Theory]
    [InlineData(-90, 30, 3000, 0)]   // tip points left → top leans right → +x
    [InlineData(90, 30, -3000, 0)]   // tip points right → top leans left → -x
    [InlineData(0, 30, 0, 3000)]     // tip points up → top leans toward the user (down) → +y
    [InlineData(180, 30, 0, -3000)]  // tip points down → top leans away (up) → -y
    [InlineData(-180, 30, 0, -3000)]
    [InlineData(-90, 60, 6000, 0)]
    [InlineData(0, 90, 0, 9000)]     // flat on the surface
    public void Single_axis_lean_maps_to_signed_axis(double orientationDeg, double tiltDeg, int expectedX, int expectedY)
    {
        TiltConverter.ToTilt(Rad(tiltDeg), Rad(orientationDeg), out short x, out short y);
        Assert.InRange(x, expectedX - 1, expectedX + 1);
        Assert.InRange(y, expectedY - 1, expectedY + 1);
    }

    [Fact]
    public void Right_handed_grip_leans_right_and_down()
    {
        // Typical right-handed hold: top toward the lower right, so the tip points up-left (orientation ≈ -45°).
        TiltConverter.ToTilt(Rad(40), Rad(-45), out short x, out short y);
        Assert.True(x > 0);
        Assert.True(y > 0);
        Assert.Equal(x, y);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 0)]
    [InlineData(0, -45)]
    [InlineData(45, 45)]
    [InlineData(-20, 35)]
    [InlineData(60, -10)]
    [InlineData(-70, -70)]
    [InlineData(89, 1)]
    [InlineData(5, 85)]
    public void Inverts_the_android_formula(double tiltXDeg, double tiltYDeg)
    {
        (float tilt, float orientation) = AndroidAxes(tiltXDeg, tiltYDeg);
        TiltConverter.ToTilt(tilt, orientation, out short x, out short y);
        Assert.InRange(x, (tiltXDeg * 100) - 2, (tiltXDeg * 100) + 2);
        Assert.InRange(y, (tiltYDeg * 100) - 2, (tiltYDeg * 100) + 2);
    }

    [Fact]
    public void Output_is_clamped_and_finite_for_any_input()
    {
        float[] values = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, -10, -1, 0, 0.7f, 1.5707964f, 2, 4, 100];
        foreach (float t in values)
        {
            foreach (float o in values)
            {
                TiltConverter.ToTilt(t, o, out short x, out short y);
                Assert.InRange(x, -TiltConverter.MaxTilt, TiltConverter.MaxTilt);
                Assert.InRange(y, -TiltConverter.MaxTilt, TiltConverter.MaxTilt);
                Assert.True(TiltConverter.ToRotation(o) <= 35999);
            }
        }
    }

    [Fact]
    public void Tilt_beyond_flat_is_clamped_to_flat()
    {
        TiltConverter.ToTilt(Rad(120), Rad(-90), out short x, out short y);
        Assert.Equal(9000, x);
        Assert.Equal(0, y);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 9000)]
    [InlineData(-90, 27000)]
    [InlineData(180, 18000)]
    [InlineData(-180, 18000)]
    [InlineData(45.5, 4550)]
    [InlineData(-0.001, 0)]
    [InlineData(359.999, 0)]
    [InlineData(720, 0)]
    public void Rotation_is_clockwise_centi_degrees_from_up(double orientationDeg, int expected)
    {
        Assert.Equal(expected, TiltConverter.ToRotation(Rad(orientationDeg)));
    }

    [Fact]
    public void Conversion_does_not_allocate()
    {
        float[] tilts = [0, 0.3f, 0.9f, 1.5f];
        float[] orientations = [-3f, -1.5f, 0, 0.8f, 3.1f];
        long ran = 0;
        long sum = 0;
        Alloc.AssertZero(4_000, i =>
        {
            float o = orientations[i % orientations.Length];
            TiltConverter.ToTilt(tilts[i % tilts.Length], o, out short x, out short y);
            sum += x + y + TiltConverter.ToRotation(o);
            ran++;
        }, () => ran);
        Assert.NotEqual(long.MinValue, sum);
    }
}
