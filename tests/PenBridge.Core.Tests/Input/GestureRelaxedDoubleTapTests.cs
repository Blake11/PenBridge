using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// Relaxed two-finger taps and double taps on an S23 Ultra-sized surface with the default options (#121): each
/// offset or timing here was rejected by the earlier defaults (2% tap slop, 5% double-tap slop, 200 ms tap). The
/// guards check that single taps still right-click and slow scrolls stay no tap. Gaps between the taps fit the 200 ms
/// double-tap window (ADR-0029; the S23 Ultra measured 78-135 ms).
/// </summary>
public class GestureRelaxedDoubleTapTests
{
    // A landscape S23 Ultra pad (2316x1080 px, about 146 x 68 mm) aspect-fit onto a 1920x1080 display: the mapped
    // short side (~895 pt) is the phone's 68 mm side.
    private const double WidthMm = 146;
    private const double HeightMm = 68;
    private static readonly CoordinateMapper Phone = new(2316, 1080, new DisplayRect(0, 0, 1920, 1080));
    private readonly RecordingSink _sink = new();
    private readonly GestureRecognizer _rec;

    public GestureRelaxedDoubleTapTests()
    {
        _rec = new GestureRecognizer(_sink) { Mapper = Phone };
    }

    private List<GestureEvent> Gestures => _sink.Gestures.Where(g => g.Kind != GestureKind.TouchDown).ToList();

    private GestureKind[] Kinds() => Gestures.Select(g => g.Kind).ToArray();

    /// <summary>A finger at (x, y) mm from the pad's top-left corner.</summary>
    private static TouchSample F(byte id, double xMm, double yMm) =>
        new(id, true, (ushort)Math.Round(xMm / WidthMm * 65535), (ushort)Math.Round(yMm / HeightMm * 65535));

    private static long Ms(double ms) => (long)(ms * 1000);

    private void Frame(double ms, params TouchSample[] touches) => _rec.Process(touches, false, Ms(ms));

    /// <summary>
    /// A relaxed two-finger tap, fingers 20 mm apart around (cx, cy) mm: the second finger lands
    /// <paramref name="staggerMs"/> after the first and lifts that much after it, each finger drifting
    /// <paramref name="driftMm"/> while down (the contact patch rolling on lift). Frames every 8 ms.
    /// </summary>
    private void RelaxedTap(double startMs, double cx, double cy, double downMs = 150, double staggerMs = 40, double driftMm = 0)
    {
        double x1 = cx - 10;
        double x2 = cx + 10;
        double end = startMs + staggerMs + downMs; // first finger lifts at startMs + downMs, second at end
        for (double t = startMs; t < end; t += 8)
        {
            double f = (t - startMs) / (end - startMs);
            bool first = t < startMs + downMs;
            bool second = t >= startMs + staggerMs;
            if (first && second)
            {
                Frame(t, F(1, x1, cy + (driftMm * f)), F(2, x2, cy + (driftMm * f)));
            }
            else if (first)
            {
                Frame(t, F(1, x1, cy + (driftMm * f)));
            }
            else
            {
                Frame(t, F(2, x2, cy + (driftMm * f)));
            }
        }

        Frame(end);
    }

    [Fact]
    public void Second_tap_landing_8_mm_off_the_first_smart_zooms()
    {
        RelaxedTap(0, 70, 34);
        RelaxedTap(360, 76, 39); // midpoint ~7.8 mm away (the old 5% slop was ~3.4 mm), 170 ms after the lift
        Assert.Equal([GestureKind.SmartZoom], Kinds());
    }

    [Fact]
    public void Fingers_drifting_2_mm_on_lift_still_tap_and_smart_zoom()
    {
        RelaxedTap(0, 70, 34, driftMm: 2); // the old 2% per-finger slop was ~1.4 mm
        RelaxedTap(360, 70, 34, driftMm: 2);
        Assert.Equal([GestureKind.SmartZoom], Kinds());
    }

    [Fact]
    public void A_slow_relaxed_tap_of_270_ms_still_counts()
    {
        RelaxedTap(0, 70, 34, downMs: 200, staggerMs: 70); // 270 ms from first landing to last lift (old max 200)
        RelaxedTap(450, 70, 34, downMs: 200, staggerMs: 70); // lands 180 ms after the first lift
        Assert.Equal([GestureKind.SmartZoom], Kinds());
    }

    [Fact]
    public void A_190_ms_gap_between_the_taps_smart_zooms()
    {
        RelaxedTap(0, 70, 34); // lifts at 190 ms
        RelaxedTap(380, 70, 34); // lands 190 ms later, near the end of the 200 ms window
        Assert.Equal([GestureKind.SmartZoom], Kinds());
    }

    [Fact]
    public void A_210_ms_gap_is_two_right_clicks()
    {
        RelaxedTap(0, 70, 34); // lifts at 190 ms
        RelaxedTap(400, 70, 34); // lands 210 ms later: past the 200 ms window (ADR-0029 traded it for latency)
        _rec.CheckDeadline(Ms(2000));
        Assert.Equal([GestureKind.Tap, GestureKind.Tap], Kinds());
    }

    [Fact]
    public void A_relaxed_double_tap_with_every_offset_at_once_smart_zooms()
    {
        RelaxedTap(0, 70, 34, downMs: 180, staggerMs: 60, driftMm: 1.8);
        RelaxedTap(420, 64, 30, downMs: 170, staggerMs: 50, driftMm: -1.8); // 180 ms gap, ~7 mm off
        GestureEvent zoom = Assert.Single(Gestures);
        Assert.Equal(GestureKind.SmartZoom, zoom.Kind);
    }

    [Fact]
    public void A_single_relaxed_tap_still_right_clicks_after_the_window()
    {
        RelaxedTap(0, 70, 34, downMs: 200, staggerMs: 60, driftMm: 2); // lifts at 260 ms
        Assert.Empty(Gestures);
        Assert.Equal(Ms(260 + 200 + 20), _rec.PendingDeadlineUs);
        _rec.CheckDeadline(Ms(479));
        Assert.Empty(Gestures);
        _rec.CheckDeadline(Ms(480));
        Assert.Equal([GestureKind.Tap], Kinds());
    }

    [Fact]
    public void Taps_far_apart_are_two_right_clicks()
    {
        RelaxedTap(0, 50, 34);
        RelaxedTap(360, 70, 34); // 20 mm away: beyond the ~14 mm double-tap slop, inside the window
        _rec.CheckDeadline(Ms(2000));
        Assert.Equal([GestureKind.Tap, GestureKind.Tap], Kinds());
    }

    [Fact]
    public void Resting_two_fingers_for_350_ms_is_no_tap()
    {
        RelaxedTap(0, 70, 34, downMs: 310, staggerMs: 40);
        _rec.CheckDeadline(Ms(2000));
        Assert.Empty(Gestures);
    }

    [Fact]
    public void A_slow_short_two_finger_scroll_is_no_tap()
    {
        // 2.6 mm in 250 ms: beyond the ~2.4 mm tap slop, short of the ~2.7 mm scroll threshold.
        RelaxedTap(0, 70, 34, downMs: 250, staggerMs: 0, driftMm: 2.6);
        _rec.CheckDeadline(Ms(2000));
        Assert.Empty(Gestures);
    }

    [Fact]
    public void A_nudge_just_under_the_scroll_threshold_with_a_slow_lift_is_no_tap()
    {
        // 2.3 mm: inside the ~2.4 mm tap slop and under the ~2.7 mm scroll threshold, but lifted after 340 ms.
        RelaxedTap(0, 70, 34, downMs: 330, staggerMs: 10, driftMm: 2.3);
        _rec.CheckDeadline(Ms(2000));
        Assert.Empty(Gestures);
    }

    [Fact]
    public void A_slow_two_finger_scroll_scrolls_and_never_taps()
    {
        RelaxedTap(0, 70, 34, downMs: 280, staggerMs: 10, driftMm: 6); // ~21 mm/s
        _rec.CheckDeadline(Ms(2000));
        Assert.Equal(GestureKind.Scroll, Gestures[0].Kind);
        Assert.DoesNotContain(GestureKind.Tap, Kinds());
        Assert.DoesNotContain(GestureKind.SmartZoom, Kinds());
    }

    [Fact]
    public void A_scroll_right_after_a_tap_right_clicks_then_scrolls()
    {
        RelaxedTap(0, 70, 34);
        RelaxedTap(360, 70, 34, downMs: 280, staggerMs: 10, driftMm: 8); // inside the window
        Assert.Equal(GestureKind.Tap, Gestures[0].Kind);
        Assert.Equal(GestureKind.Scroll, Gestures[1].Kind);
        Assert.DoesNotContain(GestureKind.SmartZoom, Kinds());
    }
}
