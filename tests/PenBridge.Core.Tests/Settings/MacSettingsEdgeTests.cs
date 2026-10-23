using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Settings;

/// <summary>Edges of the Mac setting table and validator (#279): epsilon bounds, bool spellings, string limit, formatting.</summary>
public class MacSettingsEdgeTests
{
    private static readonly DisplayListEntry[] NoDisplays = [];

    // ---------------------------------------------------------------- full region

    [Theory]
    [InlineData(1e-6, 0, 1, 1, true)]
    [InlineData(0, 1e-6, 1, 1, true)]
    [InlineData(0, 0, 1 - 1e-6, 1, true)]
    [InlineData(0, 0, 1, 1 - 1e-6, true)]
    [InlineData(2e-6, 0, 1, 1, false)]
    [InlineData(0, 2e-6, 1, 1, false)]
    [InlineData(0, 0, 1 - 2e-6, 1, false)]
    [InlineData(0, 0, 1, 1 - 2e-6, false)]
    public void Full_region_epsilon_is_inclusive_on_each_side_and_each_side_counts(double x, double y, double w, double h, bool full) =>
        Assert.Equal(full, MacSettings.IsFullRegion(new NormalizedRect(x, y, w, h)));

    // ---------------------------------------------------------------- validation results

    [Fact]
    public void A_rejected_pressure_pair_leaves_a_default_normalized_entry()
    {
        var current = new MacSettingValues();
        Assert.True(current.TryApply(SettingEntry.Float(MacSettingKey.FullPressureAt, 0.55f), NoDisplays));

        SettingEntry tooHigh = SettingEntry.Float(MacSettingKey.ClickThreshold, 0.5f);
        Assert.False(MacSettings.TryValidate(tooHigh, current, NoDisplays, out SettingEntry normalized));
        Assert.Equal(default, normalized);
    }

    [Fact]
    public void Negative_zero_is_normalized_to_positive_zero()
    {
        Assert.True(MacSettings.TryValidate(SettingEntry.Float(MacSettingKey.TipFeel, -0f), new MacSettingValues(), NoDisplays, out SettingEntry normalized));
        Assert.Equal(0f, normalized.FloatValue);
        Assert.False(float.IsNegative(normalized.FloatValue));
    }

    [Fact]
    public void A_region_is_trimmed_to_exactly_what_is_left_of_the_display()
    {
        // 0.5 + 0.5000003 is within the wire's tolerance past 1; the validated width is exactly 0.5.
        var entry = SettingEntry.Rect(MacSettingKey.Region, new NormalizedRect(0.5f, 0.25f, 0.5000003f, 0.75000024f));
        Assert.True(MacSettings.TryValidate(entry, new MacSettingValues(), NoDisplays, out SettingEntry normalized));
        NormalizedRect r = normalized.RectValue;
        Assert.Equal(0.5, r.X);
        Assert.Equal(0.5, r.Width);
        Assert.Equal(0.25, r.Y);
        Assert.Equal(0.75, r.Height);
    }

    [Fact]
    public void An_int_index_is_valid_below_the_value_count_only()
    {
        var values = new MacSettingValues();
        int count = MacSettings.Get(MacSettingKey.StylusMode).Values.Count;
        Assert.True(MacSettings.TryValidate(SettingEntry.Int(MacSettingKey.StylusMode, count - 1), values, NoDisplays, out _));
        Assert.False(MacSettings.TryValidate(SettingEntry.Int(MacSettingKey.StylusMode, count), values, NoDisplays, out _));
        Assert.False(MacSettings.TryValidate(SettingEntry.Int(MacSettingKey.StylusMode, -1), values, NoDisplays, out _));
    }

    [Fact]
    public void Looking_up_an_unknown_key_throws_and_a_name_or_number_finds_the_same_descriptor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MacSettings.Get((MacSettingKey)999));
        Assert.Throws<ArgumentNullException>(() => MacSettings.TryGetDescriptor((string)null!, out _));
        Assert.True(MacSettings.TryGetDescriptor("zoom", out MacSettingDescriptor? byName));
        Assert.True(MacSettings.TryGetDescriptor(((ushort)MacSettingKey.Zoom).ToString(System.Globalization.CultureInfo.InvariantCulture), out MacSettingDescriptor? byNumber));
        Assert.Same(byName, byNumber);
        Assert.False(MacSettings.TryGetDescriptor("999", out _));
        Assert.False(MacSettings.TryGetDescriptor("-1", out _));
    }

    // ---------------------------------------------------------------- parsing

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData(" yes ", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("NO", false)]
    [InlineData(" no ", false)]
    public void Bool_spellings_parse(string spelling, bool expected)
    {
        Assert.True(MacSettings.TryParseAssignment("Zoom=" + spelling, out SettingEntry entry));
        Assert.Equal((ushort)MacSettingKey.Zoom, entry.Key);
        Assert.Equal(expected, entry.BoolValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("y")]
    [InlineData("nope")]
    public void Other_bool_spellings_are_rejected(string spelling) =>
        Assert.False(MacSettings.TryParseAssignment("Zoom=" + spelling, out _));

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void A_string_of_exactly_the_wire_limit_is_accepted(int bytes, bool accepted)
    {
        Assert.Equal(256, WireString.MaxBytes);
        Assert.Equal(accepted, MacSettings.TryParseAssignment("TargetDisplay=" + new string('a', bytes), out SettingEntry entry));
        if (accepted)
        {
            Assert.Equal(bytes, entry.StringValue.Length);
        }
    }

    [Theory]
    [InlineData(128, true)] // 2 bytes each
    [InlineData(129, false)]
    public void The_string_limit_counts_utf8_bytes(int chars, bool accepted) =>
        Assert.Equal(accepted, MacSettings.TryParseAssignment("TargetDisplay=" + new string('é', chars), out _));

    [Theory]
    [InlineData("Region=a,0,1,1")]
    [InlineData("Region=0,0,1,b")]
    [InlineData("Region=0,0,1,1,1")]
    [InlineData("Region=0,0,0,1")]
    public void Malformed_rects_are_rejected(string text) =>
        Assert.False(MacSettings.TryParseAssignment(text, out _));

    [Fact]
    public void Parsing_null_throws() =>
        Assert.Throws<ArgumentNullException>(() => MacSettings.TryParseAssignment(null!, out _));

    // ---------------------------------------------------------------- formatting

    [Fact]
    public void An_unknown_key_or_a_wrong_type_formats_as_the_raw_entry()
    {
        var unknown = SettingEntry.Bool(999, true);
        Assert.Equal(unknown.ToString(), MacSettings.Format(unknown));

        var wrongType = SettingEntry.Int(MacSettingKey.Zoom, 1);
        Assert.Equal(wrongType.ToString(), MacSettings.Format(wrongType));
    }

    [Theory]
    [InlineData(0, "StylusMode=Pen")]
    [InlineData(1, "StylusMode=Mouse")]
    [InlineData(2, "StylusMode=2")]
    [InlineData(-1, "StylusMode=-1")]
    public void An_int_index_outside_the_value_names_formats_as_the_number(int index, string expected) =>
        Assert.Equal(expected, MacSettings.Format(SettingEntry.Int(MacSettingKey.StylusMode, index)));

    // ---------------------------------------------------------------- values

    [Fact]
    public void The_first_key_is_a_known_key_for_the_value_accessors()
    {
        var values = new MacSettingValues();
        Assert.True(values.Carries(MacSettingKey.TargetDisplay));
        Assert.Equal(MacSettings.MainDisplay, values.Get(MacSettingKey.TargetDisplay).StringValue);
        Assert.Throws<ArgumentOutOfRangeException>(() => values.Get((MacSettingKey)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => values.Carries((MacSettingKey)0));
    }
}
