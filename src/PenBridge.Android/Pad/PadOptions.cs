using PenBridge.Core.Input;

namespace PenBridge.Android.Pad;

/// <summary>Initial pad settings (P3-C loads them from <c>SettingsStore</c>).</summary>
public sealed record PadOptions
{
    public PadOrientationMode Orientation { get; init; } = PadOrientationMode.Landscape;

    /// <summary>When true and no stylus is present, a single finger draws like a pen with <see cref="FingerFallbackPressure"/>.</summary>
    public bool FingerFallbackEnabled { get; init; } = true;

    /// <summary>Synthetic pressure (0..65535) for finger drawing.</summary>
    public ushort FingerFallbackPressure { get; init; } = new InputFilterOptions().FingerFallbackPressure;

    /// <summary>Show the faint fading local trail of the last ~1 s of pen positions.</summary>
    public bool InkEcho { get; init; } = true;
}
