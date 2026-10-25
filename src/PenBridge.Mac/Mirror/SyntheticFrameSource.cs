#if DEBUG
using CoreVideo;
using Foundation;

namespace PenBridge.Mac.Mirror;

/// <summary>
/// Debug only: NV12 frames of a moving test pattern at a fixed rate, standing in for ScreenCaptureKit so the encoder
/// path can be checked without the Screen Recording permission (the dump hook's <c>synthetic</c> target).
/// </summary>
/// <remarks>
/// A dedicated user-initiated thread fills IOSurface-backed pool buffers (vectorised row fills, about a millisecond for
/// 2.5 MP) and hands each to the callback. <see cref="Paused"/> stops producing frames, like a static screen.
/// </remarks>
internal sealed class SyntheticFrameSource : IDisposable
{
    private const int TextureRows = 1024;
    private const int TextureSlack = 512;
    private const int TextureStride = 4096 + TextureSlack;

    private static readonly byte[] Texture = MakeTexture();

    private readonly Action<IntPtr, long> _onFrame;
    private readonly Lock _lock = new();
    private CVPixelBufferPool? _pool;
    private int _width;
    private int _height;
    private int _fps;
    private Thread? _thread;
    private volatile bool _stop;
    private long _frame;

    /// <param name="onFrame">Called on the source's thread with a CVPixelBufferRef (valid during the call) and its time in µs.</param>
    public SyntheticFrameSource(int width, int height, int fps, Action<IntPtr, long> onFrame)
    {
        _onFrame = onFrame;
        Configure(width, height, fps);
    }

    /// <summary>No frames while true (a static screen).</summary>
    public bool Paused { get; set; }

    /// <summary>Frames produced so far.</summary>
    public long Frames => Interlocked.Read(ref _frame);

    /// <summary>Changes the frame size or rate; takes effect with the next frame.</summary>
    public void Configure(int width, int height, int fps)
    {
        lock (_lock)
        {
            if (width != _width || height != _height)
            {
                _pool?.Dispose();
                using var surface = new NSDictionary();
                var attributes = new CVPixelBufferAttributes
                {
                    PixelFormatType = CVPixelFormatType.CV420YpCbCr8BiPlanarVideoRange,
                    Width = width,
                    Height = height,
                };
                attributes.Dictionary[CVPixelBuffer.IOSurfacePropertiesKey] = surface;
                _pool = new CVPixelBufferPool(new CVPixelBufferPoolSettings { MinimumBufferCount = 4 }, attributes);
                _width = width;
                _height = height;
            }

            _fps = fps;
        }
    }

    public void Start()
    {
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "PenBridge synthetic frames", Priority = ThreadPriority.Normal };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join();
        _thread = null;
        _pool?.Dispose();
        _pool = null;
    }

    private static byte[] MakeTexture()
    {
        var texture = new byte[TextureRows * TextureStride];
        new Random(1).NextBytes(texture);
        for (int i = 0; i < texture.Length; i++)
        {
            texture[i] = (byte)(16 + (texture[i] % 220));
        }

        return texture;
    }

    private void Run()
    {
        long next = MirrorClock.NowUs();
        while (!_stop)
        {
            int fps;
            lock (_lock)
            {
                fps = _fps;
            }

            long now = MirrorClock.NowUs();
            if (now < next)
            {
                Thread.Sleep(TimeSpan.FromMicroseconds(next - now));
                continue;
            }

            next += 1_000_000 / fps;
            if (next < now)
            {
                next = now + (1_000_000 / fps); // fell behind: don't burst
            }

            if (!Paused)
            {
                Produce();
            }
        }
    }

    private unsafe void Produce()
    {
        CVPixelBuffer? buffer;
        lock (_lock)
        {
            buffer = _pool?.CreatePixelBuffer();
        }

        if (buffer is null)
        {
            return;
        }

        using (buffer)
        {
            long frame = Interlocked.Increment(ref _frame);
            if (buffer.Lock(CVPixelBufferLock.None) != CVReturn.Success)
            {
                return;
            }

            try
            {
                int width = (int)buffer.Width;
                int height = (int)buffer.Height;
                byte* y = (byte*)buffer.GetBaseAddress(0);
                int yStride = (int)buffer.GetBytesPerRowOfPlane(0);
                for (int row = 0; row < height; row++)
                {
                    // Bands scrolling down plus a moving bright bar: real motion for the encoder.
                    var line = new Span<byte>(y + ((long)row * yStride), width);
                    line.Fill((byte)(16 + (((row + (frame * 6)) >> 3) & 0x7F)));
                    int bar = (int)((frame * 8) % Math.Max(1, width - 64));
                    line.Slice(bar, Math.Min(64, width - bar)).Fill(235);
                }

                // A band of fine random texture scrolling sideways (a quarter of the height): detail like a photo or
                // canvas being panned, so throughput and bitrate figures aren't flattered by the flat bands, while the
                // motion stays predictable like real screen content.
                int band = Math.Min(height / 4, TextureRows);
                int shift = (int)((frame * 4) % TextureSlack);
                for (int row = 0; row < band; row++)
                {
                    Texture.AsSpan((row * TextureStride) + shift, Math.Min(width, TextureStride - TextureSlack))
                        .CopyTo(new Span<byte>(y + ((long)(row + (height / 2)) * yStride), width));
                }

                byte* uv = (byte*)buffer.GetBaseAddress(1);
                int uvStride = (int)buffer.GetBytesPerRowOfPlane(1);
                for (int row = 0; row < height / 2; row++)
                {
                    new Span<byte>(uv + ((long)row * uvStride), width).Fill((byte)(96 + ((row + frame) & 0x3F)));
                }
            }
            finally
            {
                buffer.Unlock(CVPixelBufferLock.None);
            }

            _onFrame(buffer.Handle, MirrorClock.NowUs()); // stamped once the frame is "captured" (filled)
        }
    }
}
#endif
