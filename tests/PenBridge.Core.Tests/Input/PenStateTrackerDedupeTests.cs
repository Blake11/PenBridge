using PenBridge.Core.Input;
using static PenBridge.Core.Input.PenEventKind;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// Sample dedupe (#92, <c>ProtocolCapabilities.SampleDedupe</c>): a frame whose time doesn't advance past the last
/// applied frame's applies its current sample but not its history, so a resent click frame clicks once.
/// </summary>
[Collection(AllocationGroup.Name)]
public class PenStateTrackerDedupeTests
{
    private const ushort X = 30000;
    private const ushort Y = 20000;
    private static readonly CoordinateMapper Square = new(1, 1, new DisplayRect(0, 0, 65535, 65535));
    private static readonly PenSample[] Tap =
    [
        Samples.Hover(X, Y, PenTool.FingerFallback),
        Samples.Touch(X, Y, tool: PenTool.FingerFallback),
    ];

    private static readonly PenSample Gone = Samples.Hover(X, Y, PenTool.FingerFallback) with { State = PenState.None };

    private readonly RecordingSink _sink = new();
    private readonly FakeTime _time = new();
    private readonly PenStateTracker _tracker;

    public PenStateTrackerDedupeTests()
    {
        _tracker = new PenStateTracker(_sink, _time) { Mapper = Square };
    }

    private static readonly PenEventKind[] OneClick = [ProximityEnter, Down, Up, ProximityLeave];

    [Fact]
    public void A_resent_click_frame_after_the_original_clicks_once()
    {
        _tracker.ProcessFrame(10_000, Gone, Tap, []);
        _tracker.ProcessFrame(10_000, Gone, Tap, []); // release repeat 1
        _tracker.ProcessFrame(10_000, Gone, Tap, []); // release repeat 2

        Assert.Equal(OneClick, _sink.PenKinds());
    }

    [Fact]
    public void A_lost_original_is_recovered_by_its_resend_at_the_original_time()
    {
        _tracker.ProcessFrame(5_000, null, [], []);    // an earlier, empty frame
        _tracker.ProcessFrame(10_000, Gone, Tap, []);  // the resend (the original was lost)
        _tracker.ProcessFrame(10_000, Gone, Tap, []);

        Assert.Equal(OneClick, _sink.PenKinds());
        Assert.All(_sink.Pen, e => Assert.InRange(e.TimestampUs, long.MinValue, 10_000));
        Assert.InRange(_sink.Pen[1].TimestampUs, long.MinValue, 9_999); // the Down is placed before the frame time, as in the original
    }

    [Fact]
    public void A_double_tap_with_resends_clicks_twice()
    {
        _tracker.ProcessFrame(10_000, Gone, Tap, []);
        _tracker.ProcessFrame(10_000, Gone, Tap, []);
        _tracker.ProcessFrame(150_000, Gone, Tap, []);
        _tracker.ProcessFrame(150_000, Gone, Tap, []);

        Assert.Equal([.. OneClick, .. OneClick], _sink.PenKinds());
    }

    [Fact]
    public void A_frame_older_than_the_last_one_applies_no_history()
    {
        _tracker.ProcessFrame(20_000, null, [], []);
        _tracker.ProcessFrame(10_000, Gone, Tap, []); // reordered: an earlier frame arriving late

        Assert.Empty(_sink.Pen);
    }

    [Fact]
    public void Fresh_frames_still_apply_their_history()
    {
        _tracker.ProcessFrame(10_000, Gone, Tap, []);
        _tracker.ProcessFrame(10_001, Gone, Tap, []);

        Assert.Equal([.. OneClick, .. OneClick], _sink.PenKinds());
    }

    [Fact]
    public void The_current_sample_of_a_resent_frame_is_still_applied()
    {
        _tracker.ProcessFrame(10_000, Samples.Hover(X, Y), [], []);
        _tracker.ProcessFrame(10_000, null, [], []); // same time, but the state changed: still the full state

        Assert.Equal([ProximityEnter, ProximityLeave], _sink.PenKinds());
    }

    [Fact]
    public void A_new_session_starts_a_new_watermark()
    {
        _tracker.ProcessFrame(50_000, Gone, Tap, []);
        var next = new PenStateTracker(_sink, _time) { Mapper = Square }; // a new session has a new tracker
        next.ProcessFrame(1_000, Gone, Tap, []);

        Assert.Equal([.. OneClick, .. OneClick], _sink.PenKinds());
    }

    [Fact]
    public void A_surface_change_keeps_the_watermark()
    {
        // The phone clock runs on across a SurfaceInfo (rotation), so a resend straddling it must still dedupe.
        _tracker.ProcessFrame(10_000, Gone, Tap, []);
        _tracker.Mapper = new CoordinateMapper(2, 1, new DisplayRect(0, 0, 65535, 65535));
        _tracker.ProcessFrame(10_000, Gone, Tap, []);

        Assert.Equal(OneClick, _sink.PenKinds());
    }

    [Fact]
    public void Deduped_frames_are_allocation_free()
    {
        var tracker = new PenStateTracker(new NullSink(), new FakeTime()) { Mapper = Square };
        PenSample[] tap = Tap;
        long frames = 0;

        Alloc.AssertZero(2_000, i =>
        {
            uint t = (uint)((i / 3) * 100_000);
            tracker.ProcessFrame(t, Gone, tap, []); // original, then two resends at the same time
            frames++;
        }, () => frames);
    }

    private sealed class NullSink : IInputSink
    {
        public void OnPenEvent(in PenEvent e)
        {
        }

        public void OnGestureEvent(in GestureEvent e)
        {
        }
    }
}
