using Microsoft.Extensions.Logging;

namespace PenBridge.Mac.Input;

/// <summary>Source-generated log messages for the injector and display resolver. Nothing here runs per sample.</summary>
internal static partial class InputLog
{
    [LoggerMessage(100, LogLevel.Warning,
        "Accessibility permission missing: pen input is dropped. Grant PenBridge in System Settings > Privacy & Security > Accessibility")]
    public static partial void AccessibilityMissing(ILogger logger);

    [LoggerMessage(101, LogLevel.Information, "Accessibility permission granted: pen input is injected")]
    public static partial void AccessibilityGranted(ILogger logger);

    [LoggerMessage(102, LogLevel.Information, "Target display {Name} ({Uuid}) at {X},{Y} {Width}x{Height} pt{Fallback}")]
    public static partial void TargetDisplay(ILogger logger, string name, string uuid, double x, double y, double width, double height, string fallback);

    [LoggerMessage(103, LogLevel.Debug, "Displays reconfigured: {Count} active")]
    public static partial void DisplaysReconfigured(ILogger logger, int count);

    [LoggerMessage(104, LogLevel.Warning, "No active display: pen input is dropped until a display is available")]
    public static partial void NoDisplays(ILogger logger);

    [LoggerMessage(105, LogLevel.Information, "Natural scrolling: {Natural}")]
    public static partial void NaturalScrolling(ILogger logger, bool natural);

    [LoggerMessage(106, LogLevel.Information, "Four-finger gestures: {Mode}")]
    public static partial void SystemGestureMode(ILogger logger, string mode);

    [LoggerMessage(107, LogLevel.Warning, "No app launcher found ({BundleIds}): four-finger pinch-in does nothing")]
    public static partial void LauncherMissing(ILogger logger, string bundleIds);

    [LoggerMessage(108, LogLevel.Warning, "Opening the app launcher failed: {Error}")]
    public static partial void LauncherFailed(ILogger logger, string error);
}
