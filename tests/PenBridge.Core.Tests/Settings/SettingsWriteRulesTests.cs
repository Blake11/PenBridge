using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Settings;

/// <summary>How a host stores a phone's SettingsUpdate (#204, ADR-0037): <see cref="SettingsWriteRules.Write"/>.</summary>
public class SettingsWriteRulesTests
{
    private const string Main = "MAIN-UUID";
    private const string Other = "OTHER-UUID";
    private static readonly NormalizedRect Half = new(0.25, 0.25, 0.5, 0.5);

    // ---------------------------------------------------------------- carried / not carried

    [Fact]
    public void An_update_without_target_or_region_writes_neither()
    {
        var store = new FakeWriter(Other, Main, Other);
        store.Regions[Other] = Half;

        Apply(store, SettingEntry.Int(MacSettingKey.StylusMode, 1));

        Assert.Equal(["Write StylusMode"], store.Log);
        Assert.Equal(Other, store.StoredTargetDisplay);
    }

    [Fact]
    public void Only_changed_values_of_other_keys_are_written()
    {
        var store = new FakeWriter(Main, Main);

        Apply(store, SettingEntry.Int(MacSettingKey.StylusMode, 0), SettingEntry.Bool(MacSettingKey.Zoom, false));

        Assert.Equal(["Write Zoom"], store.Log);
    }

    [Fact]
    public void A_carried_target_is_written_even_when_unchanged()
    {
        var store = new FakeWriter(Main, Main);

        Apply(store, SettingEntry.String(MacSettingKey.TargetDisplay, Main));

        Assert.Equal([$"Target {Main}"], store.Log);
    }

    [Fact]
    public void A_rejected_target_is_not_written()
    {
        var store = new FakeWriter(Main, Main);

        Apply(store, SettingEntry.String(MacSettingKey.TargetDisplay, "UNKNOWN"));

        Assert.Empty(store.Log);
        Assert.Equal(Main, store.StoredTargetDisplay);
    }

    [Fact]
    public void The_last_target_of_an_update_wins_and_a_wrong_typed_one_is_ignored()
    {
        var store = new FakeWriter(Main, Main, Other);

        Apply(
            store,
            SettingEntry.String(MacSettingKey.TargetDisplay, Main),
            SettingEntry.String(MacSettingKey.TargetDisplay, Other),
            SettingEntry.Int((ushort)MacSettingKey.TargetDisplay, 0));

        Assert.Equal([$"Target {Other}"], store.Log);
    }

    // ---------------------------------------------------------------- empty target vs a remembered display (ADR-0037)

    [Fact]
    public void An_empty_target_keeps_a_remembered_disconnected_display()
    {
        var store = new FakeWriter(Other, Main);

        Apply(store, SettingEntry.String(MacSettingKey.TargetDisplay, MacSettings.MainDisplay));

        Assert.Empty(store.Log);
        Assert.Equal(Other, store.StoredTargetDisplay);
    }

    [Fact]
    public void An_empty_target_replaces_a_connected_display()
    {
        var store = new FakeWriter(Other, Main, Other);

        Apply(store, SettingEntry.String(MacSettingKey.TargetDisplay, MacSettings.MainDisplay));

        Assert.Equal(["Target "], store.Log);
        Assert.Equal(MacSettings.MainDisplay, store.StoredTargetDisplay);
    }

    [Theory]
    [InlineData(MacSettings.AllDisplays)]
    [InlineData(Main)]
    public void A_non_empty_target_replaces_a_remembered_disconnected_display(string target)
    {
        var store = new FakeWriter(Other, Main);

        Apply(store, SettingEntry.String(MacSettingKey.TargetDisplay, target));

        Assert.Equal(target, store.StoredTargetDisplay);
    }

    [Fact]
    public void An_empty_target_replaces_all_displays()
    {
        var store = new FakeWriter(MacSettings.AllDisplays, Main);

        Apply(store, SettingEntry.String(MacSettingKey.TargetDisplay, MacSettings.MainDisplay));

        Assert.Equal(MacSettings.MainDisplay, store.StoredTargetDisplay);
    }

    [Fact]
    public void A_kept_remembered_display_still_takes_the_other_values()
    {
        var store = new FakeWriter(Other, Main);

        Apply(
            store,
            SettingEntry.String(MacSettingKey.TargetDisplay, MacSettings.MainDisplay),
            SettingEntry.Int(MacSettingKey.StylusMode, 1),
            SettingEntry.Rect(MacSettingKey.Region, Half));

        Assert.Equal(["Write StylusMode", $"Region {Main} {Half}"], store.Log);
        Assert.Equal(Other, store.StoredTargetDisplay);
    }

    // ---------------------------------------------------------------- region tied to the resolved target

    [Fact]
    public void A_region_goes_to_the_new_target_even_when_equal_to_the_old_targets()
    {
        var store = new FakeWriter(Main, Main, Other);
        store.Regions[Main] = Half;

        Apply(store, SettingEntry.String(MacSettingKey.TargetDisplay, Other), SettingEntry.Rect(MacSettingKey.Region, Half));

        Assert.Equal([$"Target {Other}", $"Region {Other} {Half}"], store.Log);
    }

    [Fact]
    public void A_region_is_written_normalized_to_the_display()
    {
        var store = new FakeWriter(Main, Main);
        var sent = new NormalizedRect(0.5f, 0.25f, 0.5000003f, 0.75000024f); // within the wire tolerance past 1

        Apply(store, SettingEntry.Rect(MacSettingKey.Region, sent));

        Assert.Equal([$"Region {Main} {new NormalizedRect(0.5, 0.25, 0.5, 0.75)}"], store.Log);
    }

    [Fact]
    public void A_rejected_region_is_not_written()
    {
        var store = new FakeWriter(Main, Main);
        store.Regions[Main] = Half;

        Apply(store, SettingEntry.Rect(MacSettingKey.Region, new NormalizedRect(0, 0, 0.01, 0.01)));

        Assert.Empty(store.Log);
    }

    [Fact]
    public void A_region_is_not_written_for_all_displays()
    {
        var store = new FakeWriter(Main, Main, Other);

        Apply(store, SettingEntry.String(MacSettingKey.TargetDisplay, MacSettings.AllDisplays), SettingEntry.Rect(MacSettingKey.Region, Half));

        Assert.Equal([$"Target {MacSettings.AllDisplays}"], store.Log);
    }

    [Fact]
    public void A_region_is_not_written_without_a_display()
    {
        var store = new FakeWriter(Main);

        Apply(store, SettingEntry.Rect(MacSettingKey.Region, Half));

        Assert.Empty(store.Log);
    }

    [Fact]
    public void A_region_on_a_fallback_goes_to_the_display_in_use()
    {
        var store = new FakeWriter(Other, Main);

        Apply(store, SettingEntry.Rect(MacSettingKey.Region, Half));

        Assert.Equal([$"Region {Main} {Half}"], store.Log);
    }

    [Fact]
    public void The_last_region_of_an_update_wins()
    {
        var store = new FakeWriter(Main, Main);
        var second = new NormalizedRect(0, 0, 0.5, 0.5);

        Apply(store, SettingEntry.Rect(MacSettingKey.Region, Half), SettingEntry.Rect(MacSettingKey.Region, second));

        Assert.Equal([$"Region {Main} {second}"], store.Log);
    }

    // ---------------------------------------------------------------- pressure pair: widening end first

    [Fact]
    public void Raising_the_pair_writes_full_pressure_first()
    {
        var store = new FakeWriter(Main, Main) { ClickThreshold = 0f, FullPressureAt = 0.6f };

        Apply(store, SettingEntry.Float(MacSettingKey.ClickThreshold, 0.5f), SettingEntry.Float(MacSettingKey.FullPressureAt, 0.7f));

        Assert.Equal(["Write FullPressureAt", "Write ClickThreshold"], store.Log);
        Assert.Equal(0, store.PairViolations);
        Assert.Equal((0.5f, 0.7f), (store.ClickThreshold, store.FullPressureAt));
    }

    [Fact]
    public void Lowering_the_pair_writes_the_click_threshold_first()
    {
        var store = new FakeWriter(Main, Main) { ClickThreshold = 0.5f, FullPressureAt = 0.7f };

        Apply(store, SettingEntry.Float(MacSettingKey.FullPressureAt, 0.6f), SettingEntry.Float(MacSettingKey.ClickThreshold, 0f));

        Assert.Equal(["Write ClickThreshold", "Write FullPressureAt"], store.Log);
        Assert.Equal(0, store.PairViolations);
        Assert.Equal((0f, 0.6f), (store.ClickThreshold, store.FullPressureAt));
    }

    [Fact]
    public void Raising_full_pressure_alone_writes_it_once()
    {
        var store = new FakeWriter(Main, Main) { FullPressureAt = 0.6f };

        Apply(store, SettingEntry.Float(MacSettingKey.FullPressureAt, 0.8f), SettingEntry.Int(MacSettingKey.StylusMode, 1));

        Assert.Equal(["Write FullPressureAt", "Write StylusMode"], store.Log);
    }

    [Fact]
    public void The_target_is_written_before_the_other_values()
    {
        var store = new FakeWriter(Main, Main, Other) { FullPressureAt = 0.6f };

        Apply(store, SettingEntry.Float(MacSettingKey.FullPressureAt, 0.8f), SettingEntry.String(MacSettingKey.TargetDisplay, Other));

        Assert.Equal([$"Target {Other}", "Write FullPressureAt"], store.Log);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var store = new FakeWriter(Main, Main);
        var values = new MacSettingValues();
        SettingEntry[] none = [];
        DisplayListEntry[] displays = [];
        Assert.Throws<ArgumentNullException>(() => SettingsWriteRules.Write(null!, none, none, values, displays));
        Assert.Throws<ArgumentNullException>(() => SettingsWriteRules.Write(store, null!, none, values, displays));
        Assert.Throws<ArgumentNullException>(() => SettingsWriteRules.Write(store, none, null!, values, displays));
        Assert.Throws<ArgumentNullException>(() => SettingsWriteRules.Write(store, none, none, null!, displays));
        Assert.Throws<ArgumentNullException>(() => SettingsWriteRules.Write(store, none, none, values, null!));
    }

    /// <summary>Runs an update the way a host does: current values from the store, ApplyUpdate, then the write rules.</summary>
    private static void Apply(FakeWriter store, params SettingEntry[] update)
    {
        DisplayListEntry[] displays = store.DisplayList();
        var values = new MacSettingValues();
        _ = values.ApplyUpdate(store.ToEntries(), displays);
        IReadOnlyList<SettingEntry> changed = values.ApplyUpdate(update, displays);
        SettingsWriteRules.Write(store, update, changed, values, displays);
    }

    /// <summary>
    /// A host store and display set: the first connected display is main; the target resolves to the stored display
    /// when connected, else main (none for All displays or no display). Logs every write; counts pressure-pair breaks.
    /// </summary>
    private sealed class FakeWriter(string storedTarget, params string[] connected) : ISettingsWriter
    {
        public List<string> Log { get; } = [];

        public Dictionary<string, NormalizedRect> Regions { get; } = [];

        public float ClickThreshold { get; set; }

        public float FullPressureAt { get; set; } = 1f;

        public int PairViolations { get; private set; }

        public string StoredTargetDisplay { get; private set; } = storedTarget;

        public float StoredFullPressureAt => FullPressureAt;

        public string? RegionDisplay =>
            connected.Length == 0 || StoredTargetDisplay == MacSettings.AllDisplays ? null
            : IsDisplayConnected(StoredTargetDisplay) ? StoredTargetDisplay
            : connected[0];

        public bool IsDisplayConnected(string displayId) => Array.IndexOf(connected, displayId) >= 0;

        public void WriteTargetDisplay(string value)
        {
            Log.Add($"Target {value}");
            StoredTargetDisplay = value;
        }

        public void Write(in SettingEntry entry)
        {
            Log.Add($"Write {(MacSettingKey)entry.Key}");
            if (entry.Key == (ushort)MacSettingKey.ClickThreshold)
            {
                ClickThreshold = entry.FloatValue;
            }
            else if (entry.Key == (ushort)MacSettingKey.FullPressureAt)
            {
                FullPressureAt = entry.FloatValue;
            }
            else
            {
                return;
            }

            if (!MacSettings.IsPressurePairValid(ClickThreshold, FullPressureAt))
            {
                PairViolations++;
            }
        }

        public void WriteRegion(string displayId, NormalizedRect region)
        {
            Log.Add($"Region {displayId} {region}");
            Regions[displayId] = region;
        }

        public DisplayListEntry[] DisplayList() =>
            Array.ConvertAll(connected, id => new DisplayListEntry(id, id, id == connected[0], 1920, 1080));

        public SettingEntry[] ToEntries()
        {
            string? regionDisplay = RegionDisplay;
            NormalizedRect region = regionDisplay is not null && Regions.TryGetValue(regionDisplay, out NormalizedRect r) ? r : NormalizedRect.Full;
            return
            [
                SettingEntry.String(MacSettingKey.TargetDisplay, StoredTargetDisplay),
                SettingEntry.Rect(MacSettingKey.Region, region),
                SettingEntry.Float(MacSettingKey.ClickThreshold, ClickThreshold),
                SettingEntry.Float(MacSettingKey.FullPressureAt, FullPressureAt),
            ];
        }
    }
}
