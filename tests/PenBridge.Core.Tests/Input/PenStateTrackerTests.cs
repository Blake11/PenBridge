using PenBridge.Core.Input;
using static PenBridge.Core.Input.PenEventKind;

namespace PenBridge.Core.Tests.Input;

[Collection(AllocationGroup.Name)]
public class PenStateTrackerTests
{
    private readonly RecordingSink _sink = new();
    private readonly ManualTimeProvider _time = new();
    private static readonly CoordinateMapper Square = new(1, 1, new DisplayRect(0, 0, 65535, 65535));
    private readonly PenStateTracker _tracker;
    private uint _timeUs = 10_000;

    public PenStateTrackerTests()
    {
        _tracker = new PenStateTracker(_sink, _time) { Mapper = Square };
    }

    private void Frame(PenSample? pen, params TouchSample[] touches)
    {
        _tracker.ProcessFrame(_timeUs, pen, [], touches);
        _timeUs += 5_000;
    }

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
        _tracker.ProcessFrame(_timeUs, Samples.Touch(4, 0), [Samples.Touch(1, 0), Samples.Touch(2, 0), Samples.Touch(3, 0)], []);
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
        Assert.Equal([GestureKind.TouchDown, GestureKind.Scroll], _sink.Gestures.Select(g => g.Kind));
        Assert.Equal(GesturePhase.Began, _sink.Gestures[1].Phase);
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
        Assert.Equal([GestureKind.TouchDown, GestureKind.Scroll, GestureKind.Scroll], _sink.Gestures.Select(g => g.Kind));
        Assert.Equal([GesturePhase.Began, GesturePhase.Began, GesturePhase.Cancelled], _sink.Gestures.Select(g => g.Phase));
        Assert.False(_sink.Gestures[2].Synthetic);
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
    public void Frames_are_dropped_until_a_mapper_is_configured()
    {
        var tracker = new PenStateTracker(_sink, _time);
        Assert.False(tracker.IsMapperConfigured);
        tracker.ProcessFrame(0, Samples.Touch(1, 1), [], [new TouchSample(1, true, 1, 1), new TouchSample(2, true, 9, 9)]);
        Assert.Empty(_sink.Pen);
        Assert.False(tracker.IsInRange);
        Assert.Null(tracker.WatchdogDueIn);

        tracker.Mapper = Square;
        Assert.True(tracker.IsMapperConfigured);
        tracker.ProcessFrame(1, Samples.Touch(1, 1), [], []);
        Assert.Equal([ProximityEnter, Down], _sink.PenKinds());
    }

    [Fact]
    public void In_range_sample_with_unknown_tool_is_ignored()
    {
        Frame(Samples.Hover(1, 1, tool: PenTool.None));
        Frame(Samples.Touch(1, 1, tool: (PenTool)42));
        Assert.Empty(_sink.Pen);
        Assert.False(_tracker.IsInRange);

        Frame(Samples.Touch(1, 1));
        Frame(Samples.Touch(9, 9, tool: PenTool.None));
        _tracker.ProcessFrame(_timeUs, Samples.Touch(1, 1), [Samples.Hover(5, 5, tool: PenTool.None)], []);
        Assert.Equal([ProximityEnter, Down], _sink.PenKinds());
        Assert.True(_tracker.IsInContact);
        Assert.Equal(PenTool.Pen, _tracker.Tool);
    }

    [Fact]
    public void Finger_fallback_tool_is_accepted()
    {
        Frame(Samples.Touch(1, 1, tool: PenTool.FingerFallback));
        Assert.Equal([ProximityEnter, Down], _sink.PenKinds());
    }

    [Fact]
    public void Events_carry_frame_time()
    {
        _tracker.ProcessFrame(1234, Samples.Hover(1, 1), [], []);
        _tracker.ProcessFrame(2000, Samples.Hover(2, 1), [], []);
        Assert.Equal([1234L, 2000L], _sink.Pen.Select(e => e.TimestampUs));
    }

    [Fact]
    public void Time_unwraps_across_u32_overflow()
    {
        _tracker.ProcessFrame(uint.MaxValue - 99, Samples.Hover(1, 1), [], []);
        _tracker.ProcessFrame(100, Samples.Hover(2, 1), [], []);
        Assert.Equal((long)uint.MaxValue - 99 + 200, _sink.Pen[1].TimestampUs);
    }

    [Fact]
    public void Backwards_time_step_holds_the_clock()
    {
        _tracker.ProcessFrame(5000, Samples.Hover(1, 1), [], []);
        _tracker.ProcessFrame(4000, Samples.Hover(2, 1), [], []);
        _tracker.ProcessFrame(4500, Samples.Hover(3, 1), [], []);
        Assert.Equal([5000L, 5000L, 5500L], _sink.Pen.Select(e => e.TimestampUs));
    }

    [Fact]
    public void History_samples_are_spaced_at_nominal_interval_before_frame_time()
    {
        _tracker.ProcessFrame(100_000, Samples.Hover(0, 0), [], []);
        _sink.Clear();
        _tracker.ProcessFrame(120_000, Samples.Hover(4, 0), [Samples.Hover(1, 0), Samples.Hover(2, 0), Samples.Hover(3, 0)], []);
        Assert.Equal([120_000L - (3 * 4167), 120_000L - (2 * 4167), 120_000L - 4167, 120_000L], _sink.Pen.Select(e => e.TimestampUs));
    }

    [Fact]
    public void History_spacing_is_compressed_when_frames_are_close()
    {
        _tracker.ProcessFrame(100_000, Samples.Hover(0, 0), [], []);
        _sink.Clear();
        _tracker.ProcessFrame(108_000, Samples.Hover(4, 0), [Samples.Hover(1, 0), Samples.Hover(2, 0), Samples.Hover(3, 0)], []);
        Assert.Equal([102_000L, 104_000L, 106_000L, 108_000L], _sink.Pen.Select(e => e.TimestampUs));
    }

    [Fact]
    public void Watchdog_events_are_stamped_at_frame_time_plus_elapsed()
    {
        _tracker.ProcessFrame(50_000, Samples.Touch(1, 1), [], []);
        _sink.Clear();
        _time.Advance(TimeSpan.FromMilliseconds(150));
        _tracker.CheckWatchdog();
        Assert.All(_sink.Pen, e => Assert.Equal(200_000, e.TimestampUs));
    }

    [Fact]
    public void Held_tap_deadline_drives_the_timer_and_fires_without_frames()
    {
        // Square maps 1 normalized unit to 1 point; fingers 200 pt apart.
        Frame(null, new TouchSample(1, true, 10000, 10000), new TouchSample(2, true, 10200, 10000)); // t = 10 ms
        Frame(null); // lift at 15 ms: the tap is held for the 300 ms window + 50 ms jitter allowance
        Assert.DoesNotContain(_sink.Gestures, g => g.Kind == GestureKind.Tap);
        Assert.Equal(TimeSpan.FromMilliseconds(350), _tracker.WatchdogDueIn);

        _time.Advance(TimeSpan.FromMilliseconds(349));
        Assert.False(_tracker.CheckWatchdog());
        Assert.DoesNotContain(_sink.Gestures, g => g.Kind == GestureKind.Tap);
        Assert.Equal(TimeSpan.FromMilliseconds(1), _tracker.WatchdogDueIn);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(_tracker.CheckWatchdog()); // the watchdog itself didn't fire
        GestureEvent tap = Assert.Single(_sink.Gestures, g => g.Kind == GestureKind.Tap);
        Assert.Equal(365_000, tap.TimestampUs);
        Assert.Null(_tracker.WatchdogDueIn); // idle again: no timer
    }

    [Fact]
    public void Watchdog_comes_first_when_it_is_sooner_than_the_tap_deadline()
    {
        Frame(null, new TouchSample(1, true, 10000, 10000), new TouchSample(2, true, 10200, 10000));
        Frame(null); // held tap: deadline in 300 ms
        Frame(null, new TouchSample(1, true, 10000, 10000), new TouchSample(2, true, 10200, 10000)); // second touch: 200 ms + 1 µs
        Assert.Equal(TimeSpan.FromMilliseconds(150), _tracker.WatchdogDueIn); // contact watchdog
    }

    [Fact]
    public void Delayed_lift_datagram_still_completes_a_smart_zoom()
    {
        TouchSample[] pair = [new TouchSample(1, true, 10000, 10000), new TouchSample(2, true, 10200, 10000)];
        _tracker.ProcessFrame(10_000, null, [], pair);
        _tracker.ProcessFrame(60_000, null, [], []); // first tap lifts
        _tracker.ProcessFrame(160_000, null, [], pair); // second tap lands
        Assert.Equal(TimeSpan.FromMilliseconds(150), _tracker.WatchdogDueIn); // only the contact watchdog

        _time.Advance(TimeSpan.FromMilliseconds(140)); // the lift datagram is held up by Wi-Fi jitter
        Assert.False(_tracker.CheckWatchdog());
        _tracker.ProcessFrame(210_000, null, [], []); // the lift: 50 ms after landing on the phone's clock

        Assert.Equal([GestureKind.SmartZoom], _sink.Gestures.Where(g => g.Kind is GestureKind.Tap or GestureKind.SmartZoom).Select(g => g.Kind));
    }

    [Fact]
    public void Reset_drops_a_held_tap()
    {
        Frame(null, new TouchSample(1, true, 10000, 10000), new TouchSample(2, true, 10200, 10000));
        Frame(null);
        _tracker.Reset();
        Assert.Null(_tracker.WatchdogDueIn);
        _time.Advance(TimeSpan.FromSeconds(1));
        _tracker.CheckWatchdog();
        Assert.DoesNotContain(_sink.Gestures, g => g.Kind == GestureKind.Tap);
    }

    [Fact]
    public void Gesture_events_share_the_frame_clock()
    {
        _tracker.ProcessFrame(7_000, null, [], [new TouchSample(1, true, 10000, 10000), new TouchSample(2, true, 20000, 10000)]);
        _tracker.ProcessFrame(9_000, null, [], [new TouchSample(1, true, 10000, 20000), new TouchSample(2, true, 20000, 20000)]);
        Assert.Equal([7_000L, 9_000L], _sink.Gestures.Select(g => g.TimestampUs)); // TouchDown, then scroll Began
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

        Alloc.AssertZero(5_000, i =>
        {
            int phase = i % 100;
            if (phase < 40)
            {
                PenSample? pen = phase < 5 ? Samples.Hover((ushort)i, 3)
                    : Samples.Touch((ushort)i, 3, (ushort)(i * 7), buttons: (phase & 8) != 0 ? PenButtons.Primary : PenButtons.None,
                        tool: phase > 30 ? PenTool.Eraser : PenTool.Pen);
                tracker.ProcessFrame((uint)(i * 5000), pen, history, []);
            }
            else if (phase == 40)
            {
                tracker.ProcessFrame((uint)(i * 5000), null, [], []);
            }
            else if (phase < 90)
            {
                touches[0] = new TouchSample(1, true, 10000, (ushort)(10000 + (phase * 200)));
                touches[1] = new TouchSample(2, true, (ushort)(20000 + (phase * 100)), (ushort)(10000 + (phase * 200)));
                tracker.ProcessFrame((uint)(i * 5000), null, [], touches);
            }
            else
            {
                time.Advance(TimeSpan.FromMilliseconds(200));
                _ = tracker.WatchdogDueIn;
                tracker.CheckWatchdog();
            }
        }, () => sink.Pen, () => sink.Gestures);
    }

    private sealed class CountingSink : IInputSink
    {
        public int Pen { get; private set; }

        public int Gestures { get; private set; }

        public void OnPenEvent(in PenEvent e) => Pen++;

        public void OnGestureEvent(in GestureEvent e) => Gestures++;
    }
}
