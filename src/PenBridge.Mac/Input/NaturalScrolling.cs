using Foundation;

namespace PenBridge.Mac.Input;

/// <summary>
/// Reads the macOS natural-scrolling preference (System Settings ▸ Trackpad/Mouse ▸ Natural scrolling), the global
/// <c>com.apple.swipescrolldirection</c> key. Absent means on (the macOS default). Read at each gesture start, never
/// polled (ADR-0023).
/// </summary>
/// <remarks>
/// macOS applies this preference to real trackpad/mouse events before they become CGEvents. Posted events arrive
/// unchanged, so the injector applies the sign itself (<see cref="PenBridge.Core.Input.GestureOutput.ToScrollWheel"/>).
/// </remarks>
internal static class NaturalScrolling
{
    // kCFPreferencesAnyApplication is CFSTR("kCFPreferencesAnyApplication"): an equal string works the same, without
    // a dlsym lookup. Both strings live for the process (toll-free bridged NSString = CFStringRef).
    private static readonly NSString AnyApplication = new("kCFPreferencesAnyApplication");
    private static readonly NSString Key = new("com.apple.swipescrolldirection");

    /// <summary>True when content should follow the fingers. Not per event: one preferences sync per call.</summary>
    public static bool Read()
    {
        _ = NativeMethods.CFPreferencesAppSynchronize(AnyApplication.Handle);
        byte value = NativeMethods.CFPreferencesGetAppBooleanValue(Key.Handle, AnyApplication.Handle, out byte valid);
        return valid == 0 || value != 0;
    }
}
