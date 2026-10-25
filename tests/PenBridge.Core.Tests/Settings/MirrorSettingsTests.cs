using PenBridge.Core.Mirror;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Settings;

/// <summary>The protocol 1.3 mirroring keys 30–32 (docs/specs/screen-mirroring.md "Mac settings").</summary>
public class MirrorSettingsTests
{
    private static bool Valid(in SettingEntry entry) => MacSettings.TryValidate(entry, new MacSettingValues(), [], out _);

    [Theory]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(120, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(29, false)]
    [InlineData(31, false)]
    [InlineData(59, false)]
    [InlineData(90, false)]
    [InlineData(144, false)]
    [InlineData(-30, false)]
    public void MirrorFps_is_auto_or_a_known_limit(int value, bool valid) =>
        Assert.Equal(valid, Valid(SettingEntry.Int(MacSettingKey.MirrorFps, value)));

    [Fact]
    public void Mirror_keys_reject_other_types()
    {
        Assert.False(Valid(SettingEntry.Float(MacSettingKey.MirrorFps, 60)));
        Assert.False(Valid(SettingEntry.Int(MacSettingKey.MirrorEnabled, 1)));
        Assert.False(Valid(SettingEntry.Bool(MacSettingKey.MirrorQuality, true)));
    }

    [Fact]
    public void Defaults_are_off_balanced_and_auto()
    {
        var values = new MacSettingValues();
        Assert.False(values.MirrorEnabled);
        Assert.Equal(MirrorQuality.Balanced, values.MirrorQuality);
        Assert.Equal(MacSettings.MirrorFpsAuto, values.MirrorFps);
        Assert.Equal(0, MacSettings.MirrorFpsAuto);
    }

    [Fact]
    public void Values_apply_and_read_back()
    {
        var values = new MacSettingValues();
        IReadOnlyList<SettingEntry> changed = values.ApplyUpdate(
            [
                SettingEntry.Bool(MacSettingKey.MirrorEnabled, true),
                SettingEntry.Int(MacSettingKey.MirrorQuality, (int)MirrorQuality.High),
                SettingEntry.Int(MacSettingKey.MirrorFps, 120),
                SettingEntry.Int(MacSettingKey.MirrorFps, 45), // invalid: ignored, the 120 stays
            ],
            []);
        Assert.Equal(3, changed.Count);
        Assert.True(values.MirrorEnabled);
        Assert.Equal(MirrorQuality.High, values.MirrorQuality);
        Assert.Equal(120, values.MirrorFps);
    }

    [Fact]
    public void A_snapshot_from_an_older_mac_keeps_the_defaults_and_says_so()
    {
        MacSettingValues values = MacSettingValues.FromEntries([SettingEntry.Bool(MacSettingKey.Zoom, false)]);
        Assert.False(values.Carries(MacSettingKey.MirrorEnabled));
        Assert.False(values.Carries(MacSettingKey.MirrorFps));
        Assert.Equal(MacSettings.MirrorFpsAuto, values.MirrorFps);

        MacSettingValues newer = MacSettingValues.FromEntries([SettingEntry.Int(MacSettingKey.MirrorFps, 30)]);
        Assert.True(newer.Carries(MacSettingKey.MirrorFps));
        Assert.Equal(30, newer.MirrorFps);
    }

    [Fact]
    public void Assignments_parse_numbers_and_quality_names()
    {
        Assert.True(MacSettings.TryParseAssignment("MirrorFps=60", out SettingEntry fps));
        Assert.Equal(SettingEntry.Int(MacSettingKey.MirrorFps, 60), fps);
        Assert.True(MacSettings.TryParseAssignment("mirrorquality=low", out SettingEntry quality));
        Assert.Equal(SettingEntry.Int(MacSettingKey.MirrorQuality, 0), quality);
        Assert.True(MacSettings.TryParseAssignment("MirrorEnabled=on", out SettingEntry enabled));
        Assert.Equal(SettingEntry.Bool(MacSettingKey.MirrorEnabled, true), enabled);
        Assert.Equal("MirrorFps=120", MacSettings.Format(SettingEntry.Int(MacSettingKey.MirrorFps, 120)));
        Assert.Equal("MirrorQuality=High", MacSettings.Format(SettingEntry.Int(MacSettingKey.MirrorQuality, 2)));
    }

    [Fact]
    public void Snapshot_entries_fit_the_wire_cap() =>
        Assert.InRange(MacSettings.All.Count, 1, SettingsWire.MaxEntries);
}
