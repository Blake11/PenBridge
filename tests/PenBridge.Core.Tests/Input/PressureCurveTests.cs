using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>ADR-0032 pressure curve and the <see cref="InputSettings"/> ranges.</summary>
[Collection(AllocationGroup.Name)]
public class PressureCurveTests
{
    private static ushort W(double p) => (ushort)Math.Round(p * 65535);

    private static double Out(PressureCurve c, double p)
    {
        Assert.True(c.TryApply(W(p), out ushort o));
        return o / 65535.0;
    }

    [Fact]
    public void Default_is_the_identity()
    {
        PressureCurve c = InputSettings.Default.PressureCurve;
        Assert.True(c.IsIdentity);
        for (int p = 0; p <= 65535; p += 4369)
        {
            Assert.True(c.TryApply((ushort)p, out ushort o));
            Assert.Equal(p, o);
        }

        Assert.True(default(PressureCurve).TryApply(1234, out ushort d));
        Assert.Equal(1234, d);
    }

    [Fact]
    public void Soft_raises_and_firm_lowers_the_middle()
    {
        Assert.Equal(Math.Sqrt(0.25), Out(new PressureCurve(-1, 0, 1), 0.25), 4); // γ 0.5
        Assert.Equal(0.25 * 0.25, Out(new PressureCurve(1, 0, 1), 0.25), 4); // γ 2
        Assert.Equal(0.25, Out(new PressureCurve(0, 0, 1), 0.25), 4);
        Assert.Equal(Math.Pow(0.5, Math.Pow(2, 0.5)), Out(new PressureCurve(0.5, 0, 1), 0.5), 4);
    }

    [Fact]
    public void Below_the_threshold_hovers_and_the_range_is_rescaled()
    {
        var c = new PressureCurve(0, 0.2, 0.8);
        Assert.False(c.TryApply(W(0.1), out ushort hover));
        Assert.Equal(0, hover);
        Assert.Equal(0, Out(c, 0.2), 4);
        Assert.Equal(0.5, Out(c, 0.5), 4);
        Assert.Equal(1, Out(c, 0.8), 4);
        Assert.Equal(1, Out(c, 0.95), 4); // full pressure at 0.8
    }

    [Fact]
    public void Ends_stay_at_zero_and_full()
    {
        foreach (double feel in new[] { -1.0, 0, 1 })
        {
            var c = new PressureCurve(feel, 0, 0.5);
            Assert.Equal(0, Out(c, 0), 6);
            Assert.Equal(1, Out(c, 0.5), 6);
            Assert.Equal(1, Out(c, 1), 6);
        }
    }

    [Fact]
    public void Apply_on_unit_pressure_matches_the_wire_path()
    {
        var c = new PressureCurve(0.7, 0.1, 0.9);
        Assert.Equal(0, c.Apply(0.05));
        Assert.Equal(Out(c, 0.4), c.Apply(0.4), 4);
    }

    [Fact]
    public void Try_apply_is_allocation_free()
    {
        var c = new PressureCurve(0.5, 0.1, 0.9);
        long ran = 0;
        Alloc.AssertZero(5_000, i =>
        {
            c.TryApply((ushort)(i * 13), out _);
            ran++;
        }, () => ran);
    }

    [Fact]
    public void Settings_defaults_validate_and_out_of_range_values_throw()
    {
        InputSettings.Default.Validate();
        new InputSettings { ClickThreshold = 0.5, FullPressureAt = 0.6 }.Validate();
        InputSettings[] bad =
        [
            new() { MouseSpeed = 0.2 },
            new() { PointerSpeed = 4.5 },
            new() { ScrollSpeed = double.NaN },
            new() { TipFeel = -1.5 },
            new() { ClickThreshold = 0.6 },
            new() { FullPressureAt = 0.4 },
            new() { ClickThreshold = 0.45, FullPressureAt = 0.5 },
            new() { StylusMode = (StylusMode)2 },
            new() { SideButton = (SideButtonAction)5 },
            new() { SmartZoom = (SmartZoomMode)3 },
        ];
        foreach (InputSettings s in bad)
        {
            Assert.Throws<ArgumentOutOfRangeException>(s.Validate);
        }
    }
}
