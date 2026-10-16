using Android.Graphics;

namespace PenBridge.Android.Pad;

/// <summary>Pad background choice (ADR-0031, phone-only setting).</summary>
public enum SurfaceBackground
{
    Black = 0,
    DarkGrey = 1,
    Light = 2,
}

/// <summary>
/// How the pad surface looks (ADR-0031 "Phone surface"): background, marker opacity, and whether the centre cross is
/// shown. The corner ticks and the dimmed margins are always drawn; their position comes from the active area.
/// </summary>
public sealed record SurfaceLook
{
    /// <summary>Smallest selectable marker opacity, so the corner ticks never disappear entirely.</summary>
    public const float MinMarkerOpacity = 0.1f;

    public const float DefaultMarkerOpacity = 0.5f;

    public SurfaceBackground Background { get; init; } = SurfaceBackground.DarkGrey;

    /// <summary>Opacity of the corner ticks and centre cross, <see cref="MinMarkerOpacity"/>..1.</summary>
    public float MarkerOpacity { get; init; } = DefaultMarkerOpacity;

    /// <summary>Draw a small cross at the active area's centre.</summary>
    public bool CentreMarker { get; init; } = true;

    /// <summary>True for the light background: markers, ink echo and status dot switch to dark/stronger colours.</summary>
    public bool IsLight => Background == SurfaceBackground.Light;

    /// <summary>Colour of the active area.</summary>
    internal Color ActiveColor => Background switch
    {
        SurfaceBackground.Black => Color.Rgb(0, 0, 0),
        SurfaceBackground.Light => Color.Rgb(236, 236, 240),
        _ => Color.Rgb(44, 46, 50),
    };

    /// <summary>
    /// Colour of the margins outside the active area: dimmer than the active area. Black can't get darker, so its
    /// margins are a faint grey instead; either way the boundary reads as "outside".
    /// </summary>
    internal Color MarginColor => Background switch
    {
        SurfaceBackground.Black => Color.Rgb(22, 22, 25),
        SurfaceBackground.Light => Color.Rgb(178, 178, 186),
        _ => Color.Rgb(20, 21, 23),
    };

    /// <summary>Opaque marker colour; the opacity is applied as alpha.</summary>
    internal Color MarkerColor => IsLight ? Color.Rgb(20, 20, 24) : Color.White;

    /// <summary>Ink echo colour: readable on the background.</summary>
    internal Color InkColor => IsLight ? Color.Rgb(20, 20, 24) : Color.White;
}
