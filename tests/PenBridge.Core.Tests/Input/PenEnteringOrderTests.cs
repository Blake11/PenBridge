using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// The pen (or a one-finger cursor) coming into range ends the gestures before its own events
/// (<see cref="GestureRecognizer.PenEntering"/>): a held Tap right-clicks and a scroll ends at the cursor the pen then moves.
/// </summary>
[Collection(AllocationGroup.Name)]
public class PenEnteringOrderTests
{
    private static readonly CoordinateMapper Square = new(1, 1, new DisplayRect(0, 0, 65535, 65535));
    private static readonly TouchSample[] Pair = [new(1, true, 10000, 10000), new(2, true, 10200, 10000)];

    private readonly OrderedSink _sink = new();
    private readonly PenStateTracker _tracker;

    public PenEnteringOrderTests()
    {
        _tracker = new PenStateTracker(_sink, new FakeTime()) { Mapper = Square };
    }

    private string[] Names() => _sink.Events.Select(e => e.Name).ToArray();

    [Fact]
    public void Held_tap_comes_before_a_cursor_click_replayed_in_one_frame()
    {
        _tracker.ProcessFrame(10_000, null, [], Pair);
        _tracker.ProcessFrame(60_000, null, [], []); // tap held for the double-tap window

        // A one-finger tap: hover, contact, then out of range, all in one datagram.
        PenSample hover = Samples.Hover(30000, 30000, PenTool.FingerFallback);
        _tracker.ProcessFrame(160_000, hover with { State = PenState.None }, [hover, Samples.Touch(30000, 30000, 0x8000, PenTool.FingerFallback)], []);

        Assert.Equal(["TouchDown", "Tap", "ProximityEnter", "Down", "Up", "ProximityLeave"], Names());
        Assert.True(_sink.Events[1].Gesture.TimestampUs <= _sink.Events[2].Pen.TimestampUs);
        Assert.Null(_tracker.Gestures.PendingDeadlineUs);
    }

    [Fact]
    public void Held_tap_comes_before_the_cursor_hover()
    {
        _tracker.ProcessFrame(10_000, null, [], Pair);
        _tracker.ProcessFrame(60_000, null, [], []);
        _tracker.ProcessFrame(160_000, Samples.Hover(30000, 30000, PenTool.FingerFallback), [], []);

        Assert.Equal(["TouchDown", "Tap", "ProximityEnter"], Names());
    }

    [Fact]
    public void Active_scroll_is_cancelled_before_the_pen_enters()
    {
        _tracker.ProcessFrame(10_000, null, [], [new(1, true, 10000, 10000), new(2, true, 20000, 10000)]);
        _tracker.ProcessFrame(20_000, null, [], [new(1, true, 10000, 20000), new(2, true, 20000, 20000)]);
        _tracker.ProcessFrame(30_000, Samples.Hover(5, 5), [], []);
        _tracker.ProcessFrame(40_000, Samples.Hover(6, 5), [], [new(1, true, 10000, 25000), new(2, true, 20000, 25000)]);
        _tracker.ProcessFrame(50_000, null, [], [new(1, true, 10000, 30000), new(2, true, 20000, 30000)]); // still blocked

        Assert.Equal(["TouchDown", "Scroll.Began", "Scroll.Cancelled", "ProximityEnter", "Move", "ProximityLeave"], Names());
        Assert.False(_tracker.Gestures.IsActive);
    }

    [Fact]
    public void Pen_with_unknown_tool_does_not_interrupt()
    {
        _tracker.ProcessFrame(10_000, null, [], Pair);
        _tracker.ProcessFrame(60_000, null, [], []);
        _tracker.ProcessFrame(100_000, Samples.Hover(5, 5, PenTool.None), [], []); // ignored sample

        Assert.Equal(["TouchDown"], Names());
        Assert.NotNull(_tracker.Gestures.PendingDeadlineUs);
    }

    [Fact]
    public void Pen_entering_is_allocation_free()
    {
        var sink = new CountingSink();
        var tracker = new PenStateTracker(sink, new FakeTime()) { Mapper = Square };
        TouchSample[] moved = [new(1, true, 10000, 20000), new(2, true, 20000, 20000)];
        TouchSample[] apart = [new(1, true, 10000, 10000), new(2, true, 20000, 10000)];
        PenSample hover = Samples.Hover(30000, 30000, PenTool.FingerFallback);
        PenSample[] click = [hover, Samples.Touch(30000, 30000, 0x8000, PenTool.FingerFallback)];
        PenSample gone = hover with { State = PenState.None };
        uint t = 0;

        Alloc.AssertZero(2_000, i =>
        {
            t += 10_000;
            switch (i % 8)
            {
                case 0: tracker.ProcessFrame(t, null, [], Pair); break; // two-finger tap
                case 1: tracker.ProcessFrame(t, null, [], []); break; // held
                case 2: tracker.ProcessFrame(t, gone, click, []); break; // a click: the tap fires first
                case 3: tracker.ProcessFrame(t, null, [], apart); break;
                case 4: tracker.ProcessFrame(t, null, [], moved); break; // scroll
                case 5: tracker.ProcessFrame(t, hover, [], []); break; // cursor: cancels it first
                default: tracker.ProcessFrame(t, null, [], []); break;
            }
        }, () => sink.Taps, () => sink.Cancels);
    }

    private sealed class CountingSink : IInputSink
    {
        public long Taps { get; private set; }

        public long Cancels { get; private set; }

        public void OnPenEvent(in PenEvent e)
        {
        }

        public void OnGestureEvent(in GestureEvent e)
        {
            Taps += e.Kind == GestureKind.Tap ? 1 : 0;
            Cancels += e.Phase == GesturePhase.Cancelled ? 1 : 0;
        }
    }
}
