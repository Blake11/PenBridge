namespace PenBridge.Android.Pad;

/// <summary>Pad orientation setting (ADR-0019): locked landscape by default.</summary>
public enum PadOrientationMode : byte
{
    Landscape = 0,
    Portrait = 1,
    /// <summary>Follows the sensor, but never rotates during a stroke (locked while pen contact or touches are active).</summary>
    Auto = 2,
}
