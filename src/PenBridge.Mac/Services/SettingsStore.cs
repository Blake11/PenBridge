using System.Text;
using Foundation;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;

namespace PenBridge.Mac.Services;

/// <summary>Raised by <see cref="SettingsStore.Changed"/>.</summary>
/// <param name="key">The setting's name (<see cref="SettingsStore.Keys"/>).</param>
/// <param name="displayUuid">For <see cref="SettingsStore.Keys.Region"/>: the display whose region changed; else null.</param>
public sealed class SettingChangedEventArgs(string key, string? displayUuid = null) : EventArgs
{
    public string Key { get; } = key;

    public string? DisplayUuid { get; } = displayUuid;
}

/// <summary>
/// Non-secret user settings in <see cref="NSUserDefaults.StandardUserDefaults"/> (domain = bundle id). Secrets never
/// go here; they live in <see cref="KeychainSecretStore"/>.
/// </summary>
/// <remarks>
/// <para>Holds every Mac setting of Core's key table (<see cref="MacSettings"/>, ADR-0030–0033) under the key names,
/// with the table's ranges and defaults; a missing or out-of-range stored value reads as its default or clamped.
/// Setters clamp (also to the pressure pair rule), ignore non-finite numbers, persist, and raise <see cref="Changed"/>
/// only when the value actually changed. <see cref="ToEntries"/> / <see cref="TryWrite"/> bridge to Core's
/// <see cref="MacSettingValues"/> (<see cref="SettingsSync"/>).</para>
/// <para>Regions are stored per display UUID (<c>Region.&lt;uuid&gt;</c>, an array x, y, w, h normalized to the
/// display, top-left origin); the full display is stored as no entry.</para>
/// <para>Use from the main thread: <see cref="Changed"/> is raised synchronously on the setter's thread.</para>
/// </remarks>
public sealed class SettingsStore
{
    /// <summary>NSUserDefaults names. The Phase 5 ones match the <c>MacSettingKey</c> names of the key table.</summary>
    public static class Keys
    {
        public const string TargetDisplay = "TargetDisplay";
        public const string Region = "Region";
        public const string RegionAspectLock = "RegionAspectLock";
        public const string RegionTint = "RegionTint";
        public const string StylusMode = "StylusMode";
        public const string MouseSpeed = "MouseSpeed";
        public const string MouseAcceleration = "MouseAcceleration";
        public const string SideButton = "SideButton";
        public const string TipFeel = "TipFeel";
        public const string ClickThreshold = "ClickThreshold";
        public const string FullPressureAt = "FullPressureAt";
        public const string SmartZoom = "SmartZoom";
        public const string PointerSpeed = "PointerSpeed";
        public const string ScrollSpeed = "ScrollSpeed";
        public const string Zoom = "Zoom";
        public const string Rotate = "Rotate";
        public const string ThreeFingerDrag = "ThreeFingerDrag";
        public const string FourFingerGestures = "FourFingerGestures";
        public const string AllowTakeover = "AllowTakeover";
        public const string Paused = "Paused";
        public const string RunAtStartup = "RunAtStartup";
    }

    /// <summary>Phase 2 name of the target display (UUID, absent = main); read once and migrated to TargetDisplay.</summary>
    private const string LegacyTargetDisplayUuidKey = "TargetDisplayUuid";
    private const string RegionKeyPrefix = Keys.Region + ".";
    private const double RegionEpsilon = 1e-6;

    private readonly NSUserDefaults _defaults;

    public SettingsStore()
        : this(NSUserDefaults.StandardUserDefaults)
    {
    }

    public SettingsStore(NSUserDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        _defaults = defaults;
    }

    /// <summary>Raised after a setting changed through this store (not for external <c>defaults write</c> edits).</summary>
    public event EventHandler<SettingChangedEventArgs>? Changed;

    // ---- Display (ADR-0031) ----

    /// <summary>
    /// The target: <see cref="MacSettings.MainDisplay"/> (""), <see cref="MacSettings.AllDisplays"/> ("all"), or a
    /// display UUID (<c>CGDisplayCreateUUIDFromDisplayID</c> string). Default "". A UUID of a display that isn't
    /// connected is kept (the resolver falls back to main meanwhile).
    /// </summary>
    public string TargetDisplay
    {
        get
        {
            string? value = _defaults.StringForKey(Keys.TargetDisplay) ?? _defaults.StringForKey(LegacyTargetDisplayUuidKey);
            return value is null || !FitsWire(value) ? MacSettings.MainDisplay : value;
        }

        set
        {
            value ??= MacSettings.MainDisplay;
            if (!FitsWire(value))
            {
                return;
            }

            bool changed = !string.Equals(value, TargetDisplay, StringComparison.Ordinal);
            _defaults.SetString(value, Keys.TargetDisplay);
            _defaults.RemoveObject(LegacyTargetDisplayUuidKey);
            if (changed)
            {
                Raise(Keys.TargetDisplay);
            }
        }
    }

    /// <summary>The region of a display (normalized to it, top-left origin). Default the full display.</summary>
    public NormalizedRect GetRegion(string displayUuid)
    {
        ArgumentException.ThrowIfNullOrEmpty(displayUuid);
        NSObject[]? values = _defaults.ArrayForKey(RegionKeyPrefix + displayUuid);
        if (values is null || values.Length != 4)
        {
            return NormalizedRect.Full;
        }

        Span<double> v = stackalloc double[4];
        for (int i = 0; i < 4; i++)
        {
            if (values[i] is not NSNumber number)
            {
                return NormalizedRect.Full;
            }

            v[i] = number.DoubleValue;
        }

        var rect = new NormalizedRect(v[0], v[1], v[2], v[3]);
        return IsValidRegion(rect) ? rect : NormalizedRect.Full;
    }

    /// <summary>Sets a display's region; an invalid rectangle is ignored, the full display removes the entry.</summary>
    public void SetRegion(string displayUuid, NormalizedRect region)
    {
        ArgumentException.ThrowIfNullOrEmpty(displayUuid);
        if (!IsValidRegion(region))
        {
            return;
        }

        bool changed = GetRegion(displayUuid) != region;
        string key = RegionKeyPrefix + displayUuid;
        if (IsFull(region))
        {
            _defaults.RemoveObject(key);
        }
        else
        {
            using var array = NSArray.FromNSObjects(
                NSNumber.FromDouble(region.X),
                NSNumber.FromDouble(region.Y),
                NSNumber.FromDouble(region.Width),
                NSNumber.FromDouble(region.Height));
            using var nsKey = new NSString(key);
            _defaults.SetValueForKey(array, nsKey);
        }

        if (changed)
        {
            Raise(Keys.Region, displayUuid);
        }
    }

    /// <summary>
    /// A rectangle inside 0..1 whose width and height are at least <see cref="MacSettings.MinRegionSize"/> (the
    /// key table's Region rule, so every stored region is one the phone may see and send back).
    /// </summary>
    public static bool IsValidRegion(NormalizedRect r) =>
        double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height)
        && r.X >= 0 && r.Y >= 0 && r.Width >= MacSettings.MinRegionSize && r.Height >= MacSettings.MinRegionSize
        && r.X + r.Width <= 1 + RegionEpsilon && r.Y + r.Height <= 1 + RegionEpsilon;

    /// <summary>True when the rectangle covers the whole display.</summary>
    public static bool IsFull(NormalizedRect r) => MacSettings.IsFullRegion(r);

    /// <summary>Region aspect lock (ADR-0031). Default false.</summary>
    public bool RegionAspectLock
    {
        get => GetBool(MacSettingKey.RegionAspectLock);
        set => SetBool(Keys.RegionAspectLock, value, RegionAspectLock);
    }

    /// <summary>Tint the target display outside its region while a phone is connected (ADR-0035). Default true.</summary>
    public bool RegionTint
    {
        get => GetBool(MacSettingKey.RegionTint);
        set => SetBool(Keys.RegionTint, value, RegionTint);
    }

    // ---- Pen (ADR-0032) ----

    /// <summary>Pen or Mouse mode. Default Pen.</summary>
    public StylusMode StylusMode
    {
        get => (StylusMode)GetEnum(MacSettingKey.StylusMode);
        set => SetEnum(MacSettingKey.StylusMode, (int)value, (int)StylusMode);
    }

    /// <summary>Mouse-mode speed, 0.25..4. Default 1.</summary>
    public float MouseSpeed
    {
        get => GetFloat(MacSettingKey.MouseSpeed);
        set => SetFloat(MacSettingKey.MouseSpeed, value, MouseSpeed);
    }

    /// <summary>Mouse-mode acceleration curve on/off. Default true.</summary>
    public bool MouseAcceleration
    {
        get => GetBool(MacSettingKey.MouseAcceleration);
        set => SetBool(Keys.MouseAcceleration, value, MouseAcceleration);
    }

    /// <summary>Side-button action. Default Right click.</summary>
    public SideButtonAction SideButton
    {
        get => (SideButtonAction)GetEnum(MacSettingKey.SideButton);
        set => SetEnum(MacSettingKey.SideButton, (int)value, (int)SideButton);
    }

    /// <summary>Tip feel, −1 (soft) .. +1 (firm). Default 0 (linear).</summary>
    public float TipFeel
    {
        get => GetFloat(MacSettingKey.TipFeel);
        set => SetFloat(MacSettingKey.TipFeel, value, TipFeel);
    }

    /// <summary>
    /// Click threshold, 0..0.5. Default 0. Kept below <see cref="FullPressureAt"/> by the key table's pair rule
    /// (<see cref="MacSettings.IsPressurePairValid"/>): a larger value is clamped to the largest one it allows.
    /// </summary>
    public float ClickThreshold
    {
        get => GetFloat(MacSettingKey.ClickThreshold);
        set => SetFloat(MacSettingKey.ClickThreshold, value, ClickThreshold, max: MaxClickThresholdFor(FullPressureAt));
    }

    /// <summary>
    /// Full pressure at, 0.5..1. Default 1. Kept above <see cref="ClickThreshold"/> by the key table's pair rule: a
    /// smaller value is clamped to the smallest one it allows.
    /// </summary>
    public float FullPressureAt
    {
        get
        {
            float value = GetFloat(MacSettingKey.FullPressureAt);
            float threshold = ClickThreshold;
            return MacSettings.IsPressurePairValid(threshold, value) ? value : MinFullPressureFor(threshold);
        }

        set => SetFloat(MacSettingKey.FullPressureAt, value, FullPressureAt, min: MinFullPressureFor(ClickThreshold));
    }

    /// <summary>The smallest FullPressureAt the pair rule allows above <paramref name="clickThreshold"/> (in <c>f32</c>, as validated).</summary>
    public static float MinFullPressureFor(float clickThreshold)
    {
        float full = clickThreshold + MacSettings.MinPressureSpan;
        for (int i = 0; i < 16 && !MacSettings.IsPressurePairValid(clickThreshold, full); i++)
        {
            full = MathF.BitIncrement(full);
        }

        return full;
    }

    /// <summary>The largest ClickThreshold the pair rule allows below <paramref name="fullPressureAt"/> (at least 0).</summary>
    public static float MaxClickThresholdFor(float fullPressureAt)
    {
        float threshold = fullPressureAt - MacSettings.MinPressureSpan;
        for (int i = 0; i < 16 && threshold > 0 && !MacSettings.IsPressurePairValid(threshold, fullPressureAt); i++)
        {
            threshold = MathF.BitDecrement(threshold);
        }

        return MathF.Max(threshold, 0);
    }

    // ---- Gestures (ADR-0033) ----

    /// <summary>Smart Zoom: follow macOS, on, off. Default follow macOS.</summary>
    public SmartZoomMode SmartZoom
    {
        get => (SmartZoomMode)GetEnum(MacSettingKey.SmartZoom);
        set => SetEnum(MacSettingKey.SmartZoom, (int)value, (int)SmartZoom);
    }

    /// <summary>Finger pointer speed (one-finger cursor, three-finger drag), 0.25..4. Default 1.</summary>
    public float PointerSpeed
    {
        get => GetFloat(MacSettingKey.PointerSpeed);
        set => SetFloat(MacSettingKey.PointerSpeed, value, PointerSpeed);
    }

    /// <summary>Two-finger scroll speed (and momentum), 0.25..4. Default 1.</summary>
    public float ScrollSpeed
    {
        get => GetFloat(MacSettingKey.ScrollSpeed);
        set => SetFloat(MacSettingKey.ScrollSpeed, value, ScrollSpeed);
    }

    /// <summary>Pinch zoom. Default true.</summary>
    public bool Zoom
    {
        get => GetBool(MacSettingKey.Zoom);
        set => SetBool(Keys.Zoom, value, Zoom);
    }

    /// <summary>Two-finger rotate. Default true.</summary>
    public bool Rotate
    {
        get => GetBool(MacSettingKey.Rotate);
        set => SetBool(Keys.Rotate, value, Rotate);
    }

    /// <summary>Three-finger drag. Default true.</summary>
    public bool ThreeFingerDrag
    {
        get => GetBool(MacSettingKey.ThreeFingerDrag);
        set => SetBool(Keys.ThreeFingerDrag, value, ThreeFingerDrag);
    }

    /// <summary>Four-finger gestures. Default true.</summary>
    public bool FourFingerGestures
    {
        get => GetBool(MacSettingKey.FourFingerGestures);
        set => SetBool(Keys.FourFingerGestures, value, FourFingerGestures);
    }

    // ---- Key table bridge (ADR-0030) ----

    /// <summary>True for the names of the keys the phone sees (the Phase 5 key table), false for General ones.</summary>
    public static bool IsSyncedKey(string key) => MacSettings.TryGetDescriptor(key, out _);

    /// <summary>
    /// Every key-table setting as stored, in key order, for <see cref="MacSettingValues.ApplyUpdate"/>. Region is the
    /// region of <paramref name="regionDisplayUuid"/> (the display the target resolves to), or the full display when
    /// null (All displays, no display).
    /// </summary>
    public IReadOnlyList<SettingEntry> ToEntries(string? regionDisplayUuid)
    {
        NormalizedRect region = string.IsNullOrEmpty(regionDisplayUuid) ? NormalizedRect.Full : GetRegion(regionDisplayUuid);
        return
        [
            SettingEntry.String(MacSettingKey.TargetDisplay, TargetDisplay),
            RegionEntry(region),
            SettingEntry.Bool(MacSettingKey.RegionAspectLock, RegionAspectLock),
            SettingEntry.Bool(MacSettingKey.RegionTint, RegionTint),
            SettingEntry.Int(MacSettingKey.StylusMode, (int)StylusMode),
            SettingEntry.Float(MacSettingKey.MouseSpeed, MouseSpeed),
            SettingEntry.Bool(MacSettingKey.MouseAcceleration, MouseAcceleration),
            SettingEntry.Int(MacSettingKey.SideButton, (int)SideButton),
            SettingEntry.Float(MacSettingKey.TipFeel, TipFeel),
            SettingEntry.Float(MacSettingKey.ClickThreshold, ClickThreshold),
            SettingEntry.Float(MacSettingKey.FullPressureAt, FullPressureAt),
            SettingEntry.Int(MacSettingKey.SmartZoom, (int)SmartZoom),
            SettingEntry.Float(MacSettingKey.PointerSpeed, PointerSpeed),
            SettingEntry.Float(MacSettingKey.ScrollSpeed, ScrollSpeed),
            SettingEntry.Bool(MacSettingKey.Zoom, Zoom),
            SettingEntry.Bool(MacSettingKey.Rotate, Rotate),
            SettingEntry.Bool(MacSettingKey.ThreeFingerDrag, ThreeFingerDrag),
            SettingEntry.Bool(MacSettingKey.FourFingerGestures, FourFingerGestures),
        ];
    }

    // A stored region is inside 0..1 in double; as f32 it may sit a hair over the edge: then the full display.
    private static SettingEntry RegionEntry(NormalizedRect region)
    {
        try
        {
            return SettingEntry.Rect(MacSettingKey.Region, region);
        }
        catch (ArgumentOutOfRangeException)
        {
            return SettingEntry.Rect(MacSettingKey.Region, NormalizedRect.Full);
        }
    }

    /// <summary>
    /// Stores one validated value (from <see cref="MacSettingValues"/>) through its typed setter. TargetDisplay and
    /// Region are the caller's (they depend on the display list and the resolved target), so they return false here.
    /// </summary>
    public bool TryWrite(in SettingEntry entry)
    {
        switch ((MacSettingKey)entry.Key)
        {
            case MacSettingKey.RegionAspectLock: RegionAspectLock = entry.BoolValue; return true;
            case MacSettingKey.RegionTint: RegionTint = entry.BoolValue; return true;
            case MacSettingKey.StylusMode: StylusMode = (StylusMode)entry.IntValue; return true;
            case MacSettingKey.MouseSpeed: MouseSpeed = entry.FloatValue; return true;
            case MacSettingKey.MouseAcceleration: MouseAcceleration = entry.BoolValue; return true;
            case MacSettingKey.SideButton: SideButton = (SideButtonAction)entry.IntValue; return true;
            case MacSettingKey.TipFeel: TipFeel = entry.FloatValue; return true;
            case MacSettingKey.ClickThreshold: ClickThreshold = entry.FloatValue; return true;
            case MacSettingKey.FullPressureAt: FullPressureAt = entry.FloatValue; return true;
            case MacSettingKey.SmartZoom: SmartZoom = (SmartZoomMode)entry.IntValue; return true;
            case MacSettingKey.PointerSpeed: PointerSpeed = entry.FloatValue; return true;
            case MacSettingKey.ScrollSpeed: ScrollSpeed = entry.FloatValue; return true;
            case MacSettingKey.Zoom: Zoom = entry.BoolValue; return true;
            case MacSettingKey.Rotate: Rotate = entry.BoolValue; return true;
            case MacSettingKey.ThreeFingerDrag: ThreeFingerDrag = entry.BoolValue; return true;
            case MacSettingKey.FourFingerGestures: FourFingerGestures = entry.BoolValue; return true;
            default: return false;
        }
    }

    // ---- General ----

    /// <summary>"Allow takeover" (ADR-0010). Default false.</summary>
    public bool AllowTakeover
    {
        get => GetBool(Keys.AllowTakeover, false);
        set => SetBool(Keys.AllowTakeover, value, AllowTakeover);
    }

    /// <summary>Input paused. Default false.</summary>
    public bool Paused
    {
        get => GetBool(Keys.Paused, false);
        set => SetBool(Keys.Paused, value, Paused);
    }

    /// <summary>
    /// Mirror of the "Run at startup" choice. The truth is <see cref="LoginItem.Status"/>; this remembers what the user
    /// asked for, e.g. while approval in System Settings is pending.
    /// </summary>
    public bool RunAtStartup
    {
        get => GetBool(Keys.RunAtStartup, false);
        set => SetBool(Keys.RunAtStartup, value, RunAtStartup);
    }

    private static bool FitsWire(string value) => Encoding.UTF8.GetByteCount(value) <= WireString.MaxBytes;

    private bool GetBool(string key, bool defaultValue) =>
        _defaults[key] is NSNumber number ? number.BoolValue : defaultValue;

    private bool GetBool(MacSettingKey key) => GetBool(key.ToString(), MacSettings.Get(key).Default.BoolValue);

    private void SetBool(string key, bool value, bool current)
    {
        bool changed = current != value;
        _defaults.SetBool(value, key);
        if (changed)
        {
            Raise(key);
        }
    }

    // Int keys of the table are enums with the values 0..Values.Count-1.
    private int GetEnum(MacSettingKey key)
    {
        MacSettingDescriptor d = MacSettings.Get(key);
        if (_defaults[key.ToString()] is not NSNumber number)
        {
            return d.Default.IntValue;
        }

        long value = number.Int64Value;
        return value >= 0 && value < d.Values.Count ? (int)value : d.Default.IntValue;
    }

    private void SetEnum(MacSettingKey key, int value, int current)
    {
        if (value < 0 || value >= MacSettings.Get(key).Values.Count)
        {
            return;
        }

        bool changed = current != value;
        _defaults.SetInt(value, key.ToString());
        if (changed)
        {
            Raise(key.ToString());
        }
    }

    private float GetFloat(MacSettingKey key)
    {
        MacSettingDescriptor d = MacSettings.Get(key);
        if (_defaults[key.ToString()] is not NSNumber number)
        {
            return d.Default.FloatValue;
        }

        float value = number.FloatValue;
        return float.IsFinite(value) ? Math.Clamp(value, d.Min, d.Max) : d.Default.FloatValue;
    }

    // Clamps to the table's range, narrowed by min/max (the pressure pair rule).
    private void SetFloat(MacSettingKey key, float value, float current, float min = float.NegativeInfinity, float max = float.PositiveInfinity)
    {
        MacSettingDescriptor d = MacSettings.Get(key);
        min = MathF.Max(min, d.Min);
        max = MathF.Min(max, d.Max);
        if (!float.IsFinite(value) || min > max)
        {
            return;
        }

        value = Math.Clamp(value, min, max);
        bool changed = current != value;
        _defaults.SetFloat(value, key.ToString());
        if (changed)
        {
            Raise(key.ToString());
        }
    }

    private void Raise(string key, string? displayUuid = null) =>
        Changed?.Invoke(this, new SettingChangedEventArgs(key, displayUuid));
}
