using CoreGraphics;
using CoreImage;
using CoreVideo;
using Foundation;
using ObjCRuntime;

namespace PenBridge.Mac.Mirror;

/// <summary>
/// The All displays image (ADR-0038 "Content"): the latest frame of each display's stream placed at its spot in the
/// bounding box, gaps black, rendered on the GPU into an NV12 buffer for the encoder.
/// </summary>
/// <remarks>
/// <para><b>Why Core Image, not a Metal shader:</b> a <c>CIContext</c> renders on the GPU (Metal) and does the
/// NV12 → RGB → NV12 conversion with the buffers' colour tags, in a few calls. A Metal compute kernel would need a
/// shader, a texture cache for both planes and its own YCbCr maths for the same result. All displays is the uncommon
/// target, and its cost is one GPU pass per output frame; the single-display path doesn't use this at all (capture
/// buffers go to the encoder untouched).</para>
/// <para>Each composite creates a few short-lived Core Image wrappers (images, the output buffer); only this path does.
/// Capture queue only (not thread-safe).</para>
/// </remarks>
internal sealed class DisplayCompositor : IDisposable
{
    private readonly CIContext _context;
    private readonly CVPixelBufferPool _pool;
    private readonly CGRect[] _slots;
    private readonly IntPtr[] _latest;
    private readonly CIImage _background;
    private bool _disposed;

    /// <param name="width">Output width in pixels.</param>
    /// <param name="height">Output height in pixels.</param>
    /// <param name="slots">Per display: its rectangle in the output, in pixels, top-left origin.</param>
    public DisplayCompositor(int width, int height, ReadOnlySpan<CGRect> slots)
    {
        Width = width;
        Height = height;
        _slots = new CGRect[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            // Core Image uses a bottom-left origin.
            CGRect s = slots[i];
            _slots[i] = new CGRect(s.X, height - s.Y - s.Height, s.Width, s.Height);
        }

        _latest = new IntPtr[slots.Length];
        _context = CIContext.Create();
        using var surface = new NSDictionary();
        var attributes = new CVPixelBufferAttributes
        {
            PixelFormatType = CVPixelFormatType.CV420YpCbCr8BiPlanarVideoRange,
            Width = width,
            Height = height,
            MetalCompatibility = true,
        };
        attributes.Dictionary[CVPixelBuffer.IOSurfacePropertiesKey] = surface;
        _pool = new CVPixelBufferPool(new CVPixelBufferPoolSettings { MinimumBufferCount = 3 }, attributes);
        _background = CIImage.BlackImage.ImageByCroppingToRect(new CGRect(0, 0, width, height));
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>True once every display delivered a frame (until then the composite would show a black display).</summary>
    public bool HasAll
    {
        get
        {
            foreach (IntPtr p in _latest)
            {
                if (p == IntPtr.Zero)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Keeps <paramref name="pixelBuffer"/> (retained) as display <paramref name="index"/>'s latest frame.</summary>
    public void Update(int index, IntPtr pixelBuffer)
    {
        if ((uint)index >= (uint)_latest.Length || pixelBuffer == IntPtr.Zero)
        {
            return;
        }

        MirrorNative.CFRetain(pixelBuffer);
        IntPtr old = _latest[index];
        _latest[index] = pixelBuffer;
        if (old != IntPtr.Zero)
        {
            MirrorNative.CFRelease(old);
        }
    }

    /// <summary>Renders the composite; returns a +1 retained CVPixelBufferRef (release it), or 0 on failure.</summary>
    public IntPtr Compose()
    {
        CIImage image = _background;
        var owned = new List<IDisposable>(_latest.Length * 2);
        try
        {
            IntPtr colourSource = IntPtr.Zero;
            for (int i = 0; i < _latest.Length; i++)
            {
                if (_latest[i] == IntPtr.Zero)
                {
                    continue;
                }

                colourSource = colourSource == IntPtr.Zero ? _latest[i] : colourSource;
                CVPixelBuffer buffer = Runtime.GetINativeObject<CVPixelBuffer>(_latest[i], false)!;
                owned.Add(buffer);
                CIImage frame = CIImage.FromImageBuffer(buffer);
                owned.Add(frame);
                CGRect slot = _slots[i];
                CGRect extent = frame.Extent;
                var place = CGAffineTransform.MakeScale(slot.Width / extent.Width, slot.Height / extent.Height);
                place.Multiply(CGAffineTransform.MakeTranslation(slot.X, slot.Y));
                CIImage placed = frame.ImageByApplyingTransform(place);
                owned.Add(placed);
                image = placed.CreateByCompositingOverImage(image);
                owned.Add(image);
            }

            CVPixelBuffer? output = _pool.CreatePixelBuffer();
            if (output is null)
            {
                return IntPtr.Zero;
            }

            if (colourSource != IntPtr.Zero)
            {
                MirrorNative.CVBufferPropagateAttachments(colourSource, output.Handle);
            }

            _context.Render(image, output);
            IntPtr handle = output.Handle;
            MirrorNative.CFRetain(handle);
            output.Dispose();
            return handle;
        }
        finally
        {
            foreach (IDisposable d in owned)
            {
                d.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = 0; i < _latest.Length; i++)
        {
            if (_latest[i] != IntPtr.Zero)
            {
                MirrorNative.CFRelease(_latest[i]);
                _latest[i] = IntPtr.Zero;
            }
        }

        _background.Dispose();
        _pool.Dispose();
        _context.Dispose();
    }
}
