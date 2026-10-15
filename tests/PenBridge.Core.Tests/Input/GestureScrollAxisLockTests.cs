using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// #123: a two-finger scroll that starts straight is locked to that axis (<see cref="GestureRecognizerOptions.ScrollAxisLockRatio"/>),
/// so a horizontal swipe's vertical drift never scrolls a vertical-only view; momentum inherits the lock.
/// </summary>
[Collection(AllocationGroup.Name)]
public class GestureScrollAxisLockTests
{
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));
    private static readonly GestureRecognizerOptions Linear = new() { ScrollAcceleration = GestureAcceleration.None };
    private readonly RecordingSink _sink = new();
    private GestureRecognizer _rec;

    public GestureScrollAxisLockTests()
    {
        _rec = new GestureRecognizer(_sink, Linear) { Mapper = Square };
    }

    private static ushort N(double points) => (ushort)Math.Round(Math.Clamp(points, 0, 1000) * 65535.0 / 1000.0);

    private void Two(double ms, double cx, double cy) =>
        _rec.Process([new TouchSample(1, true, N(cx - 100), N(cy)), new TouchSample(2, true, N(cx + 100), N(cy))], false, (long)(ms * 1000));

    private List<GestureEvent> Scrolls => _sink.Gestures.Where(g => g.Kind == GestureKind.Scroll).ToList();

    /// <summary>Two fingers moving (vx, vy) points per 8 ms frame for <paramref name="frames"/> frames, then lifting at speed.</summary>
    private void Swipe(double vx, double vy, int frames = 12, Func<int, double>? driftY = null, Func<int, double>? driftX = null)
    {
        for (int i = 0; i <= frames; i++)
        {
            Two(i * 8, 300 + (vx * i) + (driftX?.Invoke(i) ?? 0), 500 + (vy * i) + (driftY?.Invoke(i) ?? 0));
        }

        _rec.Process([], false, (frames + 1) * 8 * 1000L);
    }

    private static (double X, double Y) Coast(List<GestureEvent> scrolls)
    {
        var momentum = new ScrollMomentum();
        bool coasting = false;
        foreach (GestureEvent e in scrolls)
        {
            coasting = momentum.Observe(e);
        }

        Assert.True(coasting, "a flick coasts");
        return (momentum.ReleaseVelocityX, momentum.ReleaseVelocityY);
    }

    [Fact]
    public void A_horizontal_swipe_with_vertical_drift_scrolls_only_horizontally_including_momentum()
    {
        Swipe(20, 0, driftY: i => (i % 3) * 4 + (i * 0.8)); // ~13 pt of drift across the swipe, up to 27% of the step

        List<GestureEvent> scrolls = Scrolls;
        Assert.Equal(GesturePhase.Began, scrolls[0].Phase);
        Assert.Equal(GesturePhase.Ended, scrolls[^1].Phase);
        Assert.All(scrolls, e => Assert.Equal(0, e.DeltaY));
        Assert.True(scrolls.Sum(e => e.DeltaX) > 150);

        (double vx, double vy) = Coast(scrolls);
        Assert.True(vx > 0);
        Assert.Equal(0, vy);
    }

    [Fact]
    public void A_vertical_swipe_with_horizontal_drift_scrolls_only_vertically()
    {
        Swipe(0, -20, driftX: i => (i % 2) * 5);

        List<GestureEvent> scrolls = Scrolls;
        Assert.All(scrolls, e => Assert.Equal(0, e.DeltaX));
        Assert.True(scrolls.Sum(e => e.DeltaY) < -150);
        (double vx, double vy) = Coast(scrolls);
        Assert.Equal(0, vx);
        Assert.True(vy < 0);
    }

    [Fact]
    public void A_diagonal_swipe_keeps_both_axes_free()
    {
        Swipe(20, 15); // 37° off horizontal: past the 0.5 ratio

        List<GestureEvent> scrolls = Scrolls;
        Assert.Contains(scrolls, e => e.DeltaX != 0 && e.DeltaY != 0);
        Assert.Equal(15.0 / 20, scrolls.Sum(e => e.DeltaY) / scrolls.Sum(e => e.DeltaX), 2);
        (double vx, double vy) = Coast(scrolls);
        Assert.True(vx > 0 && vy > 0);
    }

    [Theory]
    [InlineData(10, true)]  // 26.6°: locked
    [InlineData(11, false)] // 28.8°: free
    public void The_lock_is_decided_by_the_movement_that_starts_the_scroll(double dy, bool locked)
    {
        Two(0, 300, 500);
        Two(8, 320, 500 + dy); // 20 pt, under the 40 pt threshold
        Two(16, 340, 500 + (2 * dy)); // crosses it: dx 40, dy 2·dy
        Two(24, 360, 500 + (6 * dy)); // later steep movement doesn't unlock it
        GestureEvent began = Scrolls[0];
        Assert.Equal(GesturePhase.Began, began.Phase);
        Assert.Equal(locked, began.DeltaY == 0);
        Assert.Equal(locked, Scrolls[^1].DeltaY == 0);
    }

    [Fact]
    public void The_lock_ends_with_the_scroll()
    {
        Swipe(20, 0);
        _sink.Clear();
        Swipe(0, 20); // the next scroll decides again
        Assert.All(Scrolls, e => Assert.Equal(0, e.DeltaX));
        Assert.True(Scrolls.Sum(e => e.DeltaY) > 150);
    }

    [Fact]
    public void Zero_ratio_locks_only_an_exactly_straight_start()
    {
        _rec = new GestureRecognizer(_sink, Linear with { ScrollAxisLockRatio = 0 }) { Mapper = Square };
        Swipe(20, 0, driftY: i => i % 2);
        Assert.Contains(Scrolls, e => e.DeltaY != 0);
    }

    [Fact]
    public void Drag_is_not_axis_locked()
    {
        for (int i = 0; i <= 10; i++)
        {
            double x = 300 + (i * 10);
            double y = 500 + (i % 2);
            _rec.Process(
                [new TouchSample(1, true, N(x), N(y)), new TouchSample(2, true, N(x + 100), N(y)), new TouchSample(3, true, N(x + 200), N(y))],
                false,
                i * 8_000L);
        }

        Assert.Contains(_sink.Gestures, e => e.Kind == GestureKind.Drag && e.DeltaY != 0);
    }

    [Fact]
    public void Options_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GestureRecognizer(_sink, new GestureRecognizerOptions { ScrollAxisLockRatio = -0.1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GestureRecognizer(_sink, new GestureRecognizerOptions { ScrollAxisLockRatio = double.PositiveInfinity }));
    }

    [Fact]
    public void Locked_scrolling_is_allocation_free()
    {
        var sink = new CountingSink();
        var rec = new GestureRecognizer(sink, Linear) { Mapper = Square };
        var two = new TouchSample[2];
        var none = Array.Empty<TouchSample>();
        long t = 0;
        Alloc.AssertZero(3_000, i =>
        {
            int phase = i % 20;
            t += 8_000;
            if (phase == 19)
            {
                rec.Process(none, false, t);
                return;
            }

            bool vertical = (i / 20) % 2 == 1;
            double step = phase * 20;
            double drift = phase % 3;
            double cx = vertical ? 300 + drift : 200 + step;
            double cy = vertical ? 200 + step : 500 + drift;
            two[0] = new TouchSample(1, true, N(cx - 100), N(cy));
            two[1] = new TouchSample(2, true, N(cx + 100), N(cy));
            rec.Process(two, false, t);
        }, () => sink.Scrolls);
    }

    private sealed class CountingSink : IInputSink
    {
        public long Scrolls;

        public void OnPenEvent(in PenEvent e)
        {
        }

        public void OnGestureEvent(in GestureEvent e)
        {
            if (e.Kind == GestureKind.Scroll)
            {
                Scrolls++;
            }
        }
    }
}
