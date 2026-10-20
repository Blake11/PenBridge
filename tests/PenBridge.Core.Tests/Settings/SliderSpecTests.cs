using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Settings;

/// <summary>The slider model (#215).</summary>
public class SliderSpecTests
{
    private static SliderSpec Get(MacSettingKey key) => SettingSliders.TryGet(key, out SliderSpec s) ? s : throw new InvalidOperationException();

    private static readonly SliderSpec Speed = Get(MacSettingKey.MouseSpeed);
    private static readonly SliderSpec Linear = new(0, 10, 5, 1, false);

    [Fact]
    public void Every_float_key_has_a_spec_from_the_table_and_others_have_none()
    {
        foreach (MacSettingDescriptor d in MacSettings.All)
        {
            bool has = SettingSliders.TryGet(d.Key, out SliderSpec s);
            Assert.Equal(d.Type == PenBridge.Core.Protocol.SettingType.Float, has);
            if (has)
            {
                Assert.Equal(d.Min, s.Min);
                Assert.Equal(d.Max, s.Max);
                Assert.Equal(d.Default.FloatValue, s.Default);
            }
        }
    }

    [Theory]
    [InlineData(MacSettingKey.MouseSpeed, 0.25, 4, 1, 0.05, true)]
    [InlineData(MacSettingKey.PointerSpeed, 0.25, 4, 1, 0.05, true)]
    [InlineData(MacSettingKey.ScrollSpeed, 0.25, 4, 1, 0.05, true)]
    [InlineData(MacSettingKey.TipFeel, -1, 1, 0, 0.05, false)]
    [InlineData(MacSettingKey.ClickThreshold, 0, 0.5, 0, 0.01, false)]
    [InlineData(MacSettingKey.FullPressureAt, 0.5, 1, 1, 0.01, false)]
    public void Specs_match_the_contract(MacSettingKey key, double min, double max, double def, double step, bool log) =>
        Assert.Equal(new SliderSpec(min, max, def, step, log), Get(key));

    [Fact]
    public void Log_track_puts_one_at_the_middle()
    {
        Assert.Equal(0.5, Speed.ToPosition(1), 12);
        Assert.Equal(0, Speed.ToPosition(0.25), 12);
        Assert.Equal(1, Speed.ToPosition(4), 12);
        Assert.Equal(1, Speed.FromPosition(0.5));
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.3)]
    [InlineData(0.7)]
    [InlineData(0.95)]
    public void Position_round_trips_within_a_step(double position)
    {
        double value = Speed.FromPosition(position);
        Assert.InRange(Speed.ToPosition(value), position - 0.04, position + 0.04);
        Assert.Equal(value, Speed.Coerce(value));
    }

    [Fact]
    public void Linear_position_round_trips()
    {
        Assert.Equal(0.2, Linear.ToPosition(2), 12);
        Assert.Equal(2, Linear.FromPosition(0.2));
        Assert.Equal(0, Linear.FromPosition(0));
        Assert.Equal(10, Linear.FromPosition(1));
    }

    [Fact]
    public void Positions_outside_the_track_clamp()
    {
        Assert.Equal(0.25, Speed.FromPosition(-1));
        Assert.Equal(4, Speed.FromPosition(2));
        Assert.Equal(0, Linear.ToPosition(-5));
        Assert.Equal(1, Linear.ToPosition(50));
    }

    [Fact]
    public void Snaps_to_the_default_inside_SnapWidth_only()
    {
        Assert.Equal(5, Linear.FromPosition(0.5 + 0.029));
        Assert.Equal(5, Linear.FromPosition(0.5 - 0.029));
        Assert.Equal(6, Linear.FromPosition(0.5 + 0.06));
        Assert.Equal(4, Linear.FromPosition(0.5 - 0.06));
        Assert.Equal(1, Speed.FromPosition(0.5 + 0.029));
        Assert.NotEqual(1, Speed.FromPosition(0.5 + 0.05));
    }

    [Fact]
    public void Snaps_at_the_end_when_the_default_is_the_maximum()
    {
        SliderSpec s = Get(MacSettingKey.FullPressureAt);
        Assert.Equal(1, s.FromPosition(0.98));
        Assert.Equal(1, s.FromPosition(1));
    }

    [Fact]
    public void Coerce_rounds_to_step_and_clamps_without_float_noise()
    {
        Assert.Equal(1.05, Speed.Coerce(1.049));
        Assert.Equal(1.05, Speed.Coerce(1.0500000001));
        Assert.Equal(0.25, Speed.Coerce(0.1));
        Assert.Equal(4, Speed.Coerce(9));
        SliderSpec pct = Get(MacSettingKey.ClickThreshold);
        Assert.Equal(0.07, pct.Coerce(0.071));
        Assert.Equal(0.5, pct.Coerce(2));
        Assert.Equal(0, pct.Coerce(-1));
    }

    [Fact]
    public void FromPosition_values_are_exact_step_multiples()
    {
        for (double p = 0; p <= 1; p += 0.013)
        {
            double v = Speed.FromPosition(p);
            Assert.Equal(Math.Round(v / 0.05) * 0.05, v, 9);
            Assert.Equal(v, Math.Round(v, 2));
        }
    }

    [Fact]
    public void Steps_are_additive_and_stop_at_the_ends()
    {
        Assert.Equal(1.05, Speed.StepUp(1));
        Assert.Equal(0.95, Speed.StepDown(1));
        Assert.Equal(1.1, Speed.StepUp(1.05));
        Assert.Equal(4, Speed.StepUp(4));
        Assert.Equal(4, Speed.StepUp(3.99));
        Assert.Equal(0.25, Speed.StepDown(0.25));
        SliderSpec tip = Get(MacSettingKey.TipFeel);
        Assert.Equal(-1, tip.StepDown(-0.98));
        Assert.Equal(0.05, tip.StepUp(0));
        Assert.Equal(-0.05, tip.StepDown(0));
    }

    [Fact]
    public void NaN_and_infinities_are_handled()
    {
        Assert.Equal(1, Speed.Coerce(double.NaN));
        Assert.Equal(0.5, Speed.ToPosition(double.NaN), 12);
        Assert.Equal(0.25, Speed.FromPosition(double.NaN));
        Assert.Equal(4, Speed.Coerce(double.PositiveInfinity));
        Assert.Equal(0.25, Speed.Coerce(double.NegativeInfinity));
        Assert.Equal(1, Speed.ToPosition(double.PositiveInfinity));
        Assert.Equal(0, Speed.ToPosition(double.NegativeInfinity));
        Assert.Equal(4, Speed.FromPosition(double.PositiveInfinity));
        Assert.Equal(0.25, Speed.FromPosition(double.NegativeInfinity));
        Assert.Equal(1.05, Speed.StepUp(double.NaN));
    }

    [Fact]
    public void Empty_range_has_position_zero_and_no_NaN()
    {
        var s = new SliderSpec(2, 2, 2, 0.5, false);
        Assert.Equal(0, s.ToPosition(2));
        Assert.Equal(2, s.FromPosition(0.7));
        Assert.Equal(2, s.Coerce(5));
        Assert.Equal(2, s.StepUp(2));
    }
}
