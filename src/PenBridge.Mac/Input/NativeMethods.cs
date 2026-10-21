using System.Runtime.InteropServices;

namespace PenBridge.Mac.Input;

/// <summary>
/// CoreGraphics/ApplicationServices functions used directly: the ones .NET for macOS does not bind (display list and
/// UUID, Accessibility, the window list and AX observers of a window target), and the CGEvent calls of the per-sample hot path, which go through raw handles so no managed
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

    /// <summary><c>CGRect CGDisplayBounds(CGDirectDisplayID)</c>: global display coordinates (points).</summary>
    [DllImport(CoreGraphics)]
    internal static extern NativeRect CGDisplayBounds(uint display);

    /// <summary>
    /// <c>CGEventRef CGEventCreateKeyboardEvent(source, CGKeyCode, bool keyDown)</c>; +1 retained, NULL on failure.
    /// C <c>bool</c> is passed as <see cref="byte"/>.
    /// </summary>
    [DllImport(CoreGraphics)]
    internal static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, byte keyDown);

    /// <summary><c>void CGEventSetFlags(CGEventRef, CGEventFlags)</c>.</summary>
    [DllImport(CoreGraphics)]
    internal static extern void CGEventSetFlags(IntPtr ev, ulong flags);

    /// <summary>
    /// <c>CGEventFlags CGEventSourceFlagsState(CGEventSourceStateID)</c>: the modifier state of a source state table
    /// (1 = HID system). Posted keyboard events update it, and new events from that source inherit it.
    /// </summary>
    [DllImport(CoreGraphics)]
    internal static extern ulong CGEventSourceFlagsState(int stateId);

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

    // ---- Window target (ADR-0036): the window list, Accessibility elements and observers ----

    /// <summary><c>kCGWindowListOptionOnScreenOnly</c>: windows on screen now (the current Space), front to back.</summary>
    internal const uint WindowListOnScreenOnly = 1;

    /// <summary><c>kCGWindowListOptionIncludingWindow</c>: just the given window, on screen or not.</summary>
    internal const uint WindowListIncludingWindow = 8;

    /// <summary><c>kCGWindowListExcludeDesktopElements</c>: no desktop picture or icons.</summary>
    internal const uint WindowListExcludeDesktopElements = 16;

    /// <summary><c>kAXValueCGPointType</c>.</summary>
    internal const int AXValueCGPointType = 1;

    /// <summary><c>kAXValueCGSizeType</c>.</summary>
    internal const int AXValueCGSizeType = 2;

    /// <summary>
    /// <c>CFArrayRef CGWindowListCopyWindowInfo(CGWindowListOption, CGWindowID relativeToWindow)</c>; +1 retained, may be
    /// NULL. Bounds, owner pid and name, layer and number need no Screen Recording permission (titles do; not used).
    /// </summary>
    [DllImport(CoreGraphics)]
    internal static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    /// <summary><c>AXUIElementRef AXUIElementCreateApplication(pid_t)</c>; +1 retained.</summary>
    [DllImport(ApplicationServices)]
    internal static extern IntPtr AXUIElementCreateApplication(int pid);

    /// <summary><c>AXError AXUIElementCopyAttributeValue(AXUIElementRef, CFStringRef, CFTypeRef *)</c>; the value is +1 retained; 0 = success.</summary>
    [DllImport(ApplicationServices)]
    internal static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);

    /// <summary>
    /// <c>AXError AXUIElementSetMessagingTimeout(AXUIElementRef, float seconds)</c>: bounds how long a call to a busy app
    /// blocks (the calls run on the main thread).
    /// </summary>
    [DllImport(ApplicationServices)]
    internal static extern int AXUIElementSetMessagingTimeout(IntPtr element, float timeoutInSeconds);

    /// <summary><c>Boolean AXValueGetValue(AXValueRef, kAXValueCGPointType, CGPoint *)</c>; 0 when the value isn't a point.</summary>
    [DllImport(ApplicationServices, EntryPoint = "AXValueGetValue")]
    internal static extern byte AXValueGetPoint(IntPtr value, int type, out NativePoint point);

    /// <summary><c>Boolean AXValueGetValue(AXValueRef, kAXValueCGSizeType, CGSize *)</c>; 0 when the value isn't a size.</summary>
    [DllImport(ApplicationServices, EntryPoint = "AXValueGetValue")]
    internal static extern byte AXValueGetSize(IntPtr value, int type, out NativeSize size);

    /// <summary>
    /// <c>AXError AXObserverCreate(pid_t, AXObserverCallback, AXObserverRef *)</c>; +1 retained. The callback is
    /// <c>void (AXObserverRef, AXUIElementRef element, CFStringRef notification, void *refcon)</c>.
    /// </summary>
    [DllImport(ApplicationServices)]
    internal static extern unsafe int AXObserverCreate(int pid, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> callback, out IntPtr observer);

    /// <summary><c>AXError AXObserverAddNotification(AXObserverRef, AXUIElementRef, CFStringRef, void *refcon)</c>.</summary>
    [DllImport(ApplicationServices)]
    internal static extern int AXObserverAddNotification(IntPtr observer, IntPtr element, IntPtr notification, IntPtr refcon);

    /// <summary><c>AXError AXObserverRemoveNotification(AXObserverRef, AXUIElementRef, CFStringRef)</c>.</summary>
    [DllImport(ApplicationServices)]
    internal static extern int AXObserverRemoveNotification(IntPtr observer, IntPtr element, IntPtr notification);

    /// <summary><c>CFRunLoopSourceRef AXObserverGetRunLoopSource(AXObserverRef)</c>; not retained (owned by the observer).</summary>
    [DllImport(ApplicationServices)]
    internal static extern IntPtr AXObserverGetRunLoopSource(IntPtr observer);

    /// <summary><c>CFRunLoopRef CFRunLoopGetMain(void)</c>; not retained.</summary>
    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFRunLoopGetMain();

    [DllImport(CoreFoundation)]
    internal static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);

    [DllImport(CoreFoundation)]
    internal static extern void CFRunLoopRemoveSource(IntPtr runLoop, IntPtr source, IntPtr mode);

    /// <summary><c>CFIndex CFArrayGetCount(CFArrayRef)</c>.</summary>
    [DllImport(CoreFoundation)]
    internal static extern nint CFArrayGetCount(IntPtr array);

    /// <summary><c>const void *CFArrayGetValueAtIndex(CFArrayRef, CFIndex)</c>; not retained.</summary>
    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    /// <summary><c>CFTypeRef CFRetain(CFTypeRef)</c>.</summary>
    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFRetain(IntPtr cf);

    /// <summary><c>Boolean CFEqual(CFTypeRef, CFTypeRef)</c>.</summary>
    [DllImport(CoreFoundation)]
    internal static extern byte CFEqual(IntPtr a, IntPtr b);

    /// <summary><c>CFTypeID CFGetTypeID(CFTypeRef)</c>.</summary>
    [DllImport(CoreFoundation)]
    internal static extern nuint CFGetTypeID(IntPtr cf);

    /// <summary><c>CFTypeID CFBooleanGetTypeID(void)</c>.</summary>
    [DllImport(CoreFoundation)]
    internal static extern nuint CFBooleanGetTypeID();

    /// <summary><c>Boolean CFBooleanGetValue(CFBooleanRef)</c>.</summary>
    [DllImport(CoreFoundation)]
    internal static extern byte CFBooleanGetValue(IntPtr boolean);
}

/// <summary>C <c>CGPoint</c> on 64-bit macOS (two <c>CGFloat</c> = double), blittable.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly struct NativePoint(double x, double y)
{
    public readonly double X = x;
    public readonly double Y = y;
}

/// <summary>C <c>CGRect</c> on 64-bit macOS (origin, size; four <c>CGFloat</c>), blittable.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly struct NativeRect(double x, double y, double width, double height)
{
    public readonly double X = x;
    public readonly double Y = y;
    public readonly double Width = width;
    public readonly double Height = height;

    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>C <c>CGSize</c> on 64-bit macOS (two <c>CGFloat</c> = double), blittable.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly struct NativeSize(double width, double height)
{
    public readonly double Width = width;
    public readonly double Height = height;
}
