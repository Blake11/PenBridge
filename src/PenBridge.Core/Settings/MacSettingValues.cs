using PenBridge.Core.Input;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Settings;

/// <summary>
/// A complete, always-valid set of Mac setting values (ADR-0030): starts at the defaults and changes only through
/// <see cref="TryApply"/> / <see cref="ApplyUpdate"/>, which validate with <see cref="MacSettings.TryValidate"/>.
/// The Mac keeps its current settings in one (loaded from and saved to its store); the phone builds one from each
/// SettingsSnapshot (<see cref="FromEntries"/>). Not thread-safe.
/// </summary>
public sealed class MacSettingValues
{
    private readonly SettingEntry[] _values;

    /// <summary>All settings at their defaults.</summary>
    public MacSettingValues() => _values = [.. MacSettings.Defaults];

    private MacSettingValues(SettingEntry[] values) => _values = values;

    /// <summary><see cref="MacSettings.MainDisplay"/>, <see cref="MacSettings.AllDisplays"/> or a display id.</summary>
    public string TargetDisplay => Get(MacSettingKey.TargetDisplay).StringValue;

    public NormalizedRect Region => Get(MacSettingKey.Region).RectValue;

    public bool RegionAspectLock => Get(MacSettingKey.RegionAspectLock).BoolValue;

    public bool RegionTint => Get(MacSettingKey.RegionTint).BoolValue;

    /// <summary>0 Pen, 1 Mouse.</summary>
    public int StylusMode => Get(MacSettingKey.StylusMode).IntValue;

    public float MouseSpeed => Get(MacSettingKey.MouseSpeed).FloatValue;

    public bool MouseAcceleration => Get(MacSettingKey.MouseAcceleration).BoolValue;

    /// <summary>0 RightClick, 1 MiddleClick, 2 Eraser, 3 DoubleClick, 4 None.</summary>
    public int SideButton => Get(MacSettingKey.SideButton).IntValue;

    public float TipFeel => Get(MacSettingKey.TipFeel).FloatValue;

    public float ClickThreshold => Get(MacSettingKey.ClickThreshold).FloatValue;

    public float FullPressureAt => Get(MacSettingKey.FullPressureAt).FloatValue;

    /// <summary>0 FollowSystem, 1 On, 2 Off.</summary>
    public int SmartZoom => Get(MacSettingKey.SmartZoom).IntValue;

    public float PointerSpeed => Get(MacSettingKey.PointerSpeed).FloatValue;

    public float ScrollSpeed => Get(MacSettingKey.ScrollSpeed).FloatValue;

    public bool Zoom => Get(MacSettingKey.Zoom).BoolValue;

    public bool Rotate => Get(MacSettingKey.Rotate).BoolValue;

    public bool ThreeFingerDrag => Get(MacSettingKey.ThreeFingerDrag).BoolValue;

    public bool FourFingerGestures => Get(MacSettingKey.FourFingerGestures).BoolValue;

    /// <summary>
    /// Values from a SettingsSnapshot (the phone side): starts at the defaults and applies the entries as one update,
    /// so unknown keys, wrong types and out-of-range values keep their defaults. The TargetDisplay is taken as the Mac
    /// sent it (any id): the Mac validated it against its displays. The Mac itself loads its store with
    /// <see cref="ApplyUpdate"/> and its current display list.
    /// </summary>
    public static MacSettingValues FromEntries(IReadOnlyList<SettingEntry> entries)
    {
        var values = new MacSettingValues();
        values.Apply(entries, null);
        return values;
    }

    public SettingEntry Get(MacSettingKey key)
    {
        int index = MacSettings.IndexOf((ushort)key);
        return index < 0 ? throw new ArgumentOutOfRangeException(nameof(key)) : _values[index];
    }

    /// <summary>
    /// The input settings these values describe (stylus mode, side button, pressure curve, gesture settings), for
    /// <see cref="Net.PenBridgeServer.SetInputSettings"/>. TargetDisplay, Region and RegionAspectLock are the Mac's
    /// mapping target (<see cref="Net.PenBridgeServer.SetTarget(MappingTarget)"/>) and RegionTint its display tint,
    /// not input settings. The values are always valid, so the result passes <see cref="InputSettings.Validate"/>.
    /// </summary>
    public InputSettings ToInputSettings() => new()
    {
        StylusMode = (StylusMode)StylusMode,
        MouseSpeed = MouseSpeed,
        MouseAcceleration = MouseAcceleration,
        SideButton = (SideButtonAction)SideButton,
        TipFeel = TipFeel,
        ClickThreshold = ClickThreshold,
        FullPressureAt = FullPressureAt,
        SmartZoom = (SmartZoomMode)SmartZoom,
        PointerSpeed = PointerSpeed,
        ScrollSpeed = ScrollSpeed,
        ZoomEnabled = Zoom,
        RotateEnabled = Rotate,
        ThreeFingerDragEnabled = ThreeFingerDrag,
        FourFingerGesturesEnabled = FourFingerGestures,
    };

    /// <summary>Every setting, in key order: the content of a SettingsSnapshot.</summary>
    public IReadOnlyList<SettingEntry> ToEntries() => [.. _values];

    public MacSettingValues Clone() => new([.. _values]);

    /// <summary>Applies one entry if <see cref="MacSettings.TryValidate"/> accepts it against the current values and <paramref name="displays"/>.</summary>
    public bool TryApply(in SettingEntry entry, IReadOnlyList<DisplayListEntry> displays)
    {
        if (!MacSettings.TryValidate(entry, this, displays, out SettingEntry normalized))
        {
            return false;
        }

        _values[MacSettings.IndexOf(normalized.Key)] = normalized;
        return true;
    }

    /// <summary>
    /// The Mac applies a SettingsUpdate (or its stored values): each entry valid on its own
    /// (<see cref="MacSettings.TryValidate"/>, a TargetDisplay must be main, all or one of <paramref name="displays"/>)
    /// is applied (later entries for the same key win), the rest are ignored. The pressure pair is checked on the
    /// result, so one update can move both ends; if the result breaks the rule, the update's ClickThreshold and
    /// FullPressureAt are both ignored. Returns the entries that changed a value.
    /// </summary>
    public IReadOnlyList<SettingEntry> ApplyUpdate(IReadOnlyList<SettingEntry> entries, IReadOnlyList<DisplayListEntry> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        return Apply(entries, displays);
    }

    private List<SettingEntry> Apply(IReadOnlyList<SettingEntry> entries, IReadOnlyList<DisplayListEntry>? displays)
    {
        ArgumentNullException.ThrowIfNull(entries);
        SettingEntry[] candidate = [.. _values];
        foreach (SettingEntry entry in entries)
        {
            if (MacSettings.TryValidateAlone(entry, out SettingEntry normalized, displays))
            {
                candidate[MacSettings.IndexOf(normalized.Key)] = normalized;
            }
        }

        int threshold = MacSettings.IndexOf((ushort)MacSettingKey.ClickThreshold);
        int full = MacSettings.IndexOf((ushort)MacSettingKey.FullPressureAt);
        if (!MacSettings.IsPressurePairValid(candidate[threshold].FloatValue, candidate[full].FloatValue))
        {
            candidate[threshold] = _values[threshold];
            candidate[full] = _values[full];
        }

        var changed = new List<SettingEntry>();
        for (int i = 0; i < _values.Length; i++)
        {
            if (candidate[i] != _values[i])
            {
                _values[i] = candidate[i];
                changed.Add(candidate[i]);
            }
        }

        return changed;
    }
}
