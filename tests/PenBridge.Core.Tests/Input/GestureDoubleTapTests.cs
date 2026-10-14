using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>Held two-finger taps, the double-tap window, SmartZoom and the deadline hook (ADR-0023).</summary>
public class GestureDoubleTapTests
{
    // Square 1000x1000 surface onto a 1000x1000 display: double-tap slop 5% = 50 pt.
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));
    private readonly RecordingSink _sink = new();
    private readonly GestureRecognizer _rec;

    public GestureDoubleTapTests()
    {
        _rec = new GestureRecognizer(_sink) { Mapper = Square };
    }

    private List<GestureEvent> Gestures => _sink.Gestures.Where(g => g.Kind != GestureKind.TouchDown).ToList();

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    private static TouchSample F(byte id, double x, double y) => new(id, true, N(x), N(y));

    private static long Ms(double ms) => (long)(ms * 1000);

    private void Frame(double ms, bool pen = false, params TouchSample[] touches) => _rec.Process(touches, pen, Ms(ms));

    /// <summary>A two-finger tap with its midpoint at (cx, 500): lands at <paramref name="ms"/>, lifts 50 ms later.</summary>
    private void Tap(double ms, double cx = 500)
    {
        Frame(ms, false, F(1, cx - 100, 500), F(2, cx + 100, 500));
        Frame(ms + 50);
    }

    [Fact]
    public void A_tap_is_held_for_the_double_tap_window()
    {
        Tap(0);
        Assert.Empty(Gestures);
        Assert.Equal(Ms(350), _rec.PendingDeadlineUs);

        _rec.CheckDeadline(Ms(349));
        Assert.Empty(Gestures);

        _rec.CheckDeadline(Ms(350));
        GestureEvent tap = Assert.Single(Gestures);
        Assert.Equal(GestureKind.Tap, tap.Kind);
        Assert.Equal(GesturePhase.Ended, tap.Phase);
        Assert.Equal(Ms(350), tap.TimestampUs);
        Assert.Equal(500, tap.Center.X, 0.1);
        Assert.Null(_rec.PendingDeadlineUs);

        _rec.CheckDeadline(Ms(1000));
        Assert.Single(Gestures);
    }

    [Fact]
    public void A_frame_after_the_window_flushes_the_tap_too()
    {
        Tap(0);
        Frame(400); // e.g. an empty keepalive
        Assert.Equal(GestureKind.Tap, Assert.Single(Gestures).Kind);
    }

    [Fact]
    public void Two_taps_in_the_window_are_one_smart_zoom()
    {
        Tap(0);
        Tap(200, cx: 530); // lands 150 ms after the first lift, 30 pt away
        GestureEvent zoom = Assert.Single(Gestures);
        Assert.Equal(GestureKind.SmartZoom, zoom.Kind);
        Assert.Equal(GesturePhase.Ended, zoom.Phase);
        Assert.Equal(530, zoom.Center.X, 0.1);
        Assert.Equal(Ms(250), zoom.TimestampUs);
        Assert.Null(_rec.PendingDeadlineUs);
    }

    [Fact]
    public void A_second_tap_landing_after_the_window_is_a_new_tap()
    {
        Tap(0);
        Tap(360);
        Assert.Equal(GestureKind.Tap, Assert.Single(Gestures).Kind);
        Assert.Equal(Ms(360), Gestures[0].TimestampUs); // flushed when the next touch arrived
        Assert.Equal(Ms(710), _rec.PendingDeadlineUs);
    }

    [Fact]
    public void A_second_tap_too_far_away_is_two_taps()
    {
        Tap(0, cx: 400);
        Tap(150, cx: 600);
        Assert.Equal(GestureKind.Tap, Assert.Single(Gestures).Kind);
        Assert.Equal(400, Gestures[0].Center.X, 0.1);
        _rec.CheckDeadline(Ms(1000));
        Assert.Equal([GestureKind.Tap, GestureKind.Tap], Gestures.Select(g => g.Kind));
        Assert.Equal(600, Gestures[1].Center.X, 0.1);
    }

    [Fact]
    public void A_second_touch_held_too_long_flushes_the_tap()
    {
        Tap(0);
        Frame(100, false, F(1, 400, 500), F(2, 600, 500));
        Assert.Equal(Ms(300) + 1, _rec.PendingDeadlineUs); // the second touch can't be a tap after 200 ms
        _rec.CheckDeadline(Ms(300) + 1);
        Assert.Equal(GestureKind.Tap, Assert.Single(Gestures).Kind);
    }

    [Fact]
    public void A_second_touch_that_is_no_tap_flushes_the_tap_on_lift()
    {
        Tap(0);
        Frame(100, false, F(1, 400, 500)); // one finger only
        Frame(150);
        Assert.Equal(GestureKind.Tap, Assert.Single(Gestures).Kind);
        Assert.Equal(Ms(150), Gestures[0].TimestampUs);
    }

    [Fact]
    public void Pen_in_range_drops_a_held_tap()
    {
        Tap(0);
        Frame(100, pen: true);
        Assert.Null(_rec.PendingDeadlineUs);
        _rec.CheckDeadline(Ms(1000));
        Assert.Empty(Gestures);
    }

    [Fact]
    public void Cancel_drops_a_held_tap()
    {
        Tap(0);
        _rec.Cancel(Ms(100));
        _rec.CheckDeadline(Ms(1000));
        Assert.Empty(Gestures);
    }

    [Fact]
    public void A_gesture_beginning_drops_a_held_tap()
    {
        Tap(0);
        Frame(100, false, F(1, 400, 500), F(2, 600, 500));
        Frame(116, false, F(1, 400, 560), F(2, 600, 560)); // scroll
        Frame(130);
        _rec.CheckDeadline(Ms(1000));
        Assert.Equal([GestureKind.Scroll, GestureKind.Scroll], Gestures.Select(g => g.Kind));
    }

    [Fact]
    public void Three_taps_are_a_smart_zoom_then_a_held_tap()
    {
        Tap(0);
        Tap(150);
        Tap(300);
        _rec.CheckDeadline(Ms(1000));
        Assert.Equal([GestureKind.SmartZoom, GestureKind.Tap], Gestures.Select(g => g.Kind));
    }

    [Fact]
    public void Double_tap_window_and_slop_are_configurable()
    {
        var rec = new GestureRecognizer(_sink, new GestureRecognizerOptions { DoubleTapWindow = TimeSpan.FromMilliseconds(500), DoubleTapSlop = 0.1 })
        {
            Mapper = Square,
        };
        rec.Process([F(1, 300, 500), F(2, 500, 500)], false, 0);
        rec.Process([], false, Ms(50));
        Assert.Equal(Ms(550), rec.PendingDeadlineUs);
        rec.Process([F(1, 380, 500), F(2, 580, 500)], false, Ms(500)); // 80 pt away, 450 ms later
        rec.Process([], false, Ms(550));
        Assert.Equal(GestureKind.SmartZoom, Assert.Single(Gestures).Kind);
    }
}
