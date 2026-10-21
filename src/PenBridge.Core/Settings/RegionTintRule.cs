using PenBridge.Core.Input;

namespace PenBridge.Core.Settings;

/// <summary>What decides the tint outside the active region (ADR-0035); the Mac fills it from its live state.</summary>
/// <param name="Connected">A phone session is active.</param>
/// <param name="Paused">Input is paused.</param>
/// <param name="Enabled">The <see cref="MacSettingKey.RegionTint"/> setting.</param>
/// <param name="OverlayOpen">The region drag overlay is open.</param>
/// <param name="SingleDisplay">The applied target is one display (not All displays, and a display exists).</param>
/// <param name="Region">That display's region, normalized, top-left origin, y down.</param>
/// <param name="Mode">The stylus mode (ADR-0032).</param>
public readonly record struct RegionTintState(
    bool Connected,
    bool Paused,
    bool Enabled,
    bool OverlayOpen,
    bool SingleDisplay,
    NormalizedRect Region,
    StylusMode Mode);

/// <summary>A rectangle in a view's own coordinates, bottom-left origin, y up (AppKit's convention), in points.</summary>
public readonly record struct ViewRect(double X, double Y, double Width, double Height);

/// <summary>
/// The rules of the tint outside the active region (ADR-0035): when it shows and where its hole is. Platform-free;
/// the Mac only draws it.
/// </summary>
public static class RegionTintRule
{
    /// <summary>
    /// True when the tint shows: a session is active, input isn't paused, the setting is on, the overlay is closed,
    /// the target is one display whose region isn't the full display, and the stylus is in Pen mode (in Mouse mode
    /// the region doesn't limit the relative cursor).
    /// </summary>
    public static bool IsVisible(in RegionTintState state) =>
        state.Connected
        && !state.Paused
        && state.Enabled
        && !state.OverlayOpen
        && state.SingleDisplay
        && state.Mode == StylusMode.Pen
        && !MacSettings.IsFullRegion(state.Region);

    /// <summary>
    /// The region as a rectangle of a view of <paramref name="width"/> × <paramref name="height"/> points covering the
    /// display: scaled, and flipped from the region's top-left origin (y down) to the view's bottom-left origin (y up).
    /// </summary>
    public static ViewRect HoleInView(NormalizedRect region, double width, double height)
    {
        double h = region.Height * height;
        return new ViewRect(region.X * width, height - (region.Y * height) - h, region.Width * width, h);
    }
}
