namespace PenBridge.Core.Settings;

/// <summary>The slider of each Float setting (#212); range and default come from <see cref="MacSettings"/>.</summary>
public static class SettingSliders
{
    /// <summary>The slider of a Float key; false for every other key.</summary>
    public static bool TryGet(MacSettingKey key, out SliderSpec spec)
    {
        (double step, bool log) = key switch
        {
            MacSettingKey.MouseSpeed or MacSettingKey.PointerSpeed or MacSettingKey.ScrollSpeed => (0.05, true),
            MacSettingKey.TipFeel => (0.05, false),
            MacSettingKey.ClickThreshold or MacSettingKey.FullPressureAt => (0.01, false),
            _ => (0.0, false),
        };
        if (step == 0)
        {
            spec = default;
            return false;
        }

        MacSettingDescriptor d = MacSettings.Get(key);
        spec = new SliderSpec(d.Min, d.Max, d.Default.FloatValue, step, log);
        return true;
    }
}
