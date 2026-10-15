using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// Withdrawn touches on the Mac (ADR-0024): a present touch with <c>Contact=false</c> cancels the gesture (no Tap, no
/// momentum) and blocks gestures until every touch present then is gone.
/// </summary>
public class GestureWithdrawnTests
{
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));
    private readonly OrderedSink _sink = new();
    private readonly GestureRecognizer _rec;
    private long _t;

    public GestureWithdrawnTests()
    {
        _rec = new GestureRecognizer(_sink) { Mapper = Square };
    }

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    private static TouchSample F(byte id, double x, double y) => new(id, true, N(x), N(y));

    private static TouchSample W(byte id, double x, double y) => new(id, false, N(x), N(y));

    private void Frame(params TouchSample[] touches) => Frame(false, touches);

    private void Frame(bool pen, params TouchSample[] touches)
    {
        _t += 10_000;
        _rec.Process(touches, pen, _t);
    }

    private string[] Names() => _sink.Events.Select(e => e.Name).ToArray();

    private void Scroll(byte a, byte b, int frames, double fromY = 300)
    {
        for (int i = 0; i < frames; i++)
        {
            double y = fromY + (i * 30);
            Frame(F(a, 400, y), F(b, 600, y));
        }
    }

    [Fact]
    public void Withdrawn_tap_is_no_tap()
    {
        Frame(F(1, 400, 500), F(2, 600, 500));
        Frame(W(1, 400, 500), W(2, 600, 500)); // a palm landed: the phone withdrew both
        Frame();
        _t += 1_000_000;
        _rec.CheckDeadline(_t);
        Frame();

        Assert.Equal(["TouchDown"], Names());
        Assert.Null(_rec.PendingDeadlineUs);
    }

    [Fact]
    public void Withdrawn_scroll_is_cancelled_not_ended()
    {
        Scroll(1, 2, 4);
        Assert.True(_rec.IsActive);
        Frame(W(1, 400, 400), W(2, 600, 400));
        Assert.False(_rec.IsActive);
        Frame(W(1, 400, 450), W(2, 600, 450)); // still moving under the palm: nothing
        Frame();

        Assert.Equal(GesturePhase.Cancelled, _sink.Events[^1].Gesture.Phase);
        Assert.DoesNotContain(_sink.Events, e => e.Gesture.Phase == GesturePhase.Ended);
    }

    [Fact]
    public void Contacts_present_at_a_withdrawal_stay_blocked_until_they_lift()
    {
        Frame(F(1, 400, 500), F(2, 600, 500));
        Frame(F(1, 400, 500), W(2, 600, 500)); // finger 2 canceled by the platform
        Frame(F(1, 400, 500)); // 2 gone; 1 was there: still blocked
        Frame(F(1, 400, 500), F(3, 600, 500)); // a new finger with a blocked one: no TouchDown, no gesture
        Scroll(1, 3, 6, 500);
        Assert.Equal(["TouchDown"], Names());

        Frame();
        Frame(F(4, 400, 500), F(5, 600, 500)); // all lifted: new fingers work
        Scroll(4, 5, 4, 500);
        Assert.Equal(["TouchDown", "TouchDown", "Scroll.Began"], Names()[..3]);
    }

    [Fact]
    public void Withdrawn_touches_vanishing_without_an_empty_frame_unblock_new_fingers()
    {
        Frame(F(1, 400, 500), F(2, 600, 500));
        Frame(W(1, 400, 500), W(2, 600, 500)); // ACTION_CANCEL: the last frame the phone sends
        Frame(F(7, 400, 500), F(8, 600, 500)); // next touch, much later
        Scroll(7, 8, 4, 500);

        Assert.Equal(["TouchDown", "TouchDown", "Scroll.Began"], Names()[..3]);
    }

    [Fact]
    public void Withdrawn_id_back_in_contact_is_a_new_finger()
    {
        Frame(F(0, 400, 500), F(1, 600, 500));
        Frame(W(0, 400, 500), W(1, 600, 500));
        Frame(F(0, 400, 500), F(1, 600, 500)); // Android restarts ids after a cancel
        Frame();
        _t += 1_000_000;
        _rec.CheckDeadline(_t);

        Assert.Equal(["TouchDown", "TouchDown", "Tap"], Names());
    }

    [Fact]
    public void Finger_landing_while_a_touch_is_withdrawn_stays_blocked_until_it_lifts()
    {
        Frame(F(1, 400, 500), F(2, 600, 500));
        Frame(W(1, 400, 500), W(2, 600, 500));
        Frame(W(1, 400, 500), F(3, 700, 500)); // 2 lifted, 3 landed during the withdrawal: no TouchDown
        Frame(F(3, 700, 500)); // 1 lifted: 3 is still blocked
        Frame(F(3, 700, 500), F(4, 800, 500));
        Assert.Equal(["TouchDown"], Names());
    }

    [Fact]
    public void Tap_held_from_an_earlier_sequence_still_fires_first()
    {
        Frame(F(1, 400, 500), F(2, 600, 500));
        Frame(); // tap held for the double-tap window
        Frame(F(1, 400, 500), F(2, 600, 500));
        Frame(W(1, 400, 500), W(2, 600, 500)); // second sequence withdrawn: no smart zoom, the first tap happened
        Frame();

        Assert.Equal(["TouchDown", "TouchDown", "Tap"], Names());
    }

    [Fact]
    public void Pen_after_a_withdrawal_keeps_gestures_blocked_until_the_touches_are_gone()
    {
        Frame(F(1, 400, 500), F(2, 600, 500));
        Frame(W(1, 400, 500), W(2, 600, 500));
        _rec.PenEntering(_t);
        Frame(pen: true);
        Frame(W(1, 400, 500), W(2, 600, 500), F(3, 500, 300)); // pen left; the withdrawn touches are still there
        Frame(W(2, 600, 500), F(3, 500, 300));
        Frame();
        Frame(F(4, 400, 500), F(5, 600, 500));

        Assert.Equal(["TouchDown", "TouchDown"], Names());
    }

    [Fact]
    public void Old_phone_stream_without_withdrawn_touches_behaves_as_before()
    {
        // A 1.0.0 phone drops suppressed touches: the Mac still reads a quick lift as a tap (the pre-ADR-0024 result).
        Frame(F(1, 400, 500), F(2, 600, 500));
        Frame();
        _t += 1_000_000;
        _rec.CheckDeadline(_t);

        Scroll(3, 4, 4);
        Frame();
        Assert.Equal(["TouchDown", "Tap", "TouchDown", "Scroll.Began"], Names()[..4]);
        Assert.Equal("Scroll.Ended", Names()[^1]);
    }

    [Fact]
    public void Withdrawal_is_allocation_free()
    {
        var rec = new GestureRecognizer(new OrderedNullSink()) { Mapper = Square };
        TouchSample[] two = [F(1, 400, 500), F(2, 600, 500)];
        TouchSample[] moved = [F(1, 400, 600), F(2, 600, 600)];
        TouchSample[] gone = [W(1, 400, 600), W(2, 600, 600)];
        TouchSample[] reused = [F(1, 400, 500), F(2, 600, 500)];
        TouchSample[] none = [];
        long t = 0;
        long blocked = 0;

        Alloc.AssertZero(2_000, i =>
        {
            t += 10_000;
            TouchSample[] state = (i % 6) switch
            {
                0 => two,
                1 => moved, // scroll
                2 => gone, // cancelled
                3 => reused, // a new finger pair on the same ids
                4 => gone,
                _ => none,
            };
            rec.Process(state, false, t);
            if (i % 6 == 2 && !rec.IsActive)
            {
                blocked++;
            }
        }, () => blocked);
    }

    private sealed class OrderedNullSink : IInputSink
    {
        public void OnPenEvent(in PenEvent e)
        {
        }

        public void OnGestureEvent(in GestureEvent e)
        {
        }
    }
}
