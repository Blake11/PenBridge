namespace PenBridge.Mac.Settings;

/// <summary>Stylus mode (ADR-0032). Values match the wire table (key StylusMode).</summary>
public enum StylusMode
{
    /// <summary>Absolute: the active area maps onto the target.</summary>
    Pen = 0,

    /// <summary>Relative: the hovering pen moves the cursor from where it is.</summary>
    Mouse = 1,
}

/// <summary>Side-button action (ADR-0032). Values match the wire table (key SideButton).</summary>
public enum SideButtonAction
{
    RightClick = 0,
    MiddleClick = 1,
    Eraser = 2,
    DoubleClick = 3,
    None = 4,
}

/// <summary>Smart Zoom override (ADR-0033). Values match the wire table (key SmartZoom).</summary>
public enum SmartZoomMode
{
    FollowSystem = 0,
    On = 1,
    Off = 2,
}

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
