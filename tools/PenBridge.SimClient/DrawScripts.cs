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
        ["line", "circle", "pressure-ramp", "tilt-sweep", "hover", "button-click", "two-finger-scroll", "pinch", PacketLoss];

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

    private static IEnumerable<ScriptFrame> TwoFingerScroll()
    {
        for (int i = 0; i <= 60; i++)
        {
            ushort y = (ushort)(20000 + (i * 400));
            yield return new ScriptFrame(null, [new TouchSample(1, true, 25000, y), new TouchSample(2, true, 40000, y)], NoHistory, FrameInterval);
        }

        yield return Frame(null);
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
}
