namespace PenBridge.Core.Settings;

#pragma warning disable CA1028 // Wire enum: storage matches the u16 key field.

/// <summary>
/// <c>u16</c> keys of the Mac settings carried by SettingsSnapshot / SettingsUpdate (ADR-0030). Values are stable:
/// append new keys, never renumber. Types, ranges and defaults: <see cref="MacSettings"/>.
/// </summary>
public enum MacSettingKey : ushort
{
    /// <summary>String: <c>""</c> main display, <c>"all"</c> all displays, else a display id from the DisplayList (ADR-0031).</summary>
    TargetDisplay = 1,

    /// <summary>Rect: the region of the current target display (ignored for all displays). Default 0,0,1,1.</summary>
    Region = 2,

    /// <summary>Bool: the dragged region keeps the phone surface's aspect ratio. Default false.</summary>
    RegionAspectLock = 3,

    /// <summary>Int: 0 Pen, 1 Mouse (ADR-0032). Default 0.</summary>
    StylusMode = 10,

    /// <summary>Float 0.25..4: Mouse-mode speed. Default 1.</summary>
    MouseSpeed = 11,

    /// <summary>Bool: Mouse-mode acceleration. Default true.</summary>
    MouseAcceleration = 12,

    /// <summary>Int: 0 RightClick, 1 MiddleClick, 2 Eraser, 3 DoubleClick, 4 None. Default 0.</summary>
    SideButton = 13,

    /// <summary>Float −1..1: pressure curve, soft to firm (γ = 2^tipFeel). Default 0.</summary>
    TipFeel = 14,

    /// <summary>Float 0..0.5: below it the pen hovers. Default 0.</summary>
    ClickThreshold = 15,

    /// <summary>Float 0.5..1: pressure that maps to full. Default 1. Must exceed <see cref="ClickThreshold"/> + 0.1.</summary>
    FullPressureAt = 16,

    /// <summary>Int: 0 FollowSystem, 1 On, 2 Off (ADR-0033). Default 0.</summary>
    SmartZoom = 20,

    /// <summary>Float 0.25..4: one-finger cursor and three-finger drag speed. Default 1.</summary>
    PointerSpeed = 21,

    /// <summary>Float 0.25..4: two-finger scroll speed. Default 1.</summary>
    ScrollSpeed = 22,

    /// <summary>Bool: pinch zoom. Default true.</summary>
    Zoom = 23,

    /// <summary>Bool: two-finger rotate. Default true.</summary>
    Rotate = 24,

    /// <summary>Bool: three-finger drag. Default true.</summary>
    ThreeFingerDrag = 25,

    /// <summary>Bool: four-finger swipes and pinches. Default true.</summary>
    FourFingerGestures = 26,
}

#pragma warning restore CA1028
