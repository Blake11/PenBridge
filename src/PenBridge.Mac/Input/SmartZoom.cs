using Foundation;

namespace PenBridge.Mac.Input;

/// <summary>
/// Reads the macOS Smart Zoom setting (System Settings ▸ Trackpad ▸ Scroll &amp; Zoom ▸ Smart zoom), the
/// <c>TrackpadTwoFingerDoubleTapGesture</c> key, for <see cref="PenBridge.Core.Input.GestureRecognizerOptions.SmartZoomEnabled"/>
/// (ADR-0029). With it off a two-finger tap right-clicks on the lift instead of waiting for a possible double tap.
/// </summary>
/// <remarks>
/// <para>The recognizer calls <see cref="IsEnabled"/> once per decided two-finger tap, so the value is read live
/// (no cache, no change notification) and a System Settings change applies at the next tap. A read costs a few
/// microseconds and allocates nothing: the keys are cached CFStrings and <c>CFPreferencesGetAppBooleanValue</c>
/// returns a plain Boolean.</para>
/// <para>The setting lives in two domains, the built-in trackpad one and the Bluetooth (Magic Trackpad) one; System
/// Settings writes both. The built-in domain wins when it holds a valid value, else the Bluetooth one; neither
/// (or any failure) counts as on, the macOS default — on keeps the double-tap window, so smart zoom never breaks.</para>
/// <para>Thread-safe: CFPreferences may be read from any thread (the session input thread calls this).</para>
/// </remarks>
internal static class SmartZoom
{
    // Live for the process (toll-free bridged NSString = CFStringRef).
    private static readonly NSString Key = new("TrackpadTwoFingerDoubleTapGesture");
    private static readonly NSString BuiltInDomain = new("com.apple.AppleMultitouchTrackpad");
    private static readonly NSString BluetoothDomain = new("com.apple.driver.AppleBluetoothMultitouch.trackpad");

    /// <summary>Cached delegate for the gesture options, so passing it never allocates.</summary>
    public static readonly Func<bool> Delegate = IsEnabled;

    /// <summary>True unless the Smart Zoom setting is explicitly off. Never throws.</summary>
    public static bool IsEnabled()
    {
        try
        {
            if (TryRead(BuiltInDomain, out bool builtIn))
            {
                return builtIn;
            }

            return !TryRead(BluetoothDomain, out bool bluetooth) || bluetooth;
        }
        catch (Exception)
        {
            return true; // unreadable counts as on (ADR-0029)
        }
    }

    private static bool TryRead(NSString domain, out bool value)
    {
        // Synchronize so a value another process (System Settings, `defaults`) wrote is seen at once.
        _ = NativeMethods.CFPreferencesAppSynchronize(domain.Handle);
        byte raw = NativeMethods.CFPreferencesGetAppBooleanValue(Key.Handle, domain.Handle, out byte valid);
        value = raw != 0;
        return valid != 0;
    }
}
