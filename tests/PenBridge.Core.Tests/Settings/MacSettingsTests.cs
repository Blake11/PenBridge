using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Settings;

/// <summary>The Mac setting table and validator (ADR-0030, #154 key table).</summary>
public class MacSettingsTests
{
    private static readonly MacSettingValues Defaults = new();

    private static readonly DisplayListEntry[] NoDisplays = [];

    private static bool Valid(in SettingEntry entry, MacSettingValues? current = null, IReadOnlyList<DisplayListEntry>? displays = null) =>
        MacSettings.TryValidate(entry, current ?? Defaults, displays ?? NoDisplays, out _);

    [Fact]
    public void Table_matches_the_contract()
    {
        (MacSettingKey Key, int Number, SettingType Type, string Default)[] expected =
        [
            (MacSettingKey.TargetDisplay, 1, SettingType.String, string.Empty),
            (MacSettingKey.Region, 2, SettingType.Rect, "0,0,1,1"),
            (MacSettingKey.RegionAspectLock, 3, SettingType.Bool, "false"),
            (MacSettingKey.StylusMode, 10, SettingType.Int, "0"),
            (MacSettingKey.MouseSpeed, 11, SettingType.Float, "1"),
            (MacSettingKey.MouseAcceleration, 12, SettingType.Bool, "true"),
            (MacSettingKey.SideButton, 13, SettingType.Int, "0"),
            (MacSettingKey.TipFeel, 14, SettingType.Float, "0"),
            (MacSettingKey.ClickThreshold, 15, SettingType.Float, "0"),
            (MacSettingKey.FullPressureAt, 16, SettingType.Float, "1"),
            (MacSettingKey.SmartZoom, 20, SettingType.Int, "0"),
            (MacSettingKey.PointerSpeed, 21, SettingType.Float, "1"),
            (MacSettingKey.ScrollSpeed, 22, SettingType.Float, "1"),
            (MacSettingKey.Zoom, 23, SettingType.Bool, "true"),
            (MacSettingKey.Rotate, 24, SettingType.Bool, "true"),
            (MacSettingKey.ThreeFingerDrag, 25, SettingType.Bool, "true"),
            (MacSettingKey.FourFingerGestures, 26, SettingType.Bool, "true"),
        ];
        Assert.Equal(expected.Length, MacSettings.All.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            MacSettingDescriptor d = MacSettings.All[i];
            Assert.Equal(expected[i].Key, d.Key);
            Assert.Equal(expected[i].Number, (int)d.Key);
            Assert.Equal(expected[i].Type, d.Type);
            Assert.Equal(expected[i].Default, d.Default.FormatValue());
            Assert.Equal((ushort)d.Key, d.Default.Key);
            Assert.True(Valid(d.Default), d.Name);
            Assert.Equal(d.Default, MacSettings.Defaults[i]);
        }

        Assert.Equal(["Pen", "Mouse"], MacSettings.Get(MacSettingKey.StylusMode).Values);
        Assert.Equal(["RightClick", "MiddleClick", "Eraser", "DoubleClick", "None"], MacSettings.Get(MacSettingKey.SideButton).Values);
        Assert.Equal(["FollowSystem", "On", "Off"], MacSettings.Get(MacSettingKey.SmartZoom).Values);
    }

    [Theory]
    [InlineData(MacSettingKey.MouseSpeed, 0.25f, 4f)]
    [InlineData(MacSettingKey.PointerSpeed, 0.25f, 4f)]
    [InlineData(MacSettingKey.ScrollSpeed, 0.25f, 4f)]
    [InlineData(MacSettingKey.TipFeel, -1f, 1f)]
    [InlineData(MacSettingKey.ClickThreshold, 0f, 0.5f)]
    [InlineData(MacSettingKey.FullPressureAt, 0.5f, 1f)]
    public void Float_ranges(MacSettingKey key, float min, float max)
    {
        // At the defaults (threshold 0, full 1) every in-range end keeps the pressure pair valid; it has its own tests.
        MacSettingValues current = Defaults;
        Assert.True(Valid(SettingEntry.Float(key, min), current));
        Assert.True(Valid(SettingEntry.Float(key, max), current));
        Assert.True(Valid(SettingEntry.Float(key, (min + max) / 2), current));
        Assert.False(Valid(SettingEntry.Float(key, BitDecrement(min)), current));
        Assert.False(Valid(SettingEntry.Float(key, BitIncrement(max)), current));
        Assert.False(Valid(SettingEntry.Int(key, 1), current)); // type mismatch
    }

    [Theory]
    [InlineData(MacSettingKey.StylusMode, 2)]
    [InlineData(MacSettingKey.SideButton, 5)]
    [InlineData(MacSettingKey.SmartZoom, 3)]
    public void Int_enums(MacSettingKey key, int count)
    {
        for (int v = 0; v < count; v++)
        {
            Assert.True(Valid(SettingEntry.Int(key, v)));
        }

        Assert.False(Valid(SettingEntry.Int(key, count)));
        Assert.False(Valid(SettingEntry.Int(key, -1)));
        Assert.False(Valid(SettingEntry.Float(key, 0)));
    }

    [Theory]
    [InlineData(MacSettingKey.RegionAspectLock)]
    [InlineData(MacSettingKey.MouseAcceleration)]
    [InlineData(MacSettingKey.Zoom)]
    [InlineData(MacSettingKey.Rotate)]
    [InlineData(MacSettingKey.ThreeFingerDrag)]
    [InlineData(MacSettingKey.FourFingerGestures)]
    public void Bools(MacSettingKey key)
    {
        Assert.True(Valid(SettingEntry.Bool(key, false)));
        Assert.True(Valid(SettingEntry.Bool(key, true)));
        Assert.False(Valid(SettingEntry.Int(key, 1)));
    }

    [Fact]
    public void Target_display()
    {
        DisplayListEntry[] displays = [new("uuid-1", "Studio", true, 2560, 1440)];
        Assert.True(Valid(SettingEntry.String(MacSettingKey.TargetDisplay, MacSettings.MainDisplay), displays: displays));
        Assert.True(Valid(SettingEntry.String(MacSettingKey.TargetDisplay, MacSettings.AllDisplays), displays: displays));
        Assert.True(Valid(SettingEntry.String(MacSettingKey.TargetDisplay, "uuid-1"), displays: displays));
        Assert.False(Valid(SettingEntry.String(MacSettingKey.TargetDisplay, "uuid-2"), displays: displays));
        Assert.False(Valid(SettingEntry.String(MacSettingKey.TargetDisplay, "uuid-1"))); // an empty list knows no ids
        Assert.True(Valid(SettingEntry.String(MacSettingKey.TargetDisplay, MacSettings.AllDisplays)));
        Assert.False(Valid(SettingEntry.Bool(MacSettingKey.TargetDisplay, true)));
    }

    [Fact]
    public void The_mac_side_needs_its_display_list()
    {
        SettingEntry target = SettingEntry.String(MacSettingKey.TargetDisplay, "uuid-1");
        Assert.Throws<ArgumentNullException>(() => MacSettings.TryValidate(target, Defaults, null!, out _));
        Assert.Throws<ArgumentNullException>(() => new MacSettingValues().ApplyUpdate([target], null!));
        Assert.Throws<ArgumentNullException>(() => new MacSettingValues().TryApply(target, null!));
        Assert.Empty(new MacSettingValues().ApplyUpdate([target], NoDisplays));
    }

    [Fact]
    public void The_phone_side_takes_the_macs_target_display_as_sent()
    {
        MacSettingValues values = MacSettingValues.FromEntries([SettingEntry.String(MacSettingKey.TargetDisplay, "uuid-9")]);
        Assert.Equal("uuid-9", values.TargetDisplay);
    }

    [Fact]
    public void Region_is_pulled_exactly_inside_the_display()
    {
        Assert.True(Valid(SettingEntry.Rect(MacSettingKey.Region, new NormalizedRect(0.1, 0.2, 0.5, 0.5))));
        Assert.False(Valid(SettingEntry.Bool(MacSettingKey.Region, true)));

        // 0.1f + 0.9f rounds past 1 in float; the wire allows that much, the validator trims it.
        SettingEntry edge = SettingEntry.Rect(MacSettingKey.Region, new NormalizedRect(0.3f, 0.7f, 0.7f, 0.3f));
        Assert.True(MacSettings.TryValidate(edge, Defaults, NoDisplays, out SettingEntry normalized));
        NormalizedRect r = normalized.RectValue;
        Assert.True(r.X + r.Width <= 1 + 1e-6);
        Assert.True(r.Y + r.Height <= 1 + 1e-6);
    }

    [Theory]
    [InlineData(0, 0, 0.05f, 0.05f, true)]
    [InlineData(0.95f, 0.95f, 0.05f, 0.05f, true)]
    [InlineData(0, 0, 1, 0.05f, true)]
    [InlineData(0, 0, 0.0499999f, 1, false)]
    [InlineData(0, 0, 1, 0.0499999f, false)]
    [InlineData(0.5f, 0.5f, 0.01f, 0.01f, false)]
    public void Region_is_at_least_five_percent_wide_and_high(float x, float y, float w, float h, bool valid)
    {
        Assert.Equal(0.05f, MacSettings.MinRegionSize);
        Assert.Equal(valid, Valid(SettingEntry.Rect(MacSettingKey.Region, new NormalizedRect(x, y, w, h))));
        Assert.False(Valid(SettingEntry.Rect(MacSettingKey.Region, new NormalizedRect(0, 0, MathF.BitDecrement(MacSettings.MinRegionSize), 1))));
    }

    [Fact]
    public void Unknown_keys_are_rejected() =>
        Assert.False(Valid(SettingEntry.Bool(99, true)));

    [Fact]
    public void Pressure_pair_needs_full_above_threshold_plus_a_tenth()
    {
        Assert.True(MacSettings.IsPressurePairValid(0, 1));
        Assert.True(MacSettings.IsPressurePairValid(0.4f, 0.55f));
        Assert.False(MacSettings.IsPressurePairValid(0.4f, 0.5f));
        Assert.False(MacSettings.IsPressurePairValid(0.5f, 0.6f));

        var current = MacSettingValues.FromEntries([SettingEntry.Float(MacSettingKey.FullPressureAt, 0.6f)]);
        Assert.Equal(0.6f, current.FullPressureAt);
        Assert.True(Valid(SettingEntry.Float(MacSettingKey.ClickThreshold, 0.45f), current));
        Assert.False(Valid(SettingEntry.Float(MacSettingKey.ClickThreshold, 0.5f), current));

        Assert.True(current.TryApply(SettingEntry.Float(MacSettingKey.ClickThreshold, 0.4f), NoDisplays));
        Assert.False(Valid(SettingEntry.Float(MacSettingKey.FullPressureAt, 0.5f), current));
        Assert.True(Valid(SettingEntry.Float(MacSettingKey.FullPressureAt, 0.55f), current));
        Assert.False(current.TryApply(SettingEntry.Float(MacSettingKey.FullPressureAt, 0.5f), NoDisplays));
        Assert.Equal(0.6f, current.FullPressureAt);
    }

    [Fact]
    public void One_update_can_move_both_pressure_ends()
    {
        var values = MacSettingValues.FromEntries(
        [
            SettingEntry.Float(MacSettingKey.ClickThreshold, 0.4f),
            SettingEntry.Float(MacSettingKey.FullPressureAt, 0.6f),
        ]);

        // Full 0.5 alone breaks the rule against threshold 0.4; together with threshold 0.1 it's fine, in any order.
        IReadOnlyList<SettingEntry> changed = values.ApplyUpdate(
        [
            SettingEntry.Float(MacSettingKey.FullPressureAt, 0.5f),
            SettingEntry.Float(MacSettingKey.ClickThreshold, 0.1f),
        ],
            NoDisplays);
        Assert.Equal(2, changed.Count);
        Assert.Equal(0.1f, values.ClickThreshold);
        Assert.Equal(0.5f, values.FullPressureAt);

        // A combined result that breaks it drops both pressure entries, but keeps the rest of the update.
        changed = values.ApplyUpdate(
        [
            SettingEntry.Float(MacSettingKey.ClickThreshold, 0.45f),
            SettingEntry.Bool(MacSettingKey.Zoom, false),
        ],
            NoDisplays);
        Assert.Equal([SettingEntry.Bool(MacSettingKey.Zoom, false)], changed);
        Assert.Equal(0.1f, values.ClickThreshold);
    }

    [Fact]
    public void Apply_update_ignores_invalid_entries_and_reports_changes()
    {
        var values = new MacSettingValues();
        IReadOnlyList<SettingEntry> changed = values.ApplyUpdate(
        [
            SettingEntry.Int(MacSettingKey.StylusMode, 1),
            SettingEntry.Float(MacSettingKey.MouseSpeed, 9f), // out of range
            SettingEntry.Bool(MacSettingKey.Rotate, true), // unchanged
            SettingEntry.Bool(500, true), // unknown key
            SettingEntry.Int(MacSettingKey.Zoom, 0), // wrong type
            SettingEntry.Int(MacSettingKey.SideButton, 2),
            SettingEntry.Int(MacSettingKey.SideButton, 3), // later wins
        ],
            NoDisplays);
        Assert.Equal([SettingEntry.Int(MacSettingKey.StylusMode, 1), SettingEntry.Int(MacSettingKey.SideButton, 3)], changed);
        Assert.Equal(1, values.StylusMode);
        Assert.Equal(3, values.SideButton);
        Assert.Equal(1f, values.MouseSpeed);
        Assert.True(values.Zoom);
    }

    [Fact]
    public void Values_round_trip_through_a_snapshot()
    {
        var values = new MacSettingValues();
        values.ApplyUpdate(
        [
            SettingEntry.String(MacSettingKey.TargetDisplay, "all"),
            SettingEntry.Rect(MacSettingKey.Region, new NormalizedRect(0, 0, 0.5, 0.5)),
            SettingEntry.Bool(MacSettingKey.RegionAspectLock, true),
            SettingEntry.Int(MacSettingKey.SmartZoom, 2),
            SettingEntry.Float(MacSettingKey.TipFeel, -0.5f),
            SettingEntry.Float(MacSettingKey.PointerSpeed, 2f),
            SettingEntry.Float(MacSettingKey.ScrollSpeed, 0.5f),
            SettingEntry.Bool(MacSettingKey.MouseAcceleration, false),
            SettingEntry.Bool(MacSettingKey.ThreeFingerDrag, false),
            SettingEntry.Bool(MacSettingKey.FourFingerGestures, false),
        ],
            NoDisplays);
        IReadOnlyList<SettingEntry> entries = values.ToEntries();
        Assert.Equal(MacSettings.All.Count, entries.Count);
        var buffer = new byte[ControlFrame.GetFrameSize(new SettingsSnapshotMessage(entries))];
        ControlFrame.Write(buffer, new SettingsSnapshotMessage(entries));
        Assert.True(SettingsSnapshotMessage.TryRead(buffer.AsSpan(ControlFrame.HeaderSize), out SettingsSnapshotMessage snapshot));
        MacSettingValues back = MacSettingValues.FromEntries(snapshot.Entries);
        Assert.Equal(entries, back.ToEntries());
        Assert.Equal("all", back.TargetDisplay);
        Assert.Equal(new NormalizedRect(0, 0, 0.5, 0.5), back.Region);
        Assert.True(back.RegionAspectLock);
        Assert.Equal(2, back.SmartZoom);
        Assert.Equal(-0.5f, back.TipFeel);
        Assert.Equal(2f, back.PointerSpeed);
        Assert.Equal(0.5f, back.ScrollSpeed);
        Assert.False(back.MouseAcceleration);
        Assert.False(back.ThreeFingerDrag);
        Assert.False(back.FourFingerGestures);
        Assert.True(back.Rotate);

        MacSettingValues clone = back.Clone();
        clone.TryApply(SettingEntry.Bool(MacSettingKey.Rotate, false), NoDisplays);
        Assert.True(back.Rotate);
    }

    [Theory]
    [InlineData("StylusMode=1", "StylusMode=Mouse")]
    [InlineData("stylusmode=mouse", "StylusMode=Mouse")]
    [InlineData("10=0", "StylusMode=Pen")]
    [InlineData("Zoom=off", "Zoom=false")]
    [InlineData("MouseSpeed=2.5", "MouseSpeed=2.5")]
    [InlineData("TargetDisplay=all", "TargetDisplay=all")]
    [InlineData("Region=0,0,0.5,0.5", "Region=0,0,0.5,0.5")]
    [InlineData("SideButton=eraser", "SideButton=Eraser")]
    public void Assignments_parse_and_format(string text, string formatted)
    {
        Assert.True(MacSettings.TryParseAssignment(text, out SettingEntry entry));
        Assert.Equal(formatted, MacSettings.Format(entry));
    }

    [Theory]
    [InlineData("Nope=1")]
    [InlineData("StylusMode")]
    [InlineData("=1")]
    [InlineData("Zoom=maybe")]
    [InlineData("MouseSpeed=fast")]
    [InlineData("MouseSpeed=NaN")]
    [InlineData("Region=0,0,2,1")]
    [InlineData("Region=0,0,1")]
    [InlineData("StylusMode=Pencil")]
    public void Bad_assignments_are_rejected(string text) =>
        Assert.False(MacSettings.TryParseAssignment(text, out _));

    private static float BitDecrement(float v) => MathF.BitDecrement(v);

    private static float BitIncrement(float v) => MathF.BitIncrement(v);
}
