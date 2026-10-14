using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>Cursor mode (the default finger fallback, ADR-0022): one finger moves, a tap clicks, double-tap-hold drags.</summary>
public class InputFilterCursorTests
{
    private const PenState Hover = PenState.InRange;
    private const PenState Contact = PenState.InRange | PenState.Contact;

    private readonly TouchSample[] _out = new TouchSample[InputFilter.MaxTouches];
    private readonly RecordingSink _sink = new();
    private readonly ClickCountingSink _clicks = new();
    private readonly PenStateTracker _tracker;
    private uint _us = 10_000;

    public InputFilterCursorTests()
    {
        _tracker = new PenStateTracker(new TeeSink(_sink, _clicks), new ManualTimeProvider())
        {
            Mapper = new(1, 1, new DisplayRect(0, 0, 1512, 982)),
        };
    }

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static RawTouch Finger(byte id, ushort x = 10000, ushort y = 20000) => new(id, true, false, x, y, 0.05f);

    [Fact]
    public void Defaults_are_cursor_mode_with_tap_and_double_tap_thresholds()
    {
        var o = new InputFilterOptions();
        Assert.Equal(FingerFallbackMode.Cursor, o.FingerMode);
        Assert.Equal(Ms(200), o.TapMaxDuration);
        Assert.Equal(0.02f, o.TapSlop);
        Assert.Equal(Ms(300), o.DoubleTapWindow);
        Assert.Equal(0.05f, o.DoubleTapSlop);
        Assert.Equal(Ms(100), o.FingerFallbackArmDelay);
    }

    [Fact]
    public void Moving_finger_sends_hover_samples_and_never_presses()
    {
        var f = new InputFilter();
        Assert.Equal(default, Feed(f, 0, Finger(1, 10000)));
        Assert.Equal(Ms(100), f.PendingDeadline);
        Feed(f, 50, Finger(1, 11000));

        InputFilterResult armed = Feed(f, 100, Finger(1, 13000));
        Assert.True(armed.HasPen);
        Assert.Equal(new PenSample(13000, 20000, 0, 0, 0, 0, PenTool.FingerFallback, Hover, PenButtons.None), armed.Pen);
        Assert.Equal(2, armed.ReplayCount);
        Assert.All(f.ReplaySamples.ToArray(), s => Assert.Equal(Hover, s.State));

        for (int t = 110; t < 600; t += 10)
        {
            InputFilterResult r = Feed(f, t, Finger(1, (ushort)(13000 + (t * 10))));
            Assert.Equal(Hover, r.Pen.State);
            Assert.Equal(0, r.Pen.Pressure);
        }

        InputFilterResult lift = Feed(f, 600);
        Assert.True(lift.HasPen);
        Assert.Equal(PenState.None, lift.Pen.State);

        Assert.DoesNotContain(PenEventKind.Down, _sink.PenKinds());
        Assert.Equal(PenEventKind.ProximityEnter, _sink.Pen[0].Kind);
        Assert.Equal(PenEventKind.ProximityLeave, _sink.Pen[^1].Kind);
        Assert.Contains(PenEventKind.Move, _sink.PenKinds());
    }

    [Fact]
    public void Still_finger_keeps_the_cursor_on_the_hover_watchdog()
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1));
        Feed(f, 100, Finger(1)); // the head's call at the deadline arms it
        Assert.True(_tracker.IsInRange);
        Assert.False(_tracker.IsInContact);

        // The client's hover keepalive (every 250 ms) resends this state; the Mac waits the hover timeout, not 150 ms.
        Assert.Equal(new PenStateTrackerOptions().HoverTimeout, _tracker.WatchdogDueIn);
    }

    [Fact]
    public void Quick_tap_clicks_where_it_landed()
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1, 10000, 20000));
        Feed(f, 40, Finger(1, 10500, 20300)); // within the 2% slop

        InputFilterResult tap = Feed(f, 60);
        Assert.True(tap.HasPen);
        Assert.Equal(2, tap.ReplayCount);
        Assert.Equal(new PenSample(10000, 20000, 0, 0, 0, 0, PenTool.FingerFallback, Hover, PenButtons.None), f.ReplaySamples[0]);
        Assert.Equal(new PenSample(10000, 20000, 0x8000, 0, 0, 0, PenTool.FingerFallback, Contact, PenButtons.None), f.ReplaySamples[1]);
        Assert.Equal(PenState.None, tap.Pen.State);

        Assert.Equal(
            [PenEventKind.ProximityEnter, PenEventKind.Down, PenEventKind.Up, PenEventKind.ProximityLeave],
            _sink.PenKinds());
    }

    [Fact]
    public void Tap_after_the_arming_delay_still_clicks_at_the_touch_down_point()
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1, 10000, 20000));
        Assert.Equal(Hover, Feed(f, 120, Finger(1, 10400, 20000)).Pen.State);

        InputFilterResult tap = Feed(f, 190);
        Assert.Equal(2, tap.ReplayCount);
        Assert.Equal(10000, f.ReplaySamples[1].X);
        Assert.Equal(Contact, f.ReplaySamples[1].State);
        Assert.Equal(1, _sink.PenKinds().Count(k => k == PenEventKind.Down));
        Assert.Equal(1, _sink.PenKinds().Count(k => k == PenEventKind.Up));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(250)]
    [InlineData(2000)]
    public void Slow_touch_is_never_a_click(int liftMs)
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1));
        Feed(f, 100, Finger(1));
        InputFilterResult lift = Feed(f, liftMs);
        Assert.Equal(PenState.None, lift.Pen.State);
        Assert.Equal(0, lift.ReplayCount);
        Assert.DoesNotContain(PenEventKind.Down, _sink.PenKinds());
    }

    [Fact]
    public void Quick_touch_that_moved_beyond_the_slop_is_not_a_click()
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1, 10000, 20000));
        Feed(f, 30, Finger(1, 11400, 20000)); // 2.1% of the width
        Feed(f, 40, Finger(1, 10000, 20000)); // back again: it already moved
        InputFilterResult lift = Feed(f, 60);
        Assert.Equal(PenState.None, lift.Pen.State);
        Assert.All(f.ReplaySamples.ToArray(), s => Assert.Equal(Hover, s.State));
        Assert.DoesNotContain(PenEventKind.Down, _sink.PenKinds());
    }

    [Fact]
    public void Tap_thresholds_are_configurable()
    {
        var f = new InputFilter(new InputFilterOptions { TapMaxDuration = Ms(50), TapSlop = 0.1f });
        Feed(f, 0, Finger(1, 10000));
        Feed(f, 30, Finger(1, 16000)); // 9%: within the wider slop
        Assert.Equal(2, Feed(f, 40).ReplayCount);
        Assert.Equal(Contact, f.ReplaySamples[1].State);

        Feed(f, 1000, Finger(1, 10000));
        InputFilterResult slow = Feed(f, 1060); // 60 ms is longer than the 50 ms tap
        Assert.Equal(PenState.None, slow.Pen.State);
        Assert.All(f.ReplaySamples.ToArray(), s => Assert.Equal(Hover, s.State));
    }

    [Fact]
    public void Double_tap_clicks_twice_at_the_first_point()
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1, 10000, 20000));
        Feed(f, 50);

        // Second touch 150 ms later, 3% away: held until it is decided (the tap duration).
        Assert.Equal(default, Feed(f, 200, Finger(2, 12000, 20000)));
        Assert.Equal(Ms(400), f.PendingDeadline);
        InputFilterResult second = Feed(f, 260);
        Assert.Equal(2, second.ReplayCount);
        Assert.Equal(new PenSample(10000, 20000, 0x8000, 0, 0, 0, PenTool.FingerFallback, Contact, PenButtons.None), f.ReplaySamples[1]);

        PenEvent[] downs = _sink.Pen.Where(e => e.Kind == PenEventKind.Down).ToArray();
        Assert.Equal(2, downs.Length);
        Assert.Equal(downs[0].Position, downs[1].Position);
    }

    [Fact]
    public void Double_tap_and_hold_drags_until_lift()
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1, 10000, 20000));
        Feed(f, 50);
        _sink.Clear();

        Assert.Equal(default, Feed(f, 150, Finger(2, 10200, 20000)));
        Assert.Equal(default, Feed(f, 300, Finger(2, 10200, 20000))); // past the arming delay, still maybe a tap
        Assert.Equal(Ms(350), f.PendingDeadline);

        // The head calls again at the deadline: the hold becomes a drag from where the finger landed.
        InputFilterResult drag = Feed(f, 350, Finger(2, 10200, 20000));
        Assert.True(drag.HasPen);
        Assert.Equal(Contact, drag.Pen.State);
        Assert.Equal(2, drag.ReplayCount);
        Assert.Equal(10200, f.ReplaySamples[0].X);
        Assert.Null(f.PendingDeadline);

        for (int t = 360; t <= 800; t += 20)
        {
            Assert.Equal(Contact, Feed(f, t, Finger(2, (ushort)(10200 + (t * 20)), 20000)).Pen.State);
        }

        Assert.Equal(default, Feed(f, 810));
        PenEventKind[] kinds = _sink.PenKinds();
        Assert.Equal([PenEventKind.ProximityEnter, PenEventKind.Down], kinds[..2]);
        Assert.Equal([PenEventKind.Up, PenEventKind.ProximityLeave], kinds[^2..]);
        Assert.All(kinds[2..^2], k => Assert.Equal(PenEventKind.Drag, k));
    }

    [Fact]
    public void Double_tap_reaches_the_host_as_a_double_click()
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1, 10000, 20000));
        Feed(f, 50);
        Feed(f, 200, Finger(2, 10300, 20100));
        Feed(f, 260);
        Assert.Equal([1L, 2L], _clicks.LeftDownClickStates);
    }

    [Theory]
    [InlineData(10000, 20000)] // lands exactly on the tap's point: nudged off it
    [InlineData(10100, 20050)] // within the 6 pt pen slop of the tap
    public void Double_tap_and_hold_drag_starts_with_click_state_one(int x, int y)
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1, 10000, 20000));
        Feed(f, 50);
        Feed(f, 150, Finger(2, (ushort)x, (ushort)y));
        Feed(f, 350, Finger(2, (ushort)x, (ushort)y)); // decided: a drag
        Feed(f, 400, Finger(2, 20000, 20000));
        Feed(f, 450);

        // The tap's click, then the drag's press: a plain single-click drag, never a word selection.
        Assert.Equal([1L, 1L], _clicks.LeftDownClickStates);
        Assert.Contains(TabletAction.LeftDragged, _clicks.Actions);
    }

    [Fact]
    public void Finger_presses_repeat_the_click_count_only_at_the_exact_point()
    {
        Assert.Equal(0, TabletEventMath.ClickSlopFor(PenTool.FingerFallback, 6));
        Assert.Equal(6, TabletEventMath.ClickSlopFor(PenTool.Pen, 6));
        Assert.Equal(6, TabletEventMath.ClickSlopFor(PenTool.Eraser, 6));

        var state = default(ClickState);
        Assert.Equal(1, TabletEventMath.NextClickCount(ref state, 0, new DisplayPoint(10, 10), 500_000_000, 0));
        Assert.Equal(2, TabletEventMath.NextClickCount(ref state, 100_000_000, new DisplayPoint(10, 10), 500_000_000, 0));
        Assert.Equal(1, TabletEventMath.NextClickCount(ref state, 200_000_000, new DisplayPoint(10.01, 10), 500_000_000, 0));
    }

    [Fact]
    public void Double_tap_and_move_starts_the_drag_after_the_arming_delay()
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1, 10000, 20000));
        Feed(f, 50);

        Assert.Equal(default, Feed(f, 150, Finger(2, 10000, 20000)));
        Assert.Equal(default, Feed(f, 200, Finger(2, 12000, 20000))); // moved, but within the arming delay
        InputFilterResult drag = Feed(f, 250, Finger(2, 13000, 20000));
        Assert.Equal(Contact, drag.Pen.State);
        Assert.Equal(13000, drag.Pen.X);
        Assert.Equal(2, drag.ReplayCount);
        Assert.Equal(10001, f.ReplaySamples[0].X); // landed exactly on the tap point: nudged off it
    }

    [Theory]
    [InlineData(400, 10000)] // too late
    [InlineData(150, 14000)] // too far (6%)
    public void Touch_outside_the_double_tap_window_or_slop_just_moves(int downMs, int x)
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1, 10000, 20000));
        Feed(f, 50);
        _sink.Clear();

        Feed(f, downMs, Finger(2, (ushort)x, 20000));
        Assert.Equal(Ms(downMs + 100), f.PendingDeadline); // a plain hold, not a double tap's second touch
        Assert.Equal(Hover, Feed(f, downMs + 100, Finger(2, (ushort)x, 20000)).Pen.State);
        Assert.Equal(PenState.None, Feed(f, downMs + 500).Pen.State);
        Assert.DoesNotContain(PenEventKind.Down, _sink.PenKinds());
    }

    [Fact]
    public void A_tap_counts_only_for_the_next_touch()
    {
        var f = new InputFilter(new InputFilterOptions { DoubleTapWindow = Ms(2000) });
        Feed(f, 0, Finger(1, 10000));
        Feed(f, 50); // tap
        Feed(f, 100, Finger(2, 30000)); // elsewhere, slow
        Feed(f, 200, Finger(2, 30000));
        Feed(f, 400);

        Feed(f, 450, Finger(3, 10000));
        Assert.Equal(Ms(550), f.PendingDeadline); // a plain hold, not a double tap's second touch
    }

    [Fact]
    public void Second_finger_during_a_double_tap_hold_is_a_gesture_not_a_click_or_drag()
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1, 10000));
        Feed(f, 50);
        _sink.Clear();

        Assert.Equal(default, Feed(f, 150, Finger(2, 10000)));
        InputFilterResult two = Feed(f, 180, Finger(2, 10000), Finger(3, 40000));
        Assert.False(two.HasPen);
        Assert.Equal(2, two.TouchCount);
        Feed(f, 400, Finger(2, 10000));
        Assert.Equal(default, Feed(f, 410));
        Assert.Empty(_sink.Pen);
    }

    [Fact]
    public void Pen_arriving_ends_the_cursor_and_forgets_the_tap()
    {
        var f = new InputFilter(new InputFilterOptions { DoubleTapWindow = Ms(2000) });
        Feed(f, 0, Finger(1, 10000));
        Feed(f, 50); // tap
        Assert.Equal(PenTool.Pen, f.Process(Ms(60), Samples.Hover(5, 5), [], _out).Pen.Tool);
        Assert.Equal(default, f.Process(Ms(500), null, [], _out));

        // Past the pen suppression the next touch is a plain hold, not a double tap.
        f.Process(Ms(500), null, [Finger(2, 10000)], _out);
        Assert.Equal(Ms(600), f.PendingDeadline);
    }

    [Fact]
    public void Finger_replaced_in_the_same_snapshot_ends_the_cursor_then_holds_the_new_one()
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1, 10000));
        Feed(f, 100, Finger(1, 10000));
        Feed(f, 300, Finger(1, 15000));

        InputFilterResult ended = Feed(f, 310, Finger(2, 40000));
        Assert.Equal(PenState.None, ended.Pen.State);
        Assert.Equal(15000, ended.Pen.X);
        Assert.Equal(Ms(410), f.PendingDeadline);
    }

    [Fact]
    public void Canceled_finger_is_never_a_click()
    {
        var f = new InputFilter();
        Feed(f, 0, Finger(1));
        Assert.Equal(default, f.Process(Ms(30), null, [Finger(1) with { Canceled = true }], _out));
        Assert.Equal(default, f.Process(Ms(40), null, [], _out));
    }

    [Fact]
    public void Draw_mode_keeps_finger_strokes()
    {
        var f = new InputFilter(new InputFilterOptions { FingerMode = FingerFallbackMode.Draw, FingerFallbackArmDelay = TimeSpan.Zero });
        Assert.Equal(Contact, f.Process(Ms(0), null, [Finger(1)], _out).Pen.State);
    }

    [Fact]
    public void Cursor_tap_double_tap_and_drag_paths_are_allocation_free()
    {
        var f = new InputFilter();
        RawTouch[][] frames =
        [
            [Finger(1, 100, 100)],
            [], // tap (click)
            [Finger(2, 120, 100)],
            [], // second tap (double-click)
            [Finger(3, 120, 100)],
            [Finger(3, 120, 100)],
            [Finger(3, 120, 100)],
            [Finger(3, 120, 100)],
            [Finger(3, 120, 100)], // past the tap duration: no click
            [],
            [Finger(4, 100, 100)],
            [], // tap
            [Finger(5, 100, 100)],
            [Finger(5, 100, 100)],
            [Finger(5, 100, 100)],
            [Finger(5, 100, 100)],
            [Finger(5, 9000, 100)], // double-tap-hold: drag
            [],
            [Finger(10, 30000, 100)],
            [Finger(10, 30000, 100)],
            [Finger(10, 30100, 100)], // armed: the cursor moves
            [Finger(10, 30200, 100)],
            [Finger(10, 30300, 100)],
            [], // slow: no click
            [Finger(6, 100, 100)],
            [Finger(7, 100, 100)], // replaced in one snapshot
            [],
        ];
        RawTouch[] history = [Finger(3, 50, 50), Finger(5, 60, 60), Finger(9, 1, 1)];
        TouchSample[] dest = _out;
        long pens = 0;
        long replayed = 0;
        Alloc.AssertZero(4_200, i =>
        {
            InputFilterResult r = f.Process(TimeSpan.FromMilliseconds(i * 60.0), null, frames[i % frames.Length], dest, history);
            if (r.HasPen)
            {
                pens++;
            }

            replayed += f.ReplaySamples.Length;
            _ = f.PendingDeadline;
        }, () => pens, () => replayed);
    }

    /// <summary>Runs one snapshot through the filter and sends the result to the Mac tracker the way the head does.</summary>
    private InputFilterResult Feed(InputFilter f, double ms, params RawTouch[] touches)
    {
        InputFilterResult r = f.Process(Ms(ms), null, touches, _out);
        if (r.HasPen || f.PendingDeadline is null)
        {
            PenSample? pen = r.HasPen ? r.Pen : null;
            ReadOnlySpan<PenSample> history = f.ReplaySamples;
            _tracker.ProcessFrame(_us, pen, history, _out.AsSpan(0, r.TouchCount));
        }

        _us += 10_000;
        return r;
    }
}

/// <summary>Forwards pen and gesture events to two sinks.</summary>
internal sealed class TeeSink(IInputSink a, IInputSink b) : IInputSink
{
    public void OnPenEvent(in PenEvent e)
    {
        a.OnPenEvent(e);
        b.OnPenEvent(e);
    }

    public void OnGestureEvent(in GestureEvent e)
    {
        a.OnGestureEvent(e);
        b.OnGestureEvent(e);
    }
}

/// <summary>
/// Plans host posts and computes the click state of each press the way the Mac injector does
/// (<see cref="TabletEventMath.NextClickCount"/> with <see cref="TabletEventMath.ClickSlopFor"/>, a 500 ms interval and
/// a 6 pt pen slop).
/// </summary>
internal sealed class ClickCountingSink : IInputSink
{
    private const long IntervalNs = 500_000_000;
    private const double PenSlop = 6;
    private readonly TabletStep[] _buffer = new TabletStep[TabletEventPlanner.MaxSteps];
    private TabletPostedState _state;
    private ClickState _left;

    public List<TabletAction> Actions { get; } = [];

    public List<long> LeftDownClickStates { get; } = [];

    public void OnPenEvent(in PenEvent e)
    {
        int n = TabletEventPlanner.Plan(ref _state, e.Kind, e.Button, e.Sample.Tool, _buffer, e.Sample.Buttons);
        for (int i = 0; i < n; i++)
        {
            Actions.Add(_buffer[i].Action);
            if (_buffer[i].Action == TabletAction.LeftDown)
            {
                LeftDownClickStates.Add(TabletEventMath.NextClickCount(
                    ref _left, e.TimestampUs * 1000, e.Position, IntervalNs, TabletEventMath.ClickSlopFor(_buffer[i].Tool, PenSlop)));
            }
        }
    }

    public void OnGestureEvent(in GestureEvent e)
    {
    }
}
