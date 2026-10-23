using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// The values the recognizer emits (ADR-0023, ADR-0026), not only the gesture kind: deltas and their gain, directions at
/// ties, transform scale and rotation, progress, centers, and the limits of the tap and pointer rules.
/// </summary>
public class GestureValueTests
{
    // Square 1000x1000 surface onto a 1000x1000 display: 1 display point ~= 65.535 normalized units; thresholds are
    // fractions of 1000 pt.
    private static readonly CoordinateMapper Square = new(1000, 1000, new DisplayRect(0, 0, 1000, 1000));
    private static readonly GestureAcceleration Curve = new(); // 1:1 up to 400 pt/s, 3x from 4000 pt/s

    private readonly RecordingSink _sink = new();

    private static ushort N(double points) => (ushort)Math.Round(points * 65535.0 / 1000.0);

    private static TouchSample F(byte id, double x, double y) => new(id, true, N(x), N(y));

    private static long Ms(double ms) => (long)Math.Round(ms * 1000);

    private GestureRecognizer Rec(GestureRecognizerOptions? options = null) => new(_sink, options) { Mapper = Square };

    private List<GestureEvent> Gestures => _sink.Gestures.Where(g => g.Kind != GestureKind.TouchDown).ToList();

    private static void Two(GestureRecognizer rec, double ms, double ax, double ay, double bx, double by) =>
        rec.Process([F(1, ax, ay), F(2, bx, by)], false, Ms(ms));

    private static void Four(GestureRecognizer rec, double ms, double cx = 500, double cy = 500, double r = 100) =>
        rec.Process([F(1, cx - r, cy - r), F(2, cx + r, cy - r), F(3, cx + r, cy + r), F(4, cx - r, cy + r)], false, Ms(ms));

    // --- Four fingers ---

    [Fact]
    public void A_four_finger_swipe_exactly_diagonal_goes_along_x()
    {
        var rec = Rec();
        Four(rec, 0);
        Four(rec, 10, 560, 560);
        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal((GestureKind.SystemSwipe, GestureDirection.Right), (began.Kind, began.Direction));
        Assert.Equal(0.06, began.Progress, 3);
    }

    // --- Scroll gain from the speed ---

    [Theory]
    [InlineData(10, 150)] // 50 pt in 10 ms = 5000 pt/s: the full 3x gain
    [InlineData(500, 50)] // 50 pt in 500 ms = 100 pt/s: 1:1
    public void Scroll_began_is_accelerated_by_the_speed_since_the_fingers_settled(double ms, double expected)
    {
        var rec = Rec(new GestureRecognizerOptions { ScrollAcceleration = Curve });
        Two(rec, 1000, 400, 400, 600, 400);
        Two(rec, 1000 + ms, 400, 450, 600, 450);
        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal(GesturePhase.Began, began.Phase);
        Assert.Equal(expected, began.DeltaY, 0.1);
        Assert.Equal(0, began.DeltaX);
    }

    // --- Scroll axis lock at the ratio ---

    [Fact]
    public void A_start_exactly_at_the_lock_ratio_locks_horizontally()
    {
        var rec = Rec(new GestureRecognizerOptions { ScrollAcceleration = GestureAcceleration.None, ScrollAxisLockRatio = 1 });
        Two(rec, 0, 400, 400, 600, 600);
        Two(rec, 10, 440, 440, 640, 640); // exactly 45°: |dy| = 1 · |dx|
        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal(GestureKind.Scroll, began.Kind);
        Assert.Equal(40, began.DeltaX, 0.1);
        Assert.Equal(0, began.DeltaY);
    }

    [Fact]
    public void With_a_zero_ratio_an_exactly_vertical_start_stays_vertical()
    {
        var rec = Rec(new GestureRecognizerOptions { ScrollAcceleration = GestureAcceleration.None, ScrollAxisLockRatio = 0 });
        Two(rec, 0, 400, 400, 600, 400);
        Two(rec, 10, 400, 450, 600, 450);
        Two(rec, 20, 430, 470, 630, 470); // later sideways drift
        Assert.Equal([GesturePhase.Began, GesturePhase.Changed], Gestures.Select(g => g.Phase));
        Assert.All(Gestures, g => Assert.Equal(0, g.DeltaX));
        Assert.Equal(20, Gestures[1].DeltaY, 0.1);
    }

    // --- Rotation ---

    [Theory]
    [InlineData(400, 600)] // 0° → 180°
    [InlineData(600, 400)] // 180° → 0°: −180 wraps to +180
    public void A_half_turn_between_two_frames_is_plus_180_degrees(double ax, double bx)
    {
        var rec = Rec();
        Two(rec, 0, ax, 500, bx, 500);
        Two(rec, 10, bx, 500, ax, 500); // the fingers swap places
        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal((GestureKind.Pinch, GesturePhase.Began), (began.Kind, began.Phase));
        Assert.Equal(180, began.RotationDelta, 9);
        Assert.Equal(1, began.ScaleDelta, 9);
    }

    [Fact]
    public void Fingers_on_one_point_have_no_angle_so_spreading_them_turns_nothing()
    {
        var rec = Rec();
        Two(rec, 0, 500, 500, 500, 500);
        Two(rec, 10, 500, 450, 500, 550); // apart along 90°
        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal(GestureKind.Pinch, began.Kind);
        Assert.Equal(0, began.RotationDelta);

        Two(rec, 20, 550, 500, 450, 500); // now a real turn: 90° → 180°
        Assert.Equal(90, Gestures[^1].RotationDelta, 6);
    }

    // --- Two-finger tap timing at the limits ---

    [Fact]
    public void A_frame_exactly_at_the_tap_duration_keeps_the_tap()
    {
        var rec = Rec(new GestureRecognizerOptions { DoubleTapWindow = TimeSpan.Zero });
        Two(rec, 0, 400, 500, 600, 500);
        Two(rec, 300, 400, 500, 600, 500);
        rec.Process([], false, Ms(300));
        GestureEvent tap = Assert.Single(Gestures);
        Assert.Equal(GestureKind.Tap, tap.Kind);
        Assert.Equal((500.0, 500.0), (Math.Round(tap.Center.X, 1), Math.Round(tap.Center.Y, 1)));
    }

    [Fact]
    public void The_first_finger_moving_beyond_the_tap_slop_is_no_tap()
    {
        var rec = Rec(new GestureRecognizerOptions { DoubleTapWindow = TimeSpan.Zero });
        Two(rec, 0, 400, 500, 600, 500);
        Two(rec, 40, 362, 500, 600, 500); // finger 1: 38 pt > 35 pt (spread change below the pinch threshold)
        rec.Process([], false, Ms(80));
        Assert.Empty(Gestures);
    }

    // --- One finger (ADR-0026) ---

    private static readonly GestureRecognizerOptions Cursor = new() { OneFingerCursor = true, DragAcceleration = GestureAcceleration.None };

    private static void One(GestureRecognizer rec, double ms, double x, double y, byte id = 1) => rec.Process([F(id, x, y)], false, Ms(ms));

    [Theory]
    [InlineData(99, false)] // still settling: the 35 pt tap slop applies
    [InlineData(100, true)] // settled: the 15 pt pointer slop applies
    public void The_pointer_slop_applies_from_the_settle_time(double ms, bool moves)
    {
        var rec = Rec(Cursor);
        One(rec, 0, 500, 500);
        One(rec, ms, 520, 500);
        if (!moves)
        {
            Assert.Empty(Gestures);
            return;
        }

        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal((GestureKind.PointerMove, GesturePhase.Began), (began.Kind, began.Phase));
        Assert.Equal(20, began.DeltaX, 0.1);
        Assert.Equal(0, began.DeltaY, 0.1);
        Assert.Equal(520, began.Center.X, 0.1);
    }

    [Theory]
    [InlineData(10, 150)] // 50 pt in 10 ms: 3x
    [InlineData(500, 50)] // 50 pt in 500 ms: 1:1
    public void Pointer_move_began_is_accelerated_by_the_speed_since_the_landing(double ms, double expected)
    {
        var rec = Rec(Cursor with { DragAcceleration = Curve });
        One(rec, 1000, 500, 500);
        One(rec, 1000 + ms, 500, 550);
        GestureEvent began = Assert.Single(Gestures);
        Assert.Equal(GestureKind.PointerMove, began.Kind);
        Assert.Equal(expected, began.DeltaY, 0.1);
    }

    [Fact]
    public void Holding_still_after_a_click_drags_only_once_past_the_tap_duration()
    {
        var rec = Rec(Cursor);
        One(rec, 0, 500, 500);
        rec.Process([], false, Ms(50)); // Click
        One(rec, 100, 500, 500);
        One(rec, 400, 500, 500); // exactly the tap duration since landing
        Assert.Equal([GestureKind.Click], Gestures.Select(g => g.Kind));

        rec.Process([F(1, 500, 500)], false, Ms(400) + 1);
        GestureEvent began = Gestures[^1];
        Assert.Equal((GestureKind.PointerDrag, GesturePhase.Began), (began.Kind, began.Phase));
        Assert.Equal((0.0, 0.0), (began.DeltaX, began.DeltaY));
    }

    [Theory]
    [InlineData(450, 2)] // lands exactly ClickWindow (400 ms) after the click's lift: counts on
    [InlineData(450.001, 1)]
    public void The_click_window_includes_its_limit(double landMs, int count)
    {
        var rec = Rec(Cursor);
        One(rec, 0, 500, 500);
        rec.Process([], false, Ms(50));
        One(rec, landMs, 500, 500);
        rec.Process([], false, Ms(landMs + 50));

        GestureEvent[] clicks = [.. Gestures.Where(g => g.Kind == GestureKind.Click)];
        Assert.Equal(2, clicks.Length);
        Assert.Equal(count, clicks[1].ClickCount);
        Assert.Equal(Ms(landMs + 50), clicks[1].TimestampUs);
        Assert.Equal(500, clicks[1].Center.X, 0.1);
    }
}
