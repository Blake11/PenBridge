namespace PenBridge.Core.Net;

/// <summary>The connected phone's drawing surface in pixels, as its SurfaceInfo declared it.</summary>
/// <param name="WidthPx">Surface width in pixels.</param>
/// <param name="HeightPx">Surface height in pixels.</param>
public readonly record struct PhoneSurfaceSize(int WidthPx, int HeightPx)
{
    /// <summary>Width divided by height; 0 when the height is not positive.</summary>
    public double AspectRatio => HeightPx > 0 ? (double)WidthPx / HeightPx : 0d;
}
