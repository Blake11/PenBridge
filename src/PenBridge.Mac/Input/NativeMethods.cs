using System.Runtime.InteropServices;

namespace PenBridge.Mac.Input;

/// <summary>
/// The few CoreGraphics/ApplicationServices functions .NET for macOS does not bind. Everything else (CGEvent,
/// CGEventSource, CGEventField, CGDisplay bounds/main id, NSScreen) uses the bindings.
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

    /// <summary><c>Boolean AXIsProcessTrusted(void)</c>: whether this process may post input events (Accessibility).</summary>
    [DllImport(ApplicationServices)]
    internal static extern byte AXIsProcessTrusted();

    /// <summary><c>Boolean AXIsProcessTrustedWithOptions(CFDictionaryRef)</c>; with the prompt option it shows the system prompt.</summary>
    [DllImport(ApplicationServices)]
    internal static extern byte AXIsProcessTrustedWithOptions(IntPtr options);
}
