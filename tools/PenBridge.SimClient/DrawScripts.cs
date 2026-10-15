using PenBridge.Core.Input;

namespace PenBridge.SimClient;

/// <summary>One step of a script: the complete state to send, earlier batched pen samples, then a pause.</summary>
internal readonly record struct ScriptFrame(PenSample? Pen, TouchSample[] Touches, PenSample[] History, TimeSpan Delay);

/// <summary>Built-in draw scripts. Coordinates are normalized (0..65535); frames go out at about 120 Hz.</summary>
internal static class DrawScripts
{
    public const string PacketLoss = "packet-loss";

    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(8);

    public static readonly string[] Names =
        [
            "line", "circle", "pressure-ramp", "tilt-sweep", "hover", "button-click", "two-finger-scroll", "pinch", "rotate",
            "two-finger-tap", "smart-zoom", "three-finger-drag", "swipe-up", "swipe-down", "swipe-left", "swipe-right",
            "four-finger-pinch-in", "four-finger-pinch-out", "page-swipe-left", "page-swipe-right", "one-finger", PacketLoss,
        ];

    /// <summary>
    /// Long enough after a tap for the server's held-tap deadline (GestureRecognizerOptions.DoubleTapWindow 400 ms +
    /// TapDeadlineJitterAllowance 50 ms) to pass before the script ends, so the right-click is posted.
    /// </summary>
    private static readonly TimeSpan TapSettle = TimeSpan.FromMilliseconds(600);

    // The SimClient's surface (Program.Surface), for scripts laid out in phone pixels.
    private const double SurfaceWidth = 1440;
    private const double SurfaceHeight = 3088;

    private static readonly TouchSample[] NoTouches = [];
    private static readonly PenSample[] NoHistory = [];

    public static bool TryGet(string name, out IEnumerable<ScriptFrame> frames)
    {
        frames = name switch
        {
            "line" => Batched(LinePoints(10000, 32768, 55000, 32768, 120, p => 30000)),
            "circle" => Batched(CirclePoints()),
            "pressure-ramp" => Batched(LinePoints(8000, 32768, 57000, 32768, 160, t => (ushort)(t * 65535))),
            "tilt-sweep" => TiltSweep(),
            "hover" => HoverPath(),
            "button-click" => ButtonClick(),
            "two-finger-scroll" => TwoFingerScroll(),
            "pinch" => Pinch(),
            "rotate" => Rotate(),
            "two-finger-tap" => TwoFingerTap(),
            "smart-zoom" => SmartZoom(),
            "three-finger-drag" => ThreeFingerDrag(),
            "swipe-up" => FourFingerSwipe(0, -1),
            "swipe-down" => FourFingerSwipe(0, 1),
            "swipe-left" => FourFingerSwipe(-1, 0),
            "swipe-right" => FourFingerSwipe(1, 0),
            "page-swipe-left" => PageSwipe(-1),
            "page-swipe-right" => PageSwipe(1),
            "four-finger-pinch-in" => FourFingerPinch(300, 120),
            "four-finger-pinch-out" => FourFingerPinch(120, 300),
            "one-finger" => OneFinger(),
            PacketLoss => Batched(CirclePoints()),
            _ => [],
        };
        return Array.IndexOf(Names, name) >= 0;
    }

    private static PenSample Pen(double x, double y, ushort pressure, PenState state, short tiltX = 0, short tiltY = 0, ushort rotation = 0, PenButtons buttons = PenButtons.None) =>
        new(Clamp(x), Clamp(y), pressure, tiltX, tiltY, rotation, PenTool.Pen, state, buttons);

    private static ushort Clamp(double v) => (ushort)Math.Clamp(Math.Round(v), 0, 65535);

    private static ScriptFrame Frame(PenSample? pen) => new(pen, NoTouches, NoHistory, FrameInterval);

    private static IEnumerable<PenSample> LinePoints(double x0, double y0, double x1, double y1, int samples, Func<double, ushort> pressure)
    {
        for (int i = 0; i <= samples; i++)
        {
            double t = (double)i / samples;
            yield return Pen(x0 + ((x1 - x0) * t), y0 + ((y1 - y0) * t), pressure(t), PenState.InRange | PenState.Contact);
        }
    }

    private static IEnumerable<PenSample> CirclePoints()
    {
        const int Samples = 240;
        for (int i = 0; i <= Samples; i++)
        {
            double a = 2 * Math.PI * i / Samples;
            yield return Pen(32768 + (15000 * Math.Cos(a)), 32768 + (15000 * Math.Sin(a)), 28000, PenState.InRange | PenState.Contact);
        }
    }

    /// <summary>
    /// Hover in, then the contact samples (generated at ~240 Hz) sent two per frame: the newest as the current
    /// sample and the one before it as history, like Android's batched MotionEvents. Then lift and leave.
    /// </summary>
    private static IEnumerable<ScriptFrame> Batched(IEnumerable<PenSample> contact)
    {
        PenSample[] points = [.. contact];
        PenSample first = points[0] with { State = PenState.InRange, Pressure = 0 };
        for (int i = 0; i < 5; i++)
        {
            yield return Frame(first);
        }

        for (int i = 0; i < points.Length; i += 2)
        {
            PenSample[] history = i > 0 ? [points[i - 1]] : NoHistory;
            yield return new ScriptFrame(points[i], NoTouches, history, FrameInterval);
        }

        PenSample last = points[^1] with { State = PenState.InRange, Pressure = 0 };
        for (int i = 0; i < 5; i++)
        {
            yield return Frame(last);
        }

        yield return Frame(null);
    }

    private static IEnumerable<ScriptFrame> TiltSweep()
    {
        yield return Frame(Pen(32768, 32768, 0, PenState.InRange));
        const int Samples = 120;
        for (int i = 0; i <= Samples; i++)
        {
            double t = (double)i / Samples;
            short tiltX = (short)(-6000 + (12000 * t));
            short tiltY = (short)(4000 * Math.Sin(2 * Math.PI * t));
            yield return Frame(Pen(32768 + (2000 * t), 32768, 30000, PenState.InRange | PenState.Contact, tiltX, tiltY, (ushort)(35999 * t)));
        }

        yield return Frame(Pen(34768, 32768, 0, PenState.InRange));
        yield return Frame(null);
    }

    private static IEnumerable<ScriptFrame> HoverPath()
    {
        for (int i = 0; i <= 60; i++)
        {
            yield return Frame(Pen(10000 + (i * 700), 20000 + (i * 300), 0, PenState.InRange));
        }

        // Rest still: the client keeps it alive with 200 ms hover keepalives.
        yield return new ScriptFrame(Pen(52000, 38000, 0, PenState.InRange), NoTouches, NoHistory, TimeSpan.FromMilliseconds(700));
        yield return Frame(null);
    }

    private static IEnumerable<ScriptFrame> ButtonClick()
    {
        foreach (PenButtons button in new[] { PenButtons.Primary, PenButtons.Secondary })
        {
            for (int i = 0; i < 5; i++)
            {
                yield return Frame(Pen(30000, 30000, 0, PenState.InRange));
            }

            for (int i = 0; i < 10; i++)
            {
                yield return Frame(Pen(30000, 30000, 0, PenState.InRange, buttons: button));
            }
        }

        yield return Frame(Pen(30000, 30000, 0, PenState.InRange));
        yield return Frame(null);
    }

    /// <summary>A touch at phone pixel (x, y) on the SimClient surface.</summary>
    private static TouchSample Finger(byte id, double x, double y) =>
        new(id, true, Clamp(x / SurfaceWidth * 65535), Clamp(y / SurfaceHeight * 65535));

    private static ScriptFrame Fingers(params TouchSample[] touches) => new(null, touches, NoHistory, FrameInterval);

    /// <summary>A slow two-finger pan down, then a flick that lifts while moving fast (momentum follows).</summary>
    private static IEnumerable<ScriptFrame> TwoFingerScroll()
    {
        double y = 900;
        for (int i = 0; i < 30; i++)
        {
            yield return Fingers(Finger(1, 500, y), Finger(2, 940, y));
            y += 10;
        }

        for (int i = 1; i <= 8; i++)
        {
            y += 10 + (i * 7); // accelerate to about 66 px per frame
            yield return Fingers(Finger(1, 500, y), Finger(2, 940, y));
        }

        yield return Frame(null); // lift at speed: no pause before it
    }

    private static IEnumerable<ScriptFrame> Pinch()
    {
        for (int i = 0; i <= 60; i++)
        {
            ushort spread = (ushort)(3000 + (i * 300));
            yield return new ScriptFrame(
                null,
                [new TouchSample(1, true, (ushort)(32768 - spread), 32768), new TouchSample(2, true, (ushort)(32768 + spread), 32768)],
                NoHistory,
                FrameInterval);
        }

        yield return Frame(null);
    }

    /// <summary>Two fingers turning 90 degrees clockwise around the surface centre at a constant spread.</summary>
    private static IEnumerable<ScriptFrame> Rotate()
    {
        const double Radius = 300;
        for (int i = 0; i <= 45; i++)
        {
            double a = i * 2 * Math.PI / 180;
            double ox = Radius * Math.Cos(a);
            double oy = Radius * Math.Sin(a);
            yield return Fingers(
                Finger(1, (SurfaceWidth / 2) - ox, (SurfaceHeight / 2) - oy),
                Finger(2, (SurfaceWidth / 2) + ox, (SurfaceHeight / 2) + oy));
        }

        yield return Frame(null);
    }

    /// <summary>
    /// Two fingers landing together and lifting after about 50 ms with a little jitter: a right-click once the
    /// double-tap window passed.
    /// </summary>
    private static IEnumerable<ScriptFrame> TwoFingerTap() => TapFrames(TapSettle);

    /// <summary>Two two-finger taps 150 ms apart at nearly the same place: a smart zoom (no right-click).</summary>
    private static IEnumerable<ScriptFrame> SmartZoom()
    {
        foreach (ScriptFrame f in TapFrames(TimeSpan.FromMilliseconds(150)))
        {
            yield return f;
        }

        foreach (ScriptFrame f in TapFrames(TapSettle, dx: 10))
        {
            yield return f;
        }
    }

    private static IEnumerable<ScriptFrame> TapFrames(TimeSpan after, double dx = 0)
    {
        for (int i = 0; i < 6; i++)
        {
            double jitter = i % 2;
            yield return Fingers(Finger(1, 520 + dx + jitter, 1500), Finger(2, 920 + dx, 1520 - jitter));
        }

        yield return new ScriptFrame(null, NoTouches, NoHistory, after);
    }

    /// <summary>Three fingers landing a frame apart, then moving right and down together: a left drag.</summary>
    private static IEnumerable<ScriptFrame> ThreeFingerDrag()
    {
        yield return Fingers(Finger(1, 420, 1400), Finger(2, 720, 1350));
        yield return Fingers(Finger(1, 420, 1400), Finger(2, 720, 1350), Finger(3, 1020, 1400));
        for (int i = 1; i <= 40; i++)
        {
            double dx = i * 8;
            double dy = i * 4;
            yield return Fingers(Finger(1, 420 + dx, 1400 + dy), Finger(2, 720 + dx, 1350 + dy), Finger(3, 1020 + dx, 1400 + dy));
        }

        yield return Frame(null);
    }

    /// <summary>
    /// A two-finger horizontal flick (trackpad "swipe between pages"): fingers accelerate sideways over 12 frames
    /// (about 100 ms, ~650 px) and lift while still moving, so the scroll ends at speed and momentum follows.
    /// <paramref name="direction"/> −1 moves the fingers left, +1 right.
    /// </summary>
    private static IEnumerable<ScriptFrame> PageSwipe(int direction)
    {
        double x = direction < 0 ? 900 : 300;
        for (int i = 0; i <= 12; i++)
        {
            yield return Fingers(Finger(1, x, 1300), Finger(2, x + 240, 1700));
            x += direction * (10 + (i * 8)); // accelerate to about 100 px per frame
        }

        yield return Frame(null); // lift at speed: no pause before it
    }

    /// <summary>Four fingers on a square, moving 600 px in (sx, sy) over 30 frames.</summary>
    private static IEnumerable<ScriptFrame> FourFingerSwipe(int sx, int sy)
    {
        for (int i = 0; i <= 30; i++)
        {
            double cx = (SurfaceWidth / 2) + (sx * i * 20);
            double cy = (SurfaceHeight / 2) + (sy * i * 20);
            yield return FourFingers(cx, cy, 200);
        }

        yield return Frame(null);
    }

    /// <summary>Four fingers on a square around the centre whose half-size goes from r0 to r1 px.</summary>
    private static IEnumerable<ScriptFrame> FourFingerPinch(double r0, double r1)
    {
        for (int i = 0; i <= 30; i++)
        {
            yield return FourFingers(SurfaceWidth / 2, SurfaceHeight / 2, r0 + ((r1 - r0) * i / 30));
        }

        yield return Frame(null);
    }

    /// <summary>
    /// One finger as a relative cursor (ADR-0026; the server must offer FingerTouches): a move right, a pause, a click,
    /// a double click, then tap and a half: a left drag down and right, released on the lift.
    /// </summary>
    private static IEnumerable<ScriptFrame> OneFinger()
    {
        for (int i = 0; i <= 30; i++)
        {
            yield return Fingers(Finger(1, 400 + (i * 15), 1500)); // move 450 px right
        }

        yield return new ScriptFrame(null, NoTouches, NoHistory, TimeSpan.FromMilliseconds(600)); // past the double-tap window
        foreach (ScriptFrame f in OneFingerTap(TimeSpan.FromMilliseconds(600)))
        {
            yield return f; // click
        }

        foreach (ScriptFrame f in OneFingerTap(TimeSpan.FromMilliseconds(150)))
        {
            yield return f;
        }

        foreach (ScriptFrame f in OneFingerTap(TimeSpan.FromMilliseconds(600)))
        {
            yield return f; // double click
        }

        foreach (ScriptFrame f in OneFingerTap(TimeSpan.FromMilliseconds(150)))
        {
            yield return f;
        }

        for (int i = 0; i <= 40; i++)
        {
            yield return Fingers(Finger(1, 720 + (i * 6), 1500 + (i * 6))); // tap and a half: drag
        }

        yield return Frame(null);
    }

    private static IEnumerable<ScriptFrame> OneFingerTap(TimeSpan after)
    {
        for (int i = 0; i < 6; i++)
        {
            yield return Fingers(Finger(1, 720 + (i % 2), 1500));
        }

        yield return new ScriptFrame(null, NoTouches, NoHistory, after);
    }

    private static ScriptFrame FourFingers(double cx, double cy, double r) => Fingers(
        Finger(1, cx - r, cy - r),
        Finger(2, cx + r, cy - r),
        Finger(3, cx + r, cy + r),
        Finger(4, cx - r, cy + r));
}
