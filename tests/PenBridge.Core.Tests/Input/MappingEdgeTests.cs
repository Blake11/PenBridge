using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>Exact values at the edges of the mapping (#276): offset content rects, vertical nearest-display, clamps at 0 and 1.</summary>
public class MappingEdgeTests
{
    private const double Max = CoordinateMapper.NormalizedMax;

    [Fact]
    public void Content_rect_with_an_offset_and_a_short_active_area_extends_the_target_on_both_axes()
    {
        // Region (0.25, 0.5, 0.5, 0.25) of a 2000x1000 display at (100, 200): target 1000x250 at (600, 700).
        // A 1000x1000 surface is relatively taller: full width, active height 0.25 centred (y = 0.375).
        var m = new CoordinateMapper(1000, 1000, new DisplayRect(100, 200, 2000, 1000), new NormalizedRect(0.25, 0.5, 0.5, 0.25));
        Assert.Equal(new DisplayRect(600, 700, 1000, 250), m.TargetRect);
        Assert.Equal(new NormalizedRect(0, 0.375, 1, 0.25), m.ActiveArea);
        // The surface is 4x the target's height (1000) and its top is 0.375 * 1000 above the target's top.
        Assert.Equal(new DisplayRect(600, 700 - 375, 1000, 1000), m.ContentRect);
    }

    [Fact]
    public void Content_rect_with_a_narrow_active_area_and_a_negative_origin()
    {
        // A 3000x1000 surface on a 1000x1000 display at (-1000, -500): active width 1/3 at x = 1/3.
        var m = new CoordinateMapper(3000, 1000, new DisplayRect(-1000, -500, 1000, 1000));
        Assert.Equal(1.0 / 3, m.ActiveArea.Width, 12);
        Assert.Equal(1.0 / 3, m.ActiveArea.X, 12);
        DisplayRect c = m.ContentRect;
        Assert.Multiple(
            () => Assert.Equal(-1000 - 1000, c.X, 9),
            () => Assert.Equal(-500, c.Y, 9),
            () => Assert.Equal(3000, c.Width, 9),
            () => Assert.Equal(1000, c.Height, 9));
    }

    [Fact]
    public void Scale_is_per_axis()
    {
        var m = new CoordinateMapper(1000, 1000, new DisplayRect(0, 0, 2000, 1000), new NormalizedRect(0, 0, 1, 0.5));
        // Target 2000x500, active height 0.25: ScaleY = 500 / (0.25 * Max).
        Assert.Equal(500 / (0.25 * Max), m.ScaleY, 12);
        Assert.Equal(2000 / Max, m.ScaleX, 12);
    }

    [Fact]
    public void All_displays_bounds_use_every_origin_and_size()
    {
        MappingTarget t = MappingTarget.ForAllDisplays([new DisplayRect(-1000, 200, 500, 300), new DisplayRect(300, -100, 400, 200)]);
        Assert.Equal(new DisplayRect(-1000, -100, 1700, 600), t.Bounds);

        // Order does not matter, and a display contained in the box changes nothing.
        t = MappingTarget.ForAllDisplays([new DisplayRect(300, -100, 400, 200), new DisplayRect(-1000, 200, 500, 300), new DisplayRect(0, 0, 10, 10)]);
        Assert.Equal(new DisplayRect(-1000, -100, 1700, 600), t.Bounds);
    }

    [Fact]
    public void All_displays_target_has_the_whole_region_and_the_display_count()
    {
        MappingTarget t = MappingTarget.ForAllDisplays([new DisplayRect(0, 0, 10, 10), new DisplayRect(10, 0, 10, 10)]);
        Assert.Equal(NormalizedRect.Full, t.Region);
        Assert.Equal(2, t.Displays.Length);
        MappingTarget single = MappingTarget.ForDisplay(new DisplayRect(0, 0, 10, 10), new NormalizedRect(0.5, 0.5, 0.5, 0.5));
        Assert.False(single.IsAllDisplays);
        Assert.Equal(1, single.Displays.Length);
        Assert.Equal(new DisplayRect(0, 0, 10, 10), single.Displays[0]);
    }

    [Fact]
    public void A_point_in_a_vertical_gap_goes_to_the_nearer_display()
    {
        // A above B with a gap of 200 pt (y 500..700).
        MappingTarget t = MappingTarget.ForAllDisplays([new DisplayRect(0, 0, 1000, 500), new DisplayRect(0, 700, 1000, 500)]);
        DisplayPoint nearA = t.ClampToDisplays(new DisplayPoint(300, 560));
        DisplayPoint nearB = t.ClampToDisplays(new DisplayPoint(300, 650));
        Assert.Equal(300, nearA.X);
        Assert.Equal(Math.BitDecrement(500.0), nearA.Y);
        Assert.Equal(300, nearB.X);
        Assert.Equal(700, nearB.Y);

        // Same in the opposite listing order, so the winner does not depend on position in the list.
        t = MappingTarget.ForAllDisplays([new DisplayRect(0, 700, 1000, 500), new DisplayRect(0, 0, 1000, 500)]);
        Assert.Equal(Math.BitDecrement(500.0), t.ClampToDisplays(new DisplayPoint(300, 560)).Y);
        Assert.Equal(700, t.ClampToDisplays(new DisplayPoint(300, 650)).Y);
    }

    [Fact]
    public void The_nearest_display_uses_both_axes_of_the_squared_distance()
    {
        // A (0,0,100,100) and B (200,200,100,100): a diagonal gap.
        MappingTarget t = MappingTarget.ForAllDisplays([new DisplayRect(0, 0, 100, 100), new DisplayRect(200, 200, 100, 100)]);
        double aEdge = Math.BitDecrement(100.0);
        Assert.Equal(new DisplayPoint(200, 200), t.ClampToDisplays(new DisplayPoint(120, 190))); // A is 20 + 90 away, B 80 + 10
        Assert.Equal(new DisplayPoint(200, 200), t.ClampToDisplays(new DisplayPoint(190, 120)));
        Assert.Equal(new DisplayPoint(aEdge, 60), t.ClampToDisplays(new DisplayPoint(110, 60))); // A is 10 away, B 90 + 140
        Assert.Equal(new DisplayPoint(30, aEdge), t.ClampToDisplays(new DisplayPoint(30, 130)));
        Assert.Equal(new DisplayPoint(aEdge, aEdge), t.ClampToDisplays(new DisplayPoint(105, 105)));
    }

    [Fact]
    public void Equal_distances_go_to_the_first_listed_display()
    {
        // From the origin both displays' top-left corners are 50 away (30,40 and 40,30): no rounding at the corners.
        var a = new DisplayRect(30, 40, 100, 100);
        var b = new DisplayRect(40, 30, 100, 100);
        Assert.Equal(new DisplayPoint(30, 40), MappingTarget.ForAllDisplays([a, b]).ClampToDisplays(new DisplayPoint(0, 0)));
        Assert.Equal(new DisplayPoint(40, 30), MappingTarget.ForAllDisplays([b, a]).ClampToDisplays(new DisplayPoint(0, 0)));

        // One point nearer to B and B wins whatever the order.
        var nearer = new DisplayRect(40, 29, 100, 100);
        Assert.Equal(new DisplayPoint(40, 29), MappingTarget.ForAllDisplays([a, nearer]).ClampToDisplays(new DisplayPoint(0, 0)));
        Assert.Equal(new DisplayPoint(40, 29), MappingTarget.ForAllDisplays([nearer, a]).ClampToDisplays(new DisplayPoint(0, 0)));
    }

    [Fact]
    public void A_point_on_a_display_is_returned_unchanged_even_if_another_display_is_listed_first()
    {
        MappingTarget t = MappingTarget.ForAllDisplays([new DisplayRect(0, 0, 100, 100), new DisplayRect(100, 0, 100, 100), new DisplayRect(0, 100, 100, 100)]);
        Assert.Equal(new DisplayPoint(150, 40), t.ClampToDisplays(new DisplayPoint(150, 40)));
        Assert.Equal(new DisplayPoint(50, 150), t.ClampToDisplays(new DisplayPoint(50, 150)));
        // The shared edge belongs to the right/lower display (the left one excludes it).
        Assert.Equal(new DisplayPoint(100, 50), t.ClampToDisplays(new DisplayPoint(100, 50)));
        Assert.Equal(new DisplayPoint(50, 100), t.ClampToDisplays(new DisplayPoint(50, 100)));
    }

    [Fact]
    public void Display_edges_clamp_exactly()
    {
        MappingTarget t = MappingTarget.ForDisplay(new DisplayRect(10, 20, 100, 50));
        Assert.Equal(new DisplayPoint(10, 20), t.ClampToDisplays(new DisplayPoint(10, 20)));
        Assert.Equal(new DisplayPoint(10, 20), t.ClampToDisplays(new DisplayPoint(Math.BitDecrement(10.0), Math.BitDecrement(20.0))));
        Assert.Equal(new DisplayPoint(Math.BitIncrement(10.0), Math.BitIncrement(20.0)), t.ClampToDisplays(new DisplayPoint(Math.BitIncrement(10.0), Math.BitIncrement(20.0))));
        Assert.Equal(new DisplayPoint(Math.BitDecrement(110.0), Math.BitDecrement(70.0)), t.ClampToDisplays(new DisplayPoint(110, 70)));
        Assert.Equal(new DisplayPoint(Math.BitDecrement(110.0), Math.BitDecrement(70.0)), t.ClampToDisplays(new DisplayPoint(1e6, 1e6)));
        Assert.Equal(new DisplayPoint(Math.BitDecrement(110.0), 69), t.ClampToDisplays(new DisplayPoint(Math.BitDecrement(110.0) + 1, 69)));
        Assert.Equal(new DisplayPoint(109, 69), t.ClampToDisplays(new DisplayPoint(109, 69)));
    }

    [Fact]
    public void Map_pins_exactly_at_the_ends_of_the_active_area_and_just_outside_it()
    {
        var m = new CoordinateMapper(1000, 1000, new DisplayRect(100, 200, 400, 400)); // active area is the whole surface
        Assert.Equal(new DisplayPoint(100, 200), m.Map(0.0, 0.0));
        Assert.Equal(new DisplayPoint(100, 200), m.Map(-1.0, -1.0));
        DisplayPoint one = m.Map(1.0, 1.0);
        Assert.Equal(100 + (400 / Max), one.X, 9);
        Assert.Equal(200 + (400 / Max), one.Y, 9);
        DisplayPoint end = m.Map(Max, Max);
        Assert.Equal(Math.BitDecrement(500.0), end.X);
        Assert.Equal(Math.BitDecrement(600.0), end.Y);
        DisplayPoint beyond = m.Map(Max + 1, Max + 1);
        Assert.Equal(end, beyond);
        Assert.Equal(end, m.Map(ushort.MaxValue, ushort.MaxValue));
        Assert.Equal(new DisplayPoint(300, 400), m.Map(Max / 2, Max / 2));
    }

    [Fact]
    public void Map_pins_per_axis_at_the_active_area_with_margins()
    {
        // 1000x2000 surface on a 1000x1000 display: full width, active height 0.5 at y = 0.25.
        var m = new CoordinateMapper(1000, 2000, new DisplayRect(0, 0, 1000, 1000));
        Assert.Equal(new NormalizedRect(0, 0.25, 1, 0.5), m.ActiveArea);
        Assert.Equal(new DisplayPoint(500, 0), m.Map(Max / 2, 0.25 * Max));
        Assert.Equal(new DisplayPoint(500, 0), m.Map(Max / 2, 0.25 * Max * 0.5));
        Assert.Equal(new DisplayPoint(500, 0), m.Map(Max / 2, 0));
        DisplayPoint bottom = m.Map(Max / 2, 0.75 * Max);
        Assert.Equal(Math.BitDecrement(1000.0), bottom.Y);
        Assert.Equal(Math.BitDecrement(1000.0), m.Map(Max / 2, Max).Y);
        Assert.Equal(250, m.Map(Max / 2, 0.375 * Max).Y, 6);
        Assert.Equal(750, m.Map(Max / 2, 0.625 * Max).Y, 6);
    }

    [Fact]
    public void Gesture_mapping_is_exact_at_the_ends_and_clamps_just_outside()
    {
        var m = new CoordinateMapper(1000, 1000, new DisplayRect(0, 0, 2000, 1000));
        DisplayRect g = m.GestureRect;
        Assert.Equal(new DisplayRect(500, 0, 1000, 1000), g);
        Assert.Equal(new DisplayPoint(500, 0), m.MapGesture(0, 0));
        Assert.Equal(new DisplayPoint(500, 0), m.MapGesture(-1, -1));
        Assert.Equal(new DisplayPoint(1000, 500), m.MapGesture(Max / 2, Max / 2));
        DisplayPoint end = m.MapGesture(Max, Max);
        Assert.Equal(Math.BitDecrement(1500.0), end.X);
        Assert.Equal(Math.BitDecrement(1000.0), end.Y);
        Assert.Equal(end, m.MapGesture(Max + 1, Max + 1));
        Assert.Equal(new DisplayPoint(500, 0), m.MapGesture(double.NaN, double.NaN));
        DisplayPoint one = m.MapGesture(1, 1);
        Assert.Equal(500 + (1000 / Max), one.X, 9);
        Assert.Equal(1000 / Max, one.Y, 9);
    }

    [Fact]
    public void Gesture_short_side_is_the_shorter_of_the_two()
    {
        Assert.Equal(1000, new CoordinateMapper(1000, 1000, new DisplayRect(0, 0, 2000, 1000)).GestureShortSide, 9);
        Assert.Equal(500, new CoordinateMapper(2000, 1000, new DisplayRect(0, 0, 1000, 1000)).GestureShortSide, 9);
        Assert.Equal(1000, new CoordinateMapper(2000, 1000, new DisplayRect(0, 0, 2000, 1000)).GestureShortSide, 9);
    }

    [Fact]
    public void Gesture_display_is_the_one_at_the_origin_whatever_the_order()
    {
        var a = new DisplayRect(-100, 0, 100, 100);
        var main = new DisplayRect(0, 0, 100, 100);
        var offX = new DisplayRect(0, 100, 100, 100); // x == 0 but y != 0
        var offY = new DisplayRect(100, 0, 100, 100); // y == 0 but x != 0
        Assert.Equal(main, MappingTarget.ForAllDisplays([a, offX, offY, main]).GestureDisplay);
        Assert.Equal(offX, MappingTarget.ForAllDisplays([offX, offY]).GestureDisplay);
    }

    [Fact]
    public void All_displays_mapping_matches_the_single_display_when_there_is_one()
    {
        var display = new DisplayRect(50, 60, 800, 600);
        var single = new CoordinateMapper(1000, 1000, display);
        var all = new CoordinateMapper(1000, 1000, MappingTarget.ForAllDisplays([display]));
        for (double f = 0; f <= 1; f += 0.125)
        {
            Assert.Equal(single.Map(f * Max, (1 - f) * Max), all.Map(f * Max, (1 - f) * Max));
        }
    }

    [Fact]
    public void Invalid_all_displays_input_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MappingTarget.ForAllDisplays([]));
        DisplayRect[] tooMany = new DisplayRect[MappingTarget.MaxDisplays + 1];
        Array.Fill(tooMany, new DisplayRect(0, 0, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => MappingTarget.ForAllDisplays(tooMany));
        Assert.Equal(MappingTarget.MaxDisplays, MappingTarget.ForAllDisplays(tooMany.AsSpan(1)).Displays.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => MappingTarget.ForAllDisplays([new DisplayRect(0, 0, 10, 10), new DisplayRect(10, 0, 0, 10)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MappingTarget.ForAllDisplays([new DisplayRect(double.NaN, 0, 10, 10)]));
    }
}
