using Foundation;
using PenBridge.Core.Input;
using PenBridge.Mac.Settings;

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
/// <para>Holds every Mac setting of the Phase 5 key table (issue #154, ADR-0030–0033) under the same names, with the
/// same defaults; a missing or out-of-range stored value reads as its default or clamped. Setters clamp, ignore
/// non-finite numbers, persist, and raise <see cref="Changed"/> only when the value actually changed.</para>
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
    /// The target: <see cref="SettingLimits.MainDisplay"/> (""), <see cref="SettingLimits.AllDisplays"/> ("all"), or a
    /// display UUID (<c>CGDisplayCreateUUIDFromDisplayID</c> string). Default "".
    /// </summary>
    public string TargetDisplay
    {
        get
        {
            string? value = _defaults.StringForKey(Keys.TargetDisplay) ?? _defaults.StringForKey(LegacyTargetDisplayUuidKey);
            return value is null || value.Length > SettingLimits.MaxTargetDisplayLength ? SettingLimits.MainDisplay : value;
        }

        set
        {
            value ??= SettingLimits.MainDisplay;
            if (value.Length > SettingLimits.MaxTargetDisplayLength)
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

    /// <summary>
    /// The display UUID the target resolves to for <c>DisplayTargetResolver</c>: null for the main display. "All
    /// displays" also returns null until the resolver supports it (P5-C2).
    /// </summary>
    public string? TargetDisplayUuid
    {
        get
        {
            string target = TargetDisplay;
            return target.Length == 0 || string.Equals(target, SettingLimits.AllDisplays, StringComparison.Ordinal) ? null : target;
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

    /// <summary>A rectangle inside 0..1 with positive, finite size (the #154 Rect rule).</summary>
    public static bool IsValidRegion(NormalizedRect r) =>
        double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height)
        && r.X >= 0 && r.Y >= 0 && r.Width > 0 && r.Height > 0
        && r.X + r.Width <= 1 + RegionEpsilon && r.Y + r.Height <= 1 + RegionEpsilon;

    /// <summary>True when the rectangle covers the whole display.</summary>
    public static bool IsFull(NormalizedRect r) =>
        r.X <= RegionEpsilon && r.Y <= RegionEpsilon && r.Width >= 1 - RegionEpsilon && r.Height >= 1 - RegionEpsilon;

    /// <summary>Region aspect lock (ADR-0031). Default false.</summary>
    public bool RegionAspectLock
    {
        get => GetBool(Keys.RegionAspectLock, false);
        set => SetBool(Keys.RegionAspectLock, value, RegionAspectLock);
    }

    // ---- Pen (ADR-0032) ----

    /// <summary>Pen or Mouse mode. Default Pen.</summary>
    public StylusMode StylusMode
    {
        get => (StylusMode)GetEnum(Keys.StylusMode, (int)StylusMode.Pen, (int)StylusMode.Mouse, (int)StylusMode.Pen);
        set => SetEnum(Keys.StylusMode, (int)value, (int)StylusMode.Pen, (int)StylusMode.Mouse, (int)StylusMode);
    }

    /// <summary>Mouse-mode speed, 0.25..4. Default 1.</summary>
    public float MouseSpeed
    {
        get => GetFloat(Keys.MouseSpeed, SettingLimits.DefaultSpeed, SettingLimits.MinSpeed, SettingLimits.MaxSpeed);
        set => SetFloat(Keys.MouseSpeed, value, SettingLimits.MinSpeed, SettingLimits.MaxSpeed, MouseSpeed);
    }

    /// <summary>Mouse-mode acceleration curve on/off. Default true.</summary>
    public bool MouseAcceleration
    {
        get => GetBool(Keys.MouseAcceleration, true);
        set => SetBool(Keys.MouseAcceleration, value, MouseAcceleration);
    }

    /// <summary>Side-button action. Default Right click.</summary>
    public SideButtonAction SideButton
    {
        get => (SideButtonAction)GetEnum(Keys.SideButton, (int)SideButtonAction.RightClick, (int)SideButtonAction.None, (int)SideButtonAction.RightClick);
        set => SetEnum(Keys.SideButton, (int)value, (int)SideButtonAction.RightClick, (int)SideButtonAction.None, (int)SideButton);
    }

    /// <summary>Tip feel, −1 (soft) .. +1 (firm). Default 0 (linear).</summary>
    public float TipFeel
    {
        get => GetFloat(Keys.TipFeel, 0f, SettingLimits.MinTipFeel, SettingLimits.MaxTipFeel);
        set => SetFloat(Keys.TipFeel, value, SettingLimits.MinTipFeel, SettingLimits.MaxTipFeel, TipFeel);
    }

    /// <summary>
    /// Click threshold, 0..0.5. Default 0. Kept at least <see cref="SettingLimits.MinPressureSpan"/> below
    /// <see cref="FullPressureAt"/>: a larger value is clamped.
    /// </summary>
    public float ClickThreshold
    {
        get => GetFloat(Keys.ClickThreshold, 0f, SettingLimits.MinClickThreshold, SettingLimits.MaxClickThreshold);
        set => SetFloat(Keys.ClickThreshold, value, SettingLimits.MinClickThreshold, MathF.Min(SettingLimits.MaxClickThreshold, FullPressureAt - SettingLimits.MinPressureSpan), ClickThreshold);
    }

    /// <summary>
    /// Full pressure at, 0.5..1. Default 1. Kept at least <see cref="SettingLimits.MinPressureSpan"/> above
    /// <see cref="ClickThreshold"/>: a smaller value is clamped.
    /// </summary>
    public float FullPressureAt
    {
        get
        {
            float value = GetFloat(Keys.FullPressureAt, SettingLimits.MaxFullPressureAt, SettingLimits.MinFullPressureAt, SettingLimits.MaxFullPressureAt);
            return MathF.Max(value, ClickThreshold + SettingLimits.MinPressureSpan);
        }

        set => SetFloat(Keys.FullPressureAt, value, MathF.Max(SettingLimits.MinFullPressureAt, ClickThreshold + SettingLimits.MinPressureSpan), SettingLimits.MaxFullPressureAt, FullPressureAt);
    }

    // ---- Gestures (ADR-0033) ----

    /// <summary>Smart Zoom: follow macOS, on, off. Default follow macOS.</summary>
    public SmartZoomMode SmartZoom
    {
        get => (SmartZoomMode)GetEnum(Keys.SmartZoom, (int)SmartZoomMode.FollowSystem, (int)SmartZoomMode.Off, (int)SmartZoomMode.FollowSystem);
        set => SetEnum(Keys.SmartZoom, (int)value, (int)SmartZoomMode.FollowSystem, (int)SmartZoomMode.Off, (int)SmartZoom);
    }

    /// <summary>Finger pointer speed (one-finger cursor, three-finger drag), 0.25..4. Default 1.</summary>
    public float PointerSpeed
    {
        get => GetFloat(Keys.PointerSpeed, SettingLimits.DefaultSpeed, SettingLimits.MinSpeed, SettingLimits.MaxSpeed);
        set => SetFloat(Keys.PointerSpeed, value, SettingLimits.MinSpeed, SettingLimits.MaxSpeed, PointerSpeed);
    }

    /// <summary>Two-finger scroll speed (and momentum), 0.25..4. Default 1.</summary>
    public float ScrollSpeed
    {
        get => GetFloat(Keys.ScrollSpeed, SettingLimits.DefaultSpeed, SettingLimits.MinSpeed, SettingLimits.MaxSpeed);
        set => SetFloat(Keys.ScrollSpeed, value, SettingLimits.MinSpeed, SettingLimits.MaxSpeed, ScrollSpeed);
    }

    /// <summary>Pinch zoom. Default true.</summary>
    public bool Zoom
    {
        get => GetBool(Keys.Zoom, true);
        set => SetBool(Keys.Zoom, value, Zoom);
    }

    /// <summary>Two-finger rotate. Default true.</summary>
    public bool Rotate
    {
        get => GetBool(Keys.Rotate, true);
        set => SetBool(Keys.Rotate, value, Rotate);
    }

    /// <summary>Three-finger drag. Default true.</summary>
    public bool ThreeFingerDrag
    {
        get => GetBool(Keys.ThreeFingerDrag, true);
        set => SetBool(Keys.ThreeFingerDrag, value, ThreeFingerDrag);
    }

    /// <summary>Four-finger gestures. Default true.</summary>
    public bool FourFingerGestures
    {
        get => GetBool(Keys.FourFingerGestures, true);
        set => SetBool(Keys.FourFingerGestures, value, FourFingerGestures);
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

    private bool GetBool(string key, bool defaultValue) =>
        _defaults[key] is NSNumber number ? number.BoolValue : defaultValue;

    private void SetBool(string key, bool value, bool current)
    {
        bool changed = current != value;
        _defaults.SetBool(value, key);
        if (changed)
        {
            Raise(key);
        }
    }

    private int GetEnum(string key, int min, int max, int defaultValue)
    {
        if (_defaults[key] is not NSNumber number)
        {
            return defaultValue;
        }

        long value = number.Int64Value;
        return value >= min && value <= max ? (int)value : defaultValue;
    }

    private void SetEnum(string key, int value, int min, int max, int current)
    {
        if (value < min || value > max)
        {
            return;
        }

        bool changed = current != value;
        _defaults.SetInt(value, key);
        if (changed)
        {
            Raise(key);
        }
    }

    private float GetFloat(string key, float defaultValue, float min, float max)
    {
        if (_defaults[key] is not NSNumber number)
        {
            return defaultValue;
        }

        float value = number.FloatValue;
        return float.IsFinite(value) ? Math.Clamp(value, min, max) : defaultValue;
    }

    private void SetFloat(string key, float value, float min, float max, float current)
    {
        if (!float.IsFinite(value) || min > max)
        {
            return;
        }

        value = Math.Clamp(value, min, max);
        bool changed = current != value;
        _defaults.SetFloat(value, key);
        if (changed)
        {
            Raise(key);
        }
    }

    private void Raise(string key, string? displayUuid = null) =>
        Changed?.Invoke(this, new SettingChangedEventArgs(key, displayUuid));
}
