using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// Events caused by a received frame (or the pen entering) are real, not synthetic: only <see cref="GestureRecognizer.Cancel"/>
/// from the watchdog or a session end makes synthetic events. Also the withdrawn-touch list at its capacity.
/// </summary>
public class GestureRealFrameTests
{
    // Square 1000x1000 surface onto a 1000x1000 display: thresholds are fractions of 1000 pt.
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));

    private readonly RecordingSink _sink = new();

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    private static TouchSample F(byte id, double x, double y) => new(id, true, N(x), N(y));

    private static TouchSample W(byte id, double x, double y) => new(id, false, N(x), N(y));

    private static long Ms(double ms) => (long)Math.Round(ms * 1000);

    private GestureRecognizer Rec(GestureRecognizerOptions? options = null) => new(_sink, options) { Mapper = Square };

    private List<GestureEvent> Gestures => _sink.Gestures.Where(g => g.Kind != GestureKind.TouchDown).ToList();

    private void AssertReal(params (GestureKind Kind, GesturePhase Phase)[] expected)
    {
        Assert.Equal(expected, Gestures.Select(g => (g.Kind, g.Phase)));
        Assert.All(_sink.Gestures, g => Assert.False(g.Synthetic, $"{g.Kind} {g.Phase}"));
    }

    private static void Two(GestureRecognizer rec, double ms, double ax, double ay, double bx, double by, bool pen = false) =>
        rec.Process([F(1, ax, ay), F(2, bx, by)], pen, Ms(ms));

    private static void Three(GestureRecognizer rec, double ms, double dx) =>
        rec.Process([F(1, 400 + dx, 400), F(2, 600 + dx, 400), F(3, 500 + dx, 600)], false, Ms(ms));

    private static void Four(GestureRecognizer rec, double ms, double cx = 500, double cy = 500, double r = 100) =>
        rec.Process([F(1, cx - r, cy - r), F(2, cx + r, cy - r), F(3, cx + r, cy + r), F(4, cx - r, cy + r)], false, Ms(ms));

    [Fact]
    public void A_scroll_that_ends_on_a_lift_is_real()
    {
        var rec = Rec();
        Two(rec, 0, 400, 400, 600, 400);
        Two(rec, 10, 400, 450, 600, 450); // 50 pt ≥ 40 pt
        Two(rec, 20, 400, 480, 600, 480);
        rec.Process([F(1, 400, 480)], false, Ms(30));
        AssertReal((GestureKind.Scroll, GesturePhase.Began), (GestureKind.Scroll, GesturePhase.Changed), (GestureKind.Scroll, GesturePhase.Ended));
    }

    [Fact]
    public void A_pinch_cancelled_by_the_pen_in_the_frame_is_real()
    {
        var rec = Rec();
        Two(rec, 0, 400, 500, 600, 500);
        Two(rec, 10, 350, 500, 650, 500); // spread +100 pt, no travel
        Two(rec, 20, 300, 500, 700, 500);
        Two(rec, 30, 300, 500, 700, 500, pen: true);
        AssertReal((GestureKind.Pinch, GesturePhase.Began), (GestureKind.Pinch, GesturePhase.Changed), (GestureKind.Pinch, GesturePhase.Cancelled));
    }

    [Fact]
    public void A_drag_cancelled_by_the_pen_entering_is_real()
    {
        var rec = Rec();
        Three(rec, 0, 0);
        Three(rec, 10, 30); // 30 pt ≥ 20 pt
        Three(rec, 20, 40);
        rec.PenEntering(Ms(25));
        AssertReal((GestureKind.Drag, GesturePhase.Began), (GestureKind.Drag, GesturePhase.Changed), (GestureKind.Drag, GesturePhase.Cancelled));
    }

    [Fact]
    public void A_swipe_cancelled_by_a_withdrawn_touch_is_real()
    {
        var rec = Rec();
        Four(rec, 0);
        Four(rec, 10, 560); // 60 pt ≥ 50 pt
        Four(rec, 20, 600);
        rec.Process([W(1, 500, 400), F(2, 700, 400), F(3, 700, 600), F(4, 500, 600)], false, Ms(30));
        AssertReal((GestureKind.SystemSwipe, GesturePhase.Began), (GestureKind.SystemSwipe, GesturePhase.Changed), (GestureKind.SystemSwipe, GesturePhase.Cancelled));
    }

    [Fact]
    public void A_system_pinch_is_real()
    {
        var rec = Rec();
        Four(rec, 0);
        Four(rec, 10, r: 150); // spread 2·141.4 → 2·212.1 pt: +141 pt ≥ 50 pt
        AssertReal((GestureKind.SystemPinch, GesturePhase.Began));
    }

    [Fact]
    public void A_pointer_move_ended_by_a_second_finger_is_real()
    {
        var rec = Rec(new GestureRecognizerOptions { OneFingerCursor = true });
        rec.Process([F(1, 500, 500)], false, 0);
        rec.Process([F(1, 600, 500)], false, Ms(200)); // 100 pt > 15 pt
        rec.Process([F(1, 600, 500), F(2, 800, 500)], false, Ms(210));
        AssertReal((GestureKind.PointerMove, GesturePhase.Began), (GestureKind.PointerMove, GesturePhase.Ended));
    }

    [Fact]
    public void A_click_is_real()
    {
        var rec = Rec(new GestureRecognizerOptions { OneFingerCursor = true });
        rec.Process([F(1, 500, 500)], false, 0);
        rec.Process([], false, Ms(50));
        AssertReal((GestureKind.Click, GesturePhase.Ended));
    }

    [Fact]
    public void A_smart_zoom_is_real()
    {
        var rec = Rec();
        for (int i = 0; i < 2; i++)
        {
            Two(rec, i * 150, 400, 500, 600, 500);
            rec.Process([], false, Ms((i * 150) + 50));
        }

        AssertReal((GestureKind.SmartZoom, GesturePhase.Ended));
    }

    [Fact]
    public void A_withdrawn_touch_among_more_than_the_tracked_contacts_blocks_until_all_lift()
    {
        var rec = Rec();
        TouchSample[] all = new TouchSample[GestureRecognizer.MaxTrackedContacts + 2];
        for (int i = 0; i < all.Length; i++)
        {
            all[i] = F((byte)(i + 1), 50 + (i * 70), 500);
        }

        rec.Process(all, false, Ms(0));
        all[0] = W(1, 50, 500);
        rec.Process(all, false, Ms(10)); // twelve touches, one withdrawn: the list fills up and the rest are not tracked

        rec.Process([F(2, 120, 500), F(3, 190, 500)], false, Ms(20)); // two of them stay: still blocked
        rec.Process([F(2, 120, 560), F(3, 190, 560)], false, Ms(30));
        Assert.Empty(Gestures);

        rec.Process([], false, Ms(40));
        Two(rec, 50, 400, 400, 600, 400);
        Two(rec, 60, 400, 450, 600, 450);
        AssertReal((GestureKind.Scroll, GesturePhase.Began));
    }
}
