using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

public class InputFilterTests
{
    private static readonly PenSample PenHover = Samples.Hover(100, 200);
    private readonly TouchSample[] _out = new TouchSample[InputFilter.MaxTouches];

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static RawTouch Finger(byte id, ushort x = 1000, ushort y = 2000, bool canceled = false) =>
        new(id, true, canceled, x, y);

    [Fact]
    public void Pen_passes_through_and_suppresses_touches()
    {
        var f = new InputFilter();
        InputFilterResult r = f.Process(Ms(0), PenHover, [Finger(1), Finger(2)], _out);
        Assert.True(r.HasPen);
        Assert.Equal(PenHover, r.Pen);
        Assert.Equal(0, r.TouchCount);
    }

    [Fact]
    public void Pen_sample_without_range_or_contact_counts_as_no_pen()
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackEnabled = false });
        InputFilterResult r = f.Process(Ms(0), PenHover with { State = PenState.None }, [Finger(1)], _out);
        Assert.False(r.HasPen);
        Assert.Equal(1, r.TouchCount);
    }

    [Fact]
    public void Touches_stay_suppressed_for_300ms_after_pen_leaves()
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackEnabled = false });
        f.Process(Ms(1000), PenHover, [], _out);

        Assert.Equal(default, f.Process(Ms(1299.9), null, [Finger(5)], _out));

        // Finger 5 landed during suppression: still suppressed after the window, until it lifts.
        Assert.Equal(0, f.Process(Ms(1300), null, [Finger(5)], _out).TouchCount);
        f.Process(Ms(1310), null, [], _out);

        InputFilterResult r = f.Process(Ms(1320), null, [Finger(5)], _out);
        Assert.Equal(1, r.TouchCount);
        Assert.Equal(new TouchSample(5, true, 1000, 2000), _out[0]);
    }

    [Fact]
    public void Fresh_finger_after_window_is_reported()
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackEnabled = false });
        f.Process(Ms(0), Samples.Touch(1, 1), [], _out);
        Assert.Equal(1, f.Process(Ms(300), null, [Finger(7)], _out).TouchCount);
    }

    [Fact]
    public void Suppression_window_is_configurable()
    {
        var f = new InputFilter(new InputFilterOptions { PenSuppression = Ms(50), FingerFallbackEnabled = false });
        f.Process(Ms(0), PenHover, [], _out);
        Assert.Equal(1, f.Process(Ms(50), null, [Finger(1)], _out).TouchCount);
    }

    [Fact]
    public void Canceled_pointer_is_dropped_until_it_lifts()
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackEnabled = false });
        Assert.Equal(1, f.Process(Ms(0), null, [Finger(1), Finger(2, canceled: true)], _out).TouchCount);
        Assert.Equal(1, _out[0].PointerId);

        // Platform no longer flags it, but it is the same (palm) pointer: still dropped.
        Assert.Equal(1, f.Process(Ms(10), null, [Finger(1), Finger(2)], _out).TouchCount);

        f.Process(Ms(20), null, [Finger(1)], _out);
        Assert.Equal(2, f.Process(Ms(30), null, [Finger(1), Finger(2)], _out).TouchCount);
    }

    [Fact]
    public void Single_finger_becomes_finger_fallback_pen_with_configured_pressure()
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackPressure = 12345 });
        InputFilterResult r = f.Process(Ms(0), null, [Finger(3, 111, 222)], _out);
        Assert.True(r.HasPen);
        Assert.Equal(0, r.TouchCount);
        Assert.Equal(new PenSample(111, 222, 12345, 0, 0, 0, PenTool.FingerFallback, PenState.InRange | PenState.Contact, PenButtons.None), r.Pen);
    }

    [Fact]
    public void Fallback_defaults_to_half_pressure()
    {
        InputFilterResult r = new InputFilter().Process(Ms(0), null, [Finger(3)], _out);
        Assert.Equal(0x8000, r.Pen.Pressure);
    }

    [Fact]
    public void Fallback_disabled_reports_single_finger_as_touch()
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackEnabled = false });
        InputFilterResult r = f.Process(Ms(0), null, [Finger(3)], _out);
        Assert.False(r.HasPen);
        Assert.Equal(1, r.TouchCount);
    }

    [Fact]
    public void Two_fingers_are_touches_and_stay_touches_until_all_lift()
    {
        var f = new InputFilter();
        Assert.True(f.Process(Ms(0), null, [Finger(1)], _out).HasPen);

        InputFilterResult two = f.Process(Ms(10), null, [Finger(1), Finger(2)], _out);
        Assert.False(two.HasPen);
        Assert.Equal(2, two.TouchCount);

        // One finger lifts: remaining finger must not start a stroke.
        InputFilterResult one = f.Process(Ms(20), null, [Finger(2)], _out);
        Assert.False(one.HasPen);
        Assert.Equal(1, one.TouchCount);

        Assert.Equal(default, f.Process(Ms(30), null, [], _out));
        Assert.True(f.Process(Ms(40), null, [Finger(4)], _out).HasPen);
    }

    [Fact]
    public void Pen_arriving_during_finger_fallback_wins_and_suppresses_the_finger()
    {
        var f = new InputFilter();
        Assert.Equal(PenTool.FingerFallback, f.Process(Ms(0), null, [Finger(1)], _out).Pen.Tool);
        Assert.Equal(PenTool.Pen, f.Process(Ms(10), PenHover, [Finger(1)], _out).Pen.Tool);
        Assert.Equal(default, f.Process(Ms(1000), null, [Finger(1)], _out));
    }

    [Fact]
    public void Touch_count_is_capped_by_protocol_limit_and_destination()
    {
        var f = new InputFilter();
        var many = new RawTouch[12];
        for (int i = 0; i < many.Length; i++)
        {
            many[i] = Finger((byte)i);
        }

        Assert.Equal(InputFilter.MaxTouches, f.Process(Ms(0), null, many, _out).TouchCount);
        Assert.Equal(3, f.Process(Ms(1), null, many, new TouchSample[3]).TouchCount);
    }

    [Fact]
    public void Non_contact_touches_are_ignored()
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackEnabled = false });
        Assert.Equal(default, f.Process(Ms(0), null, [new RawTouch(1, false, false, 1, 1)], _out));
    }

    [Fact]
    public void Reset_clears_suppression()
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackEnabled = false });
        f.Process(Ms(0), PenHover, [Finger(1)], _out);
        f.Reset();
        Assert.Equal(1, f.Process(Ms(1), null, [Finger(1)], _out).TouchCount);
    }

    [Fact]
    public void Process_is_allocation_free()
    {
        var f = new InputFilter();
        RawTouch[] two = [Finger(1), Finger(2)];
        RawTouch[] one = [Finger(1)];
        RawTouch[] none = [];
        TouchSample[] dest = _out;
        long touches = 0;
        Alloc.AssertZero(5_000, i =>
        {
            var t = TimeSpan.FromMilliseconds(i * 5);
            PenSample? pen = (i % 200) < 50 ? Samples.Touch((ushort)i, 1) : null;
            RawTouch[] fingers = (i % 10) == 0 ? none : (i & 1) == 0 ? two : one;
            touches += f.Process(t, pen, fingers, dest).TouchCount;
        }, () => touches);
    }
}
