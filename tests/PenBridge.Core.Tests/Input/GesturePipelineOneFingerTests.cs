using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// End-to-end relative cursor (ADR-0026): realistic one-finger sequences through the phone's <see cref="InputFilter"/>
/// with <see cref="InputFilterOptions.FingerTouches"/>, the wire format and the Mac's recognizer with
/// <see cref="GestureRecognizerOptions.OneFingerCursor"/>. No pen events: the finger never goes out as a pen sample.
/// </summary>
public class GesturePipelineOneFingerTests
{
    private readonly GesturePipeline _p = GesturePipeline.FingerTouches();

    /// <summary>Gesture names without TouchDown, runs of Changed collapsed; Clicks as "Click1", "Click2", ….</summary>
    private string[] Names(int from = 0)
    {
        var names = new List<string>();
        foreach (PipelineEvent e in _p.Events.Skip(from))
        {
            if (e.IsPen)
            {
                names.Add(e.Name);
                continue;
            }

            if (e.Gesture.Kind == GestureKind.TouchDown)
            {
                continue;
            }

            string name = e.Gesture.Kind == GestureKind.Click ? "Click" + e.Gesture.ClickCount : e.Name;
            if (names.Count == 0 || names[^1] != name || !name.EndsWith(".Changed", StringComparison.Ordinal))
            {
                names.Add(name);
            }
        }

        return [.. names];
    }

    [Fact]
    public void A_glide_moves_the_cursor_relatively_by_about_the_finger_travel()
    {
        _p.Down(0, 0, 300, 300);
        _p.Glide(0, 300, 200, 0); // slow: about 670 pt/s
        _p.Up(310, 0);
        _p.Wait(400);

        Assert.Equal(["PointerMove.Began", "PointerMove.Changed", "PointerMove.Ended"], Names());
        double dx = _p.GestureEvents().Sum(g => g.DeltaX);
        Assert.InRange(dx, 198, 200 * 1.2); // nothing lost to the slop
        Assert.Empty(_p.PenEvents());
    }

    [Fact]
    public void A_tap_clicks_and_a_double_tap_double_clicks_with_no_delay()
    {
        _p.Down(0, 0, 600, 400);
        _p.Up(60, 0);
        Assert.Equal(["Click1"], Names()); // at the lift, nothing held
        _p.Down(250, 1, 604, 401);
        _p.Up(320, 1);
        _p.Wait(1000);
        Assert.Equal(["Click1", "Click2"], Names());
        Assert.Empty(_p.PenEvents());
    }

    [Fact]
    public void Tap_and_a_half_is_a_drag_released_on_lift()
    {
        _p.Down(0, 0, 600, 400);
        _p.Up(60, 0);
        _p.Down(200, 1, 600, 400);
        _p.Glide(200, 300, 150, 80);
        _p.Up(510, 1);
        _p.Wait(1000);
        Assert.Equal(["Click1", "PointerDrag.Began", "PointerDrag.Changed", "PointerDrag.Ended"], Names());
    }

    [Fact]
    public void A_second_finger_ends_the_move_and_scrolls()
    {
        _p.Down(0, 0, 400, 300);
        _p.Glide(0, 100, 80, 0);
        _p.Down(110, 1, 560, 300);
        _p.Glide(110, 300, 0, 200);
        _p.Up(420, 0, 1);
        _p.Wait(2000);
        string[] names = Names();
        Assert.Equal(["PointerMove.Began", "PointerMove.Changed", "PointerMove.Ended", "Scroll.Began"], names[..4]);
        Assert.Equal("Scroll.Ended", names[^1]);
    }

    [Fact]
    public void A_two_finger_tap_is_still_a_right_click_and_no_click()
    {
        _p.Down(0, 0, 600, 400);
        _p.Down(30, 1, 680, 400);
        _p.Up(120, 0, 1);
        _p.Wait(1000);
        Assert.Equal(["Tap"], Names());
    }

    [Fact]
    public void A_palm_cancels_a_move_and_the_pen_cancels_a_drag()
    {
        _p.Down(0, 0, 400, 300);
        _p.Glide(0, 100, 80, 0);
        _p.Down(110, 1, 800, 500, GesturePipeline.PalmSize);
        _p.Up(150, 0, 1);
        _p.Wait(600);
        Assert.Equal(["PointerMove.Began", "PointerMove.Changed", "PointerMove.Cancelled"], Names());

        int from = _p.Events.Count;
        _p.Down(1000, 2, 600, 400);
        _p.Up(1060, 2);
        _p.Down(1200, 3, 600, 400);
        _p.Glide(1200, 100, 100, 0);
        _p.Pen(1310, new PenSample(30000, 30000, 0, 0, 0, 0, PenTool.Pen, PenState.InRange, PenButtons.None));
        string[] names = Names(from);
        Assert.Equal(["Click1", "PointerDrag.Began", "PointerDrag.Changed", "PointerDrag.Cancelled", "ProximityEnter"], names);
    }

    [Fact]
    public void A_link_loss_releases_a_drag_through_the_watchdog()
    {
        _p.Down(0, 0, 600, 400);
        _p.Up(60, 0);
        _p.Down(200, 1, 600, 400);
        _p.Glide(200, 100, 100, 0);
        _p.Tracker.Reset(); // what the watchdog or a session end does
        GestureEvent last = _p.GestureEvents()[^1];
        Assert.Equal(GestureKind.PointerDrag, last.Kind);
        Assert.Equal(GesturePhase.Cancelled, last.Phase);
        Assert.True(last.Synthetic);
    }
}
