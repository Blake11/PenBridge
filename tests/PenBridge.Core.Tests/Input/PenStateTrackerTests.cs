using PenBridge.Core.Input;
using static PenBridge.Core.Input.PenEventKind;

namespace PenBridge.Core.Tests.Input;

public class PenStateTrackerTests
{
    private readonly RecordingSink _sink = new();
    private readonly ManualTimeProvider _time = new();
    private readonly PenStateTracker _tracker;

    public PenStateTrackerTests()
    {
        _tracker = new PenStateTracker(_sink, _time);
    }

    private void Frame(PenSample? pen, params TouchSample[] touches) => _tracker.ProcessFrame(pen, [], touches);

    [Fact]
    public void Hover_enter_move_leave()
    {
        Frame(Samples.Hover(10, 10));
        Frame(Samples.Hover(20, 10));
        Frame(null);
        Assert.Equal([ProximityEnter, Move, ProximityLeave], _sink.PenKinds());
        Assert.False(_tracker.IsInRange);
        Assert.Equal(PenTool.None, _tracker.Tool);
    }

    [Fact]
    public void Stroke_produces_down_drag_up()
    {
        Frame(Samples.Hover(10, 10));
        Frame(Samples.Touch(10, 10, 100));
        Frame(Samples.Touch(12, 10, 200));
        Frame(Samples.Touch(12, 10, 300)); // pressure-only change still drags
        Frame(Samples.Hover(12, 10));
        Assert.Equal([ProximityEnter, Down, Drag, Drag, Up], _sink.PenKinds());
        Assert.Equal(300, _sink.Pen[3].Sample.Pressure);
        Assert.True(_tracker.IsInRange);
        Assert.False(_tracker.IsInContact);
    }

    [Fact]
    public void Pen_landing_directly_enters_then_downs()
    {
        Frame(Samples.Touch(5, 5));
        Assert.Equal([ProximityEnter, Down], _sink.PenKinds());
    }

    [Fact]
    public void Leaving_range_while_in_contact_releases_first()
    {
        Frame(Samples.Touch(5, 5, buttons: PenButtons.Primary));
        _sink.Clear();
        Frame(null);
        Assert.Equal([Up, ButtonUp, ProximityLeave], _sink.PenKinds());
        Assert.All(_sink.Pen, e => Assert.False(e.Synthetic));
        Assert.Equal(0, _sink.Pen[0].Sample.Pressure);
    }

    [Fact]
    public void Identical_keepalive_frames_emit_nothing()
    {
        Frame(Samples.Touch(5, 5));
        _sink.Clear();
        Frame(Samples.Touch(5, 5));
        Frame(Samples.Touch(5, 5));
        Assert.Empty(_sink.Pen);
    }

    [Fact]
    public void Button_changes_while_hovering()
    {
        Frame(Samples.Hover(1, 1));
        Frame(Samples.Hover(1, 1, buttons: PenButtons.Primary));
        Frame(Samples.Hover(2, 1, buttons: PenButtons.Primary | PenButtons.Secondary));
        Frame(Samples.Hover(2, 1));
        Assert.Equal([ProximityEnter, ButtonDown, ButtonDown, Move, ButtonUp, ButtonUp], _sink.PenKinds());
        Assert.Equal(PenButtons.Primary, _sink.Pen[1].Button);
        Assert.Equal(PenButtons.Secondary, _sink.Pen[2].Button);
        Assert.Equal(PenButtons.Primary, _sink.Pen[4].Button);
        Assert.Equal(PenButtons.Secondary, _sink.Pen[5].Button);
        Assert.False(_tracker.IsInContact);
    }

    [Fact]
    public void Button_held_on_entry_is_reported_after_enter()
    {
        Frame(Samples.Hover(1, 1, buttons: PenButtons.Primary));
        Assert.Equal([ProximityEnter, ButtonDown], _sink.PenKinds());
        Assert.Equal(PenButtons.Primary, _tracker.Buttons);
    }

    [Fact]
    public void Tool_switch_pen_to_eraser_and_back()
    {
        Frame(Samples.Touch(1, 1));
        Frame(Samples.Touch(1, 1, tool: PenTool.Eraser));
        Frame(Samples.Hover(1, 1, tool: PenTool.Pen));
        Assert.Equal([ProximityEnter, Down, Up, ToolChanged, Down, Up, ToolChanged], _sink.PenKinds());
        Assert.Equal(PenTool.Pen, _sink.Pen[3].PreviousTool);
        Assert.Equal(PenTool.Eraser, _sink.Pen[3].Sample.Tool);
        Assert.Equal(PenTool.Eraser, _sink.Pen[6].PreviousTool);
        Assert.Equal(PenTool.Pen, _tracker.Tool);
    }

    [Fact]
    public void Tool_switch_after_out_of_range_is_a_fresh_enter()
    {
        Frame(Samples.Hover(1, 1));
        Frame(null);
        Frame(Samples.Hover(1, 1, tool: PenTool.Eraser));
        Assert.Equal([ProximityEnter, ProximityLeave, ProximityEnter], _sink.PenKinds());
        Assert.Equal(PenTool.Eraser, _sink.Pen[2].Sample.Tool);
    }

    [Fact]
    public void History_samples_are_applied_oldest_first_before_current()
    {
        Frame(Samples.Hover(0, 0));
        _sink.Clear();
        _tracker.ProcessFrame(Samples.Touch(4, 0), [Samples.Touch(1, 0), Samples.Touch(2, 0), Samples.Touch(3, 0)], []);
        Assert.Equal([Down, Drag, Drag, Drag], _sink.PenKinds());
        Assert.Equal([1, 2, 3, 4], _sink.Pen.Select(e => (int)e.Sample.X));
    }

    [Fact]
    public void Lost_pen_up_is_corrected_by_next_full_state_frame()
    {
        Frame(Samples.Touch(1, 1));
        // The datagram carrying the lift was lost; the next one already shows hover elsewhere.
        Frame(Samples.Hover(50, 50));
        Assert.Equal([ProximityEnter, Down, Up], _sink.PenKinds());
    }

    [Fact]
    public void Watchdog_synthesizes_up_and_leave_after_150ms_of_silence_in_contact()
    {
        Frame(Samples.Touch(7, 8, buttons: PenButtons.Secondary));
        _sink.Clear();
        Assert.Equal(TimeSpan.FromMilliseconds(150), _tracker.WatchdogDueIn);

        _time.Advance(TimeSpan.FromMilliseconds(149));
        Assert.False(_tracker.CheckWatchdog());
        Assert.Equal(TimeSpan.FromMilliseconds(1), _tracker.WatchdogDueIn);
        Assert.Empty(_sink.Pen);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(_tracker.CheckWatchdog());
        Assert.Equal([Up, ButtonUp, ProximityLeave], _sink.PenKinds());
        Assert.All(_sink.Pen, e => Assert.True(e.Synthetic));
        Assert.All(_sink.Pen, e => Assert.Equal(7, e.Sample.X));
        Assert.False(_tracker.IsInRange);
        Assert.Null(_tracker.WatchdogDueIn);
        Assert.False(_tracker.CheckWatchdog());
    }

    [Fact]
    public void Each_frame_rearms_the_watchdog()
    {
        Frame(Samples.Touch(1, 1));
        for (int i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(50));
            Assert.False(_tracker.CheckWatchdog());
            Frame(Samples.Touch(1, 1));
        }

        Assert.True(_tracker.IsInContact);
    }

    [Fact]
    public void Hover_uses_the_longer_hover_timeout()
    {
        Frame(Samples.Hover(1, 1));
        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(_tracker.CheckWatchdog());
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(_tracker.CheckWatchdog());
        Assert.Equal([ProximityEnter, ProximityLeave], _sink.PenKinds());
    }

    [Fact]
    public void Watchdog_is_idle_when_nothing_is_active()
    {
        Assert.Null(_tracker.WatchdogDueIn);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(_tracker.CheckWatchdog());
        Assert.Empty(_sink.Pen);
    }

    [Fact]
    public void Pen_resumes_after_watchdog_with_fresh_enter_and_down()
    {
        Frame(Samples.Touch(1, 1));
        _time.Advance(TimeSpan.FromMilliseconds(200));
        _tracker.CheckWatchdog();
        _sink.Clear();
        Frame(Samples.Touch(2, 2));
        Assert.Equal([ProximityEnter, Down], _sink.PenKinds());
    }

    [Fact]
    public void Watchdog_cancels_active_gesture_as_touches_up()
    {
        Frame(null, new TouchSample(1, true, 10000, 10000), new TouchSample(2, true, 20000, 10000));
        Frame(null, new TouchSample(1, true, 10000, 20000), new TouchSample(2, true, 20000, 20000));
        Assert.Equal(GesturePhase.Began, Assert.Single(_sink.Gestures).Phase);
        Assert.Equal(TimeSpan.FromMilliseconds(150), _tracker.WatchdogDueIn);

        _time.Advance(TimeSpan.FromMilliseconds(150));
        Assert.True(_tracker.CheckWatchdog());
        GestureEvent last = _sink.Gestures[^1];
        Assert.Equal(GesturePhase.Cancelled, last.Phase);
        Assert.True(last.Synthetic);
        Assert.Null(_tracker.WatchdogDueIn);
    }

    [Fact]
    public void Pen_entering_range_cancels_gesture()
    {
        Frame(null, new TouchSample(1, true, 10000, 10000), new TouchSample(2, true, 20000, 10000));
        Frame(null, new TouchSample(1, true, 10000, 20000), new TouchSample(2, true, 20000, 20000));
        Frame(Samples.Hover(5, 5), new TouchSample(1, true, 10000, 25000), new TouchSample(2, true, 20000, 25000));
        Assert.Equal([GesturePhase.Began, GesturePhase.Cancelled], _sink.Gestures.Select(g => g.Phase));
        Assert.False(_sink.Gestures[1].Synthetic);
        Assert.Equal([ProximityEnter], _sink.PenKinds());
    }

    [Fact]
    public void Events_carry_mapped_display_positions()
    {
        _tracker.Mapper = new CoordinateMapper(1600, 1000, new DisplayRect(100, 50, 1440, 900));
        Frame(Samples.Hover(0, 0));
        Assert.Equal(new DisplayPoint(100, 50), _sink.Pen[0].Position);
        Assert.Equal(_tracker.Mapper.ContentRect, _tracker.Gestures.Mapper.ContentRect);
    }

    [Fact]
    public void Unconfigured_mapper_falls_back_to_identity()
    {
        _tracker.Mapper = default;
        Assert.True(_tracker.Mapper.IsConfigured);
    }

    [Fact]
    public void Reset_releases_everything_synthetically()
    {
        Frame(Samples.Touch(1, 1));
        _sink.Clear();
        _tracker.Reset();
        Assert.Equal([Up, ProximityLeave], _sink.PenKinds());
        Assert.All(_sink.Pen, e => Assert.True(e.Synthetic));
    }

    [Fact]
    public void Tracker_and_gestures_are_allocation_free_per_frame()
    {
        var sink = new CountingSink();
        var time = new ManualTimeProvider();
        var tracker = new PenStateTracker(sink, time)
        {
            Mapper = new CoordinateMapper(1440, 3088, new DisplayRect(0, 0, 1920, 1080)),
        };
        PenSample[] history = [Samples.Touch(1, 1), Samples.Touch(2, 2)];
        TouchSample[] touches = new TouchSample[2];

        long bytes = Alloc.Measure(5_000, i =>
        {
            int phase = i % 100;
            if (phase < 40)
            {
                PenSample? pen = phase < 5 ? Samples.Hover((ushort)i, 3)
                    : Samples.Touch((ushort)i, 3, (ushort)(i * 7), buttons: (phase & 8) != 0 ? PenButtons.Primary : PenButtons.None,
                        tool: phase > 30 ? PenTool.Eraser : PenTool.Pen);
                tracker.ProcessFrame(pen, history, []);
            }
            else if (phase == 40)
            {
                tracker.ProcessFrame(null, [], []);
            }
            else if (phase < 90)
            {
                touches[0] = new TouchSample(1, true, 10000, (ushort)(10000 + (phase * 200)));
                touches[1] = new TouchSample(2, true, (ushort)(20000 + (phase * 100)), (ushort)(10000 + (phase * 200)));
                tracker.ProcessFrame(null, [], touches);
            }
            else
            {
                time.Advance(TimeSpan.FromMilliseconds(200));
                _ = tracker.WatchdogDueIn;
                tracker.CheckWatchdog();
            }
        });

        Assert.Equal(0, bytes);
        Assert.True(sink.Pen > 0);
        Assert.True(sink.Gestures > 0);
    }

    private sealed class CountingSink : IInputSink
    {
        public int Pen { get; private set; }

        public int Gestures { get; private set; }

        public void OnPenEvent(in PenEvent e) => Pen++;

        public void OnGestureEvent(in GestureEvent e) => Gestures++;
    }
}
