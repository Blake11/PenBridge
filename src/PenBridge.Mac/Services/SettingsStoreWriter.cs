using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;
using PenBridge.Mac.Input;

namespace PenBridge.Mac.Services;

/// <summary>
/// Core's <see cref="ISettingsWriter"/> over <see cref="SettingsStore"/> and the <see cref="DisplayTargetResolver"/>
/// (which <see cref="SettingsSync"/> re-targets whenever the stored TargetDisplay changes). Main thread only.
/// </summary>
internal sealed class SettingsStoreWriter(SettingsStore store, DisplayTargetResolver resolver) : ISettingsWriter
{
    public string StoredTargetDisplay => store.TargetDisplay;

    public float StoredFullPressureAt => store.FullPressureAt;

    /// <summary>The resolved target (main for "" or a missing display), none for All displays or no display.</summary>
    public string? RegionDisplay => resolver.IsAllDisplays || !resolver.HasDisplay ? null : resolver.Target.Uuid;

    public bool IsDisplayConnected(string displayId)
    {
        foreach (DisplayInfo display in resolver.Displays)
        {
            if (string.Equals(display.Uuid, displayId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public void WriteTargetDisplay(string value) => store.TargetDisplay = value; // re-targets the resolver (SettingsSync.OnStoreChanged)

    public void Write(in SettingEntry entry) => _ = store.TryWrite(entry);

    public void WriteRegion(string displayId, NormalizedRect region) => store.SetRegion(displayId, region);
}
