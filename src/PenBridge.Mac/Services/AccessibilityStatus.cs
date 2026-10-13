using System.Runtime.InteropServices;

namespace PenBridge.Mac.Services;

/// <summary>The Accessibility (TCC) grant that CGEvent posting needs. Without it posted events are dropped silently.</summary>
public static partial class AccessibilityStatus
{
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    /// <summary>
    /// <c>AXIsProcessTrusted()</c>: a cheap TCC lookup with no prompt. Check it on launch, on menu open and on connect
    /// (there is no notification for a grant change).
    /// </summary>
    public static bool IsTrusted() => AXIsProcessTrusted() != 0;

    /// <summary>System Settings ▸ Privacy &amp; Security ▸ Accessibility.</summary>
    public const string SettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    [LibraryImport(ApplicationServices)]
    private static partial byte AXIsProcessTrusted();
}
