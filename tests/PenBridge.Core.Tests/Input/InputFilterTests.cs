using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

public class InputFilterTests
{
    private static readonly PenSample PenHover = Samples.Hover(100, 200);
    private readonly TouchSample[] _out = new TouchSample[InputFilter.MaxTouches];

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static readonly InputFilterOptions Immediate = new() { FingerFallbackArmDelay = TimeSpan.Zero };

    private static RawTouch Finger(byte id, ushort x = 1000, ushort y = 2000, bool canceled = false, float size = 0.05f) =>
        new(id, true, canceled, x, y, size);

    private static RawTouch Palm(byte id, ushort x = 30000, ushort y = 40000, float size = 0.4f) =>
        new(id, true, false, x, y, size);

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
        var f = new InputFilter(Immediate with { FingerFallbackPressure = 12345 });
        InputFilterResult r = f.Process(Ms(0), null, [Finger(3, 111, 222)], _out);
        Assert.True(r.HasPen);
        Assert.Equal(0, r.TouchCount);
        Assert.Equal(new PenSample(111, 222, 12345, 0, 0, 0, PenTool.FingerFallback, PenState.InRange | PenState.Contact, PenButtons.None), r.Pen);
    }

    [Fact]
    public void Fallback_defaults_to_half_pressure()
    {
        InputFilterResult r = new InputFilter(Immediate).Process(Ms(0), null, [Finger(3)], _out);
        Assert.Equal(0x8000, r.Pen.Pressure);
        Assert.Equal(TimeSpan.FromMilliseconds(100), new InputFilterOptions().FingerFallbackArmDelay);
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
        var f = new InputFilter(Immediate);
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
        var f = new InputFilter(Immediate);
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

    private static void AssertNothing(InputFilterResult r)
    {
        Assert.False(r.HasPen);
        Assert.Equal(0, r.TouchCount);
        Assert.Equal(0, r.ReplayCount);
    }

    [Fact]
    public void Lone_finger_arms_after_100ms_and_replays_held_samples_in_order()
    {
        var f = new InputFilter();
        for (int i = 0; i < 10; i++)
        {
            AssertNothing(f.Process(Ms(i * 10), null, [Finger(1, (ushort)(100 + i), 500)], _out));
            Assert.Equal(Ms(100), f.PendingDeadline);
        }

        InputFilterResult armed = f.Process(Ms(100), null, [Finger(1, 110, 500)], _out);
        Assert.True(armed.HasPen);
        Assert.Equal(10, armed.ReplayCount);
        Assert.Equal(new PenSample(110, 500, 0x8000, 0, 0, 0, PenTool.FingerFallback, PenState.InRange | PenState.Contact, PenButtons.None), armed.Pen);
        PenSample[] replay = f.ReplaySamples.ToArray();
        Assert.Equal(10, replay.Length);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(armed.Pen with { X = (ushort)(100 + i) }, replay[i]);
        }

        Assert.Null(f.PendingDeadline);

        InputFilterResult live = f.Process(Ms(105), null, [Finger(1, 111, 500)], _out);
        Assert.True(live.HasPen);
        Assert.Equal(0, live.ReplayCount);
        Assert.Equal(0, f.ReplaySamples.Length);
        Assert.Equal(111, live.Pen.X);
    }

    [Fact]
    public void Stationary_finger_arms_when_the_head_calls_again_at_the_deadline()
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackArmDelay = Ms(80) });
        AssertNothing(f.Process(Ms(1000), null, [Finger(1)], _out));
        Assert.Equal(Ms(1080), f.PendingDeadline);
        AssertNothing(f.Process(Ms(1079), null, [Finger(1)], _out));

        InputFilterResult armed = f.Process(Ms(1080), null, [Finger(1)], _out);
        Assert.True(armed.HasPen);
        Assert.Equal(2, armed.ReplayCount);
        Assert.Equal(PenTool.FingerFallback, armed.Pen.Tool);
    }

    [Fact]
    public void First_finger_of_a_two_finger_gesture_never_becomes_fallback_or_click()
    {
        var f = new InputFilter();
        var results = new List<InputFilterResult>
        {
            f.Process(Ms(0), null, [Finger(1, 100, 100)], _out),
            f.Process(Ms(30), null, [Finger(1, 110, 100)], _out),
            f.Process(Ms(60), null, [Finger(1, 120, 100), Finger(2, 900, 100)], _out),
        };
        Assert.Null(f.PendingDeadline);
        Assert.Equal(2, results[^1].TouchCount);

        // The gesture continues past the arm delay; then finger 2 lifts and finger 1 lingers alone.
        results.Add(f.Process(Ms(200), null, [Finger(1, 300, 100), Finger(2, 1100, 100)], _out));
        results.Add(f.Process(Ms(250), null, [Finger(1, 310, 100)], _out));
        results.Add(f.Process(Ms(400), null, [Finger(1, 320, 100)], _out));
        Assert.Equal(1, results[^1].TouchCount);

        // All lift: no tap is replayed.
        results.Add(f.Process(Ms(410), null, [], _out));
        Assert.Equal(default, results[^1]);

        foreach (InputFilterResult r in results)
        {
            Assert.False(r.HasPen);
            Assert.Equal(0, r.ReplayCount);
        }
    }

    [Fact]
    public void Quick_tap_is_replayed_as_a_whole_stroke_ending_out_of_range()
    {
        var f = new InputFilter();
        AssertNothing(f.Process(Ms(0), null, [Finger(1, 100, 200)], _out));
        AssertNothing(f.Process(Ms(30), null, [Finger(1, 101, 200)], _out));

        InputFilterResult tap = f.Process(Ms(50), null, [], _out);
        Assert.True(tap.HasPen);
        Assert.Equal(2, tap.ReplayCount);
        Assert.Equal(100, f.ReplaySamples[0].X);
        Assert.Equal(101, f.ReplaySamples[1].X);
        Assert.Equal(PenState.InRange | PenState.Contact, f.ReplaySamples[1].State);
        Assert.Equal(new PenSample(101, 200, 0, 0, 0, 0, PenTool.FingerFallback, PenState.None, PenButtons.None), tap.Pen);

        Assert.Null(f.PendingDeadline);
        Assert.Equal(default, f.Process(Ms(60), null, [], _out));
    }

    [Fact]
    public void Held_finger_canceled_by_the_platform_is_dropped_not_tapped()
    {
        var f = new InputFilter();
        AssertNothing(f.Process(Ms(0), null, [Finger(1)], _out));
        AssertNothing(f.Process(Ms(20), null, [Finger(1, canceled: true)], _out));
        Assert.Equal(default, f.Process(Ms(30), null, [], _out));
    }

    [Fact]
    public void Pen_arriving_during_the_hold_drops_the_held_finger()
    {
        var f = new InputFilter();
        AssertNothing(f.Process(Ms(0), null, [Finger(1)], _out));
        InputFilterResult r = f.Process(Ms(50), PenHover, [Finger(1)], _out);
        Assert.Equal(PenHover, r.Pen);
        Assert.Equal(0, r.ReplayCount);
        Assert.Null(f.PendingDeadline);
        Assert.Equal(default, f.Process(Ms(1000), null, [Finger(1)], _out));
    }

    [Fact]
    public void Hold_buffer_overflow_keeps_the_touch_down_point_and_the_latest_sample()
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackArmDelay = Ms(1000) });
        int n = InputFilter.MaxHeldSamples + 20;
        for (int i = 0; i < n; i++)
        {
            AssertNothing(f.Process(Ms(i), null, [Finger(1, (ushort)i, 0)], _out));
        }

        InputFilterResult armed = f.Process(Ms(1000), null, [Finger(1, 5000, 0)], _out);
        Assert.Equal(InputFilter.MaxHeldSamples - 1, armed.ReplayCount);
        Assert.Equal(0, f.ReplaySamples[0].X);
        Assert.Equal(InputFilter.MaxHeldSamples - 2, f.ReplaySamples[^1].X);
        Assert.Equal(5000, armed.Pen.X);
    }

    [Fact]
    public void Palm_landing_before_the_pen_never_leaks()
    {
        var f = new InputFilter();
        var results = new List<InputFilterResult>();

        // The palm lands in pieces over 0.36 s (up to 5 ids), its first contact small, before the pen is in range.
        results.Add(f.Process(Ms(0), null, [Finger(1, size: 0.08f)], _out));
        results.Add(f.Process(Ms(40), null, [Palm(1), Finger(2)], _out));
        for (int t = 60; t < 360; t += 20)
        {
            results.Add(f.Process(Ms(t), null, [Palm(1), Finger(2), Finger(3), Palm(4, size: 0.25f), Finger(5)], _out));
            Assert.Null(f.PendingDeadline);
        }

        foreach (InputFilterResult r in results)
        {
            AssertNothing(r);
        }

        // The pen comes in range while the palm rests: the pen passes, touches stay suppressed.
        InputFilterResult pen = f.Process(Ms(360), PenHover, [Palm(1), Finger(2)], _out);
        Assert.Equal(new InputFilterResult(true, PenHover, 0), pen);
    }

    [Fact]
    public void Palm_suppresses_every_finger_until_all_lift()
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackEnabled = false });
        Assert.Equal(1, f.Process(Ms(0), null, [Finger(1)], _out).TouchCount);
        AssertNothing(f.Process(Ms(10), null, [Finger(1), Palm(2)], _out));

        // The palm lifts but finger 1 stays: still suppressed, and so is a fresh finger.
        AssertNothing(f.Process(Ms(20), null, [Finger(1)], _out));
        AssertNothing(f.Process(Ms(600), null, [Finger(1), Finger(3)], _out));

        f.Process(Ms(610), null, [], _out);
        Assert.Equal(1, f.Process(Ms(810), null, [Finger(4)], _out).TouchCount);
    }

    [Fact]
    public void Touches_stay_suppressed_for_a_window_after_a_palm_lifts()
    {
        var f = new InputFilter(Immediate);
        AssertNothing(f.Process(Ms(0), null, [Palm(1)], _out));
        AssertNothing(f.Process(Ms(100), null, [], _out)); // all lifted: the 200 ms window starts

        // A finger landing inside the window is suppressed until it lifts, even past the window...
        AssertNothing(f.Process(Ms(299), null, [Finger(2)], _out));
        AssertNothing(f.Process(Ms(500), null, [Finger(2)], _out));

        // ...and restarts the window when it lifts.
        AssertNothing(f.Process(Ms(510), null, [], _out));
        AssertNothing(f.Process(Ms(709), null, [Finger(3)], _out));
        AssertNothing(f.Process(Ms(720), null, [], _out));

        // A fresh finger after the window works normally.
        InputFilterResult r = f.Process(Ms(920), null, [Finger(4)], _out);
        Assert.True(r.HasPen);
        Assert.Equal(PenTool.FingerFallback, r.Pen.Tool);
    }

    [Fact]
    public void Palm_window_is_configurable()
    {
        var f = new InputFilter(new InputFilterOptions { PalmSuppression = Ms(50), FingerFallbackEnabled = false });
        AssertNothing(f.Process(Ms(0), null, [Palm(1)], _out));
        f.Process(Ms(10), null, [], _out);
        AssertNothing(f.Process(Ms(59), null, [Finger(2)], _out));
        f.Process(Ms(60), null, [], _out);
        Assert.Equal(1, f.Process(Ms(110), null, [Finger(3)], _out).TouchCount);
    }

    [Fact]
    public void Palm_ends_a_fallback_stroke_in_progress()
    {
        var f = new InputFilter(Immediate);
        Assert.True(f.Process(Ms(0), null, [Finger(1)], _out).HasPen);
        Assert.Equal(default, f.Process(Ms(10), null, [Finger(1), Palm(2)], _out));
        Assert.Equal(default, f.Process(Ms(20), null, [Finger(1)], _out));
    }

    [Theory]
    [InlineData(0.19f, false)]
    [InlineData(0.2f, true)]
    [InlineData(0f, false)]
    [InlineData(float.NaN, false)]
    public void Default_palm_threshold_is_point_two(float size, bool palm)
    {
        var f = new InputFilter(new InputFilterOptions { FingerFallbackEnabled = false });
        Assert.Equal(palm ? 0 : 1, f.Process(Ms(0), null, [Finger(1, size: size)], _out).TouchCount);
    }

    [Fact]
    public void Palm_threshold_is_configurable_and_above_one_disables_it()
    {
        var low = new InputFilter(new InputFilterOptions { PalmSizeThreshold = 0.05f, FingerFallbackEnabled = false });
        Assert.Equal(0, low.Process(Ms(0), null, [Finger(1, size: 0.05f)], _out).TouchCount);

        var off = new InputFilter(new InputFilterOptions { PalmSizeThreshold = 1.01f, FingerFallbackEnabled = false });
        Assert.Equal(1, off.Process(Ms(0), null, [Finger(1, size: 1f)], _out).TouchCount);
    }

    [Fact]
    public void Reset_clears_palm_and_hold()
    {
        var f = new InputFilter();
        f.Process(Ms(0), null, [Palm(1)], _out);
        f.Reset();
        AssertNothing(f.Process(Ms(1), null, [Finger(2)], _out));
        Assert.NotNull(f.PendingDeadline);
        f.Reset();
        Assert.Null(f.PendingDeadline);
    }

    /// <summary>Feeds the filter output to a Mac-side tracker the way the head sends it (replay as history).</summary>
    private static void Send(PenStateTracker tracker, InputFilter f, InputFilterResult r, TouchSample[] touches, ref uint timeUs)
    {
        PenSample? pen = r.HasPen ? r.Pen : null;
        ReadOnlySpan<PenSample> history = f.ReplaySamples;
        ReadOnlySpan<TouchSample> t = touches.AsSpan(0, r.TouchCount);
        while (history.Length > 8)
        {
            tracker.ProcessFrame(timeUs, history[8], history[..8], t);
            history = history[9..];
            timeUs += 1_000;
        }

        tracker.ProcessFrame(timeUs, pen, history, t);
        timeUs += 10_000;
    }

    [Fact]
    public void Two_finger_gesture_produces_no_pen_events_on_the_mac()
    {
        var sink = new RecordingSink();
        var tracker = new PenStateTracker(sink, new ManualTimeProvider()) { Mapper = new(1, 1, new DisplayRect(0, 0, 65535, 65535)) };
        var f = new InputFilter();
        uint us = 10_000;
        Send(tracker, f, f.Process(Ms(0), null, [Finger(1, 100, 100)], _out), _out, ref us);
        Send(tracker, f, f.Process(Ms(50), null, [Finger(1, 110, 100)], _out), _out, ref us);
        Send(tracker, f, f.Process(Ms(90), null, [Finger(1, 120, 100), Finger(2, 9000, 100)], _out), _out, ref us);
        Send(tracker, f, f.Process(Ms(300), null, [Finger(1, 400, 100)], _out), _out, ref us);
        Send(tracker, f, f.Process(Ms(310), null, [], _out), _out, ref us);
        Assert.Empty(sink.Pen);
    }

    [Fact]
    public void Lone_quick_tap_is_a_single_click_on_the_mac()
    {
        var sink = new RecordingSink();
        var tracker = new PenStateTracker(sink, new ManualTimeProvider()) { Mapper = new(1, 1, new DisplayRect(0, 0, 65535, 65535)) };
        var f = new InputFilter();
        uint us = 10_000;
        Send(tracker, f, f.Process(Ms(0), null, [Finger(1, 100, 100)], _out), _out, ref us);
        Send(tracker, f, f.Process(Ms(40), null, [Finger(1, 100, 100)], _out), _out, ref us);
        Send(tracker, f, f.Process(Ms(60), null, [], _out), _out, ref us);
        Assert.Equal(
            [PenEventKind.ProximityEnter, PenEventKind.Down, PenEventKind.Up, PenEventKind.ProximityLeave],
            sink.PenKinds());
    }

    [Fact]
    public void Hold_arm_tap_and_palm_paths_are_allocation_free()
    {
        var f = new InputFilter();
        RawTouch[][] frames =
        [
            [Finger(1, 100, 100)],
            [Finger(1, 101, 100)],
            [], // tap
            [Finger(2, 100, 100)],
            [Finger(2, 120, 100)],
            [Finger(2, 140, 100)], // armed: frames are 50 ms apart
            [Finger(2, 150, 100)],
            [],
            [Finger(3)],
            [Finger(3), Finger(4)], // gesture
            [],
            [Finger(5), Palm(6)],
            [],
            [],
            [],
            [],
        ];
        TouchSample[] dest = _out;
        long pens = 0;
        long replayed = 0;
        Alloc.AssertZero(4_000, i =>
        {
            InputFilterResult r = f.Process(TimeSpan.FromMilliseconds(i * 50.0), null, frames[i % frames.Length], dest);
            if (r.HasPen)
            {
                pens++;
            }

            replayed += f.ReplaySamples.Length;
            _ = f.PendingDeadline;
        }, () => pens, () => replayed);
    }
}
