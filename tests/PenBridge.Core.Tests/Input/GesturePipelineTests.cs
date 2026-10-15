using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// End-to-end gestures: realistic multi-touch sequences through the phone's <see cref="InputFilter"/> (default options:
/// cursor mode, the arming delay, palm threshold), the wire format, and the Mac's <see cref="PenStateTracker"/> +
/// <see cref="GestureRecognizer"/> (ADR-0022, ADR-0023). See <see cref="GesturePipeline"/>.
/// </summary>
public class GesturePipelineTests
{
    /// <summary>Second-finger landing delays: together, quick, slow, and past the 100 ms arming delay.</summary>
    public static TheoryData<int> WithinArmDelay => new() { 0, 20, 60 };

    private readonly GesturePipeline _p = new();

    private static string[] GestureNames(IEnumerable<PipelineEvent> events) =>
        events.Where(e => !e.IsPen).Select(e => e.Name).ToArray();

    /// <summary>Event names with runs of moves and Changed collapsed (["Scroll.Began", "Scroll.Changed", "Scroll.Ended"]).</summary>
    private static string[] Collapsed(IEnumerable<PipelineEvent> events)
    {
        var names = new List<string>();
        foreach (PipelineEvent e in events)
        {
            bool run = e.Name is "Move" or "Drag" || e.Name.EndsWith(".Changed", StringComparison.Ordinal);
            if (names.Count == 0 || names[^1] != e.Name || !run)
            {
                names.Add(e.Name);
            }
        }

        return names.ToArray();
    }

    /// <summary>Two fingers land <paramref name="stagger"/> ms apart at about the content center (70 pt apart).</summary>
    private void TwoFingers(int at, int stagger, double x = 720, double y = 350, double apart = 70)
    {
        _p.Down(at, 0, x, y);
        _p.Down(at + stagger, 1, x + apart, y);
    }

    /// <summary>After any gesture, one finger still moves the cursor (no press) and a tap still clicks.</summary>
    private void AssertCursorModeStillWorks(int at)
    {
        int from = _p.Events.Count;
        _p.Down(at, 5, 300, 300);
        _p.Glide(at, 300, 200, 50);
        _p.Up(at + 310, 5);
        _p.Wait(at + 400);
        Assert.Equal(["ProximityEnter", "Move", "ProximityLeave"], Collapsed(_p.Events.Skip(from)));

        from = _p.Events.Count;
        _p.Down(at + 800, 6, 600, 400);
        _p.Up(at + 850, 6);
        _p.Wait(at + 1300);
        Assert.Equal(["ProximityEnter", "Down", "Up", "ProximityLeave"], _p.Names(from));
        Assert.False(_p.Tracker.IsInRange);
        Assert.False(_p.Tracker.Gestures.IsActive);
        Assert.Equal(0, _p.Tracker.Gestures.ContactCount);
    }

    // ---- Two fingers ----

    [Theory]
    [MemberData(nameof(WithinArmDelay))]
    public void Two_finger_scroll_and_flick_never_moves_the_cursor_and_coasts(int stagger)
    {
        TwoFingers(0, stagger);
        int moveAt = stagger + 30;
        _p.Glide(moveAt, 240, 0, 120); // slow pan down
        _p.Glide(moveAt + 240, 64, 0, -200); // flick up
        _p.Up(moveAt + 304, 0, 1);

        Assert.Empty(_p.PenEvents()); // the first finger was held, then dropped: no cursor jump, no click
        string[] names = Collapsed(_p.Events);
        Assert.Equal(["TouchDown", "Scroll.Began", "Scroll.Changed", "Scroll.Ended"], names);

        var momentum = new ScrollMomentum();
        foreach (GestureEvent g in _p.GestureEvents())
        {
            momentum.Observe(g);
        }

        Assert.True(momentum.IsCoasting);
        Assert.True(momentum.ReleaseVelocityY < 0); // the flick's direction (fingers up)

        GestureEvent[] scroll = _p.GestureEvents().Where(g => g.Kind == GestureKind.Scroll).ToArray();
        Assert.True(scroll.Take(10).Sum(g => g.DeltaY) > 0); // the slow part scrolled down first
        AssertCursorModeStillWorks(moveAt + 1000);
    }

    [Fact]
    public void Second_finger_past_the_arm_delay_moved_the_cursor_first_but_never_clicks()
    {
        TwoFingers(0, 120);
        _p.Glide(150, 240, 0, 120);
        _p.Up(400, 0, 1);

        // The lone finger armed at 100 ms: the cursor went to it (hover), and left range when the second landed.
        Assert.Equal(["ProximityEnter", "ProximityLeave", "TouchDown", "Scroll.Began", "Scroll.Changed", "Scroll.Ended"], Collapsed(_p.Events));
        Assert.DoesNotContain(_p.PenEvents(), e => e.Kind == PenEventKind.Down);
        Assert.InRange(_p.PenEvents()[0].TimestampUs, 90_000, 100_000); // the held landing sample, replayed at 100 ms
        AssertCursorModeStillWorks(1000);
    }

    [Theory]
    [MemberData(nameof(WithinArmDelay))]
    public void Two_finger_spread_is_a_pinch(int stagger)
    {
        TwoFingers(0, stagger, apart: 100);
        _p.Spread(stagger + 30, 200, 60);
        _p.Up(stagger + 240, 1);
        _p.Up(stagger + 260, 0);

        Assert.Empty(_p.PenEvents());
        Assert.Equal(["TouchDown", "Pinch.Began", "Pinch.Changed", "Pinch.Ended"], Collapsed(_p.Events));
        double scale = _p.GestureEvents().Where(g => g.Kind == GestureKind.Pinch).Aggregate(1.0, (s, g) => s * g.ScaleDelta);
        Assert.InRange(scale, 2.0, 2.4); // 100 → 220 pt
        AssertCursorModeStillWorks(1000);
    }

    [Theory]
    [MemberData(nameof(WithinArmDelay))]
    public void Two_finger_turn_is_a_rotation(int stagger)
    {
        // Fingers 140 pt apart around (760, 350), turning 45° clockwise on screen.
        _p.Down(0, 0, 690, 350);
        _p.Down(stagger, 1, 830, 350);
        _p.Animate(stagger + 30, 240, (id, _, p) =>
        {
            double a = (id == 0 ? Math.PI : 0) + (p * Math.PI / 4);
            return (760 + (70 * Math.Cos(a)), 350 + (70 * Math.Sin(a)));
        });
        _p.Up(stagger + 280, 0, 1);

        Assert.Empty(_p.PenEvents());
        Assert.Equal(["TouchDown", "Pinch.Began", "Pinch.Changed", "Pinch.Ended"], Collapsed(_p.Events));
        double rotation = _p.GestureEvents().Where(g => g.Kind == GestureKind.Pinch).Sum(g => g.RotationDelta);
        Assert.InRange(rotation, 43, 47);
        AssertCursorModeStillWorks(1000);
    }

    [Theory]
    [MemberData(nameof(WithinArmDelay))]
    public void Two_finger_tap_is_one_tap_and_no_click(int stagger)
    {
        TwoFingers(0, stagger);
        _p.Up(stagger + 60, 1); // staggered lifts
        _p.Up(stagger + 90, 0);
        _p.Wait(stagger + 90 + 299);
        Assert.Equal(["TouchDown"], _p.Names()); // held for the double-tap window

        _p.Wait(stagger + 1000);
        Assert.Equal(["TouchDown", "Tap"], _p.Names());
        Assert.Equal(GesturePhase.Ended, _p.GestureEvents()[1].Phase);
        AssertCursorModeStillWorks(2000);
    }

    [Fact]
    public void Two_finger_tap_past_the_arm_delay_still_taps_without_a_click()
    {
        TwoFingers(0, 120);
        _p.Up(190, 0, 1);
        _p.Wait(1000);
        Assert.Equal(["ProximityEnter", "ProximityLeave", "TouchDown", "Tap"], _p.Names());
        AssertCursorModeStillWorks(2000);
    }

    [Theory]
    [MemberData(nameof(WithinArmDelay))]
    public void Two_finger_double_tap_is_a_smart_zoom(int stagger)
    {
        TwoFingers(0, stagger);
        _p.Up(stagger + 60, 0, 1);
        TwoFingers(stagger + 180, stagger, x: 725, y: 352);
        _p.Up(stagger + 180 + stagger + 60, 1, 0);
        _p.Wait(1500);

        Assert.Empty(_p.PenEvents());
        Assert.Equal(["TouchDown", "TouchDown", "SmartZoom"], _p.Names());
        AssertCursorModeStillWorks(2000);
    }

    [Fact]
    public void Cursor_movement_right_after_a_two_finger_tap_fires_the_tap_first()
    {
        TwoFingers(0, 20);
        _p.Up(80, 0, 1);
        _p.Down(150, 5, 300, 300);
        _p.Glide(150, 200, 100, 0);
        _p.Up(360, 5);
        _p.Wait(1000);

        // The tap is a right-click at the cursor: it must come before the cursor moves (ADR-0023).
        Assert.Equal(["TouchDown", "Tap", "ProximityEnter", "Move", "ProximityLeave"], Collapsed(_p.Events));
    }

    [Fact]
    public void One_finger_tap_right_after_a_two_finger_tap_fires_the_tap_before_the_click()
    {
        TwoFingers(0, 20);
        _p.Up(80, 0, 1);
        _p.Down(150, 5, 300, 300);
        _p.Up(200, 5);
        _p.Wait(1000);

        // Right-click where the cursor was, then the left click at the finger (not a right-click at the click's point).
        Assert.Equal(["TouchDown", "Tap", "ProximityEnter", "Down", "Up", "ProximityLeave"], _p.Names());
    }

    [Fact]
    public void Second_finger_during_a_double_tap_hold_makes_a_gesture_without_a_click()
    {
        _p.Down(0, 0, 400, 300);
        _p.Up(50, 0); // a one-finger tap: click
        int from = _p.Events.Count;
        _p.Down(150, 1, 402, 301); // the second touch of a double tap: held up to 200 ms
        _p.Down(250, 2, 470, 301); // past the arming delay, still inside the hold
        _p.Glide(280, 240, 0, 120);
        _p.Up(530, 1, 2);
        _p.Wait(1000);

        Assert.Equal(["ProximityEnter", "Down", "Up", "ProximityLeave"], _p.Names()[..from]);
        Assert.Equal(["TouchDown", "Scroll.Began", "Scroll.Changed", "Scroll.Ended"], Collapsed(_p.Events.Skip(from)));
        AssertCursorModeStillWorks(2000);
    }

    [Fact]
    public void Second_finger_during_a_double_tap_drag_releases_the_button_then_scrolls()
    {
        _p.Down(0, 0, 400, 300);
        _p.Up(50, 0);
        _p.Down(150, 1, 402, 301); // double-tap-hold: a drag once it stays down for the tap duration
        _p.Wait(360);
        Assert.True(_p.Tracker.IsInContact);
        int from = _p.Events.Count;

        _p.Down(400, 2, 470, 301);
        Assert.False(_p.Tracker.IsInContact);
        _p.Glide(420, 240, 0, 120);
        _p.Up(670, 1, 2);

        Assert.Equal(["Up", "ProximityLeave", "TouchDown", "Scroll.Began", "Scroll.Changed", "Scroll.Ended"], Collapsed(_p.Events.Skip(from)));
        AssertCursorModeStillWorks(2000);
    }

    // ---- Three and four fingers ----

    [Fact]
    public void Three_finger_drag_with_staggered_landings_and_lifts()
    {
        _p.Down(0, 0, 650, 350);
        _p.Down(20, 1, 720, 330);
        _p.Down(60, 2, 790, 350);
        _p.Glide(90, 300, 150, 40);
        _p.Up(400, 2);
        _p.Up(430, 0);
        _p.Up(470, 1);
        _p.Wait(1200);

        Assert.Empty(_p.PenEvents());
        Assert.Equal(["TouchDown", "TouchDown", "Drag.Began", "Drag.Changed", "Drag.Ended"], Collapsed(_p.Events));
        GestureEvent[] drag = _p.GestureEvents().Where(g => g.Kind == GestureKind.Drag).ToArray();
        Assert.InRange(drag.Sum(g => g.DeltaX), 140, 400); // accelerated, relative
        AssertCursorModeStillWorks(2000);
    }

    [Fact]
    public void Four_finger_swipe_up_with_staggered_landings_and_lifts()
    {
        _p.Down(0, 0, 600, 450);
        _p.Down(15, 1, 680, 420);
        _p.Down(40, 2, 760, 420);
        _p.Down(70, 3, 840, 450);
        _p.Glide(100, 250, 0, -200);
        _p.Up(360, 0);
        _p.Up(380, 3, 1);
        _p.Up(400, 2);
        _p.Wait(1200);

        Assert.Empty(_p.PenEvents());
        Assert.Equal(["TouchDown", "TouchDown", "TouchDown", "SystemSwipe.Began", "SystemSwipe.Changed", "SystemSwipe.Ended"], Collapsed(_p.Events));
        GestureEvent end = _p.GestureEvents()[^1];
        Assert.Equal(GestureDirection.Up, end.Direction);
        Assert.InRange(end.Progress, 0.2, 0.35);
        AssertCursorModeStillWorks(2000);
    }

    [Fact]
    public void Four_finger_pinch_in_with_staggered_landings()
    {
        _p.Down(0, 0, 600, 200);
        _p.Down(25, 1, 900, 200);
        _p.Down(45, 2, 600, 500);
        _p.Down(80, 3, 900, 500);
        _p.Spread(110, 250, -150);
        _p.Up(370, 2, 0, 3, 1);
        _p.Wait(1200);

        Assert.Empty(_p.PenEvents());
        Assert.Equal(["TouchDown", "TouchDown", "TouchDown", "SystemPinch.Began", "SystemPinch.Changed", "SystemPinch.Ended"], Collapsed(_p.Events));
        Assert.Equal(GestureDirection.In, _p.GestureEvents()[^1].Direction);
        AssertCursorModeStillWorks(2000);
    }

    // ---- Palm ----

    [Fact(Skip = "Open question (needs a wire signal): the filter drops the palm-suppressed touches, which the Mac reads as a quick two-finger lift, a Tap")]
    public void Palm_landing_right_after_two_fingers_is_no_right_click()
    {
        TwoFingers(0, 20);
        _p.Down(40, 2, 900, 600, GesturePipeline.PalmSize);
        _p.Up(400, 2, 0, 1);
        _p.Wait(1000);

        Assert.Equal(["TouchDown"], _p.Names());
    }

    [Fact(Skip = "Open question (needs a wire signal): a canceled two-finger touch reaches the Mac as a quick lift, a Tap")]
    public void Platform_cancel_right_after_two_fingers_is_no_right_click()
    {
        TwoFingers(0, 20);
        _p.CancelAll(50);
        _p.Wait(1000);

        Assert.Equal(["TouchDown"], _p.Names());
    }

    [Fact]
    public void Platform_cancel_mid_scroll_ends_it_and_cursor_mode_recovers()
    {
        TwoFingers(0, 20);
        _p.Glide(50, 200, 0, 120);
        _p.CancelAll(260);
        _p.Wait(1000);

        Assert.Empty(_p.PenEvents());
        Assert.Equal(["TouchDown", "Scroll.Began", "Scroll.Changed", "Scroll.Ended"], Collapsed(_p.Events));
        AssertCursorModeStillWorks(2000);
    }

    [Fact]
    public void Palm_landing_among_moving_fingers_stops_any_gesture_from_starting()
    {
        TwoFingers(0, 20);
        _p.Wait(230); // past the two-finger tap duration: the palm's cut can't look like a tap
        _p.Down(230, 2, 900, 600, GesturePipeline.PalmSize);
        _p.Glide(240, 60, 0, 150);
        _p.Up(310, 2); // palm lifts first: the fingers stay suppressed until they lift
        _p.Glide(320, 200, 0, -150);
        _p.Up(530, 0, 1);
        _p.Wait(1000);

        Assert.Empty(_p.PenEvents());
        Assert.Equal(["TouchDown"], _p.Names()); // only the landing; never a scroll, tap or click
        AssertCursorModeStillWorks(2000);
    }

    [Fact]
    public void Resting_palm_then_fingers_is_nothing()
    {
        _p.Down(0, 2, 900, 600, GesturePipeline.PalmSize);
        TwoFingers(100, 20);
        _p.Glide(150, 240, 0, 150);
        _p.Up(400, 0, 1);
        _p.Up(420, 2);
        _p.Wait(1000);

        Assert.Empty(_p.Events);
        AssertCursorModeStillWorks(2000);
    }

    [Fact]
    public void Palm_landing_mid_scroll_ends_the_scroll()
    {
        TwoFingers(0, 20);
        _p.Glide(50, 200, 0, 120);
        Assert.True(_p.Tracker.Gestures.IsActive);
        _p.Down(260, 2, 900, 600, GesturePipeline.PalmSize);
        _p.Glide(270, 100, 0, 60);
        _p.Up(380, 2, 0, 1);
        _p.Wait(1000);

        Assert.Empty(_p.PenEvents());
        Assert.Equal(["TouchDown", "Scroll.Began", "Scroll.Changed", "Scroll.Ended"], Collapsed(_p.Events));
        AssertCursorModeStillWorks(2000);
    }

    // ---- Pen ----

    [Fact]
    public void Pen_in_range_mid_scroll_cancels_it_before_the_pen_events()
    {
        TwoFingers(0, 20);
        _p.Glide(50, 200, 0, 120);
        int from = _p.Events.Count;
        _p.Pen(260, Samples.Hover(20000, 20000));
        _p.Glide(270, 100, 0, 60); // fingers still moving under the pen: suppressed
        _p.Pen(380, Samples.Touch(21000, 20000));
        _p.Pen(400, Samples.Hover(21000, 20000));
        _p.Pen(420, null);
        _p.Glide(430, 400, 0, -60); // past the pen suppression: still blocked until they lift
        _p.Up(850, 0, 1);
        _p.Wait(1200);

        Assert.Equal(["Scroll.Cancelled", "ProximityEnter", "Down", "Up", "ProximityLeave"], _p.Names(from));
        AssertCursorModeStillWorks(2000);
    }

    [Fact]
    public void Pen_in_range_during_a_held_tap_fires_the_tap_first()
    {
        TwoFingers(0, 20);
        _p.Up(80, 0, 1);
        _p.Pen(150, Samples.Hover(20000, 20000));
        _p.Pen(170, null);
        _p.Wait(1000);

        Assert.Equal(["TouchDown", "Tap", "ProximityEnter", "ProximityLeave"], _p.Names());
    }
}
