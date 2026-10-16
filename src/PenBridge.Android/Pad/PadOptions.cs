using PenBridge.Core.Input;

namespace PenBridge.Android.Pad;

/// <summary>Initial pad settings (P3-C loads them from <c>SettingsStore</c>).</summary>
public sealed record PadOptions
{
    public PadOrientationMode Orientation { get; init; } = PadOrientationMode.Landscape;

    /// <summary>
    /// The pad's finger fallback default. There is no user setting for it, so the initial SurfaceInfo (sent before the
    /// pad's first layout) uses this too; the pad's own SurfaceInfo then carries <see cref="FingerFallbackEnabled"/>.
    /// </summary>
    public const bool DefaultFingerFallbackEnabled = true;

    /// <summary>
    /// When true and no stylus is present, a single finger acts per <see cref="FingerMode"/>. Reported to the Mac as
    /// <c>SurfaceCapabilities.FingerFallback</c> in every SurfaceInfo; when false a lone finger does nothing.
    /// </summary>
    public bool FingerFallbackEnabled { get; init; } = DefaultFingerFallbackEnabled;

    /// <summary>What one finger does: move the cursor with tap to click (default) or draw (<see cref="InputFilterOptions.FingerMode"/>).</summary>
    public FingerFallbackMode FingerMode { get; init; } = new InputFilterOptions().FingerMode;

    /// <summary>Synthetic pressure (0..65535) for finger drawing.</summary>
    public ushort FingerFallbackPressure { get; init; } = new InputFilterOptions().FingerFallbackPressure;

    /// <summary>
    /// How long a lone finger is held before it starts a finger-fallback stroke; a second finger inside it makes a
    /// gesture (<see cref="InputFilterOptions.FingerFallbackArmDelay"/>).
    /// </summary>
    public TimeSpan FingerFallbackArmDelay { get; init; } = new InputFilterOptions().FingerFallbackArmDelay;

    /// <summary>
    /// Normalized contact size (0..1) at or above which a touch is a palm; above 1 disables size-based palm rejection
    /// (<see cref="InputFilterOptions.PalmSizeThreshold"/>).
    /// </summary>
    public float PalmSizeThreshold { get; init; } = new InputFilterOptions().PalmSizeThreshold;

    /// <summary>Debug: show each finger's live normalized contact size (to tune <see cref="PalmSizeThreshold"/>).</summary>
    public bool ShowTouchSize { get; init; }

    /// <summary>Background, marker opacity and centre marker (ADR-0031).</summary>
    public SurfaceLook Look { get; init; } = new();

    /// <summary>Show the faint fading local trail of the last ~1 s of pen positions.</summary>
    public bool InkEcho { get; init; } = true;
}
