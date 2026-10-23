using PenBridge.Core.Input;
using static PenBridge.Core.Input.PenEventKind;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// Mouse-mode movement math, the pressure curve's scope, synthetic flags and settings changes in the tracker (#276).
/// The surface is 100 mm square on a 1000x1000 pt display (8 pt per mm, 655.35 wire units per mm).
/// </summary>
[Collection(AllocationGroup.Name)]
public class PenStateTrackerMouseTests
{
    private const double PointsPerMm = 8;
    private const double MmPerUnit = 100 / CoordinateMapper.NormalizedMax;

    private readonly FakeTime _time = new();
    private readonly CursorSink _sink = new();
    private readonly PenStateTracker _tracker;
    private uint _timeUs = 10_000;

    public PenStateTrackerMouseTests()
    {
        _tracker = new PenStateTracker(_sink, _time) { Mapper = new CoordinateMapper(1000, 1000, new DisplayRect(0, 0, 1000, 1000)) };
        _tracker.SetSurfaceSize(100, 100);
    }

    private static readonly InputSettings Accelerated = new() { StylusMode = StylusMode.Mouse };

    private static readonly InputSettings Linear = new() { StylusMode = StylusMode.Mouse, MouseAcceleration = false };

    private static ushort Mm(double mm) => (ushort)Math.Round(mm * 655.35);

    private PenEvent Last => _sink.Pen[^1];

    private void Frame(PenSample? pen, uint stepUs = 5_000)
    {
        _tracker.ProcessFrame(_timeUs, pen, [], []);
        _timeUs += stepUs;
    }

    /// <summary>What the cursor should move by for a pen movement of whole wire units, following the documented math.</summary>
    private static (double X, double Y) Expected(int dxUnits, int dyUnits, long dtUs, ref double mouseSpeed, double speedSetting = 1, double mmX = 100, double mmY = 100)
    {
        double px = dxUnits * (mmX / CoordinateMapper.NormalizedMax) * PointsPerMm * speedSetting;
        double py = dyUnits * (mmY / CoordinateMapper.NormalizedMax) * PointsPerMm * speedSetting;
        double speed = Math.Sqrt((px * px) + (py * py)) * 1e6 / Math.Clamp(dtUs, 1_000, 100_000);
        mouseSpeed = (mouseSpeed + speed) / 2;
        double gain = new GestureAcceleration().GainAt(mouseSpeed);
        return (px * gain, py * gain);
    }

    [Fact]
    public void Acceleration_uses_the_speed_of_both_axes_and_applies_the_gain_to_both()
    {
        _tracker.ApplySettings(Accelerated);
        _sink.Cursor = new DisplayPoint(300, 300);
        Frame(Samples.Hover(Mm(10), Mm(10)));
        Frame(Samples.Hover(Mm(11.5), Mm(12))); // 12 pt and 16 pt: 20 pt in 5 ms
        double mouseSpeed = 0;
        (double ex, double ey) = Expected(Mm(11.5) - Mm(10), Mm(12) - Mm(10), 5_000, ref mouseSpeed);
        Assert.InRange(mouseSpeed, 1999, 2001); // between the slow and fast speeds: a gain strictly between 1 and 3
        double gain = new GestureAcceleration().GainAt(mouseSpeed);
        Assert.InRange(gain, 1.5, 2.5);
        Assert.Equal(300 + ex, Last.Position.X, 6);
        Assert.Equal(300 + ey, Last.Position.Y, 6);
        Assert.InRange(ey / ex, 1.3, 1.34); // the direction is kept
    }

    [Fact]
    public void Acceleration_by_vertical_movement_alone_accelerates_y()
    {
        _tracker.ApplySettings(Accelerated);
        _sink.Cursor = new DisplayPoint(300, 300);
        Frame(Samples.Hover(Mm(10), Mm(10)));
        Frame(Samples.Hover(Mm(10), Mm(13))); // 24 pt in 5 ms: 4800 pt/s, halved by the average to 2400
        double mouseSpeed = 0;
        (double ex, double ey) = Expected(0, Mm(13) - Mm(10), 5_000, ref mouseSpeed);
        Assert.InRange(mouseSpeed, 2399, 2401);
        Assert.Equal(300, Last.Position.X);
        Assert.Equal(300 + ey, Last.Position.Y, 6);
        Assert.InRange(ey, 24 * 1.5, 24 * 3.0);
        Assert.Equal(0, ex);
    }

    [Fact]
    public void The_speed_average_carries_over_between_moves_and_restarts_after_an_anchor()
    {
        _tracker.ApplySettings(Accelerated);
        _sink.Cursor = new DisplayPoint(300, 300);
        Frame(Samples.Hover(Mm(10), Mm(10)));
        double mouseSpeed = 0;
        double x = 300;
        double y = 300;
        for (int i = 1; i <= 3; i++)
        {
            ushort ax = Mm(10 + (i * 1.5));
            ushort bx = Mm(10 + ((i - 1) * 1.5));
            Frame(Samples.Hover(ax, Mm(10)));
            (double ex, _) = Expected(ax - bx, 0, 5_000, ref mouseSpeed);
            x += ex;
            Assert.Equal(x, Last.Position.X, 6);
            Assert.Equal(y, Last.Position.Y, 6);
        }

        // Leave and come back: the average restarts from 0, whatever speed came before.
        Frame(null);
        _sink.Cursor = new DisplayPoint(600, 600);
        Frame(Samples.Hover(Mm(10), Mm(10)));
        Frame(Samples.Hover(Mm(11.5), Mm(10)));
        mouseSpeed = 0;
        (double fx, _) = Expected(Mm(11.5) - Mm(10), 0, 5_000, ref mouseSpeed);
        Assert.Equal(600 + fx, Last.Position.X, 6);
    }

    [Fact]
    public void The_interval_between_moves_is_clamped_for_the_speed()
    {
        _tracker.ApplySettings(Accelerated);
        _sink.Cursor = new DisplayPoint(100, 100);

        // Very close frames count as 1 ms: a 1.6 pt move is 1600 pt/s, not 16000.
        Frame(Samples.Hover(Mm(10), Mm(10)), 100);
        Frame(Samples.Hover(Mm(10.2), Mm(10)));
        double mouseSpeed = 0;
        (double ex, _) = Expected(Mm(10.2) - Mm(10), 0, 100, ref mouseSpeed);
        Assert.InRange(mouseSpeed, 700, 900);
        Assert.Equal(100 + ex, Last.Position.X, 6);

        // A long pause counts as 100 ms: a 160 pt move is 1600 pt/s, not 160.
        Frame(null);
        _sink.Cursor = new DisplayPoint(100, 100);
        Frame(Samples.Hover(Mm(10), Mm(10)), 1_000_000);
        Frame(Samples.Hover(Mm(30), Mm(10)));
        mouseSpeed = 0;
        (ex, _) = Expected(Mm(30) - Mm(10), 0, 1_000_000, ref mouseSpeed);
        Assert.InRange(mouseSpeed, 700, 900);
        Assert.Equal(100 + ex, Last.Position.X, 6);
        Assert.InRange(Last.Position.X - 100, 160.5, 200);
    }

    [Fact]
    public void Mouse_speed_scales_both_axes()
    {
        _tracker.ApplySettings(Linear with { MouseSpeed = 2.5 });
        _sink.Cursor = new DisplayPoint(100, 100);
        Frame(Samples.Hover(Mm(10), Mm(10)));
        Frame(Samples.Hover(Mm(20), Mm(14)));
        Assert.Equal(100 + ((Mm(20) - Mm(10)) * MmPerUnit * PointsPerMm * 2.5), Last.Position.X, 6);
        Assert.Equal(100 + ((Mm(14) - Mm(10)) * MmPerUnit * PointsPerMm * 2.5), Last.Position.Y, 6);
    }

    [Fact]
    public void Each_axis_uses_its_own_physical_size()
    {
        _tracker.SetSurfaceSize(200, 50);
        _tracker.ApplySettings(Linear);
        _sink.Cursor = new DisplayPoint(100, 100);
        Frame(Samples.Hover(1000, 1000));
        Frame(Samples.Hover(1000 + 6553, 1000 + 6553)); // about a tenth of the surface on both axes: 20 mm and 5 mm
        Assert.Equal(100 + (6553 * (200 / CoordinateMapper.NormalizedMax) * PointsPerMm), Last.Position.X, 6);
        Assert.Equal(100 + (6553 * (50 / CoordinateMapper.NormalizedMax) * PointsPerMm), Last.Position.Y, 6);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(-5.0, -5.0)]
    [InlineData(double.NaN, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, double.NaN)]
    public void An_invalid_surface_size_keeps_the_default_on_that_axis(double widthMm, double heightMm)
    {
        _tracker.SetSurfaceSize(widthMm, heightMm);
        _tracker.ApplySettings(Linear);
        _sink.Cursor = new DisplayPoint(100, 100);
        Frame(Samples.Hover(1000, 1000));
        Frame(Samples.Hover(1000 + 6553, 1000 + 6553));
        double expected = 6553 * MmPerUnit * PointsPerMm;
        Assert.Equal(100 + expected, Last.Position.X, 6);
        Assert.Equal(100 + expected, Last.Position.Y, 6);
    }

    [Fact]
    public void A_valid_width_with_an_invalid_height_changes_only_the_width()
    {
        _tracker.SetSurfaceSize(150, 0);
        _tracker.ApplySettings(Linear);
        _sink.Cursor = new DisplayPoint(100, 100);
        Frame(Samples.Hover(1000, 1000));
        Frame(Samples.Hover(1000 + 6553, 1000 + 6553));
        Assert.Equal(100 + (6553 * (150 / CoordinateMapper.NormalizedMax) * PointsPerMm), Last.Position.X, 6);
        Assert.Equal(100 + (6553 * MmPerUnit * PointsPerMm), Last.Position.Y, 6);

        DisplayPoint before = Last.Position;
        _tracker.SetSurfaceSize(0, 70);
        Frame(Samples.Hover(1000 + 6553 + 6553, 1000 + 6553 + 6553));
        Assert.Equal(before.X + (6553 * MmPerUnit * PointsPerMm), Last.Position.X, 6);
        Assert.Equal(before.Y + (6553 * (70 / CoordinateMapper.NormalizedMax) * PointsPerMm), Last.Position.Y, 6);
    }

    [Fact]
    public void The_eraser_also_moves_the_cursor_relatively()
    {
        _tracker.ApplySettings(Linear);
        _sink.Cursor = new DisplayPoint(100, 100);
        Frame(Samples.Hover(Mm(10), Mm(10), PenTool.Eraser));
        Assert.Equal(new DisplayPoint(100, 100), Last.Position);
        Frame(Samples.Hover(Mm(15), Mm(10), PenTool.Eraser));
        Assert.Equal(140, Last.Position.X, 0.1);
    }

    [Fact]
    public void A_pen_stationary_after_the_anchor_does_not_move_the_cursor_and_the_reference_follows()
    {
        _tracker.ApplySettings(Linear);
        _sink.Cursor = new DisplayPoint(100, 100);
        Frame(Samples.Hover(Mm(10), Mm(10)));
        Frame(Samples.Hover(Mm(10), Mm(10)));
        Frame(Samples.Hover(Mm(12), Mm(10)));
        Assert.Equal(116, Last.Position.X, 0.1);
        Frame(Samples.Hover(Mm(12), Mm(10)));
        Frame(Samples.Hover(Mm(13), Mm(10)));
        Assert.Equal(124, Last.Position.X, 0.1);
        Assert.Equal(100, Last.Position.Y, 6);
    }

    [Fact]
    public void Leaving_range_in_contact_posts_the_release_at_the_cursor()
    {
        _tracker.ApplySettings(Linear);
        _sink.Cursor = new DisplayPoint(400, 300);
        Frame(Samples.Hover(Mm(50), Mm(50)));
        Frame(Samples.Touch(Mm(50), Mm(50), buttons: PenButtons.Primary));
        _sink.Pen.Clear();
        Frame(null);
        Assert.Equal([Up, ButtonUp, ProximityLeave], _sink.Pen.Select(e => e.Kind).ToArray());
        Assert.All(_sink.Pen, e => Assert.Equal(new DisplayPoint(400, 300), e.Position));
    }

    [Fact]
    public void The_pressure_curve_only_touches_samples_in_contact()
    {
        _tracker.ApplySettings(new InputSettings { TipFeel = 1, ClickThreshold = 0.3, FullPressureAt = 0.9 });
        // A hovering sample that carries a pressure value keeps it: the curve and the threshold are for contact.
        var hovering = new PenSample(1000, 1000, 20000, 0, 0, 0, PenTool.Pen, PenState.InRange, PenButtons.None);
        _tracker.ProcessFrame(10_000, hovering, [], []);
        Assert.Equal(ProximityEnter, Last.Kind);
        Assert.Equal(20000, Last.Sample.Pressure);
        Assert.Equal(PenState.InRange, Last.Sample.State);
        _tracker.ProcessFrame(15_000, hovering with { X = 1100 }, [], []);
        Assert.Equal(Move, Last.Kind);
        Assert.Equal(20000, Last.Sample.Pressure);
    }

    [Fact]
    public void Below_the_threshold_a_contact_sample_becomes_a_hover_with_no_pressure()
    {
        _tracker.ApplySettings(new InputSettings { ClickThreshold = 0.3, FullPressureAt = 0.9 });
        _tracker.ProcessFrame(10_000, Samples.Touch(1000, 1000, 10000), [], []);
        Assert.Equal([ProximityEnter], _sink.Pen.Select(e => e.Kind).ToArray());
        Assert.Equal(PenState.InRange, Last.Sample.State);
        Assert.Equal(0, Last.Sample.Pressure);
    }

    [Fact]
    public void Switching_the_stylus_mode_re_anchors_the_cursor_even_without_a_sample_in_between()
    {
        _tracker.ApplySettings(Linear);
        _sink.Cursor = new DisplayPoint(300, 300);
        Frame(Samples.Hover(Mm(10), Mm(10)));
        Frame(Samples.Hover(Mm(12), Mm(10)));
        Assert.Equal(316, Last.Position.X, 0.1);

        _tracker.ApplySettings(InputSettings.Default);
        _tracker.ApplySettings(Linear); // back to Mouse before any sample arrives
        _sink.Cursor = new DisplayPoint(700, 700); // the user moved the mouse meanwhile
        Frame(Samples.Hover(Mm(20), Mm(20)));
        Assert.Equal(new DisplayPoint(700, 700), Last.Position); // anchored at the cursor, not moved by 8 mm
        Frame(Samples.Hover(Mm(21), Mm(20)));
        Assert.Equal(708, Last.Position.X, 0.1);
    }

    [Fact]
    public void Changing_other_settings_keeps_the_anchor_in_mouse_mode()
    {
        _tracker.ApplySettings(Linear);
        _sink.Cursor = new DisplayPoint(300, 300);
        Frame(Samples.Hover(Mm(10), Mm(10)));
        Frame(Samples.Hover(Mm(12), Mm(10)));
        _tracker.ApplySettings(Linear with { MouseSpeed = 2, SideButton = SideButtonAction.MiddleClick });
        _sink.Cursor = new DisplayPoint(700, 700);
        Frame(Samples.Hover(Mm(13), Mm(10)));
        Assert.Equal(316 + 16, Last.Position.X, 0.1); // continues from the tracker's own cursor: 1 mm at the doubled speed
        Assert.Equal(300, Last.Position.Y, 0.1);
    }

    [Fact]
    public void Every_event_of_live_input_is_not_synthetic_including_a_tool_change_in_contact()
    {
        var sink = new RecordingSink();
        var tracker = new PenStateTracker(sink, _time) { Mapper = new CoordinateMapper(1, 1, new DisplayRect(0, 0, 65535, 65535)) };
        tracker.ProcessFrame(1_000, Samples.Hover(10, 10), [], []);
        tracker.ProcessFrame(2_000, Samples.Hover(10, 10, buttons: PenButtons.Primary), [], []);
        tracker.ProcessFrame(3_000, Samples.Touch(10, 10, 100, buttons: PenButtons.Primary), [], []);
        tracker.ProcessFrame(4_000, Samples.Touch(20, 10, 200, buttons: PenButtons.Primary | PenButtons.Secondary), [], []);
        sink.Clear();
        tracker.ProcessFrame(5_000, Samples.Touch(20, 10, 200, PenTool.Eraser, PenButtons.Secondary), [], []);
        Assert.Equal([Up, ButtonUp, ButtonUp, ToolChanged, ButtonDown, Down], sink.PenKinds());
        Assert.Equal(PenTool.Pen, sink.Pen[3].PreviousTool);
        tracker.ProcessFrame(6_000, Samples.Hover(20, 10, PenTool.Eraser), [], []);
        tracker.ProcessFrame(7_000, null, [], []);
        Assert.Equal([Up, ButtonUp, ButtonUp, ToolChanged, ButtonDown, Down, Up, ButtonUp, ProximityLeave], sink.PenKinds());
        Assert.All(sink.Pen, e => Assert.False(e.Synthetic, e.Kind.ToString()));
    }

    [Fact]
    public void The_watchdog_and_reset_mark_every_release_synthetic()
    {
        var sink = new RecordingSink();
        var tracker = new PenStateTracker(sink, _time) { Mapper = new CoordinateMapper(1, 1, new DisplayRect(0, 0, 65535, 65535)) };
        tracker.ProcessFrame(1_000, Samples.Touch(10, 10, 100, buttons: PenButtons.Primary), [], []);
        Assert.Equal([ProximityEnter, ButtonDown, Down], sink.PenKinds());
        Assert.All(sink.Pen, e => Assert.False(e.Synthetic, e.Kind.ToString()));
        sink.Clear();
        tracker.Reset();
        Assert.Equal([Up, ButtonUp, ProximityLeave], sink.PenKinds());
        Assert.All(sink.Pen, e => Assert.True(e.Synthetic, e.Kind.ToString()));
    }

    private sealed class CursorSink : IInputSink, ICursorLocator
    {
        public DisplayPoint? Cursor { get; set; }

        public List<PenEvent> Pen { get; } = new(64);

        public void OnPenEvent(in PenEvent e) => Pen.Add(e);

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
