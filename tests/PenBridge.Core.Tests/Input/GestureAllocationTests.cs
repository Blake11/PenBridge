using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

public class GestureAllocationTests
{
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));

    private static ushort N(double points) => (ushort)Math.Round(Math.Clamp(points, 0, 1000) * 65535.0 / 1000.0);

    [Fact]
    public void Every_gesture_kind_is_allocation_free_per_frame()
    {
        var sink = new CountingSink();
        var rec = new GestureRecognizer(sink) { Mapper = Square };
        var two = new TouchSample[2];
        var three = new TouchSample[3];
        var four = new TouchSample[4];
        var none = Array.Empty<TouchSample>();
        long t = 0;

        void Two(double ax, double ay, double bx, double by, bool pen = false)
        {
            two[0] = new TouchSample(1, true, N(ax), N(ay));
            two[1] = new TouchSample(2, true, N(bx), N(by));
            rec.Process(two, pen, t);
        }

        Alloc.AssertZero(3_000, i =>
        {
            int phase = i % 64;
            t += 8_000;
            if (phase < 10)
            {
                double a = phase * 12 * Math.PI / 180; // rotate
                Two(500 - (100 * Math.Cos(a)), 500 - (100 * Math.Sin(a)), 500 + (100 * Math.Cos(a)), 500 + (100 * Math.Sin(a)));
            }
            else if (phase is 10 or 13 or 16 or 19 or 31 or 37 or 41 or 51 or 63)
            {
                rec.Process(none, false, t);
            }
            else if (phase is 11 or 12 or 14 or 15 or 17 or 18)
            {
                Two(400, 500, 600, 500); // tap, tap (smart zoom), tap (held)
            }
            else if (phase == 20)
            {
                t += 400_000;
                rec.CheckDeadline(t); // the held tap fires
            }
            else if (phase < 30)
            {
                Two(400, 300 + (phase * 20), 600, 300 + (phase * 20)); // scroll
            }
            else if (phase == 30)
            {
                Two(400, 900, 600, 900, pen: true); // pen cancels
            }
            else if (phase < 36)
            {
                Two(400 - ((phase - 31) * 30), 500, 600 + ((phase - 31) * 30), 500); // pinch
            }
            else if (phase == 36)
            {
                rec.Cancel(t);
            }
            else if (phase < 41)
            {
                Two(400, 500, 600, 500 + phase); // blocked after cancel, then lift
            }
            else if (phase < 51)
            {
                double y = 300 + (phase * 10); // three-finger drag
                three[0] = new TouchSample(1, true, N(400), N(y));
                three[1] = new TouchSample(2, true, N(500), N(y));
                three[2] = new TouchSample(3, true, N(600), N(y));
                rec.Process(three, false, t);
            }
            else
            {
                double c = 300 + ((phase - 51) * 30); // four-finger swipe right
                four[0] = new TouchSample(1, true, N(c - 100), N(400));
                four[1] = new TouchSample(2, true, N(c + 100), N(400));
                four[2] = new TouchSample(3, true, N(c + 100), N(600));
                four[3] = new TouchSample(4, true, N(c - 100), N(600));
                rec.Process(four, false, t);
            }

            _ = rec.PendingDeadlineUs;
        }, () => sink.Count(GestureKind.Pinch), () => sink.Count(GestureKind.Tap), () => sink.Count(GestureKind.SmartZoom),
            () => sink.Count(GestureKind.Scroll), () => sink.Count(GestureKind.Drag), () => sink.Count(GestureKind.SystemSwipe),
            () => sink.Count(GestureKind.TouchDown));
    }

    private sealed class CountingSink : IInputSink
    {
        private readonly long[] _counts = new long[16];

        public long Count(GestureKind kind) => _counts[(int)kind];

        public void OnPenEvent(in PenEvent e)
        {
        }

        public void OnGestureEvent(in GestureEvent e) => _counts[(int)e.Kind]++;
    }
}
