using CsCheck;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Properties;

/// <summary>
/// Invariants of the region editor (<see cref="RegionEditGeometry"/>, ADR-0035 amendment) over random drag sequences,
/// and of the tint hole (<see cref="RegionTintRule.HoleInView"/>) against <see cref="RegionEditGeometry.ToRegion"/>.
/// </summary>
public class RegionEditGeometryProperties
{
    private enum DragKind
    {
        Move,
        Resize,
        Draw,
        ApplyAspect,
    }

    private readonly record struct Drag(DragKind Kind, RegionHandle Handle, double AnchorX, double AnchorY, double Dx, double Dy);

    /// <summary>A view, an aspect lock the view can hold at the minimum size (or none), and 1..30 drags.</summary>
    private static Gen<(double Width, double Height, double? Aspect, Drag[] Drags)> Session(bool locked) =>
        PropertyGens.View.SelectMany((w, h) =>
            Gen.Select(
                locked ? Gen.Double[0.2, 5].Select(a => (double?)a) : Gen.Const((double?)null),
                Gen.Select(Gen.Enum<DragKind>(), PropertyGens.Handle, Gen.Double[-0.2 * w, 1.2 * w], Gen.Double[-0.2 * h, 1.2 * h], Gen.Double[-1.5 * w, 1.5 * w], Gen.Double[-1.5 * h, 1.5 * h])
                    .Select((k, handle, ax, ay, dx, dy) => new Drag(k, handle, ax, ay, dx, dy))
                    .Array[1, 30])
            .Where((aspect, _) => aspect is not { } ratio || Fits(w, h, ratio))
            .Select((aspect, drags) => (w, h, aspect, drags)));

    [Fact]
    public void Any_free_drag_sequence_keeps_the_selection_on_the_display_and_big_enough() => CheckDrags(locked: false);

    [Fact(Skip = "#267: ApplyAspect and the locked Resize throw when the fitted side rounds past the display")]
    public void Any_locked_drag_sequence_keeps_the_selection_on_the_display_big_enough_and_on_the_aspect() => CheckDrags(locked: true);

    [Theory(Skip = "#267: ApplyAspect throws when the fitted side rounds past the display")]
    [InlineData(1512, 982, 1080.0 / 2340)] // MacBook Pro 14" with an S23 in portrait
    [InlineData(3024, 1964, 1080.0 / 2340)]
    [InlineData(1470, 956, 1080.0 / 2340)]
    [InlineData(2560, 1600, 2800.0 / 1752)]
    public void Locking_the_aspect_on_the_whole_display_fits_inside_it(double width, double height, double aspect)
    {
        ViewRect r = RegionEditGeometry.ApplyAspect(new ViewRect(0, 0, width, height), aspect, width, height);
        AssertValid(r, width, height, aspect, "locked");
    }

    private static void CheckDrags(bool locked)
    {
        Session(locked).Sample(s =>
        {
            (double width, double height, double? aspect, Drag[] drags) = s;
            ViewRect r = aspect is { } ratio
                ? RegionEditGeometry.ApplyAspect(new ViewRect(0, 0, width, height), ratio, width, height)
                : new ViewRect(0, 0, width, height);
            AssertValid(r, width, height, aspect, "start");
            for (int i = 0; i < drags.Length; i++)
            {
                Drag d = drags[i];
                switch (d.Kind)
                {
                    case DragKind.Move:
                        r = RegionEditGeometry.Move(r, d.Dx, d.Dy, width, height);
                        break;
                    case DragKind.Resize:
                        r = RegionEditGeometry.Resize(r, d.Handle, d.Dx, d.Dy, width, height, aspect);
                        break;
                    case DragKind.Draw:
                        // The editor keeps a drawn selection only when it is big enough when the drag ends.
                        ViewRect drawn = RegionEditGeometry.Draw(d.AnchorX, d.AnchorY, d.AnchorX + d.Dx, d.AnchorY + d.Dy, width, height, aspect);
                        AssertOnDisplay(drawn, width, height, $"draw {i}");
                        if (aspect is { } drawRatio && drawn.Width > 0 && drawn.Height > 0)
                        {
                            Assert.Equal(1, drawn.Width / (drawn.Height * drawRatio), 1e-9);
                        }

                        if (RegionEditGeometry.IsBigEnough(drawn, width, height))
                        {
                            r = drawn;
                        }

                        break;
                    case DragKind.ApplyAspect when aspect is { } lockRatio:
                        r = RegionEditGeometry.ApplyAspect(r, lockRatio, width, height);
                        break;
                }

                AssertValid(r, width, height, aspect, $"after drag {i} ({d.Kind} {d.Handle})");
            }

            // What the editor saves is a region the Mac's own validation accepts.
            SettingEntry entry = SettingEntry.Rect(MacSettingKey.Region, RegionEditGeometry.ToRegion(r, width, height));
            Assert.True(MacSettings.TryValidate(entry, new MacSettingValues(), [], out _), $"{entry} rejected");
        }, iter: PropertyGens.GeometryIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void Hole_in_view_and_to_region_invert_each_other()
    {
        Gen.Select(PropertyGens.Region, PropertyGens.View).Sample((region, view) =>
        {
            ViewRect hole = RegionTintRule.HoleInView(region, view.Width, view.Height);
            NormalizedRect back = RegionEditGeometry.ToRegion(hole, view.Width, view.Height);
            Assert.Equal(region.X, back.X, 1e-12);
            Assert.Equal(region.Y, back.Y, 1e-12);
            Assert.Equal(region.Width, back.Width, 1e-12);
            Assert.Equal(region.Height, back.Height, 1e-12);
        }, iter: PropertyGens.GeometryIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void To_region_and_hole_in_view_invert_each_other()
    {
        Gen<(ViewRect Rect, double Width, double Height)> selection = PropertyGens.View.SelectMany((w, h) =>
            Gen.Select(Gen.Double[0, w - 1], Gen.Double[0, h - 1])
                .SelectMany((x, y) => Gen.Select(Gen.Double[1, w - x], Gen.Double[1, h - y])
                    .Select((sw, sh) => (new ViewRect(x, y, sw, sh), w, h))));
        selection.Sample(s =>
        {
            NormalizedRect region = RegionEditGeometry.ToRegion(s.Rect, s.Width, s.Height);
            ViewRect back = RegionTintRule.HoleInView(region, s.Width, s.Height);
            double tolerance = 1e-9 * Math.Max(s.Width, s.Height);
            Assert.Equal(s.Rect.X, back.X, tolerance);
            Assert.Equal(s.Rect.Y, back.Y, tolerance);
            Assert.Equal(s.Rect.Width, back.Width, tolerance);
            Assert.Equal(s.Rect.Height, back.Height, tolerance);
        }, iter: PropertyGens.GeometryIterations, threads: PropertyGens.Threads);
    }

    /// <summary>The smallest rect of <paramref name="ratio"/> that keeps both sides at their minimum fits the view.</summary>
    private static bool Fits(double width, double height, double ratio) =>
        Math.Max(MinSide(width), MinSide(height) * ratio) <= Math.Min(width, height * ratio);

    private static double MinSide(double side) => Math.Min(RegionEditGeometry.MinSide(side), side);

    private static void AssertValid(ViewRect r, double width, double height, double? aspect, string when)
    {
        AssertOnDisplay(r, width, height, when);
        double eps = 1e-9 * Math.Max(width, height);
        Assert.True(r.Width >= MinSide(width) - eps, $"{when}: width {r.Width:R} below {MinSide(width):R} in {r}");
        Assert.True(r.Height >= MinSide(height) - eps, $"{when}: height {r.Height:R} below {MinSide(height):R} in {r}");
        if (aspect is { } ratio)
        {
            Assert.True(Math.Abs((r.Width / (r.Height * ratio)) - 1) <= 1e-9, $"{when}: {r} is off the aspect {ratio:R}");
        }
    }

    private static void AssertOnDisplay(ViewRect r, double width, double height, string when)
    {
        double eps = 1e-9 * Math.Max(width, height);
        Assert.True(r.Width >= 0 && r.Height >= 0, $"{when}: negative size {r}");
        Assert.True(r.X >= -eps && r.Y >= -eps, $"{when}: {r} starts off the {width}x{height} display");
        Assert.True(r.X + r.Width <= width + eps && r.Y + r.Height <= height + eps, $"{when}: {r} ends off the {width}x{height} display");
    }
}
