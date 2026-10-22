using CsCheck;
using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Properties;

/// <summary>Invariants of clipping a window to a display and normalizing it (<see cref="WindowTargetGeometry"/>, ADR-0036).</summary>
public class WindowTargetGeometryProperties
{
    /// <summary>A window anywhere around the displays, from empty to larger than any display.</summary>
    private static readonly Gen<DisplayRect> Window =
        Gen.Select(Gen.Double[-10_000, 10_000], Gen.Double[-10_000, 10_000], Gen.Double[0, 9000], Gen.Double[0, 9000])
            .Select((x, y, w, h) => new DisplayRect(x, y, w, h));

    [Fact]
    public void A_fitted_region_is_the_window_clipped_to_the_display_and_maps_like_a_region()
    {
        Gen.Select(Window, PropertyGens.Display).Sample((window, display) =>
        {
            double left = Math.Max(window.X, display.X);
            double top = Math.Max(window.Y, display.Y);
            double right = Math.Min(window.X + window.Width, display.X + display.Width);
            double bottom = Math.Min(window.Y + window.Height, display.Y + display.Height);
            bool visible = right - left >= WindowTargetGeometry.MinVisibleSide && bottom - top >= WindowTargetGeometry.MinVisibleSide;

            bool fitted = WindowTargetGeometry.TryRegion(window, display, out NormalizedRect r);
            Assert.Equal(visible, fitted);
            if (!fitted)
            {
                Assert.Equal(NormalizedRect.Full, r);
                return;
            }

            Assert.True(r.X >= 0 && r.Y >= 0 && r.Width > 0 && r.Height > 0, $"{r}");
            Assert.True(r.X + r.Width <= 1 + 1e-12 && r.Y + r.Height <= 1 + 1e-12, $"{r} past the display");
            double tolerance = 1e-9 * (Math.Max(Math.Abs(display.X), Math.Abs(display.Y)) + Math.Max(display.Width, display.Height));
            Assert.Equal(left, display.X + (r.X * display.Width), tolerance);
            Assert.Equal(top, display.Y + (r.Y * display.Height), tolerance);
            Assert.Equal(right - left, r.Width * display.Width, tolerance);
            Assert.Equal(bottom - top, r.Height * display.Height, tolerance);

            // The mapper accepts it: a window target maps like a region of its display.
            MappingTarget target = MappingTarget.ForDisplay(display, r);
            Assert.Equal(left, target.Bounds.X, tolerance);
            Assert.Equal(top, target.Bounds.Y, tolerance);
        }, iter: PropertyGens.GeometryIterations, threads: PropertyGens.Threads);
    }

    [Fact]
    public void The_window_goes_to_the_display_holding_most_of_it()
    {
        Gen.Select(Window, PropertyGens.Display.Array[0, 4]).Sample((window, displays) =>
        {
            bool fitted = WindowTargetGeometry.TryFit(window, displays, out int index, out NormalizedRect region);
            Assert.Equal(index, WindowTargetGeometry.DisplayIndexFor(window, displays));
            if (!fitted)
            {
                // No display holds a visible part.
                Assert.Equal(-1, index);
                Assert.Equal(NormalizedRect.Full, region);
                foreach (DisplayRect d in displays)
                {
                    (double w, double h) = Overlap(window, d);
                    Assert.False(w >= WindowTargetGeometry.MinVisibleSide && h >= WindowTargetGeometry.MinVisibleSide, $"{window} is visible on {d}");
                }

                return;
            }

            // The region lies inside the chosen display and maps back onto the window's part of it.
            DisplayRect chosen = displays[index];
            Assert.True(region.X >= 0 && region.Y >= 0 && region.Width > 0 && region.Height > 0, $"{region}");
            Assert.True(region.X + region.Width <= 1 + 1e-12 && region.Y + region.Height <= 1 + 1e-12, $"{region} past the display");
            double tolerance = 1e-9 * (Math.Max(Math.Abs(chosen.X), Math.Abs(chosen.Y)) + Math.Max(chosen.Width, chosen.Height));
            Assert.Equal(Math.Max(window.X, chosen.X), chosen.X + (region.X * chosen.Width), tolerance);
            Assert.Equal(Math.Max(window.Y, chosen.Y), chosen.Y + (region.Y * chosen.Height), tolerance);
            Assert.Equal(Math.Min(window.X + window.Width, chosen.X + chosen.Width), chosen.X + ((region.X + region.Width) * chosen.Width), tolerance);
            Assert.Equal(Math.Min(window.Y + window.Height, chosen.Y + chosen.Height), chosen.Y + ((region.Y + region.Height) * chosen.Height), tolerance);

            // No display holds more of it; an earlier one holds strictly less (ties go to the earlier display).
            (double cw, double ch) = Overlap(window, chosen);
            double chosenArea = cw * ch;
            for (int i = 0; i < displays.Length; i++)
            {
                (double w, double h) = Overlap(window, displays[i]);
                if (i == index || w < WindowTargetGeometry.MinVisibleSide || h < WindowTargetGeometry.MinVisibleSide)
                {
                    continue;
                }

                Assert.True(i < index ? w * h < chosenArea : w * h <= chosenArea, $"display {i} holds {w * h:R}, the chosen {index} {chosenArea:R}");
            }
        }, iter: PropertyGens.GeometryIterations, threads: PropertyGens.Threads);
    }

    private static (double Width, double Height) Overlap(DisplayRect a, DisplayRect b) =>
        (Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X), Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
}
