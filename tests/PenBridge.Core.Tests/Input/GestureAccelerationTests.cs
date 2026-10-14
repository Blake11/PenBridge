using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

public class GestureAccelerationTests
{
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));
    private static readonly GestureAcceleration Curve = new() { SlowSpeed = 400, FastSpeed = 4000, MaxGain = 3 };

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(400, 1)]
    [InlineData(2200, 2)] // midpoint of the smoothstep
    [InlineData(4000, 3)]
    [InlineData(100_000, 3)]
    [InlineData(double.NaN, 1)]
    [InlineData(-5, 1)]
    public void Gain_is_one_when_slow_and_max_when_fast(double speed, double gain) =>
        Assert.Equal(gain, Curve.GainAt(speed), 1e-9);

    [Fact]
    public void Gain_rises_smoothly_and_monotonically()
    {
        double previous = 1;
        for (double v = 400; v <= 4000; v += 50)
        {
            double g = Curve.GainAt(v);
            Assert.True(g >= previous);
            Assert.True(g - previous < 0.05); // no steps
            previous = g;
        }
    }

    [Fact]
    public void None_never_accelerates() => Assert.Equal(1, GestureAcceleration.None.GainAt(1e6));

    [Theory]
    [InlineData(500, 400, 3)]
    [InlineData(-1, 400, 3)]
    [InlineData(0, 400, 0.5)]
    [InlineData(0, double.PositiveInfinity, 3)]
    public void Invalid_curves_throw(double slow, double fast, double max) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GestureRecognizer(new RecordingSink(), new GestureRecognizerOptions
            {
                ScrollAcceleration = new GestureAcceleration { SlowSpeed = slow, FastSpeed = fast, MaxGain = max },
            }));

    /// <summary>Scrolls two fingers by <paramref name="step"/> pt every 8 ms and returns the Changed deltas.</summary>
    private static double[] Scroll(GestureRecognizerOptions options, double step, int frames = 14)
    {
        var sink = new RecordingSink();
        var rec = new GestureRecognizer(sink, options) { Mapper = Square };
        double y = 300;
        for (int i = 0; i <= frames; i++)
        {
            rec.Process([new TouchSample(1, true, N(400), N(y)), new TouchSample(2, true, N(600), N(y))], false, i * 8_000L);
            y += i == 0 ? 0 : step;
            if (i == 0)
            {
                y += 45; // cross the threshold on the next frame
            }
        }

        return sink.Gestures.Where(g => g.Kind == GestureKind.Scroll && g.Phase == GesturePhase.Changed).Select(g => g.DeltaY).ToArray();
    }

    [Fact]
    public void Slow_scrolling_stays_one_to_one()
    {
        double[] deltas = Scroll(new GestureRecognizerOptions { ScrollAcceleration = Curve }, step: 2); // 250 pt/s
        Assert.NotEmpty(deltas);
        Assert.All(deltas[^3..], d => Assert.Equal(2, d, 0.01)); // once the speed estimate settled
    }

    [Fact]
    public void Fast_scrolling_is_amplified_up_to_the_max_gain()
    {
        double[] deltas = Scroll(new GestureRecognizerOptions { ScrollAcceleration = Curve }, step: 40); // 5000 pt/s
        Assert.Equal(120, deltas[^1], 0.5);
    }

    [Fact]
    public void Gain_and_acceleration_multiply()
    {
        double[] deltas = Scroll(new GestureRecognizerOptions { ScrollAcceleration = Curve, ScrollGain = 0.5 }, step: 40);
        Assert.Equal(60, deltas[^1], 0.5);
    }

    [Fact]
    public void Drag_uses_its_own_curve_and_gain()
    {
        var sink = new RecordingSink();
        var options = new GestureRecognizerOptions
        {
            DragGain = 2,
            DragAcceleration = GestureAcceleration.None,
            ScrollAcceleration = Curve,
        };
        var rec = new GestureRecognizer(sink, options) { Mapper = Square };
        for (int i = 0; i < 5; i++)
        {
            double y = 300 + (i * 40);
            rec.Process([new TouchSample(1, true, N(400), N(y)), new TouchSample(2, true, N(500), N(y)), new TouchSample(3, true, N(600), N(y))], false, i * 8_000L);
        }

        GestureEvent[] drags = [.. sink.Gestures.Where(g => g.Kind == GestureKind.Drag)];
        Assert.Equal(80, drags[0].DeltaY, 0.1);
        Assert.All(drags[1..], g => Assert.Equal(80, g.DeltaY, 0.1));
    }

    [Fact]
    public void Momentum_coasts_on_the_accelerated_deltas()
    {
        var sink = new RecordingSink();
        var rec = new GestureRecognizer(sink, new GestureRecognizerOptions { ScrollAcceleration = Curve }) { Mapper = Square };
        var momentum = new ScrollMomentum();
        long t = 0;
        for (int i = 0; i < 15; i++, t += 8_000)
        {
            double y = 200 + (i * 40); // 5000 pt/s
            rec.Process([new TouchSample(1, true, N(400), N(y)), new TouchSample(2, true, N(600), N(y))], false, t);
        }

        rec.Process([], false, t);
        bool coasting = false;
        foreach (GestureEvent e in sink.Gestures)
        {
            coasting = momentum.Observe(e) || coasting;
        }

        Assert.True(coasting);
        Assert.True(momentum.ReleaseVelocityY > 12_000 * 0.99); // 3x 5000 pt/s, capped at MaxSpeed
    }
}
