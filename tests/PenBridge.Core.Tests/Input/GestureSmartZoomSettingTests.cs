using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Input;

/// <summary>The smart-zoom switch (ADR-0029): off makes a two-finger tap a Tap on lift, on holds it for the window.</summary>
public class GestureSmartZoomSettingTests
{
    // Square 1000x1000 surface onto a 1000x1000 display: double-tap slop 20% = 200 pt, window 200 ms, tap 300 ms.
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));
    private readonly RecordingSink _sink = new();
    private readonly GestureRecognizer _rec;
    private bool _smartZoom;
    private int _reads;

    public GestureSmartZoomSettingTests()
    {
        _rec = new GestureRecognizer(_sink, new GestureRecognizerOptions { SmartZoomEnabled = Read }) { Mapper = Square };
    }

    private bool Read()
    {
        _reads++;
        return _smartZoom;
    }

    private List<GestureEvent> Gestures => _sink.Gestures.Where(g => g.Kind != GestureKind.TouchDown).ToList();

    private GestureKind[] Kinds() => Gestures.Select(g => g.Kind).ToArray();

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    private static TouchSample F(byte id, double x, double y) => new(id, true, N(x), N(y));

    private static long Ms(double ms) => (long)(ms * 1000);

    /// <summary>A two-finger tap with its midpoint at (cx, 500): lands at <paramref name="ms"/>, lifts 50 ms later.</summary>
    private static void Tap(GestureRecognizer rec, double ms, double cx = 500)
    {
        rec.Process([F(1, cx - 100, 500), F(2, cx + 100, 500)], false, Ms(ms));
        rec.Process([F(1, cx - 100, 500), F(2, cx + 100, 500)], false, Ms(ms + 25));
        rec.Process([], false, Ms(ms + 50));
    }

    private void Tap(double ms, double cx = 500) => Tap(_rec, ms, cx);

    [Fact]
    public void Off_a_tap_is_a_secondary_click_on_lift_with_no_hold()
    {
        Tap(0);
        GestureEvent tap = Assert.Single(Gestures);
        Assert.Equal(GestureKind.Tap, tap.Kind);
        Assert.Equal(Ms(50), tap.TimestampUs);
        Assert.Equal(500, tap.Center.X, 0.1);
        Assert.Null(_rec.PendingDeadlineUs);
    }

    [Fact]
    public void Off_a_double_tap_is_two_taps_each_on_its_lift()
    {
        Tap(0);
        Tap(150, cx: 530);
        Assert.Equal([GestureKind.Tap, GestureKind.Tap], Kinds());
        Assert.Equal(Ms(200), Gestures[1].TimestampUs);
        Assert.Null(_rec.PendingDeadlineUs);
    }

    [Fact]
    public void On_a_tap_is_held_for_200_ms_then_taps()
    {
        _smartZoom = true;
        Tap(0);
        Assert.Empty(Gestures);
        Assert.Equal(Ms(50 + 200 + 20), _rec.PendingDeadlineUs);
        _rec.Process([], false, Ms(249)); // a frame inside the window keeps it held
        Assert.Empty(Gestures);
        _rec.Process([], false, Ms(250)); // the window passed on the frame clock
        Assert.Equal([GestureKind.Tap], Kinds());
    }

    [Fact]
    public void On_a_second_tap_within_200_ms_is_a_smart_zoom()
    {
        _smartZoom = true;
        Tap(0);
        Tap(240, cx: 530); // lands 190 ms after the first lift
        Assert.Equal([GestureKind.SmartZoom], Kinds());
        Assert.Null(_rec.PendingDeadlineUs);
    }

    [Fact]
    public void Default_is_on()
    {
        var rec = new GestureRecognizer(_sink) { Mapper = Square };
        Tap(rec, 0);
        Assert.Empty(Gestures);
        Assert.NotNull(rec.PendingDeadlineUs);
        Tap(rec, 200);
        Assert.Equal([GestureKind.SmartZoom], Kinds());
    }

    [Fact]
    public void The_setting_is_read_once_per_decided_tap_not_per_frame()
    {
        Tap(0);
        Assert.Equal(1, _reads);
        _rec.Process([F(1, 300, 500)], false, Ms(500)); // one finger: no two-finger tap
        _rec.Process([F(1, 300, 600)], false, Ms(520));
        _rec.Process([], false, Ms(540));
        _rec.Process([F(1, 300, 500), F(2, 700, 500)], false, Ms(600)); // scroll: no tap
        for (int i = 1; i <= 10; i++)
        {
            _rec.Process([F(1, 300, 500 + (i * 20)), F(2, 700, 500 + (i * 20))], false, Ms(600 + (i * 8)));
        }

        _rec.Process([], false, Ms(700));
        Assert.Equal(1, _reads);
    }

    [Fact]
    public void Turned_off_while_a_tap_is_held_the_next_tap_flushes_it_and_taps_at_once()
    {
        _smartZoom = true;
        Tap(0); // held
        _smartZoom = false;
        Tap(150, cx: 530); // would have been a smart zoom
        Assert.Equal([GestureKind.Tap, GestureKind.Tap], Kinds());
        Assert.Equal(500, Gestures[0].Center.X, 0.1);
        Assert.Equal(Ms(200), Gestures[0].TimestampUs); // flushed at the lift, before the new tap
        Assert.Equal(530, Gestures[1].Center.X, 0.1);
        Assert.Null(_rec.PendingDeadlineUs);
    }

    [Fact]
    public void Turned_off_while_a_tap_is_held_with_no_next_tap_it_fires_at_its_deadline()
    {
        _smartZoom = true;
        Tap(0);
        _smartZoom = false;
        Assert.Equal(Ms(270), _rec.PendingDeadlineUs);
        _rec.CheckDeadline(Ms(270));
        Assert.Equal([GestureKind.Tap], Kinds());
    }

    [Fact]
    public void Turned_on_after_an_immediate_tap_the_next_tap_is_held()
    {
        Tap(0);
        _smartZoom = true;
        Tap(150); // the first was already emitted: this one can't complete a smart zoom
        Assert.Equal([GestureKind.Tap], Kinds());
        Assert.Equal(Ms(200 + 200 + 20), _rec.PendingDeadlineUs);
    }

    [Fact]
    public void A_zero_window_never_reads_the_setting()
    {
        var rec = new GestureRecognizer(_sink, new GestureRecognizerOptions { DoubleTapWindow = TimeSpan.Zero, SmartZoomEnabled = Read })
        {
            Mapper = Square,
        };
        _smartZoom = true;
        Tap(rec, 0);
        Assert.Equal([GestureKind.Tap], Kinds());
        Assert.Equal(0, _reads);
    }

    [Fact]
    public void Off_the_tracker_arms_no_tap_timer()
    {
        var time = new ManualTimeProvider();
        var tracker = new PenStateTracker(_sink, time, new PenStateTrackerOptions { Gestures = new GestureRecognizerOptions { SmartZoomEnabled = Read } })
        {
            Mapper = new CoordinateMapper(1, 1, new DisplayRect(0, 0, 65535, 65535)),
        };
        tracker.ProcessFrame(10_000, null, [], [new TouchSample(1, true, 10000, 10000), new TouchSample(2, true, 10200, 10000)]);
        tracker.ProcessFrame(15_000, null, [], []);
        Assert.Equal([GestureKind.Tap], Kinds());
        Assert.Null(tracker.WatchdogDueIn);
    }

    [Fact]
    public void Session_options_keep_the_setting_when_the_one_finger_cursor_is_negotiated()
    {
        Func<bool> setting = Read;
        var options = new PenStateTrackerOptions { Gestures = new GestureRecognizerOptions { SmartZoomEnabled = setting } };
        Assert.Same(setting, SessionInput.WithNegotiated(options, ProtocolCapabilities.FingerTouches).Gestures.SmartZoomEnabled);
        Assert.Same(setting, SessionInput.WithNegotiated(options, ProtocolCapabilities.None).Gestures.SmartZoomEnabled);
    }
}
