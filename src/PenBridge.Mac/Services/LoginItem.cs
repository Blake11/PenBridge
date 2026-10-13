using Foundation;
using ServiceManagement;

namespace PenBridge.Mac.Services;

/// <summary>"Run at startup" via <see cref="SMAppService.MainApp"/> (macOS 13+): the app itself is the login item.</summary>
/// <remarks>
/// Registration can end in <see cref="SMAppServiceStatus.RequiresApproval"/> when the user has disabled the app under
/// System Settings ▸ General ▸ Login Items; <see cref="OpenSystemSettings"/> takes them there. The status reflects the
/// signed bundle that registered, so a rebuilt app at another path may show <see cref="SMAppServiceStatus.NotFound"/>.
/// </remarks>
public static class LoginItem
{
    public static SMAppServiceStatus Status => SMAppService.MainApp.Status;

    /// <summary>True when the app is registered and enabled as a login item.</summary>
    public static bool IsEnabled => Status == SMAppServiceStatus.Enabled;

    /// <summary>Registers or unregisters the app; <paramref name="error"/> is the system's message on failure.</summary>
    public static bool TrySetEnabled(bool enabled, out string? error)
    {
        SMAppService service = SMAppService.MainApp;
        bool ok = enabled ? service.Register(out NSError? nsError) : service.Unregister(out nsError);
        error = ok ? null : nsError?.LocalizedDescription ?? "Unknown error.";
        return ok;
    }

    /// <summary>Opens System Settings ▸ General ▸ Login Items (for <see cref="SMAppServiceStatus.RequiresApproval"/>).</summary>
    public static void OpenSystemSettings() => SMAppService.OpenSystemSettingsLoginItems();
}
