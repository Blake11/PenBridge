using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Settings;

/// <summary>One Mac setting: its key, wire type, allowed values and default.</summary>
public sealed class MacSettingDescriptor
{
    internal MacSettingDescriptor(MacSettingKey key, SettingType type, SettingEntry defaultValue, float min = 0, float max = 0, IReadOnlyList<string>? values = null)
    {
        Key = key;
        Type = type;
        Default = defaultValue;
        Min = min;
        Max = max;
        Values = values ?? [];
    }

    public MacSettingKey Key { get; }

    /// <summary>The key's name (<c>"StylusMode"</c>), as in <see cref="MacSettingKey"/>.</summary>
    public string Name => Key.ToString();

    public SettingType Type { get; }

    public SettingEntry Default { get; }

    /// <summary>Lowest allowed value of a Float key.</summary>
    public float Min { get; }

    /// <summary>Highest allowed value of a Float key.</summary>
    public float Max { get; }

    /// <summary>For an Int key: the names of the allowed values 0..Count−1 (e.g. Pen, Mouse). Empty otherwise.</summary>
    public IReadOnlyList<string> Values { get; }
}

/// <summary>
/// The Mac setting table (ADR-0030, keys in <see cref="MacSettingKey"/>) and its one validation function, used by the
/// Mac store, the server-side validation of SettingsUpdate, and the phone UI.
/// </summary>
public static class MacSettings
{
    /// <summary>TargetDisplay value for the main display.</summary>
    public const string MainDisplay = "";

    /// <summary>TargetDisplay value for all displays as one desktop (ADR-0031).</summary>
    public const string AllDisplays = "all";

    /// <summary><see cref="MacSettingKey.FullPressureAt"/> must exceed <see cref="MacSettingKey.ClickThreshold"/> by more than this.</summary>
    public const float MinPressureSpan = 0.1f;

    /// <summary>Smallest width and height of <see cref="MacSettingKey.Region"/> (normalized; compared as the <c>f32</c> on the wire).</summary>
    public const float MinRegionSize = 0.05f;

    /// <summary>How far a region edge may be from the display edge and still count as the full display.</summary>
    public const double FullRegionEpsilon = 1e-6;

    private static readonly string[] StylusModes = ["Pen", "Mouse"];
    private static readonly string[] SideButtonActions = ["RightClick", "MiddleClick", "Eraser", "DoubleClick", "None"];
    private static readonly string[] SmartZoomModes = ["FollowSystem", "On", "Off"];

    private static readonly MacSettingDescriptor[] Table =
    [
        new(MacSettingKey.TargetDisplay, SettingType.String, SettingEntry.String(MacSettingKey.TargetDisplay, MainDisplay)),
        new(MacSettingKey.Region, SettingType.Rect, SettingEntry.Rect(MacSettingKey.Region, NormalizedRect.Full)),
        Bool(MacSettingKey.RegionAspectLock, false),
        Bool(MacSettingKey.RegionTint, true),
        Enum(MacSettingKey.StylusMode, StylusModes),
        Float(MacSettingKey.MouseSpeed, 0.25f, 4f, 1f),
        Bool(MacSettingKey.MouseAcceleration, true),
        Enum(MacSettingKey.SideButton, SideButtonActions),
        Float(MacSettingKey.TipFeel, -1f, 1f, 0f),
        Float(MacSettingKey.ClickThreshold, 0f, 0.5f, 0f),
        Float(MacSettingKey.FullPressureAt, 0.5f, 1f, 1f),
        Enum(MacSettingKey.SmartZoom, SmartZoomModes),
        Float(MacSettingKey.PointerSpeed, 0.25f, 4f, 1f),
        Float(MacSettingKey.ScrollSpeed, 0.25f, 4f, 1f),
        Bool(MacSettingKey.Zoom, true),
        Bool(MacSettingKey.Rotate, true),
        Bool(MacSettingKey.ThreeFingerDrag, true),
        Bool(MacSettingKey.FourFingerGestures, true),
    ];

    /// <summary>Every Mac setting, in key order.</summary>
    public static IReadOnlyList<MacSettingDescriptor> All => Table;

    /// <summary>Every setting at its default, in key order.</summary>
    public static IReadOnlyList<SettingEntry> Defaults { get; } = Array.ConvertAll(Table, d => d.Default);

    internal static int IndexOf(ushort key)
    {
        for (int i = 0; i < Table.Length; i++)
        {
            if ((ushort)Table[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The descriptor of a known key; false for keys this build doesn't know (skip them).</summary>
    public static bool TryGetDescriptor(ushort key, [NotNullWhen(true)] out MacSettingDescriptor? descriptor)
    {
        int index = IndexOf(key);
        descriptor = index < 0 ? null : Table[index];
        return descriptor is not null;
    }

    public static MacSettingDescriptor Get(MacSettingKey key) =>
        TryGetDescriptor((ushort)key, out MacSettingDescriptor? d) ? d : throw new ArgumentOutOfRangeException(nameof(key));

    /// <summary>Finds a key by name (case-insensitive) or by number.</summary>
    public static bool TryGetDescriptor(string nameOrNumber, [NotNullWhen(true)] out MacSettingDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(nameOrNumber);
        if (ushort.TryParse(nameOrNumber, NumberStyles.None, CultureInfo.InvariantCulture, out ushort number))
        {
            return TryGetDescriptor(number, out descriptor);
        }

        foreach (MacSettingDescriptor d in Table)
        {
            if (string.Equals(d.Name, nameOrNumber, StringComparison.OrdinalIgnoreCase))
            {
                descriptor = d;
                return true;
            }
        }

        descriptor = null;
        return false;
    }

    /// <summary>
    /// The Mac's (server-side) validation of one entry against the table, the <paramref name="current"/> values and
    /// the Mac's <paramref name="displays"/> (the list it sends in DisplayList): a known key, the key's type, a value in
    /// range (Int: one of <see cref="MacSettingDescriptor.Values"/>; Region: inside 0..1 with width and height at least
    /// <see cref="MinRegionSize"/>), a TargetDisplay of main, all or one of the display ids, and for the pressure pair
    /// <c>FullPressureAt &gt; ClickThreshold + 0.1</c> with the other one's current value. <paramref name="normalized"/>
    /// is the value to apply (a region pulled exactly inside 0..1). The phone reads snapshots with
    /// <see cref="MacSettingValues.FromEntries"/> instead, which trusts the Mac's TargetDisplay.
    /// </summary>
    public static bool TryValidate(in SettingEntry entry, MacSettingValues current, IReadOnlyList<DisplayListEntry> displays, out SettingEntry normalized)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(displays);
        if (!TryValidateAlone(entry, out normalized, displays))
        {
            return false;
        }

        bool pairOk = (MacSettingKey)entry.Key switch
        {
            MacSettingKey.ClickThreshold => IsPressurePairValid(normalized.FloatValue, current.FullPressureAt),
            MacSettingKey.FullPressureAt => IsPressurePairValid(current.ClickThreshold, normalized.FloatValue),
            _ => true,
        };
        if (!pairOk)
        {
            normalized = default;
        }

        return pairOk;
    }

    /// <summary>True when a normalized region covers the whole display (within <see cref="FullRegionEpsilon"/>).</summary>
    public static bool IsFullRegion(NormalizedRect r) =>
        r.X <= FullRegionEpsilon && r.Y <= FullRegionEpsilon && r.Width >= 1 - FullRegionEpsilon && r.Height >= 1 - FullRegionEpsilon;

    /// <summary>
    /// True when the pressure range is wide enough: <c>fullPressureAt &gt; clickThreshold + 0.1</c>, computed in
    /// <c>f32</c> as the values travel, so 0.5 and 0.6 (exactly 0.1 apart in float) are rejected on both sides.
    /// </summary>
    public static bool IsPressurePairValid(float clickThreshold, float fullPressureAt) =>
        fullPressureAt > clickThreshold + MinPressureSpan;

    /// <summary>
    /// Every check of <see cref="TryValidate"/> except the pressure pair rule. <paramref name="displays"/> null accepts
    /// any TargetDisplay id: only for reading the Mac's own snapshot (<see cref="MacSettingValues.FromEntries"/>).
    /// </summary>
    internal static bool TryValidateAlone(in SettingEntry entry, out SettingEntry normalized, IReadOnlyList<DisplayListEntry>? displays)
    {
        normalized = default;
        if (!TryGetDescriptor(entry.Key, out MacSettingDescriptor? d) || d.Type != entry.Type)
        {
            return false;
        }

        switch (d.Type)
        {
            case SettingType.Bool:
                normalized = entry;
                return true;
            case SettingType.Int:
                if (entry.IntValue < 0 || entry.IntValue >= d.Values.Count)
                {
                    return false;
                }

                normalized = entry;
                return true;
            case SettingType.Float:
                if (!(entry.FloatValue >= d.Min && entry.FloatValue <= d.Max))
                {
                    return false;
                }

                // −0 and 0 are one value.
                normalized = entry.FloatValue == 0 ? SettingEntry.Float(entry.Key, 0f) : entry;
                return true;
            case SettingType.String when d.Key == MacSettingKey.TargetDisplay:
                if (!IsKnownTarget(entry.StringValue, displays))
                {
                    return false;
                }

                normalized = entry;
                return true;
            case SettingType.Rect:
                NormalizedRect r = entry.RectValue;
                double w = Math.Min(r.Width, 1 - r.X);
                double h = Math.Min(r.Height, 1 - r.Y);
                if (!(w >= MinRegionSize && h >= MinRegionSize))
                {
                    return false;
                }

                normalized = SettingEntry.Rect(entry.Key, new NormalizedRect(r.X, r.Y, w, h));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Parses <c>Key=Value</c> (key by name or number; Bool true/false/on/off/1/0, Int a number or value name, Float invariant, Rect <c>x,y,w,h</c>). Checks the type, not the range.</summary>
    public static bool TryParseAssignment(string text, out SettingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(text);
        entry = default;
        int eq = text.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0 || !TryGetDescriptor(text[..eq].Trim(), out MacSettingDescriptor? d))
        {
            return false;
        }

        string value = text[(eq + 1)..];
        ushort key = (ushort)d.Key;
        const NumberStyles FloatStyle = NumberStyles.Float;
        switch (d.Type)
        {
            case SettingType.Bool:
                bool? b = value.Trim().ToUpperInvariant() switch
                {
                    "TRUE" or "ON" or "1" or "YES" => true,
                    "FALSE" or "OFF" or "0" or "NO" => false,
                    _ => null,
                };
                if (b is null)
                {
                    return false;
                }

                entry = SettingEntry.Bool(key, b.Value);
                return true;
            case SettingType.Int:
                if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n))
                {
                    entry = SettingEntry.Int(key, n);
                    return true;
                }

                for (int i = 0; i < d.Values.Count; i++)
                {
                    if (string.Equals(d.Values[i], value.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        entry = SettingEntry.Int(key, i);
                        return true;
                    }
                }

                return false;
            case SettingType.Float:
                if (!float.TryParse(value, FloatStyle, CultureInfo.InvariantCulture, out float f) || !float.IsFinite(f))
                {
                    return false;
                }

                entry = SettingEntry.Float(key, f);
                return true;
            case SettingType.String:
                if (System.Text.Encoding.UTF8.GetByteCount(value) > WireString.MaxBytes)
                {
                    return false;
                }

                entry = SettingEntry.String(key, value);
                return true;
            case SettingType.Rect:
                string[] parts = value.Split(',');
                var v = new float[4];
                if (parts.Length != 4)
                {
                    return false;
                }

                for (int i = 0; i < 4; i++)
                {
                    if (!float.TryParse(parts[i], FloatStyle, CultureInfo.InvariantCulture, out v[i]))
                    {
                        return false;
                    }
                }

                if (!SettingEntry.IsValidRect(v[0], v[1], v[2], v[3]))
                {
                    return false;
                }

                entry = SettingEntry.Rect(key, new NormalizedRect(v[0], v[1], v[2], v[3]));
                return true;
            default:
                return false;
        }
    }

    /// <summary><c>Name=value</c> for a known key (Int keys show the value name), else the entry's raw form. String values are peer text: sanitize before showing them.</summary>
    public static string Format(in SettingEntry entry)
    {
        if (!TryGetDescriptor(entry.Key, out MacSettingDescriptor? d) || d.Type != entry.Type)
        {
            return entry.ToString();
        }

        string value = d.Type == SettingType.Int && entry.IntValue >= 0 && entry.IntValue < d.Values.Count
            ? d.Values[entry.IntValue]
            : entry.FormatValue();
        return d.Name + "=" + value;
    }

    private static bool IsKnownTarget(string id, IReadOnlyList<DisplayListEntry>? displays)
    {
        if (id is MainDisplay or AllDisplays || displays is null)
        {
            return true;
        }

        foreach (DisplayListEntry display in displays)
        {
            if (string.Equals(display.Id, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static MacSettingDescriptor Bool(MacSettingKey key, bool value) =>
        new(key, SettingType.Bool, SettingEntry.Bool(key, value));

    private static MacSettingDescriptor Float(MacSettingKey key, float min, float max, float value) =>
        new(key, SettingType.Float, SettingEntry.Float(key, value), min, max);

    private static MacSettingDescriptor Enum(MacSettingKey key, string[] values) =>
        new(key, SettingType.Int, SettingEntry.Int(key, 0), values: values);
}
