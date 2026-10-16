namespace PenBridge.Mac.Settings;

/// <summary>
/// Ranges and defaults of the Mac settings (issue #154's key table, ADR-0031–0033). Core's <c>MacSettingKey</c>
/// table is the long-term single source; until it is wired (P5-C2) these mirror it.
/// </summary>
public static class SettingLimits
{
    /// <summary>TargetDisplay value for the main display.</summary>
    public const string MainDisplay = "";

    /// <summary>TargetDisplay value for "All displays" (ADR-0031).</summary>
    public const string AllDisplays = "all";

    /// <summary>Longest TargetDisplay string (UTF-8 bytes on the wire; display UUIDs are 36 ASCII characters).</summary>
    public const int MaxTargetDisplayLength = 256;

    public const float MinSpeed = 0.25f;
    public const float MaxSpeed = 4f;
    public const float DefaultSpeed = 1f;

    public const float MinTipFeel = -1f;
    public const float MaxTipFeel = 1f;

    public const float MinClickThreshold = 0f;
    public const float MaxClickThreshold = 0.5f;

    public const float MinFullPressureAt = 0.5f;
    public const float MaxFullPressureAt = 1f;

    /// <summary>
    /// FullPressureAt must exceed ClickThreshold by more than 0.1 (#154). The store keeps this margin, a little over
    /// 0.1 so float rounding never lands on the boundary.
    /// </summary>
    public const float MinPressureSpan = 0.101f;
}
