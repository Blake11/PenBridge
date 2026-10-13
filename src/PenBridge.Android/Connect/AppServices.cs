using Android.Content;
using PenBridge.Android.Services;

namespace PenBridge.Android.Connect;

/// <summary>
/// Process-wide composition root. The <see cref="ClientHost"/> outlives activity instances, so a recreated activity
/// (theme, locale or density change) finds the session and its reconnect target where they were; the activity only
/// drives <see cref="ClientHost.OnStart"/>/<see cref="ClientHost.OnStop"/>. Main thread only.
/// </summary>
internal static class AppServices
{
    private static ClientHost? host;

    public static ClientHost GetHost(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context app = context.ApplicationContext ?? context;
        return host ??= new ClientHost(app, ClientHost.CreateSurface(app));
    }
}
