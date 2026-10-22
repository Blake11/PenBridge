using CsCheck;
using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Properties;

/// <summary>Invariants of <see cref="CoordinateMapper"/> over random surfaces, displays, regions and points (ADR-0031).</summary>
public class CoordinateMapperProperties
{
    private static readonly Gen<CoordinateMapper> SingleDisplay =
        Gen.Select(PropertyGens.Surface, PropertyGens.Display, PropertyGens.Region)
            .Select((s, d, r) => new CoordinateMapper(s.Width, s.Height, d, r));

    private static readonly Gen<CoordinateMapper> AllDisplays =
        Gen.Select(PropertyGens.Surface, PropertyGens.Display.Array[1, 4])
            .Select((s, ds) => new CoordinateMapper(s.Width, s.Height, MappingTarget.ForAllDisplays(ds)));

    [Fact]
    public void A_mapped_point_stays_inside_the_target_rect()
    {
        Gen.Select(SingleDisplay, PropertyGens.Coordinate, PropertyGens.Coordinate).Sample((mapper, x, y) =>
        {
            DisplayPoint p = mapper.Map(x, y);
            DisplayRect t = mapper.TargetRect;
            Assert.InRange(p.X, t.X, t.X + t.Width);
            Assert.InRange(p.Y, t.Y, t.Y + t.Height);
        }, iter: PropertyGens.GeometryIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void A_mapped_point_stays_on_its_display()
    {
        // The right and bottom edges belong to the neighbouring display (ClampToDisplays' convention).
        Gen.Select(SingleDisplay, PropertyGens.Coordinate, PropertyGens.Coordinate).Sample((mapper, x, y) =>
        {
            DisplayPoint p = mapper.Map(x, y);
            DisplayRect d = mapper.Target!.Displays[0];
            Assert.True(p.X >= d.X && p.X < d.X + d.Width, $"x {p.X:R} off display {d}");
            Assert.True(p.Y >= d.Y && p.Y < d.Y + d.Height, $"y {p.Y:R} off display {d}");
        }, iter: PropertyGens.GeometryIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void With_all_displays_a_mapped_point_lands_on_one_of_them()
    {
        Gen.Select(AllDisplays, PropertyGens.Coordinate, PropertyGens.Coordinate).Sample((mapper, x, y) =>
        {
            DisplayPoint p = mapper.Map(x, y);
            bool onOne = false;
            foreach (DisplayRect d in mapper.Target!.Displays)
            {
                onOne |= p.X >= d.X && p.X < d.X + d.Width && p.Y >= d.Y && p.Y < d.Y + d.Height;
            }

            Assert.True(onOne, $"{p} is on no display");
        }, iter: PropertyGens.GeometryIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void Mapping_is_monotonic_on_each_axis()
    {
        Gen.Select(SingleDisplay, PropertyGens.Coordinate, PropertyGens.Coordinate, PropertyGens.Coordinate)
            .Where((_, a, b, c) => !double.IsNaN(a) && !double.IsNaN(b) && !double.IsNaN(c))
            .Sample((mapper, a, b, other) =>
            {
                double lo = Math.Min(a, b);
                double hi = Math.Max(a, b);
                Assert.True(mapper.Map(lo, other).X <= mapper.Map(hi, other).X, "x not monotonic");
                Assert.True(mapper.Map(other, lo).Y <= mapper.Map(other, hi).Y, "y not monotonic");
            }, iter: PropertyGens.GeometryIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void The_active_area_is_centred_inside_the_surface_with_the_target_aspect()
    {
        Gen.Select(PropertyGens.Surface, PropertyGens.Display, PropertyGens.Region).Sample((s, d, r) =>
        {
            var mapper = new CoordinateMapper(s.Width, s.Height, d, r);
            NormalizedRect a = mapper.ActiveArea;
            DisplayRect t = mapper.TargetRect;
            Assert.True(a.Width > 0 && a.Height > 0, $"empty {a}");
            Assert.True(a.Width == 1 || a.Height == 1, $"neither side full: {a}");
            Assert.Equal((1 - a.Width) / 2, a.X, 1e-12);
            Assert.Equal((1 - a.Height) / 2, a.Y, 1e-12);
            Assert.InRange(a.X + a.Width, 0, 1 + 1e-12);
            Assert.InRange(a.Y + a.Height, 0, 1 + 1e-12);
            double activeAspect = a.Width * s.Width / (a.Height * s.Height);
            Assert.Equal(1, activeAspect / (t.Width / t.Height), 1e-9);
        }, iter: PropertyGens.GeometryIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void Inside_the_active_area_the_mapping_inverts()
    {
        // A point of the target (u, v as fractions of it) taken back to surface coordinates maps onto itself.
        Gen.Select(SingleDisplay, Gen.Double[0, 0.999_999], Gen.Double[0, 0.999_999]).Sample((mapper, u, v) =>
        {
            NormalizedRect a = mapper.ActiveArea;
            DisplayRect t = mapper.TargetRect;
            double nx = (a.X + (u * a.Width)) * CoordinateMapper.NormalizedMax;
            double ny = (a.Y + (v * a.Height)) * CoordinateMapper.NormalizedMax;
            DisplayPoint p = mapper.Map(nx, ny);
            double tolerance = 1e-9 * Math.Max(Math.Abs(t.X) + t.Width, Math.Abs(t.Y) + t.Height);
            Assert.Equal(t.X + (u * t.Width), p.X, tolerance);
            Assert.Equal(t.Y + (v * t.Height), p.Y, tolerance);
        }, iter: PropertyGens.GeometryIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void A_gesture_point_stays_inside_the_gesture_rect()
    {
        Gen.Select(SingleDisplay, PropertyGens.Coordinate, PropertyGens.Coordinate).Sample((mapper, x, y) =>
        {
            DisplayPoint p = mapper.MapGesture(x, y);
            DisplayRect g = mapper.GestureRect;
            Assert.True(p.X >= g.X && p.X < g.X + g.Width, $"x {p.X:R} outside {g}");
            Assert.True(p.Y >= g.Y && p.Y < g.Y + g.Height, $"y {p.Y:R} outside {g}");
        }, iter: PropertyGens.GeometryIterations, threads: PropertyGens.Threads);
    }
}
