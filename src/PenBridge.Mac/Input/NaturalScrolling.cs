using Foundation;
using Microsoft.Extensions.Logging;

namespace PenBridge.Mac.Input;

/// <summary>
/// Tracks the macOS natural-scrolling preference (System Settings ▸ Trackpad/Mouse ▸ Natural scrolling), the global
/// <c>com.apple.swipescrolldirection</c> key. Absent means on (the macOS default). Event-driven (ADR-0023): read once
/// at construction and again whenever the system broadcasts <c>SwipeScrollDirectionDidChangeNotification</c>; the
/// input path only reads the cached <see cref="Value"/>, never the preferences.
/// </summary>
/// <remarks>
/// macOS applies this preference to real trackpad/mouse events before they become CGEvents. Posted events arrive
/// unchanged, so the injector applies the sign itself (<see cref="PenBridge.Core.Input.GestureOutput.ToScrollWheel"/>).
/// Create and dispose on the main thread; the broadcast is delivered there.
/// </remarks>
internal sealed class NaturalScrolling : IDisposable
{
    /// <summary>Distributed notification posted by the system when the setting changes (name found in the OS).</summary>
    private const string ChangedNotification = "SwipeScrollDirectionDidChangeNotification";

    // kCFPreferencesAnyApplication is CFSTR("kCFPreferencesAnyApplication"): an equal string works the same, without
    // a dlsym lookup. Both strings live for the process (toll-free bridged NSString = CFStringRef).
    private static readonly NSString AnyApplication = new("kCFPreferencesAnyApplication");
    private static readonly NSString Key = new("com.apple.swipescrolldirection");

    private readonly ILogger _logger;
    private readonly NSObject _observer;
    private volatile bool _value;

    /// <param name="logger">Logs the value at startup and on each change broadcast.</param>
    public NaturalScrolling(ILogger logger)
    {
        _logger = logger;
        Refresh();
        _observer = NSDistributedNotificationCenter.DefaultCenter.AddObserver(
            new NSString(ChangedNotification), _ => Refresh());
    }

    /// <summary>True when content should follow the fingers. A cached field read; safe from any thread.</summary>
    public bool Value => _value;

    public void Dispose()
    {
        NSDistributedNotificationCenter.DefaultCenter.RemoveObserver(_observer);
        _observer.Dispose();
    }

    private void Refresh()
    {
        bool natural = Read();
        _value = natural;
        InputLog.NaturalScrolling(_logger, natural);
    }

    private static bool Read()
    {
        _ = NativeMethods.CFPreferencesAppSynchronize(AnyApplication.Handle);
        byte value = NativeMethods.CFPreferencesGetAppBooleanValue(Key.Handle, AnyApplication.Handle, out byte valid);
        return valid == 0 || value != 0;
    }
}
