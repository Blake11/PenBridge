using PenBridge.Core.Input;
using static PenBridge.Core.Input.PenEventKind;

namespace PenBridge.Core.Tests.Input;

/// <summary>ADR-0032 in the tracker: pressure curve, click threshold, Mouse mode and when settings take effect.</summary>
[Collection(AllocationGroup.Name)]
public class PenSettingsTrackerTests
{
    private readonly FakeTime _time = new();
    private readonly LocatingSink _sink = new();
    private readonly PenStateTracker _tracker;
    private uint _timeUs = 10_000;

    // 1000x1000 surface onto a 1000x1000 pt display; the surface is 100 mm square (655.35 units per mm).
    public PenSettingsTrackerTests()
    {
        _tracker = new PenStateTracker(_sink, _time) { Mapper = new CoordinateMapper(1000, 1000, new DisplayRect(0, 0, 1000, 1000)) };
        _tracker.SetSurfaceSize(100, 100);
    }

    private static ushort Mm(double mm) => (ushort)Math.Round(mm * 655.35);

    private static ushort P(double p) => (ushort)Math.Round(p * 65535);

    private void Frame(PenSample? pen, uint stepUs = 5_000)
    {
        _tracker.ProcessFrame(_timeUs, pen, [], []);
        _timeUs += stepUs;
    }

    private PenEvent Last => _sink.Pen[^1];

    private static readonly InputSettings MouseLinear = new() { StylusMode = StylusMode.Mouse, MouseAcceleration = false };

    [Fact]
    public void Pressure_curve_is_applied_to_pen_and_eraser_but_not_the_finger_fallback()
    {
        _tracker.ApplySettings(new InputSettings { TipFeel = 1 });
        Frame(Samples.Touch(Mm(10), Mm(10), P(0.5)));
        Assert.Equal(0.25, Last.Sample.Pressure / 65535.0, 3);
        Frame(Samples.Touch(Mm(10), Mm(10), P(0.5), PenTool.Eraser));
        Assert.Equal(0.25, Last.Sample.Pressure / 65535.0, 3);
        Frame(Samples.Touch(Mm(10), Mm(10), P(0.5), PenTool.FingerFallback));
        Assert.Equal(P(0.5), Last.Sample.Pressure);
    }

    [Fact]
    public void Below_the_click_threshold_the_pen_hovers()
    {
        _tracker.ApplySettings(new InputSettings { ClickThreshold = 0.3, FullPressureAt = 0.9 });
        Frame(Samples.Touch(Mm(10), Mm(10), P(0.2)));
        Assert.Equal([ProximityEnter], _sink.PenKinds());
        Assert.False(_tracker.IsInContact);
        Frame(Samples.Touch(Mm(10), Mm(10), P(0.6)));
        Assert.Equal([ProximityEnter, Down], _sink.PenKinds());
        Assert.Equal(0.5, Last.Sample.Pressure / 65535.0, 3);
        Frame(Samples.Touch(Mm(10), Mm(10), P(0.95)));
        Assert.Equal(65535, Last.Sample.Pressure);
        Frame(Samples.Touch(Mm(10), Mm(10), P(0.1))); // back below: lifts
        Assert.Equal(Up, Last.Kind);
    }

    [Fact]
    public void Click_threshold_has_a_release_hysteresis()
    {
        _tracker.ApplySettings(new InputSettings { ClickThreshold = 0.3, FullPressureAt = 0.9 });
        Frame(Samples.Touch(Mm(10), Mm(10), P(0.31)));
        Assert.Equal([ProximityEnter, Down], _sink.PenKinds());
        Frame(Samples.Touch(Mm(11), Mm(10), P(0.29))); // flickers around the threshold: stays down
        Frame(Samples.Touch(Mm(12), Mm(10), P(0.31)));
        Frame(Samples.Touch(Mm(13), Mm(10), P(0.285)));
        Assert.Equal([ProximityEnter, Down, Drag, Drag, Drag], _sink.PenKinds());
        Assert.Equal(0, Last.Sample.Pressure);
        Frame(Samples.Touch(Mm(13), Mm(10), P(0.27))); // below 0.28: lifts
        Assert.Equal(Up, Last.Kind);
        Frame(Samples.Touch(Mm(13), Mm(10), P(0.29))); // pressing again needs the threshold
        Assert.False(_tracker.IsInContact);
        Frame(Samples.Touch(Mm(13), Mm(10), P(0.301)));
        Assert.Equal(Down, Last.Kind);
    }

    [Fact]
    public void Default_settings_pass_samples_through_unchanged()
    {
        PenSample s = Samples.Touch(Mm(10), Mm(20), 12345, buttons: PenButtons.Primary);
        Frame(s);
        Assert.All(_sink.Pen, e => Assert.Equal(s, e.Sample));
        Assert.Equal(new DisplayPoint(Mm(10) / 65.535, Mm(20) / 65.535), _sink.Pen[0].Position);
    }

    [Fact]
    public void Pen_settings_wait_for_the_stroke_to_end()
    {
        Frame(Samples.Touch(Mm(10), Mm(10), P(0.5)));
        _tracker.ApplySettings(new InputSettings { TipFeel = 1, StylusMode = StylusMode.Mouse });
        Assert.Equal(StylusMode.Pen, _tracker.Settings.StylusMode);
        Frame(Samples.Touch(Mm(30), Mm(10), P(0.5)));
        Assert.Equal(Drag, Last.Kind);
        Assert.Equal(P(0.5), Last.Sample.Pressure); // still linear
        Assert.Equal(Mm(30) / 65.535, Last.Position.X, 6); // still absolute

        _sink.Cursor = new DisplayPoint(700, 700);
        Frame(Samples.Hover(Mm(30), Mm(10)));
        Assert.Equal(StylusMode.Mouse, _tracker.Settings.StylusMode);
        Assert.Equal(Up, Last.Kind);
        Assert.Equal(Mm(30) / 65.535, Last.Position.X, 6); // the stroke ends where it was
        Frame(Samples.Hover(Mm(80), Mm(80)));
        Assert.Equal(Move, Last.Kind);
        Assert.Equal(new DisplayPoint(700, 700), Last.Position); // anchored at the cursor, no jump
        Frame(Samples.Touch(Mm(30), Mm(10), P(0.5)));
        Assert.Equal(0.25, Last.Sample.Pressure / 65535.0, 3);
    }

    [Fact]
    public void Gesture_settings_apply_at_once_even_mid_stroke()
    {
        Frame(Samples.Touch(Mm(10), Mm(10)));
        _tracker.ApplySettings(new InputSettings { ScrollSpeed = 3 });
        Assert.Equal(3, _tracker.Gestures.ScrollSpeed);
    }

    [Fact]
    public void Watchdog_release_applies_waiting_settings()
    {
        Frame(Samples.Touch(Mm(10), Mm(10)));
        _tracker.ApplySettings(MouseLinear);
        _tracker.Reset();
        Assert.Equal(StylusMode.Mouse, _tracker.Settings.StylusMode);
    }

    [Fact]
    public void Invalid_settings_throw_and_change_nothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _tracker.ApplySettings(new InputSettings { MouseSpeed = 10 }));
        Assert.Same(InputSettings.Default, _tracker.Settings);
    }

    // Mouse mode.

    [Fact]
    public void Mouse_mode_anchors_at_the_cursor_and_moves_by_the_pen_movement()
    {
        _tracker.ApplySettings(MouseLinear);
        _sink.Cursor = new DisplayPoint(300, 300);
        Frame(Samples.Hover(Mm(80), Mm(80)));
        Assert.Equal(ProximityEnter, Last.Kind);
        Assert.Equal(new DisplayPoint(300, 300), Last.Position);
        Frame(Samples.Hover(Mm(90), Mm(75))); // +10 mm, -5 mm at 8 pt/mm
        Assert.Equal(380, Last.Position.X, 0.1);
        Assert.Equal(260, Last.Position.Y, 0.1);
    }

    [Fact]
    public void Mouse_mode_re_entering_range_never_jumps_the_cursor()
    {
        _tracker.ApplySettings(MouseLinear);
        _sink.Cursor = new DisplayPoint(300, 300);
        Frame(Samples.Hover(Mm(10), Mm(10)));
        Frame(Samples.Hover(Mm(20), Mm(10)));
        Frame(null);
        _sink.Cursor = new DisplayPoint(650, 120); // the user moved the mouse meanwhile
        Frame(Samples.Hover(Mm(90), Mm(90))); // the pen comes back far away
        Assert.Equal(ProximityEnter, Last.Kind);
        Assert.Equal(new DisplayPoint(650, 120), Last.Position);
        Frame(Samples.Hover(Mm(89), Mm(90)));
        Assert.Equal(642, Last.Position.X, 0.1);
    }

    [Fact]
    public void Mouse_mode_without_a_locator_continues_from_its_own_cursor()
    {
        var sink = new RecordingSink();
        var tracker = new PenStateTracker(sink, _time) { Mapper = new CoordinateMapper(1000, 1000, new DisplayRect(0, 0, 1000, 1000)) };
        tracker.SetSurfaceSize(100, 100);
        tracker.ApplySettings(MouseLinear);
        tracker.ProcessFrame(1_000, Samples.Hover(Mm(10), Mm(10)), [], []);
        Assert.Equal(new DisplayPoint(500, 500), sink.Pen[^1].Position); // the target's centre at first
        tracker.ProcessFrame(6_000, Samples.Hover(Mm(15), Mm(10)), [], []);
        tracker.ProcessFrame(11_000, null, [], []);
        tracker.ProcessFrame(16_000, Samples.Hover(Mm(70), Mm(70)), [], []);
        Assert.Equal(540, sink.Pen[^1].Position.X, 0.1);
        Assert.Equal(500, sink.Pen[^1].Position.Y, 0.1);
    }

    [Fact]
    public void Mouse_speed_scales_the_movement()
    {
        _tracker.ApplySettings(MouseLinear with { MouseSpeed = 2.5 });
        _sink.Cursor = new DisplayPoint(100, 100);
        Frame(Samples.Hover(Mm(10), Mm(10)));
        Frame(Samples.Hover(Mm(20), Mm(10)));
        Assert.Equal(100 + (10 * 8 * 2.5), Last.Position.X, 0.1);
    }

    [Fact]
    public void Mouse_acceleration_speeds_up_fast_movement_only()
    {
        // Slow: 1 mm per 50 ms = 160 pt/s, below the curve's 400 pt/s: 1:1.
        _tracker.ApplySettings(new InputSettings { StylusMode = StylusMode.Mouse });
        _sink.Cursor = new DisplayPoint(100, 500);
        Frame(Samples.Hover(Mm(10), Mm(50)), 50_000);
        for (int i = 1; i <= 5; i++)
        {
            Frame(Samples.Hover(Mm(10 + i), Mm(50)), 50_000);
        }

        Assert.Equal(140, Last.Position.X, 0.2);

        // Fast: 5 mm per 5 ms = 8000 pt/s: well above 1:1.
        Frame(null);
        _sink.Cursor = new DisplayPoint(100, 500);
        Frame(Samples.Hover(Mm(10), Mm(50)));
        for (int i = 1; i <= 5; i++)
        {
            Frame(Samples.Hover(Mm(10 + (5 * i)), Mm(50)));
        }

        Assert.True(Last.Position.X - 100 > 2 * 25 * 8, $"moved {Last.Position.X - 100}");
    }

    [Fact]
    public void Mouse_mode_keeps_the_cursor_on_the_displays()
    {
        _tracker.ApplySettings(MouseLinear);
        _sink.Cursor = new DisplayPoint(950, 500);
        Frame(Samples.Hover(Mm(10), Mm(10)));
        Frame(Samples.Hover(Mm(60), Mm(10))); // +400 pt: past the right edge
        Assert.True(Last.Position.X < 1000 && Last.Position.X > 999.99);
        Frame(Samples.Hover(Mm(50), Mm(10))); // back 80 pt from the edge, no movement lost beyond it
        Assert.Equal(920, Last.Position.X, 0.1);
    }

    [Fact]
    public void Mouse_mode_clamps_onto_all_displays_across_a_gap()
    {
        var target = MappingTarget.ForAllDisplays([new DisplayRect(0, 0, 1000, 1000), new DisplayRect(1500, 0, 1000, 500)]);
        _tracker.Mapper = new CoordinateMapper(2500, 1000, target);
        _tracker.ApplySettings(MouseLinear);
        _sink.Cursor = new DisplayPoint(900, 200);
        Frame(Samples.Hover(Mm(10), Mm(10)));
        Frame(Samples.Hover(Mm(80), Mm(10))); // +560 pt: past A, into B
        Assert.Equal(1500, Last.Position.X, 0.1);
        Assert.Equal(200, Last.Position.Y, 0.1);
    }

    [Fact]
    public void Mouse_mode_tip_clicks_and_drags_at_the_cursor_with_pressure_and_tilt()
    {
        _tracker.ApplySettings(MouseLinear);
        _sink.Cursor = new DisplayPoint(400, 400);
        Frame(Samples.Hover(Mm(50), Mm(50)));
        Frame(Samples.Touch(Mm(50), Mm(50), 20000) with { TiltX = 1500 });
        Assert.Equal(Down, Last.Kind);
        Assert.Equal(new DisplayPoint(400, 400), Last.Position);
        Assert.Equal(20000, Last.Sample.Pressure);
        Assert.Equal(1500, Last.Sample.TiltX);
        Frame(Samples.Touch(Mm(55), Mm(50), 30000));
        Assert.Equal(Drag, Last.Kind);
        Assert.Equal(440, Last.Position.X, 0.1);
        Assert.Equal(30000, Last.Sample.Pressure);
        Frame(Samples.Hover(Mm(55), Mm(50)));
        Assert.Equal(Up, Last.Kind);
        Assert.Equal(440, Last.Position.X, 0.1);
    }

    [Fact]
    public void Mouse_mode_leaves_the_finger_fallback_absolute()
    {
        _tracker.ApplySettings(MouseLinear);
        _sink.Cursor = new DisplayPoint(10, 10);
        Frame(Samples.Hover(Mm(50), Mm(50), PenTool.FingerFallback));
        Assert.Equal(500, Last.Position.X, 0.1);
    }

    [Fact]
    public void Switching_back_to_pen_mode_is_absolute_again()
    {
        _tracker.ApplySettings(MouseLinear);
        _sink.Cursor = new DisplayPoint(10, 10);
        Frame(Samples.Hover(Mm(50), Mm(50)));
        _tracker.ApplySettings(InputSettings.Default);
        Frame(Samples.Hover(Mm(60), Mm(50)));
        Assert.Equal(600, Last.Position.X, 0.1);
    }

    [Fact]
    public void Settings_paths_are_allocation_free()
    {
        var sink = new LocatingSink { Cursor = new DisplayPoint(500, 500), Record = false };
        var tracker = new PenStateTracker(sink, _time) { Mapper = new CoordinateMapper(1000, 1000, new DisplayRect(0, 0, 1000, 1000)) };
        tracker.ApplySettings(new InputSettings
        {
            StylusMode = StylusMode.Mouse,
            SideButton = SideButtonAction.Eraser,
            TipFeel = 0.5,
            ClickThreshold = 0.1,
            FullPressureAt = 0.9,
        });
        uint t = 1_000;
        PenSample[] history = [Samples.Touch(100, 100, 30000, buttons: PenButtons.Primary)];
        Alloc.AssertZero(4_000, i =>
        {
            ushort x = (ushort)(1000 + (i % 50 * 37));
            PenSample? pen = (i % 8) switch
            {
                0 => Samples.Hover(x, 500),
                1 => Samples.Hover(x, 500, buttons: PenButtons.Primary),
                2 or 3 => Samples.Touch(x, 500, (ushort)(i * 97), buttons: PenButtons.Primary),
                4 => Samples.Touch(x, 500, 3000),
                5 => Samples.Hover(x, 500),
                6 => null,
                _ => Samples.Touch(x, 500, 40000, PenTool.FingerFallback),
            };
            tracker.ProcessFrame(t, pen, i % 8 == 3 ? history : [], []);
            t += 5_000;
        }, () => sink.Count);
    }

    private sealed class LocatingSink : IInputSink, ICursorLocator
    {
        public DisplayPoint? Cursor { get; set; }

        public bool Record { get; init; } = true;

        public List<PenEvent> Pen { get; } = new(256);

        public long Count { get; private set; }

        public PenEventKind[] PenKinds() => Pen.Select(e => e.Kind).ToArray();

        public void OnPenEvent(in PenEvent e)
        {
            Count++;
            if (Record)
            {
                Pen.Add(e);
            }
        }

        public void OnGestureEvent(in GestureEvent e)
        {
        }

        public bool TryGetCursorLocation(out DisplayPoint location)
        {
            location = Cursor.GetValueOrDefault();
            return Cursor.HasValue;
        }
    }
}
