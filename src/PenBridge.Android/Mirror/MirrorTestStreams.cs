#if DEBUG
using System.Globalization;
using System.Text.RegularExpressions;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Util;
using Android.Views;
using PenBridge.Core.Protocol;

namespace PenBridge.Android.Mirror;

/// <summary>
/// Debug only: test streams for the mirror path. Generates an Annex-B file with a known geometry pattern on the device
/// (MediaCodec encoder, real time, no B-frames, a keyframe every 2 s with its parameter sets in-band, as the spec's
/// video frames), and finds pushed or generated files in the app's external files dir
/// (<c>/sdcard/Android/data/&lt;app id&gt;/files/</c>). File name: <c>mirror-test-&lt;W&gt;x&lt;H&gt;-&lt;fps&gt;.(h264|hevc)</c>.
/// </summary>
/// <remarks>
/// The pattern, for alignment checks on a screenshot: a 4 px white border on the frame edge, 24 px corner squares
/// (top-left red, top-right green, bottom-right blue, bottom-left yellow), a grid at every tenth, a magenta centre
/// cross, and a white bar moving 8 px per frame with the frame number.
/// </remarks>
internal static partial class MirrorTestStreams
{
    private const string Tag = "PenBridge";

    public static string Directory(Context context) =>
        (context.GetExternalFilesDir(null) ?? context.FilesDir)!.AbsolutePath;

    public static string FileName(MirrorCodec codec, int width, int height, int fps) =>
        string.Create(CultureInfo.InvariantCulture, $"mirror-test-{width}x{height}-{fps}.{Extension(codec)}");

    public static string Extension(MirrorCodec codec) => codec == MirrorCodec.Hevc ? "hevc" : "h264";

    /// <summary>The newest test file for <paramref name="codec"/> (preferring <paramref name="width"/>×<paramref name="height"/>), or null.</summary>
    public static string? Find(Context context, MirrorCodec codec, int width, int height, int fps)
    {
        string dir = Directory(context);
        string exact = System.IO.Path.Combine(dir, FileName(codec, width, height, fps));
        if (File.Exists(exact))
        {
            return exact;
        }

        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string f in System.IO.Directory.EnumerateFiles(dir, "*." + Extension(codec)))
        {
            DateTime t = File.GetLastWriteTimeUtc(f);
            if (t > bestTime)
            {
                best = f;
                bestTime = t;
            }
        }

        return best;
    }

    /// <summary>Width, height and fps from a test file name (fps defaults to 60).</summary>
    public static bool TryParse(string path, out int width, out int height, out int fps)
    {
        Match m = NamePattern().Match(System.IO.Path.GetFileName(path));
        width = height = 0;
        fps = 60;
        if (!m.Success)
        {
            return false;
        }

        width = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        height = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (m.Groups[3].Success)
        {
            fps = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        return width >= 2 && height >= 2 && fps > 0;
    }

    /// <summary>Encodes <paramref name="seconds"/> of the pattern in real time. Blocking: call off the UI thread.</summary>
    public static string Generate(Context context, MirrorCodec codec, int width, int height, int fps, int seconds)
    {
        string mime = codec.Mime();
        using var list = new MediaCodecList(MediaCodecListKind.RegularCodecs);
        MediaFormat? format = null;
        string? name = null;

        // The emulator's software encoders stop below a tablet's active area: scale down (keeping the aspect) until one
        // takes it. The decoder then scales the video up to the active area.
        for (double scale = 1; scale > 0.2 && name is null; scale *= 0.9)
        {
            format?.Dispose();
            int w = (int)(width * scale) & ~1;
            int h = (int)(height * scale) & ~1;
            format = MediaFormat.CreateVideoFormat(mime, w, h);
            name = list.FindEncoderForFormat(format); // size only: the lookup rejects formats with more keys
        }

        if (name is null || format is null)
        {
            format?.Dispose();
            throw new InvalidOperationException("No " + codec.Name() + " encoder for " + width + "x" + height);
        }

        format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
        format.SetInteger(MediaFormat.KeyBitRate, 16_000_000);
        format.SetInteger(MediaFormat.KeyFrameRate, fps);
        format.SetInteger(MediaFormat.KeyIFrameInterval, 2);
        format.SetInteger(MediaFormat.KeyMaxBFrames, 0);
        width = format.GetInteger(MediaFormat.KeyWidth);
        height = format.GetInteger(MediaFormat.KeyHeight);
        string path = System.IO.Path.Combine(Directory(context), FileName(codec, width, height, fps));
        using MediaCodec encoder = MediaCodec.CreateByCodecName(name);
        encoder.Configure(format, null, null, MediaCodecConfigFlags.Encode);
        using Surface input = encoder.CreateInputSurface();
        encoder.Start();
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var info = new MediaCodec.BufferInfo();
        using var painter = new Painter(width, height);
        byte[] config = [];
        long frameTicks = TimeSpan.TicksPerSecond / fps;
        long start = DateTime.UtcNow.Ticks;
        int frames = fps * seconds;
        int written = 0;
        for (int i = 0; i < frames; i++)
        {
            Canvas canvas = input.LockHardwareCanvas()!;
            painter.Draw(canvas, i);
            input.UnlockCanvasAndPost(canvas);
            written += Drain(encoder, info, output, ref config, endOfStream: false);
            long wait = start + ((i + 1) * frameTicks) - DateTime.UtcNow.Ticks;
            if (wait > 0)
            {
                Thread.Sleep(TimeSpan.FromTicks(wait));
            }
        }

        encoder.SignalEndOfInputStream();
        written += Drain(encoder, info, output, ref config, endOfStream: true);
        encoder.Stop();
        encoder.Release();
        format.Dispose();
        Log.Info(Tag, $"Mirror test stream {path}: {written} frames, encoder {name}");
        return path;
    }

    private static int Drain(MediaCodec encoder, MediaCodec.BufferInfo info, FileStream output, ref byte[] config, bool endOfStream)
    {
        int frames = 0;
        while (true)
        {
            int index = encoder.DequeueOutputBuffer(info, endOfStream ? 100_000 : 0);
            if (index == (int)MediaCodecInfoState.TryAgainLater)
            {
                return frames; // nothing more now (at end of stream: it never came within the timeout)
            }

            if (index < 0)
            {
                continue; // output format or buffers changed
            }

            using (Java.Nio.ByteBuffer buffer = encoder.GetOutputBuffer(index)!)
            {
                byte[] bytes = new byte[info.Size];
                buffer.Position(info.Offset);
                buffer.Get(bytes, 0, info.Size);
                if ((info.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
                {
                    config = bytes;
                }
                else if (bytes.Length > 0)
                {
                    if ((info.Flags & MediaCodecBufferFlags.KeyFrame) != 0)
                    {
                        output.Write(config); // parameter sets in-band with every keyframe, as the Mac sends them
                    }

                    output.Write(bytes);
                    frames++;
                }
            }

            bool eos = (info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
            encoder.ReleaseOutputBuffer(index, false);
            if (eos)
            {
                return frames;
            }
        }
    }

    [GeneratedRegex(@"(\d+)x(\d+)(?:-(\d+))?\.(?:h264|hevc)$")]
    private static partial Regex NamePattern();

    private sealed class Painter : IDisposable
    {
        private const int Border = 4;
        private const int Corner = 24;
        private readonly int _w;
        private readonly int _h;
        private readonly Paint _fill = new();
        private readonly Paint _text = new(PaintFlags.AntiAlias);

        public Painter(int w, int h)
        {
            _w = w;
            _h = h;
            _text.Color = Color.White;
            _text.TextSize = Math.Max(16, h / 20f);
        }

        public void Draw(Canvas c, int frame)
        {
            c.DrawColor(Color.Rgb(30, 40, 64));
            _fill.Color = Color.Rgb(90, 100, 130);
            for (int k = 1; k < 10; k++)
            {
                float x = _w * k / 10f;
                float y = _h * k / 10f;
                c.DrawRect(x - 1, 0, x + 1, _h, _fill);
                c.DrawRect(0, y - 1, _w, y + 1, _fill);
            }

            // Moving bar (motion) and the frame number.
            _fill.Color = Color.White;
            float bx = (frame * 8) % _w;
            c.DrawRect(bx, _h * 0.4f, bx + 8, _h * 0.6f, _fill);
            c.DrawText(frame.ToString(CultureInfo.InvariantCulture), _w * 0.12f, _h * 0.2f, _text);

            // Border on the frame edge.
            c.DrawRect(0, 0, _w, Border, _fill);
            c.DrawRect(0, _h - Border, _w, _h, _fill);
            c.DrawRect(0, 0, Border, _h, _fill);
            c.DrawRect(_w - Border, 0, _w, _h, _fill);

            // Corners: TL red, TR green, BR blue, BL yellow.
            Square(c, 0, 0, Color.Rgb(255, 0, 0));
            Square(c, _w - Corner, 0, Color.Rgb(0, 255, 0));
            Square(c, _w - Corner, _h - Corner, Color.Rgb(0, 0, 255));
            Square(c, 0, _h - Corner, Color.Rgb(255, 255, 0));

            _fill.Color = Color.Magenta;
            float cx = _w / 2f;
            float cy = _h / 2f;
            c.DrawRect(cx - 40, cy - 2, cx + 40, cy + 2, _fill);
            c.DrawRect(cx - 2, cy - 40, cx + 2, cy + 40, _fill);
        }

        public void Dispose()
        {
            _fill.Dispose();
            _text.Dispose();
        }

        private void Square(Canvas c, float x, float y, Color color)
        {
            _fill.Color = color;
            c.DrawRect(x, y, x + Corner, y + Corner, _fill);
        }
    }
}
#endif
