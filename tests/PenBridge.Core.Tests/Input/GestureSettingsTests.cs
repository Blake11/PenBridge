using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>ADR-0033: Smart Zoom override, pointer and scroll speed, per-gesture toggles.</summary>
public class GestureSettingsTests
{
    // Square 1000x1000 surface onto a 1000x1000 display: 1 pt per 65.535 units, thresholds 4% = 40 pt.
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));
    private readonly RecordingSink _sink = new();
    private bool _system;

    private GestureRecognizer Recognizer(InputSettings? settings = null, bool oneFinger = false)
    {
        var rec = new GestureRecognizer(_sink, new GestureRecognizerOptions { SmartZoomEnabled = () => _system, OneFingerCursor = oneFinger })
        {
            Mapper = Square,
        };
        if (settings is not null)
        {
            rec.ApplySettings(settings);
        }

        return rec;
    }

    private List<GestureEvent> Gestures => _sink.Gestures.Where(g => g.Kind != GestureKind.TouchDown).ToList();

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    private static TouchSample F(byte id, double x, double y) => new(id, true, N(x), N(y));

    private static long Ms(double ms) => (long)(ms * 1000);

    private static void TwoFingerTap(GestureRecognizer rec, double ms)
    {
        rec.Process([F(1, 400, 500), F(2, 600, 500)], false, Ms(ms));
        rec.Process([F(1, 400, 500), F(2, 600, 500)], false, Ms(ms + 25));
        rec.Process([], false, Ms(ms + 50));
    }

    /// <summary>Fingers at the given x offsets move down 10 pt every 50 ms (200 pt/s, no acceleration), then lift.</summary>
    private static void Move(GestureRecognizer rec, params double[] xs)
    {
        for (int step = 0; step <= 12; step++)
        {
            var touches = new TouchSample[xs.Length];
            for (int i = 0; i < xs.Length; i++)
            {
                touches[i] = F((byte)(i + 1), xs[i], 300 + (step * 10));
            }

            rec.Process(touches, false, Ms(step * 50));
        }

        rec.Process([], false, Ms(13 * 50));
    }

    private double SumDy(GestureKind kind) => Gestures.Where(g => g.Kind == kind).Sum(g => g.DeltaY);

    [Theory]
    [InlineData(SmartZoomMode.On, false)]
    [InlineData(SmartZoomMode.Off, true)]
    public void Smart_zoom_override_replaces_the_system_setting(SmartZoomMode mode, bool system)
    {
        _system = system;
        GestureRecognizer rec = Recognizer(new InputSettings { SmartZoom = mode });
        TwoFingerTap(rec, 0);
        if (mode == SmartZoomMode.On)
        {
            Assert.Empty(Gestures); // held for a possible double tap
            TwoFingerTap(rec, 150);
            Assert.Equal(GestureKind.SmartZoom, Assert.Single(Gestures).Kind);
        }
        else
        {
            Assert.Equal(GestureKind.Tap, Assert.Single(Gestures).Kind); // at once, although the system says on
        }
    }

    [Fact]
    public void Follow_system_uses_the_system_setting()
    {
        _system = false;
        GestureRecognizer rec = Recognizer(new InputSettings { SmartZoom = SmartZoomMode.FollowSystem });
        TwoFingerTap(rec, 0);
        Assert.Equal(GestureKind.Tap, Assert.Single(Gestures).Kind);
    }

    [Fact]
    public void Scroll_speed_scales_scroll_deltas()
    {
        Move(Recognizer(), 400, 600);
        double normal = SumDy(GestureKind.Scroll);
        _sink.Clear();
        Move(Recognizer(new InputSettings { ScrollSpeed = 2.5 }), 400, 600);
        Assert.True(normal > 50);
        Assert.Equal(normal * 2.5, SumDy(GestureKind.Scroll), 6);
    }

    [Fact]
    public void Pointer_speed_scales_three_finger_drag_and_the_one_finger_cursor()
    {
        Move(Recognizer(), 300, 500, 700);
        double drag = SumDy(GestureKind.Drag);
        _sink.Clear();
        Move(Recognizer(new InputSettings { PointerSpeed = 0.5 }), 300, 500, 700);
        Assert.True(drag > 50);
        Assert.Equal(drag * 0.5, SumDy(GestureKind.Drag), 6);

        _sink.Clear();
        Move(Recognizer(oneFinger: true), 500);
        double pointer = SumDy(GestureKind.PointerMove);
        _sink.Clear();
        Move(Recognizer(new InputSettings { PointerSpeed = 3 }, oneFinger: true), 500);
        Assert.True(pointer > 50);
        Assert.Equal(pointer * 3, SumDy(GestureKind.PointerMove), 6);
    }

    [Fact]
    public void Speeds_do_not_change_scroll_by_pointer_or_the_reverse()
    {
        Move(Recognizer(new InputSettings { PointerSpeed = 4 }), 400, 600);
        double scroll = SumDy(GestureKind.Scroll);
        _sink.Clear();
        Move(Recognizer(), 400, 600);
        Assert.Equal(SumDy(GestureKind.Scroll), scroll, 9);
    }

    /// <summary>Two fingers spread from 200 pt to 360 pt apart around a fixed centre.</summary>
    private static void Spread(GestureRecognizer rec)
    {
        for (int step = 0; step <= 8; step++)
        {
            double half = 100 + (step * 10);
            rec.Process([F(1, 500 - half, 500), F(2, 500 + half, 500)], false, Ms(step * 50));
        }

        rec.Process([], false, Ms(9 * 50));
    }

    /// <summary>Two fingers 200 pt apart turn 5° per frame around a fixed centre.</summary>
    private static void Turn(GestureRecognizer rec)
    {
        for (int step = 0; step <= 10; step++)
        {
            double a = step * 5 * Math.PI / 180;
            double dx = 100 * Math.Cos(a);
            double dy = 100 * Math.Sin(a);
            rec.Process([F(1, 500 - dx, 500 - dy), F(2, 500 + dx, 500 + dy)], false, Ms(step * 50));
        }

        rec.Process([], false, Ms(11 * 50));
    }

    [Fact]
    public void Zoom_off_a_spread_starts_no_transform()
    {
        Spread(Recognizer(new InputSettings { ZoomEnabled = false }));
        Assert.DoesNotContain(Gestures, g => g.Kind == GestureKind.Pinch);
    }

    [Fact]
    public void Zoom_off_a_turn_is_rotation_only()
    {
        Turn(Recognizer(new InputSettings { ZoomEnabled = false }));
        List<GestureEvent> pinch = Gestures.Where(g => g.Kind == GestureKind.Pinch).ToList();
        Assert.NotEmpty(pinch);
        Assert.All(pinch, g => Assert.Equal(1, g.ScaleDelta));
        Assert.True(pinch.Sum(g => g.RotationDelta) > 20);
    }

    [Fact]
    public void Rotate_off_a_spread_is_zoom_only_and_a_turn_starts_nothing()
    {
        Spread(Recognizer(new InputSettings { RotateEnabled = false }));
        List<GestureEvent> pinch = Gestures.Where(g => g.Kind == GestureKind.Pinch).ToList();
        Assert.NotEmpty(pinch);
        Assert.All(pinch, g => Assert.Equal(0, g.RotationDelta));
        Assert.True(pinch.Aggregate(1.0, (s, g) => s * g.ScaleDelta) > 1.3);

        _sink.Clear();
        Turn(Recognizer(new InputSettings { RotateEnabled = false }));
        Assert.DoesNotContain(Gestures, g => g.Kind == GestureKind.Pinch);
    }

    [Fact]
    public void Zoom_and_rotate_off_two_fingers_still_scroll_and_tap()
    {
        var off = new InputSettings { ZoomEnabled = false, RotateEnabled = false };
        Spread(Recognizer(off));
        Turn(Recognizer(off));
        Assert.DoesNotContain(Gestures, g => g.Kind == GestureKind.Pinch);
        Move(Recognizer(off), 400, 600);
        Assert.Contains(Gestures, g => g.Kind == GestureKind.Scroll);
        _sink.Clear();
        _system = false;
        TwoFingerTap(Recognizer(off), 0);
        Assert.Equal(GestureKind.Tap, Assert.Single(Gestures).Kind);
    }

    [Fact]
    public void Three_finger_drag_off_three_fingers_do_nothing()
    {
        Move(Recognizer(new InputSettings { ThreeFingerDragEnabled = false }), 300, 500, 700);
        Assert.Empty(Gestures);
    }

    [Fact]
    public void Four_finger_gestures_off_four_fingers_do_nothing()
    {
        Move(Recognizer(), 200, 400, 600, 800);
        Assert.Contains(Gestures, g => g.Kind == GestureKind.SystemSwipe);
        _sink.Clear();
        Move(Recognizer(new InputSettings { FourFingerGesturesEnabled = false }), 200, 400, 600, 800);
        Assert.Empty(Gestures);
    }

    [Fact]
    public void Settings_change_mid_scroll_applies_the_new_speed_from_the_next_delta()
    {
        GestureRecognizer rec = Recognizer();
        for (int step = 0; step <= 6; step++)
        {
            rec.Process([F(1, 400, 300 + (step * 10)), F(2, 600, 300 + (step * 10))], false, Ms(step * 50));
        }

        int before = Gestures.Count;
        double last = Gestures[^1].DeltaY;
        rec.ApplySettings(new InputSettings { ScrollSpeed = 2 });
        rec.Process([F(1, 400, 370), F(2, 600, 370)], false, Ms(350));
        Assert.Equal(before + 1, Gestures.Count);
        Assert.Equal(GesturePhase.Changed, Gestures[^1].Phase);
        Assert.Equal(last * 2, Gestures[^1].DeltaY, 0.1); // 10 pt steps, quantized to 1/65.535 pt
    }

    /// <summary>Scroll, three-finger drag, four-finger swipe, spread, turn and the one-finger cursor through one mapper.</summary>
    private static List<GestureEvent> AllGestures(CoordinateMapper mapper)
    {
        var sink = new RecordingSink();
        GestureRecognizer Make(bool oneFinger) => new(sink, new GestureRecognizerOptions { OneFingerCursor = oneFinger }) { Mapper = mapper };
        Move(Make(false), 400, 600);
        Move(Make(false), 300, 500, 700);
        Move(Make(false), 200, 400, 600, 800);
        Spread(Make(false));
        Turn(Make(false));
        Move(Make(true), 500);
        return sink.Gestures;
    }

    public static TheoryData<double, double, double, double> Regions => new()
    {
        { 0.7, 0.7, 0.2, 0.1 },   // small, wide (3.6:1)
        { 0.1, 0.1, 0.1, 0.8 },   // narrow and tall (0.22:1)
        { 0.25, 0.25, 0.5, 0.5 }, // the display's own aspect
    };

    [Theory]
    [MemberData(nameof(Regions))]
    public void Gesture_deltas_do_not_depend_on_the_region_or_active_area(double x, double y, double w, double h)
    {
        var display = new DisplayRect(0, 0, 1920, 1080);
        List<GestureEvent> full = AllGestures(new CoordinateMapper(3088, 1440, display));
        Assert.Contains(full, g => g.Kind == GestureKind.Scroll);
        Assert.Contains(full, g => g.Kind == GestureKind.Drag);
        Assert.Contains(full, g => g.Kind == GestureKind.SystemSwipe);
        Assert.Contains(full, g => g.Kind == GestureKind.Pinch);
        Assert.Contains(full, g => g.Kind == GestureKind.PointerMove);

        List<GestureEvent> region = AllGestures(new CoordinateMapper(3088, 1440, display, new NormalizedRect(x, y, w, h)));
        Assert.Equal(full, region);
    }

    [Fact]
    public void Gesture_deltas_with_all_displays_match_the_main_display()
    {
        var main = new DisplayRect(0, 0, 1920, 1080);
        MappingTarget all = MappingTarget.ForAllDisplays([new DisplayRect(-1080, -400, 1080, 1920), main, new DisplayRect(1920, 0, 2560, 1440)]);
        Assert.Equal(AllGestures(new CoordinateMapper(3088, 1440, main)), AllGestures(new CoordinateMapper(3088, 1440, all)));
    }

    [Fact]
    public void Gesture_gain_is_the_phase_4_letterbox_scale()
    {
        // 3088x1440 letterboxed on 1920x1080: 1920 pt across the surface; a two-finger move of 0.1 of the surface
        // height is 0.1 * 1920 * 1440 / 3088 pt before gain, whatever the region.
        var mapper = new CoordinateMapper(3088, 1440, new DisplayRect(0, 0, 1920, 1080), new NormalizedRect(0, 0, 0.3, 0.3));
        Assert.Equal(1920 * 1440.0 / 3088, mapper.GestureShortSide, 9);
        DisplayPoint a = mapper.MapGesture(0, 0);
        DisplayPoint b = mapper.MapGesture(0, 6553.5);
        Assert.Equal(0.1 * 1920 * 1440.0 / 3088, b.Y - a.Y, 9);
    }

    [Fact]
    public void Zoom_and_rotate_toggles_apply_to_the_next_gesture_only()
    {
        GestureRecognizer rec = Recognizer();
        for (int step = 0; step <= 4; step++)
        {
            double half = 100 + (step * 10);
            rec.Process([F(1, 500 - half, 500), F(2, 500 + half, 500)], false, Ms(step * 50));
        }

        Assert.Contains(Gestures, g => g.Kind == GestureKind.Pinch && g.Phase == GesturePhase.Began);
        rec.ApplySettings(new InputSettings { ZoomEnabled = false });
        int before = Gestures.Count;
        for (int step = 5; step <= 8; step++)
        {
            double half = 100 + (step * 10);
            rec.Process([F(1, 500 - half, 500), F(2, 500 + half, 500)], false, Ms(step * 50));
        }

        rec.Process([], false, Ms(9 * 50));
        List<GestureEvent> rest = Gestures.Skip(before).Where(g => g.Phase == GesturePhase.Changed).ToList();
        Assert.NotEmpty(rest);
        Assert.All(rest, g => Assert.True(g.ScaleDelta > 1)); // the running pinch keeps zooming

        _sink.Clear();
        Spread(rec); // the next gesture: zoom off
        Assert.DoesNotContain(Gestures, g => g.Kind == GestureKind.Pinch);
    }
}
