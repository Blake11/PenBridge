using CoreGraphics;
using CoreImage;
using Foundation;

namespace PenBridge.Mac.Pairing;

/// <summary>Renders QR text to a crisp bitmap with <c>CIQRCodeGenerator</c> (error correction M).</summary>
internal static class QrCodeRenderer
{
    /// <summary>
    /// Renders <paramref name="text"/> black on white (with the generator's quiet zone) at about
    /// <paramref name="targetPoints"/> points on a 2x display: every module is an even number of pixels, so it stays
    /// pixel-exact at 2x and at an exact 2:1 nearest-neighbour downscale on 1x displays. Null if the text doesn't fit.
    /// </summary>
    /// <param name="points">The point size to display the (square) image at.</param>
    /// <remarks>Never log <paramref name="text"/>: it carries the one-time pairing token.</remarks>
    public static CGImage? Render(string text, double targetPoints, out double points)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        points = 0;

        using NSData message = NSData.FromString(text, NSStringEncoding.UTF8);
        using var filter = new CIQRCodeGenerator();
        filter.Message = message;
        filter.CorrectionLevel = "M";
        using CIImage? modules = filter.OutputImage;
        if (modules is null || modules.Extent.Width < 1)
        {
            return null;
        }

        int count = (int)modules.Extent.Width;
        int modulePixels = Math.Max(2, 2 * (int)Math.Round(targetPoints / count));

        // Nearest sampling and an integer scale keep module edges on pixel boundaries (no smoothing).
        using CIImage nearest = modules.CreateBySamplingNearest();
        using CIImage scaled = nearest.ImageByApplyingTransform(CGAffineTransform.MakeScale(modulePixels, modulePixels));
        using var context = new CIContext(new CIContextOptions { CacheIntermediates = false });
        CGImage? image = context.CreateCGImage(scaled, new CGRect(0, 0, count * modulePixels, count * modulePixels));
        if (image is not null)
        {
            points = count * modulePixels / 2.0;
        }

        return image;
    }
}
