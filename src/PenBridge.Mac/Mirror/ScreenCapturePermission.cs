using AppKit;
using Foundation;

namespace PenBridge.Mac.Mirror;

/// <summary>
/// The Screen Recording permission (TCC) that mirroring needs (spec "Heads → Mac → Permission"): checked without a
/// prompt, requested the first time mirroring is enabled, and the System Settings pane for the user to grant it later,
/// the same guidance pattern as Accessibility.
/// </summary>
/// <remarks>
/// <see cref="Request"/> shows the system prompt at most once per app (macOS remembers the answer); later calls only
/// return the state. A grant given in System Settings may only take effect after PenBridge restarts (macOS offers
/// "Quit &amp; Reopen"). Main thread for <see cref="Request"/> and <see cref="OpenSettings"/>.
/// </remarks>
public static class ScreenCapturePermission
{
    private const string SettingsPane = "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture";

    /// <summary>True when Screen Recording is granted. No prompt; any thread.</summary>
    public static bool IsGranted => MirrorNative.CGPreflightScreenCaptureAccess() != 0;

    /// <summary>Shows the system prompt if macOS hasn't asked before; returns whether it is granted now.</summary>
    public static bool Request() => MirrorNative.CGRequestScreenCaptureAccess() != 0;

    /// <summary>Opens System Settings ▸ Privacy &amp; Security ▸ Screen Recording.</summary>
    public static void OpenSettings()
    {
        using var url = new NSUrl(SettingsPane);
        NSWorkspace.SharedWorkspace.OpenUrl(url);
    }
}
