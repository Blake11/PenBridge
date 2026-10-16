namespace PenBridge.Core.Input;

/// <summary>How the stylus moves the cursor (ADR-0032). Values match the <c>StylusMode</c> setting key.</summary>
public enum StylusMode
{
    /// <summary>Absolute: the pen maps to a fixed point of the target through the active area (ADR-0031).</summary>
    Pen = 0,

    /// <summary>Relative: the pen moves the cursor from where it is by its movement, like a mouse.</summary>
    Mouse = 1,
}

/// <summary>What the pen's side button does (ADR-0032). Values match the <c>SideButton</c> setting key.</summary>
public enum SideButtonAction
{
    /// <summary>Hover press: right click. Held at contact-down: a right-button drag.</summary>
    RightClick = 0,

    /// <summary>Hover press: middle click. Held at contact-down: a middle-button drag.</summary>
    MiddleClick = 1,

    /// <summary>While held the pen is an eraser (proximity switch); held at contact-down: an eraser stroke.</summary>
    Eraser = 2,

    /// <summary>Hover press: a left double click (click states 1, 2). Held at contact-down: a normal left stroke.</summary>
    DoubleClick = 3,

    /// <summary>Nothing. Held at contact-down: a normal left stroke.</summary>
    None = 4,
}

/// <summary>Whether a two-finger double tap smart-zooms (ADR-0033). Values match the <c>SmartZoom</c> setting key.</summary>
public enum SmartZoomMode
{
    /// <summary>Follow the macOS trackpad setting (<see cref="GestureRecognizerOptions.SmartZoomEnabled"/>, ADR-0029).</summary>
    FollowSystem = 0,

    /// <summary>Always on: two-finger taps are held for the double-tap window.</summary>
    On = 1,

    /// <summary>Always off: a two-finger tap right-clicks on lift.</summary>
    Off = 2,
}

/// <summary>
/// The Mac-side input settings applied before injection (ADR-0031..0033): stylus mode, side button, pressure curve and
/// gesture settings. The defaults reproduce the behaviour without settings. Ranges match the <c>MacSettingKey</c>
/// table; <see cref="Validate"/> enforces them. Immutable; apply with <c>SessionInput.ApplySettings</c> or
/// <see cref="PenStateTracker.ApplySettings"/>.
/// </summary>
public sealed record InputSettings
{
    /// <summary>Smallest <see cref="MouseSpeed"/>, <see cref="PointerSpeed"/> and <see cref="ScrollSpeed"/>.</summary>
    public const double MinSpeed = 0.25;

    /// <summary>Largest <see cref="MouseSpeed"/>, <see cref="PointerSpeed"/> and <see cref="ScrollSpeed"/>.</summary>
    public const double MaxSpeed = 4;

    /// <summary>Smallest gap between <see cref="ClickThreshold"/> and <see cref="FullPressureAt"/>.</summary>
    public const double MinPressureSpan = 0.1;

    /// <summary>The defaults (today's behaviour).</summary>
    public static InputSettings Default { get; } = new();

    /// <summary>Pen (absolute, default) or Mouse (relative).</summary>
    public StylusMode StylusMode { get; init; } = StylusMode.Pen;

    /// <summary>Mouse mode: cursor speed multiplier, 0.25..4 (separate from <see cref="PointerSpeed"/>).</summary>
    public double MouseSpeed { get; init; } = 1;

    /// <summary>Mouse mode: apply the acceleration curve (true) or move linearly (false).</summary>
    public bool MouseAcceleration { get; init; } = true;

    /// <summary>The side button's action.</summary>
    public SideButtonAction SideButton { get; init; } = SideButtonAction.RightClick;

    /// <summary>Pressure curve: −1 soft … 0 linear … +1 firm (γ = 2^TipFeel).</summary>
    public double TipFeel { get; init; }

    /// <summary>Pressure below this (0..0.5) hovers instead of touching; 0 lets the phone's contact decide.</summary>
    public double ClickThreshold { get; init; }

    /// <summary>Pressure at or above this (0.5..1) is full pressure.</summary>
    public double FullPressureAt { get; init; } = 1;

    /// <summary>Smart Zoom override.</summary>
    public SmartZoomMode SmartZoom { get; init; } = SmartZoomMode.FollowSystem;

    /// <summary>Scales the one-finger cursor and three-finger drag, 0.25..4.</summary>
    public double PointerSpeed { get; init; } = 1;

    /// <summary>Scales two-finger scrolling (and so its momentum), 0.25..4.</summary>
    public double ScrollSpeed { get; init; } = 1;

    /// <summary>Pinch zoom (the scale part of the two-finger transform).</summary>
    public bool ZoomEnabled { get; init; } = true;

    /// <summary>Two-finger rotate (the rotation part of the two-finger transform).</summary>
    public bool RotateEnabled { get; init; } = true;

    /// <summary>Three-finger drag.</summary>
    public bool ThreeFingerDragEnabled { get; init; } = true;

    /// <summary>Four-finger swipe and pinch (system gestures).</summary>
    public bool FourFingerGesturesEnabled { get; init; } = true;

    /// <summary>The pressure curve these settings describe.</summary>
    public PressureCurve PressureCurve => new(TipFeel, ClickThreshold, FullPressureAt);

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> when a value is outside its range (or not finite), an enum value
    /// is unknown, or <see cref="FullPressureAt"/> is not above <see cref="ClickThreshold"/> by at least
    /// <see cref="MinPressureSpan"/>.
    /// </summary>
    public void Validate()
    {
        RequireDefined(StylusMode is StylusMode.Pen or StylusMode.Mouse, nameof(StylusMode));
        RequireDefined(SideButton is >= SideButtonAction.RightClick and <= SideButtonAction.None, nameof(SideButton));
        RequireDefined(SmartZoom is >= SmartZoomMode.FollowSystem and <= SmartZoomMode.Off, nameof(SmartZoom));
        RequireRange(MouseSpeed, MinSpeed, MaxSpeed, nameof(MouseSpeed));
        RequireRange(PointerSpeed, MinSpeed, MaxSpeed, nameof(PointerSpeed));
        RequireRange(ScrollSpeed, MinSpeed, MaxSpeed, nameof(ScrollSpeed));
        RequireRange(TipFeel, -1, 1, nameof(TipFeel));
        RequireRange(ClickThreshold, 0, 0.5, nameof(ClickThreshold));
        RequireRange(FullPressureAt, 0.5, 1, nameof(FullPressureAt));
        if (!(FullPressureAt - ClickThreshold >= MinPressureSpan - 1e-9))
        {
            throw new ArgumentOutOfRangeException(nameof(FullPressureAt), "FullPressureAt must exceed ClickThreshold by at least 0.1.");
        }
    }

    private static void RequireRange(double value, double min, double max, string name)
    {
        if (!(value >= min && value <= max))
        {
            throw new ArgumentOutOfRangeException(name, $"Must be in {min}..{max}.");
        }
    }

    private static void RequireDefined(bool defined, string name)
    {
        if (!defined)
        {
            throw new ArgumentOutOfRangeException(name, "Unknown value.");
        }
    }
}
