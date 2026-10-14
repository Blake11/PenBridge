using System.Runtime.InteropServices;

namespace PenBridge.Mac.Input;

/// <summary>
/// CoreGraphics/ApplicationServices functions used directly: the ones .NET for macOS does not bind (display list and
/// UUID, Accessibility), and the CGEvent calls of the per-sample hot path, which go through raw handles so no managed
/// wrapper is allocated per event. Off the hot path the bindings are used (CGEventSource, the CGEventField/CGEventType
/// enums for field and type ids, CGDisplay bounds and main id, NSScreen).
/// </summary>
/// <remarks>
/// Signatures are blittable only (IntPtr, uint, byte, blittable arrays/byrefs; C <c>Boolean</c> is returned as
/// <see cref="byte"/>), so no runtime marshalling is involved and they are NativeAOT/trim safe without reflection.
/// They use <see cref="DllImportAttribute"/> rather than <c>LibraryImport</c> because the generator's output needs
/// <c>AllowUnsafeBlocks</c>, which the Mac project does not enable; for blittable signatures both compile to the same
/// direct call. Framework paths are absolute (dyld shared cache), so no search path is involved.
/// </remarks>
internal static class NativeMethods
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    // ApplicationServices re-exports HIServices (AXIsProcessTrusted*) and ColorSync (CGDisplayCreateUUIDFromDisplayID).
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary><c>CGEventRef CGEventCreate(CGEventSourceRef)</c>; +1 retained, NULL on failure. Timestamp left at 0.</summary>
    [DllImport(CoreGraphics)]
    internal static extern IntPtr CGEventCreate(IntPtr source);

    /// <summary><c>CGEventRef CGEventCreateMouseEvent(source, CGEventType, CGPoint, CGMouseButton)</c>; +1 retained, NULL on failure.</summary>
    [DllImport(CoreGraphics)]
    internal static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint mouseType, NativePoint position, uint mouseButton);

    /// <summary>
    /// <c>CGEventRef CGEventCreateScrollWheelEvent2(source, CGScrollEventUnit, uint32_t wheelCount, int32_t wheel1,
    /// int32_t wheel2, int32_t wheel3)</c> (the non-variadic form); +1 retained, NULL on failure. Location = the current
    /// cursor position.
    /// </summary>
    [DllImport(CoreGraphics)]
    internal static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);

    /// <summary><c>CGPoint CGEventGetLocation(CGEventRef)</c>: global display coordinates (top-left origin, points).</summary>
    [DllImport(CoreGraphics)]
    internal static extern NativePoint CGEventGetLocation(IntPtr ev);

    [DllImport(CoreGraphics)]
    internal static extern void CGEventSetType(IntPtr ev, uint type);

    [DllImport(CoreGraphics)]
    internal static extern void CGEventSetLocation(IntPtr ev, NativePoint location);

    [DllImport(CoreGraphics)]
    internal static extern void CGEventSetIntegerValueField(IntPtr ev, uint field, long value);

    [DllImport(CoreGraphics)]
    internal static extern void CGEventSetDoubleValueField(IntPtr ev, uint field, double value);

    /// <summary>Nanoseconds on CLOCK_UPTIME_RAW (measured; see the mac-build-run skill).</summary>
    [DllImport(CoreGraphics)]
    internal static extern void CGEventSetTimestamp(IntPtr ev, ulong timestamp);

    /// <summary><c>void CGEventPost(CGEventTapLocation, CGEventRef)</c>; thread-safe.</summary>
    [DllImport(CoreGraphics)]
    internal static extern void CGEventPost(uint tap, IntPtr ev);

    /// <summary><c>CGError CGGetActiveDisplayList(uint32_t max, CGDirectDisplayID *displays, uint32_t *count)</c>; 0 = success.</summary>
    [DllImport(CoreGraphics)]
    internal static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[]? activeDisplays, out uint displayCount);

    /// <summary><c>CFUUIDRef CGDisplayCreateUUIDFromDisplayID(CGDirectDisplayID)</c>; +1 retained, may be NULL.</summary>
    [DllImport(ApplicationServices)]
    internal static extern IntPtr CGDisplayCreateUUIDFromDisplayID(uint display);

    /// <summary><c>CFStringRef CFUUIDCreateString(CFAllocatorRef, CFUUIDRef)</c>; +1 retained.</summary>
    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFUUIDCreateString(IntPtr allocator, IntPtr uuid);

    [DllImport(CoreFoundation)]
    internal static extern void CFRelease(IntPtr cf);

    /// <summary>
    /// <c>Boolean CFPreferencesGetAppBooleanValue(CFStringRef key, CFStringRef app, Boolean *keyExistsAndHasValidFormat)</c>;
    /// accepts CFBoolean and CFNumber values.
    /// </summary>
    [DllImport(CoreFoundation)]
    internal static extern byte CFPreferencesGetAppBooleanValue(IntPtr key, IntPtr applicationId, out byte keyExistsAndHasValidFormat);

    /// <summary><c>Boolean CFPreferencesAppSynchronize(CFStringRef app)</c>: picks up values other processes wrote.</summary>
    [DllImport(CoreFoundation)]
    internal static extern byte CFPreferencesAppSynchronize(IntPtr applicationId);

    /// <summary><c>Boolean AXIsProcessTrusted(void)</c>: whether this process may post input events (Accessibility).</summary>
    [DllImport(ApplicationServices)]
    internal static extern byte AXIsProcessTrusted();

    /// <summary><c>Boolean AXIsProcessTrustedWithOptions(CFDictionaryRef)</c>; with the prompt option it shows the system prompt.</summary>
    [DllImport(ApplicationServices)]
    internal static extern byte AXIsProcessTrustedWithOptions(IntPtr options);
}

/// <summary>C <c>CGPoint</c> on 64-bit macOS (two <c>CGFloat</c> = double), blittable.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly struct NativePoint(double x, double y)
{
    public readonly double X = x;
    public readonly double Y = y;
}
