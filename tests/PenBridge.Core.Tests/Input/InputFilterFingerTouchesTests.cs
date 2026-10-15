using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// Relative cursor on the phone side (ADR-0026, <see cref="InputFilterOptions.FingerTouches"/>): in cursor mode a lone
/// finger goes out as a raw touch at once; palm, pen suppression and withdrawn touches stay; draw mode is unchanged.
/// </summary>
[Collection(AllocationGroup.Name)]
public class InputFilterFingerTouchesTests
{
    private static readonly InputFilterOptions Relative = new() { FingerTouches = true };
    private readonly TouchSample[] _out = new TouchSample[InputFilter.MaxTouches];

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static RawTouch Finger(byte id, ushort x = 1000, ushort y = 2000, bool canceled = false) => new(id, true, canceled, x, y, 0.05f);

    private static RawTouch Palm(byte id) => new(id, true, false, 30000, 40000, 0.4f);

    private static PenSample PenHover() => new(500, 500, 0, 0, 0, 0, PenTool.Pen, PenState.InRange, PenButtons.None);

    private TouchSample[] Touches(InputFilterResult r)
    {
        Assert.False(r.HasPen);
        return _out.AsSpan(0, r.TouchCount).ToArray();
    }

    [Fact]
    public void A_lone_finger_is_a_raw_touch_from_its_first_event_without_holding()
    {
        var f = new InputFilter(Relative);
        InputFilterResult r = f.Process(Ms(0), null, [Finger(1, 1000)], _out);
        Assert.Equal([new TouchSample(1, true, 1000, 2000)], Touches(r));
        Assert.Equal(1, r.ContactCount);
        Assert.Equal(0, r.ReplayCount);
        Assert.Null(f.PendingDeadline);

        Assert.Equal([new TouchSample(1, true, 1500, 2000)], Touches(f.Process(Ms(8), null, [Finger(1, 1500)], _out)));
        Assert.Empty(Touches(f.Process(Ms(50), null, [], _out))); // a quick tap is no click here: the Mac decides
        Assert.Equal(0, f.ReplaySamples.Length);
    }

    [Fact]
    public void A_second_finger_joins_the_same_touch_stream()
    {
        var f = new InputFilter(Relative);
        f.Process(Ms(0), null, [Finger(1, 1000)], _out);
        Assert.Equal(
            [new TouchSample(1, true, 1000, 2000), new TouchSample(2, true, 5000, 2000)],
            Touches(f.Process(Ms(20), null, [Finger(1, 1000), Finger(2, 5000)], _out)));
        Assert.Equal([new TouchSample(2, true, 5000, 2000)], Touches(f.Process(Ms(40), null, [Finger(2, 5000)], _out)));
    }

    [Fact]
    public void A_palm_withdraws_the_sent_finger_and_is_never_sent()
    {
        var f = new InputFilter(Relative);
        f.Process(Ms(0), null, [Finger(1, 1000)], _out);
        InputFilterResult r = f.Process(Ms(10), null, [Finger(1, 1000), Palm(2)], _out);
        Assert.Equal([new TouchSample(1, false, 1000, 2000)], Touches(r));
        Assert.Equal(0, r.ContactCount);
        Assert.Empty(Touches(f.Process(Ms(20), null, [], _out)));
    }

    [Fact]
    public void A_canceled_finger_is_withdrawn_until_it_lifts()
    {
        var f = new InputFilter(Relative);
        f.Process(Ms(0), null, [Finger(1, 1000)], _out);
        Assert.Equal([new TouchSample(1, false, 1000, 2000)], Touches(f.Process(Ms(10), null, [Finger(1, 1000, canceled: true)], _out)));
        Assert.Equal([new TouchSample(1, false, 1200, 2000)], Touches(f.Process(Ms(20), null, [Finger(1, 1200)], _out)));
        Assert.Empty(Touches(f.Process(Ms(30), null, [], _out)));
    }

    [Fact]
    public void The_pen_suppresses_a_lone_finger_and_withdraws_it()
    {
        var f = new InputFilter(Relative);
        f.Process(Ms(0), null, [Finger(1, 1000)], _out);
        InputFilterResult pen = f.Process(Ms(10), PenHover(), [Finger(1, 1000)], _out);
        Assert.True(pen.HasPen);
        Assert.Equal(0, pen.TouchCount);

        // Pen gone, inside the suppression window: the sent finger stays withdrawn.
        Assert.Equal([new TouchSample(1, false, 1000, 2000)], Touches(f.Process(Ms(100), null, [Finger(1, 1000)], _out)));

        // A finger landing after the window is a new raw touch.
        Assert.Empty(Touches(f.Process(Ms(500), null, [], _out)));
        Assert.Equal([new TouchSample(3, true, 1000, 2000)], Touches(f.Process(Ms(510), null, [Finger(3, 1000)], _out)));
    }

    [Fact]
    public void Draw_mode_ignores_the_option()
    {
        var f = new InputFilter(Relative with { FingerMode = FingerFallbackMode.Draw, FingerFallbackArmDelay = TimeSpan.Zero });
        InputFilterResult r = f.Process(Ms(0), null, [Finger(1, 1000)], _out);
        Assert.True(r.HasPen);
        Assert.Equal(PenTool.FingerFallback, r.Pen.Tool);
        Assert.Equal(PenState.InRange | PenState.Contact, r.Pen.State);
    }

    [Fact]
    public void Without_the_option_cursor_mode_still_sends_hover_samples()
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackArmDelay = TimeSpan.Zero });
        InputFilterResult r = f.Process(Ms(0), null, [Finger(1, 1000)], _out);
        Assert.True(r.HasPen);
        Assert.Equal(PenState.InRange, r.Pen.State);
        Assert.Equal(0, r.TouchCount);
    }

    [Fact]
    public void A_relative_lone_finger_is_allocation_free()
    {
        var f = new InputFilter(Relative);
        RawTouch[] one = [Finger(1, 1000)];
        RawTouch[] none = [];
        TouchSample[] output = _out;
        long sent = 0;
        Alloc.AssertZero(5_000, i =>
        {
            one[0] = Finger(1, (ushort)(1000 + (i % 50)));
            InputFilterResult r = f.Process(Ms(i * 8), null, i % 10 == 9 ? none : one, output);
            sent += r.TouchCount + 1;
        }, () => sent);
    }
}
