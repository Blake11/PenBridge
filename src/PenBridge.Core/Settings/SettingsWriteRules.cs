using PenBridge.Core.Input;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Settings;

/// <summary>
/// A host's settings store as <see cref="SettingsWriteRules"/> sees it (ADR-0037): each host implements it over its own
/// persisted store and its display enumeration.
/// </summary>
public interface ISettingsWriter
{
    /// <summary>
    /// The stored TargetDisplay as persisted: <see cref="MacSettings.MainDisplay"/>, <see cref="MacSettings.AllDisplays"/>
    /// or a display id, which may name a display that isn't connected (the host maps to its fallback meanwhile).
    /// </summary>
    string StoredTargetDisplay { get; }

    /// <summary>The stored FullPressureAt (the store keeps the pressure pair valid on each write).</summary>
    float StoredFullPressureAt { get; }

    /// <summary>
    /// The display a Region belongs to: the display the stored target resolves to now (its fallback for a missing
    /// display), or null for All displays or no display at all. Read after <see cref="WriteTargetDisplay"/>.
    /// </summary>
    string? RegionDisplay { get; }

    /// <summary>True when the display with this id is connected now.</summary>
    bool IsDisplayConnected(string displayId);

    /// <summary>Stores the target display (and re-targets the host's resolver, so <see cref="RegionDisplay"/> follows).</summary>
    void WriteTargetDisplay(string value);

    /// <summary>Stores one validated value of any key but TargetDisplay and Region.</summary>
    void Write(in SettingEntry entry);

    /// <summary>Stores the region of one display.</summary>
    void WriteRegion(string displayId, NormalizedRect region);
}

/// <summary>
/// How a host stores the accepted values of a phone's SettingsUpdate (ADR-0030, ADR-0031, ADR-0037). Platform-free;
/// the host validates the update first (<see cref="MacSettingValues.ApplyUpdate"/> over its current values) and then
/// calls <see cref="Write"/>.
/// </summary>
public static class SettingsWriteRules
{
    /// <summary>
    /// Stores an update: the target first, so a Region in it belongs to the new target.
    /// <list type="bullet">
    /// <item>TargetDisplay and Region are written when the update carried an accepted value, not by "changed": the
    /// current values read a stored but disconnected display as main, and a Region equal to the old target's must
    /// still reach the new target.</item>
    /// <item>An empty TargetDisplay is ignored while the stored target names a disconnected display (ADR-0037): a phone
    /// echoing what it can see doesn't discard the remembered display.</item>
    /// <item>The pressure pair was validated together: the end that widens the range is written first, so the store's
    /// per-write pair rule never clamps the other.</item>
    /// <item>Region is written for <see cref="ISettingsWriter.RegionDisplay"/> (none for All displays).</item>
    /// </list>
    /// </summary>
    /// <param name="store">The host's store.</param>
    /// <param name="requested">The update's entries as received.</param>
    /// <param name="changed">The entries <see cref="MacSettingValues.ApplyUpdate"/> returned for it.</param>
    /// <param name="values">The values after that update.</param>
    /// <param name="displays">The display list the update was validated against.</param>
    public static void Write(
        ISettingsWriter store,
        IReadOnlyList<SettingEntry> requested,
        IReadOnlyList<SettingEntry> changed,
        MacSettingValues values,
        IReadOnlyList<DisplayListEntry> displays)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(displays);

        SettingEntry? target = LastOf(requested, MacSettingKey.TargetDisplay, SettingType.String);
        if (target is { } t && string.Equals(values.TargetDisplay, t.StringValue, StringComparison.Ordinal)
            && !KeepsRememberedDisplay(store, values.TargetDisplay))
        {
            store.WriteTargetDisplay(values.TargetDisplay);
        }

        bool fullFirst = values.FullPressureAt > store.StoredFullPressureAt;
        if (fullFirst)
        {
            foreach (SettingEntry entry in changed)
            {
                if (entry.Key == (ushort)MacSettingKey.FullPressureAt)
                {
                    store.Write(entry);
                }
            }
        }

        foreach (SettingEntry entry in changed)
        {
            if ((fullFirst && entry.Key == (ushort)MacSettingKey.FullPressureAt)
                || entry.Key == (ushort)MacSettingKey.TargetDisplay
                || entry.Key == (ushort)MacSettingKey.Region)
            {
                continue;
            }

            store.Write(entry);
        }

        SettingEntry? region = LastOf(requested, MacSettingKey.Region, SettingType.Rect);
        string? regionDisplay = store.RegionDisplay;
        if (region is { } r && regionDisplay is not null
            && MacSettings.TryValidate(r, values, displays, out SettingEntry normalized)
            && normalized.RectValue == values.Region)
        {
            store.WriteRegion(regionDisplay, values.Region);
        }
    }

    /// <summary>An empty target while the stored one is a display id that isn't connected (ADR-0037).</summary>
    private static bool KeepsRememberedDisplay(ISettingsWriter store, string target)
    {
        if (target != MacSettings.MainDisplay)
        {
            return false;
        }

        string stored = store.StoredTargetDisplay;
        return stored is not (MacSettings.MainDisplay or MacSettings.AllDisplays) && !store.IsDisplayConnected(stored);
    }

    private static SettingEntry? LastOf(IReadOnlyList<SettingEntry> entries, MacSettingKey key, SettingType type)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Key == (ushort)key && entries[i].Type == type)
            {
                return entries[i];
            }
        }

        return null;
    }
}
