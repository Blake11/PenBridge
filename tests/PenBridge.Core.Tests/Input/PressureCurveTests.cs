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
    public void A_stroke_releases_only_below_the_threshold_minus_the_hysteresis()
    {
        var c = new PressureCurve(0, 0.3, 0.9);
        Assert.False(c.TryApply(W(0.29), inContact: false, out _)); // pressing needs the threshold
        Assert.True(c.TryApply(W(0.301), inContact: false, out _));
        Assert.True(c.TryApply(W(0.29), inContact: true, out ushort held)); // in contact: down until 0.28
        Assert.Equal(0, held);
        Assert.True(c.TryApply(W(0.281), inContact: true, out _));
        Assert.False(c.TryApply(W(0.279), inContact: true, out ushort lifted));
        Assert.Equal(0, lifted);
        Assert.Equal(0.5, c.TryApply(W(0.6), inContact: true, out ushort o) ? o / 65535.0 : -1, 4); // same curve above
    }

    [Fact]
    public void Hysteresis_release_level_is_clamped_at_zero_and_off_without_a_threshold()
    {
        var low = new PressureCurve(0, 0.01, 0.5);
        Assert.False(low.TryApply(0, inContact: false, out _));
        Assert.True(low.TryApply(0, inContact: true, out _)); // release level clamped to 0: never below it
        var none = new PressureCurve(1, 0, 0.5);
        Assert.True(none.TryApply(0, inContact: false, out _));
        Assert.True(none.TryApply(0, inContact: true, out _));
    }

    [Fact]
    public void Apply_treats_nan_as_zero()
    {
        Assert.Equal(0, PressureCurve.Linear.Apply(double.NaN));
        Assert.Equal(0, default(PressureCurve).Apply(double.NaN));
        Assert.Equal(0, new PressureCurve(1, 0, 0.5).Apply(double.NaN));
        Assert.Equal(0, new PressureCurve(-1, 0.2, 0.8).Apply(double.NaN));
    }

    [Fact]
    public void Try_apply_is_allocation_free()
    {
        var c = new PressureCurve(0.5, 0.1, 0.9);
        long ran = 0;
        Alloc.AssertZero(5_000, i =>
        {
            c.TryApply((ushort)(i * 13), (i & 1) == 0, out _);
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

    [Fact]
    public void Constructor_validates_each_parameter()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PressureCurve(double.NaN, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PressureCurve(double.PositiveInfinity, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PressureCurve(double.NegativeInfinity, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PressureCurve(0, -0.01, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PressureCurve(0, double.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PressureCurve(0, 0, 1.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PressureCurve(0, 0, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PressureCurve(0, 0.5, 0.5)); // full must exceed the threshold
        Assert.Throws<ArgumentOutOfRangeException>(() => new PressureCurve(0, 0.6, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PressureCurve(0, 0, 0));

        // The limits themselves are valid.
        _ = new PressureCurve(-1, 0, 1);
        _ = new PressureCurve(1, 0.5, Math.BitIncrement(0.5));
        _ = new PressureCurve(100, 0, 1);
    }

    [Fact]
    public void Only_the_exact_default_parameters_are_the_identity()
    {
        Assert.True(new PressureCurve(0, 0, 1).IsIdentity);
        Assert.False(new PressureCurve(0.01, 0, 1).IsIdentity);
        Assert.False(new PressureCurve(0, 0.1, 1).IsIdentity);
        Assert.False(new PressureCurve(0, 0, 0.9).IsIdentity);
        Assert.False(default(PressureCurve).IsIdentity);
    }

    [Fact]
    public void Threshold_property_returns_the_click_threshold()
    {
        Assert.Equal(0.25, new PressureCurve(0, 0.25, 0.75).Threshold);
        Assert.Equal(0, PressureCurve.Linear.Threshold);
    }

    [Theory]
    [InlineData(-0.5, 0)]
    [InlineData(0, 0)]
    [InlineData(0.25, 0.25)]
    [InlineData(1, 1)]
    [InlineData(1.0000001, 1)]
    [InlineData(7, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(double.NegativeInfinity, 0)]
    public void Apply_clamps_to_0_1_on_the_identity_and_the_default_struct(double input, double expected)
    {
        Assert.Equal(expected, PressureCurve.Linear.Apply(input));
        Assert.Equal(expected, default(PressureCurve).Apply(input));
    }

    [Fact]
    public void Apply_at_the_threshold_and_just_below_it()
    {
        var c = new PressureCurve(0, 0.2, 0.8);
        Assert.Equal(0, c.Apply(Math.BitDecrement(0.2)));
        Assert.Equal(0, c.Apply(0.2));
        Assert.InRange(c.Apply(Math.BitIncrement(0.2)), double.Epsilon, 1e-12);
        Assert.Equal(1, c.Apply(0.8), 12);
        Assert.Equal(1, c.Apply(0.8000001), 12);
        Assert.Equal(1, c.Apply(1));
        Assert.Equal(1, c.Apply(5));
    }

    [Fact]
    public void Apply_with_a_gamma_clamps_above_full_and_curves_below()
    {
        var firm = new PressureCurve(1, 0.2, 0.8); // gamma 2
        Assert.Equal(1, firm.Apply(0.9), 12); // an unclamped u of 1.17 would give 1.36
        Assert.Equal(1, firm.Apply(1));
        Assert.Equal(0.25, firm.Apply(0.5), 12); // u = 0.5, squared
        var soft = new PressureCurve(-1, 0.2, 0.8); // gamma 0.5
        Assert.Equal(Math.Sqrt(0.5), soft.Apply(0.5), 12);
        Assert.Equal(1, soft.Apply(0.95), 12);
        Assert.Equal(0, soft.Apply(0.2));
        Assert.Equal(0, soft.Apply(0.1));
        Assert.Equal(0, firm.Apply(-1));
    }

    [Fact]
    public void Wire_pressure_at_the_threshold_presses_with_zero_output_and_one_below_hovers()
    {
        const ushort Wire = 30000;
        var c = new PressureCurve(0, Wire / 65535.0, 1);
        Assert.True(c.TryApply(Wire, out ushort atThreshold));
        Assert.Equal(0, atThreshold);
        Assert.False(c.TryApply(Wire - 1, out ushort below));
        Assert.Equal(0, below);
        Assert.True(c.TryApply(Wire + 1, out ushort above));
        Assert.InRange(above, (ushort)1, (ushort)3);
        Assert.True(c.TryApply(ushort.MaxValue, out ushort full));
        Assert.Equal(ushort.MaxValue, full);
    }

    [Fact]
    public void The_two_argument_overload_is_a_new_press_not_a_held_stroke()
    {
        var c = new PressureCurve(0, 0.3, 0.9);
        ushort between = W(0.29);
        Assert.False(c.TryApply(between, out ushort o));
        Assert.Equal(0, o);
        Assert.True(c.TryApply(between, inContact: true, out _));
        Assert.False(c.TryApply(between, inContact: false, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.4)]
    [InlineData(0.6)]
    [InlineData(1)]
    public void Between_the_release_level_and_the_threshold_a_held_stroke_outputs_zero_for_any_gamma(double tipFeel)
    {
        var c = new PressureCurve(tipFeel, 0.3, 0.9);
        Assert.True(c.TryApply(W(0.29), inContact: true, out ushort o));
        Assert.Equal(0, o);
    }

    [Fact]
    public void Release_level_is_the_threshold_minus_the_hysteresis_when_above_it()
    {
        var c = new PressureCurve(0, 0.03, 0.5); // release at 0.01
        Assert.True(c.TryApply(W(0.012), inContact: true, out _));
        Assert.False(c.TryApply(W(0.008), inContact: true, out _));
        Assert.False(c.TryApply(W(0.012), inContact: false, out _));
        var atHysteresis = new PressureCurve(0, PressureCurve.ReleaseHysteresis, 0.5); // release level 0: never lifts
        Assert.True(atHysteresis.TryApply(0, inContact: true, out _));
        Assert.False(atHysteresis.TryApply(0, inContact: false, out _));
    }

    [Fact]
    public void Wire_output_is_rounded_and_never_exceeds_full()
    {
        var c = new PressureCurve(0, 0.2, 0.8);
        Assert.True(c.TryApply(ushort.MaxValue, out ushort full));
        Assert.Equal(ushort.MaxValue, full);
        Assert.True(c.TryApply(W(0.5), out ushort mid));
        Assert.InRange(mid, (ushort)32767, (ushort)32768);
        Assert.True(c.TryApply(W(0.8), out ushort atFull));
        Assert.Equal(ushort.MaxValue, atFull);
    }
}
