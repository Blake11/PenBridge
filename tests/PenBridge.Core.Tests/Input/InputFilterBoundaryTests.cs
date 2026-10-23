using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// Exact limits and state clearing of <see cref="InputFilter"/>: palm by size, the tap and double-tap slops and window
/// at their limits, what a gesture, a palm and <see cref="InputFilter.Reset"/> forget, and the finger mode chosen with
/// no arming delay.
/// </summary>
public class InputFilterBoundaryTests
{
    private const PenState Hover = PenState.InRange;
    private const PenState Contact = PenState.InRange | PenState.Contact;

    // Slops whose squared size in normalized units, (slop · 65535)² truncated, is exactly dx² + dy² for these offsets.
    private const float ExactTapSlop = 0.019808f; // 1685108 = 1268² + 278²
    private const int TapDx = 1268;
    private const int TapDy = 278;
    private const float ExactDoubleTapSlop = 0.049801f; // 10651793 = 3263² + 68²
    private const int DoubleDx = 3263;
    private const int DoubleDy = 68;

    private static readonly InputFilterOptions Raw = new() { FingerFallbackEnabled = false };
    private static readonly InputFilterOptions DrawMode = new() { FingerMode = FingerFallbackMode.Draw };

    private readonly TouchSample[] _out = new TouchSample[InputFilter.MaxTouches];

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static RawTouch Finger(byte id, int x = 10000, int y = 20000, float size = 0.05f, bool canceled = false) =>
        new(id, true, canceled, (ushort)x, (ushort)y, size);

    private InputFilterResult At(InputFilter f, double ms, params RawTouch[] touches) => f.Process(Ms(ms), null, touches, _out);

    /// <summary>A cursor-mode tap at (x, y) from <paramref name="ms"/>, lifting 50 ms later: a click.</summary>
    private void CursorTap(InputFilter f, double ms, int x = 10000, int y = 20000)
    {
        At(f, ms, Finger(1, x, y));
        InputFilterResult click = At(f, ms + 50);
        Assert.Equal(2, click.ReplayCount);
        Assert.Equal(Contact, f.ReplaySamples[1].State);
    }

    // --- Palm by size ---

    [Fact]
    public void Two_palm_sized_contacts_landing_together_are_a_palm()
    {
        var f = new InputFilter(Raw);
        InputFilterResult r = At(f, 0, Finger(1, size: 0.4f), Finger(2, 30000, size: 0.4f));
        Assert.Equal(0, r.TouchCount);
        Assert.Equal(0, r.ContactCount);
        Assert.False(r.HasPen);
    }

    [Fact]
    public void A_contact_of_unknown_size_is_never_a_palm_even_at_a_zero_threshold()
    {
        var f = new InputFilter(Raw with { PalmSizeThreshold = 0f });
        InputFilterResult r = At(f, 0, Finger(1, size: 0f));
        Assert.Equal(1, r.ContactCount);
        Assert.Equal(new TouchSample(1, true, 10000, 20000), _out[0]);

        // Any known size is at or above a zero threshold: a palm.
        Assert.Equal(0, At(f, 10, Finger(1, size: 0f), Finger(2, 30000, size: 0.001f)).ContactCount);
    }

    // --- What a gesture, a palm and Reset forget ---

    [Fact]
    public void A_two_finger_gesture_forgets_the_tap_before_it()
    {
        var f = new InputFilter();
        CursorTap(f, 0);
        At(f, 100, Finger(2, 30000), Finger(3, 40000)); // land together: a gesture
        At(f, 150);

        At(f, 200, Finger(4)); // where the tap was, within its window
        Assert.Equal(Ms(300), f.PendingDeadline); // a plain hold (arming delay), not a double tap's second touch (tap duration)
    }

    [Fact]
    public void A_palm_drops_the_held_finger_so_a_later_finger_replays_nothing_of_it()
    {
        var f = new InputFilter(DrawMode);
        Assert.Equal(default, At(f, 0, Finger(1)));
        At(f, 50, Finger(1), Finger(2, 30000, size: 0.4f)); // palm lands while finger 1 is held
        Assert.Null(f.PendingDeadline);
        At(f, 60);

        InputFilterResult r = At(f, 300, Finger(3, 40000)); // after the palm window: a fresh hold
        Assert.Equal(default, r);
        Assert.Equal(Ms(400), f.PendingDeadline);
    }

    [Fact]
    public void Reset_forgets_sent_touches_so_they_are_not_withdrawn()
    {
        var f = new InputFilter(Raw);
        Assert.Equal(2, At(f, 0, Finger(1), Finger(2, 30000)).ContactCount);
        f.Reset();

        InputFilterResult r = At(f, 10, Finger(1, canceled: true), Finger(2, 30000, canceled: true));
        Assert.Equal(0, r.TouchCount); // never sent since the reset: nothing to withdraw
    }

    [Fact]
    public void Reset_forgets_the_pen_so_touches_are_not_suppressed()
    {
        var f = new InputFilter(Raw);
        f.Process(Ms(0), Samples.Hover(100, 200), [], _out);
        f.Reset();
        Assert.Equal(1, At(f, 10, Finger(1)).ContactCount);
    }

    [Fact]
    public void Reset_forgets_a_palm_still_down()
    {
        var f = new InputFilter(Raw);
        At(f, 0, Finger(1, size: 0.4f));
        f.Reset();
        Assert.Equal(1, At(f, 10, Finger(2, 30000)).ContactCount);
    }

    [Fact]
    public void Reset_empties_the_replay()
    {
        var f = new InputFilter(DrawMode);
        At(f, 0, Finger(1));
        Assert.Equal(1, At(f, 30).ReplayCount);
        f.Reset();
        Assert.Equal(0, f.ReplaySamples.Length);
        Assert.Null(f.PendingDeadline);
    }

    // --- Double tap window and slop exactly at the limit ---

    [Theory]
    [InlineData(350, 0, 0, true)] // lands exactly DoubleTapWindow (300 ms) after the tap lifted
    [InlineData(351, 0, 0, false)]
    [InlineData(150, DoubleDx, DoubleDy, true)] // exactly DoubleTapSlop away
    [InlineData(150, DoubleDx + 1, DoubleDy, false)]
    public void Double_tap_window_and_slop_include_their_limit(int downMs, int dx, int dy, bool second)
    {
        var f = new InputFilter(new InputFilterOptions { DoubleTapSlop = ExactDoubleTapSlop });
        CursorTap(f, 0); // lifts at 50 ms

        Assert.Equal(default, At(f, downMs, Finger(2, 10000 + dx, 20000 + dy)));
        // A double tap's second touch is held for the tap duration (200 ms), a plain touch for the arming delay (100 ms).
        Assert.Equal(Ms(downMs + (second ? 200 : 100)), f.PendingDeadline);
    }

    // --- Tap slop exactly at the limit ---

    [Theory]
    [InlineData(TapDx, TapDy, true)]
    [InlineData(TapDx + 1, TapDy, false)]
    public void Tap_slop_includes_its_limit(int dx, int dy, bool click)
    {
        var f = new InputFilter(new InputFilterOptions { TapSlop = ExactTapSlop });
        At(f, 0, Finger(1));
        At(f, 40, Finger(1, 10000 + dx, 20000 + dy));
        InputFilterResult lift = At(f, 60);

        Assert.Equal(PenState.None, lift.Pen.State);
        PenState[] states = f.ReplaySamples.ToArray().Select(s => s.State).ToArray();
        Assert.Equal(click ? [Hover, Contact] : [Hover, Hover], states);
    }

    // --- Finger mode with no arming delay ---

    [Fact]
    public void Without_an_arming_delay_a_cursor_finger_moves_at_once_and_its_slow_lift_ends_the_pointer()
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackArmDelay = TimeSpan.Zero });
        InputFilterResult first = At(f, 0, Finger(1));
        Assert.True(first.HasPen);
        Assert.Equal(new PenSample(10000, 20000, 0, 0, 0, 0, PenTool.FingerFallback, Hover, PenButtons.None), first.Pen);
        Assert.Equal(0, first.ReplayCount);
        Assert.Null(f.PendingDeadline);

        Assert.Equal(Hover, At(f, 100, Finger(1, 15000)).Pen.State);
        InputFilterResult lift = At(f, 300);
        Assert.True(lift.HasPen);
        Assert.Equal(new PenSample(15000, 20000, 0, 0, 0, 0, PenTool.FingerFallback, PenState.None, PenButtons.None), lift.Pen);
    }

    [Fact]
    public void Without_an_arming_delay_a_drawing_finger_strokes_at_once_and_its_lift_sends_no_pen()
    {
        var f = new InputFilter(DrawMode with { FingerFallbackArmDelay = TimeSpan.Zero, FingerFallbackPressure = 1234 });
        InputFilterResult first = At(f, 0, Finger(1));
        Assert.True(first.HasPen);
        Assert.Equal(new PenSample(10000, 20000, 1234, 0, 0, 0, PenTool.FingerFallback, Contact, PenButtons.None), first.Pen);
        Assert.Null(f.PendingDeadline);

        Assert.Equal(Contact, At(f, 100, Finger(1, 15000)).Pen.State);
        Assert.Equal(default, At(f, 300)); // the stroke ends when the pen sample disappears
    }

    [Fact]
    public void A_double_tap_second_touch_holds_for_the_arming_delay_when_it_exceeds_the_tap_duration()
    {
        var f = new InputFilter(new InputFilterOptions { TapMaxDuration = Ms(50), FingerFallbackArmDelay = Ms(100) });
        At(f, 0, Finger(1));
        Assert.Equal(2, At(f, 30).ReplayCount); // a click
        At(f, 60, Finger(2));
        Assert.Equal(Ms(160), f.PendingDeadline);
    }

    // --- Draw mode tap replay ---

    [Fact]
    public void Draw_mode_taps_replay_the_held_touch_and_snap_follow_ups_to_the_first_landing()
    {
        var f = new InputFilter(DrawMode with { FingerFallbackPressure = 1000 });
        At(f, 0, Finger(1, 10000));
        At(f, 20, Finger(1, 10100)); // within the tap slop
        InputFilterResult first = At(f, 40);
        Assert.Equal(
            [new PenSample(10000, 20000, 1000, 0, 0, 0, PenTool.FingerFallback, Contact, PenButtons.None),
             new PenSample(10100, 20000, 1000, 0, 0, 0, PenTool.FingerFallback, Contact, PenButtons.None)],
            f.ReplaySamples.ToArray());
        Assert.Equal(new PenSample(10100, 20000, 0, 0, 0, 0, PenTool.FingerFallback, PenState.None, PenButtons.None), first.Pen);

        // The second tap lands near: every sample goes to where the first one landed (not where it lifted).
        At(f, 140, Finger(2, 10600, 20200));
        At(f, 160, Finger(2, 10700, 20200));
        InputFilterResult second = At(f, 180);
        Assert.Equal(2, second.ReplayCount);
        Assert.All(f.ReplaySamples.ToArray(), s => Assert.Equal((10000, 20000), (s.X, s.Y)));
        Assert.Equal((10000, 20000, PenState.None), (second.Pen.X, second.Pen.Y, second.Pen.State));

        // A third tap near that point within the window snaps too (a triple click).
        At(f, 280, Finger(3, 10400, 20100));
        InputFilterResult third = At(f, 300);
        Assert.Equal(1, third.ReplayCount);
        Assert.Equal((10000, 20000), (f.ReplaySamples[0].X, f.ReplaySamples[0].Y));
        Assert.Equal((10000, 20000), (third.Pen.X, third.Pen.Y));
    }

    // --- The drag press is nudged off the tap's point ---

    [Fact]
    public void A_drag_landing_on_a_tap_at_the_right_edge_is_nudged_inwards()
    {
        var f = new InputFilter();
        CursorTap(f, 0, ushort.MaxValue);
        At(f, 150, Finger(2, ushort.MaxValue));
        InputFilterResult drag = At(f, 350, Finger(2, ushort.MaxValue)); // held for the tap duration: a drag
        Assert.Equal(Contact, drag.Pen.State);
        Assert.Equal(1, drag.ReplayCount);
        Assert.Equal(((ushort)(ushort.MaxValue - 1), (ushort)20000), (f.ReplaySamples[0].X, f.ReplaySamples[0].Y));
        Assert.Equal(ushort.MaxValue, drag.Pen.X);
    }

    [Theory]
    [InlineData(10000, 20300)] // same x, other y
    [InlineData(10300, 20000)] // same y, other x
    public void A_drag_landing_off_the_tap_point_on_one_axis_is_not_nudged(int x, int y)
    {
        var f = new InputFilter();
        CursorTap(f, 0);
        At(f, 150, Finger(2, x, y));
        At(f, 350, Finger(2, x, y));
        Assert.Equal(((ushort)x, (ushort)y), (f.ReplaySamples[0].X, f.ReplaySamples[0].Y));
        Assert.Equal(Contact, f.ReplaySamples[0].State);
    }
}
