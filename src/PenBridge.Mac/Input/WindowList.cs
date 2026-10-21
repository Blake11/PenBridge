using Foundation;
using ObjCRuntime;
using PenBridge.Core.Input;

namespace PenBridge.Mac.Input;

/// <summary>One app window from the window server's list.</summary>
/// <param name="Number">The CGWindowID.</param>
/// <param name="Pid">The owning process.</param>
/// <param name="OwnerName">The owning app's name (no Screen Recording permission needed; window titles would need it).</param>
/// <param name="Bounds">The frame in global display points, top-left origin, y down.</param>
public sealed record WindowListEntry(uint Number, int Pid, string OwnerName, DisplayRect Bounds);

/// <summary>
/// The window server's window list (<c>CGWindowListCopyWindowInfo</c>) for the window target (ADR-0036): which normal
/// app windows are on screen and whether one is on the current Space. Only bounds, owner, layer and number are read,
/// so no Screen Recording permission is needed.
/// </summary>
/// <remarks>Called only on demand (picker open, Space change), never polled.</remarks>
internal static class WindowList
{
    /// <summary>Smaller windows (helper panels, invisible utility windows) can't be picked.</summary>
    private const double MinSide = 40;

    private static readonly NSString NumberKey = new("kCGWindowNumber");
    private static readonly NSString PidKey = new("kCGWindowOwnerPID");
    private static readonly NSString OwnerNameKey = new("kCGWindowOwnerName");
    private static readonly NSString LayerKey = new("kCGWindowLayer");
    private static readonly NSString AlphaKey = new("kCGWindowAlpha");
    private static readonly NSString BoundsKey = new("kCGWindowBounds");
    private static readonly NSString OnscreenKey = new("kCGWindowIsOnscreen");
    private static readonly NSString XKey = new("X");
    private static readonly NSString YKey = new("Y");
    private static readonly NSString WidthKey = new("Width");
    private static readonly NSString HeightKey = new("Height");

    /// <summary>
    /// The normal app windows on screen now, front to back: layer 0 (so not the Dock, the menu bar or other system
    /// UI), visible, at least <see cref="MinSide"/> points per side, and not owned by <paramref name="excludePid"/>.
    /// </summary>
    public static List<WindowListEntry> OnScreenAppWindows(int excludePid)
    {
        var result = new List<WindowListEntry>();
        using NSArray? list = Copy(NativeMethods.WindowListOnScreenOnly | NativeMethods.WindowListExcludeDesktopElements, 0);
        if (list is null)
        {
            return result;
        }

        for (nuint i = 0; i < list.Count; i++)
        {
            if (list.GetItem<NSDictionary>(i) is not { } info
                || Int(info, LayerKey) != 0
                || Int(info, PidKey) is not { } pid || pid == excludePid
                || Int(info, NumberKey) is not { } number
                || (info[AlphaKey] is NSNumber alpha && !(alpha.DoubleValue > 0))
                || Bounds(info) is not { } bounds || !(bounds.Width >= MinSide) || !(bounds.Height >= MinSide))
            {
                continue;
            }

            string owner = (info[OwnerNameKey] as NSString)?.ToString() ?? string.Empty;
            if (owner is "Dock" or "Window Server")
            {
                continue;
            }

            result.Add(new WindowListEntry((uint)number, (int)pid, owner, bounds));
        }

        return result;
    }

    /// <summary>
    /// True when the window is on screen now, i.e. on the current Space and neither minimised nor hidden; null when the
    /// window server doesn't know it (closed).
    /// </summary>
    public static bool? IsOnScreen(uint windowNumber)
    {
        using NSArray? list = Copy(NativeMethods.WindowListIncludingWindow, windowNumber);
        if (list is null || list.Count == 0 || list.GetItem<NSDictionary>(0) is not { } info || Int(info, NumberKey) != windowNumber)
        {
            return null;
        }

        return info[OnscreenKey] is NSNumber onscreen && onscreen.BoolValue;
    }

    private static NSArray? Copy(uint option, uint window)
    {
        IntPtr array = NativeMethods.CGWindowListCopyWindowInfo(option, window);
        return array == IntPtr.Zero ? null : Runtime.GetNSObject<NSArray>(array, owns: true);
    }

    private static long? Int(NSDictionary info, NSString key) => info[key] is NSNumber n ? n.Int64Value : null;

    private static DisplayRect? Bounds(NSDictionary info)
    {
        if (info[BoundsKey] is not NSDictionary b
            || b[XKey] is not NSNumber x || b[YKey] is not NSNumber y
            || b[WidthKey] is not NSNumber w || b[HeightKey] is not NSNumber h)
        {
            return null;
        }

        var rect = new DisplayRect(x.DoubleValue, y.DoubleValue, w.DoubleValue, h.DoubleValue);
        return double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height) ? rect : null;
    }
}
