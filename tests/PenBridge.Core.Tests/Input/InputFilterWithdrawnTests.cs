using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// Withdrawn touches (ADR-0024): a touch already sent that the filter then suppresses stays in the result as present
/// with <c>Contact=false</c> until that pointer lifts.
/// </summary>
[Collection(AllocationGroup.Name)]
public class InputFilterWithdrawnTests
{
    private readonly TouchSample[] _out = new TouchSample[InputFilter.MaxTouches];

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static RawTouch Finger(byte id, ushort x = 1000, ushort y = 2000, bool canceled = false) => new(id, true, canceled, x, y, 0.05f);

    private static RawTouch Palm(byte id) => new(id, true, false, 30000, 40000, 0.4f);

    private TouchSample[] Touches(InputFilterResult r)
    {
        Assert.False(r.HasPen);
        return _out.AsSpan(0, r.TouchCount).ToArray();
    }

    [Fact]
    public void Palm_withdraws_the_sent_touches_until_each_lifts()
    {
        var f = new InputFilter();
        f.Process(Ms(0), null, [Finger(1, 1000)], _out); // held: never sent
        Assert.Equal(2, f.Process(Ms(20), null, [Finger(1, 1000), Finger(2, 5000)], _out).TouchCount);

        // The palm itself is never reported; the two sent fingers stay, not in contact, at their current position.
        Assert.Equal(
            [new TouchSample(1, false, 1100, 2000), new TouchSample(2, false, 5000, 2000)],
            Touches(f.Process(Ms(40), null, [Finger(1, 1100), Finger(2, 5000), Palm(3)], _out)));
        Assert.Equal(
            [new TouchSample(1, false, 1200, 2000), new TouchSample(2, false, 5000, 2000)],
            Touches(f.Process(Ms(60), null, [Finger(1, 1200), Finger(2, 5000)], _out))); // palm lifted

        Assert.Equal([new TouchSample(2, false, 5000, 2000)], Touches(f.Process(Ms(80), null, [Finger(2, 5000)], _out)));

        // A finger landing while suppressed was never sent: not reported.
        Assert.Equal([new TouchSample(2, false, 5000, 2000)], Touches(f.Process(Ms(90), null, [Finger(2, 5000), Finger(4)], _out)));
        Assert.Empty(Touches(f.Process(Ms(100), null, [Finger(4)], _out)));
        Assert.Empty(Touches(f.Process(Ms(110), null, [], _out)));
    }

    [Fact]
    public void Platform_cancel_of_one_sent_finger_withdraws_it_and_keeps_the_other()
    {
        var f = new InputFilter();
        f.Process(Ms(0), null, [Finger(1, 1000), Finger(2, 5000)], _out);

        // Android ACTION_POINTER_UP with FLAG_CANCELED: reported once as canceled, then gone.
        Assert.Equal(
            [new TouchSample(1, true, 1000, 2000), new TouchSample(2, false, 5000, 2000)],
            Touches(f.Process(Ms(10), null, [Finger(1, 1000), Finger(2, 5000, canceled: true)], _out)));
        Assert.Equal([new TouchSample(1, true, 1000, 2000)], Touches(f.Process(Ms(20), null, [Finger(1, 1000)], _out)));
    }

    [Fact]
    public void Platform_cancel_of_the_whole_stream_withdraws_all_then_ids_are_new()
    {
        var f = new InputFilter();
        f.Process(Ms(0), null, [Finger(0, 1000), Finger(1, 5000)], _out);

        // Android ACTION_CANCEL: every pointer once, canceled; the head sends nothing more until the next touch.
        Assert.Equal(
            [new TouchSample(0, false, 1000, 2000), new TouchSample(1, false, 5000, 2000)],
            Touches(f.Process(Ms(10), null, [Finger(0, 1000, canceled: true), Finger(1, 5000, canceled: true)], _out, cancelAll: true)));

        // Ids restart at 0: a new finger, held as a lone finger again (not suppressed, not withdrawn).
        Assert.Equal(0, f.Process(Ms(500), null, [Finger(0, 9000)], _out).TouchCount);
        Assert.NotNull(f.PendingDeadline);
        Assert.Equal(
            [new TouchSample(0, true, 9000, 2000), new TouchSample(1, true, 12000, 2000)],
            Touches(f.Process(Ms(520), null, [Finger(0, 9000), Finger(1, 12000)], _out)));
    }

    [Fact]
    public void Full_cancel_withdraws_pointers_even_without_their_canceled_flag()
    {
        var f = new InputFilter();
        f.Process(Ms(0), null, [Finger(0, 1000), Finger(1, 5000)], _out);

        InputFilterResult r = f.Process(Ms(10), null, [Finger(0, 1000), Finger(1, 5000)], _out, cancelAll: true);
        Assert.Equal([new TouchSample(0, false, 1000, 2000), new TouchSample(1, false, 5000, 2000)], Touches(r));
        Assert.Equal(0, r.ContactCount);
    }

    [Fact]
    public void Contact_count_excludes_withdrawn_touches()
    {
        var f = new InputFilter();
        InputFilterResult two = f.Process(Ms(0), null, [Finger(1, 1000), Finger(2, 5000)], _out);
        Assert.Equal(2, two.ContactCount);

        InputFilterResult one = f.Process(Ms(10), null, [Finger(1, 1000), Finger(2, 5000, canceled: true)], _out);
        Assert.Equal((2, 1), (one.TouchCount, one.ContactCount));

        InputFilterResult palm = f.Process(Ms(20), null, [Finger(1, 1000), Finger(2, 5000), Palm(3)], _out);
        Assert.Equal((2, 0), (palm.TouchCount, palm.ContactCount));

        InputFilterResult canceled = f.Process(Ms(30), null, [Finger(1, 1000), Finger(2, 5000)], _out, cancelAll: true);
        Assert.Equal((2, 0), (canceled.TouchCount, canceled.ContactCount));
    }

    [Fact]
    public void Resting_palm_tool_that_flips_to_a_finger_stays_suppressed_until_it_lifts()
    {
        // TOOL_TYPE_PALM: the head reports it as canceled on every event. That alone is no full cancel, so its
        // suppression survives; when the platform later reports the same pointer as a finger, it must not leak.
        var f = new InputFilter();
        for (int i = 0; i < 5; i++)
        {
            Assert.Empty(Touches(f.Process(Ms(i * 10), null, [Finger(0, 1000, canceled: true)], _out)));
        }

        Assert.Equal(default, f.Process(Ms(60), null, [Finger(0, 1000)], _out));
        Assert.Null(f.PendingDeadline); // not held as a new lone finger
        Assert.Equal(default, f.Process(Ms(300), null, [Finger(0, 1200)], _out));
        // Finger 1 lands beside it: a lone finger (held), not a two-finger gesture with the palm.
        Assert.Equal(default, f.Process(Ms(310), null, [Finger(0, 1200), Finger(1, 5000)], _out));
        Assert.NotNull(f.PendingDeadline);

        f.Process(Ms(320), null, [], _out); // both lift
        f.Process(Ms(330), null, [Finger(0, 1000)], _out); // a new finger on the same id
        Assert.NotNull(f.PendingDeadline);
    }

    [Fact]
    public void Resting_palm_tool_beside_sent_fingers_does_not_reset_them()
    {
        var f = new InputFilter();
        f.Process(Ms(0), null, [Finger(1, 1000), Finger(2, 5000)], _out);
        RawTouch[] withPalm = [Finger(1, 1000), Finger(2, 5000), Finger(3, 9000, canceled: true)];
        Assert.Equal(2, f.Process(Ms(10), null, withPalm, _out).ContactCount);

        // Palm tool flips to a finger: still suppressed (never sent), the others carry on.
        InputFilterResult r = f.Process(Ms(20), null, [Finger(1, 1000), Finger(2, 5000), Finger(3, 9000)], _out);
        Assert.Equal([new TouchSample(1, true, 1000, 2000), new TouchSample(2, true, 5000, 2000)], Touches(r));
    }

    [Fact]
    public void A_pointer_still_canceled_after_a_stream_cancel_stays_suppressed()
    {
        var f = new InputFilter();
        f.Process(Ms(0), null, [Finger(0, canceled: true)], _out, cancelAll: true);
        Assert.Empty(Touches(f.Process(Ms(10), null, [Finger(0, canceled: true), Finger(1)], _out)));
    }

    [Fact]
    public void Lone_cursor_finger_was_never_a_touch_so_a_palm_withdraws_nothing()
    {
        var f = new InputFilter();
        f.Process(Ms(0), null, [Finger(1)], _out);
        Assert.True(f.Process(Ms(120), null, [Finger(1, 1500)], _out).HasPen); // armed: a cursor (pen sample)
        Assert.Empty(Touches(f.Process(Ms(140), null, [Finger(1, 1500), Palm(2)], _out)));
    }

    [Fact]
    public void Touches_suppressed_after_the_pen_left_are_withdrawn_until_they_lift()
    {
        var f = new InputFilter();
        f.Process(Ms(0), null, [Finger(1, 1000), Finger(2, 5000)], _out);
        InputFilterResult pen = f.Process(Ms(10), Samples.Hover(100, 200), [Finger(1, 1000), Finger(2, 5000)], _out);
        Assert.True(pen.HasPen);
        Assert.Equal(0, pen.TouchCount); // pen samples carry no touches

        Assert.Equal(
            [new TouchSample(1, false, 1000, 2000), new TouchSample(2, false, 5000, 2000)],
            Touches(f.Process(Ms(20), null, [Finger(1, 1000), Finger(2, 5000)], _out)));
        Assert.Equal(2, f.Process(Ms(900), null, [Finger(1, 1000), Finger(2, 5000)], _out).TouchCount); // until they lift
        Assert.Empty(Touches(f.Process(Ms(910), null, [], _out)));
    }

    [Fact]
    public void Withdrawn_touches_are_bounded_by_the_destination()
    {
        var f = new InputFilter();
        var ten = new RawTouch[InputFilter.MaxTouches];
        for (int i = 0; i < ten.Length; i++)
        {
            ten[i] = Finger((byte)i, (ushort)(i * 1000));
        }

        Assert.Equal(10, f.Process(Ms(0), null, ten, _out).TouchCount);
        RawTouch[] withPalm = [.. ten, Palm(20)];
        Assert.Equal(10, f.Process(Ms(10), null, withPalm, _out).TouchCount);
        Assert.All(_out, t => Assert.False(t.Contact));
        Assert.Equal(4, f.Process(Ms(20), null, withPalm, _out.AsSpan(0, 4)).TouchCount);
    }

    [Fact]
    public void Withdrawing_is_allocation_free()
    {
        var f = new InputFilter();
        RawTouch[] two = [Finger(1, 1000), Finger(2, 5000)];
        RawTouch[] palm = [Finger(1, 1000), Finger(2, 5000), Palm(3)];
        RawTouch[] canceled = [Finger(1, 1000, canceled: true), Finger(2, 5000, canceled: true)];
        RawTouch[] none = [];
        PenSample hover = Samples.Hover(100, 200);
        long withdrawn = 0;
        long penWithdrawn = 0;
        long canceledAll = 0;
        int t = 0;

        Alloc.AssertZero(2_000, i =>
        {
            int step = i % 12;
            t += step is 6 or 11 ? 700 : 10; // past the palm and pen windows after all lifted
            RawTouch[] state = step switch
            {
                0 or 1 => two,
                2 or 3 => palm, // palm suppression: WriteWithdrawn
                4 => two, // palm lifted, still withdrawn
                5 => none, // all lift
                6 or 7 => two,
                8 => two, // with the pen in range
                9 => two, // pen left: pen suppression, WriteWithdrawn
                10 => canceled, // ACTION_CANCEL: ids are new next time
                _ => none,
            };
            PenSample? pen = step == 8 ? hover : null;
            InputFilterResult r = f.Process(Ms(t), pen, state, _out, default, cancelAll: step == 10);
            if (r.TouchCount > 0 && r.ContactCount == 0)
            {
                withdrawn++;
                penWithdrawn += step == 9 ? 1 : 0;
                canceledAll += step == 10 ? 1 : 0;
            }
        }, () => withdrawn, () => penWithdrawn, () => canceledAll);
    }
}
