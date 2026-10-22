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
            int expected = -1;
            double best = 0;
            for (int i = 0; i < displays.Length; i++)
            {
                DisplayRect d = displays[i];
                double w = Math.Min(window.X + window.Width, d.X + d.Width) - Math.Max(window.X, d.X);
                double h = Math.Min(window.Y + window.Height, d.Y + d.Height) - Math.Max(window.Y, d.Y);
                if (w >= WindowTargetGeometry.MinVisibleSide && h >= WindowTargetGeometry.MinVisibleSide && w * h > best)
                {
                    expected = i;
                    best = w * h;
                }
            }

            Assert.Equal(expected, WindowTargetGeometry.DisplayIndexFor(window, displays));
            bool fitted = WindowTargetGeometry.TryFit(window, displays, out int index, out NormalizedRect region);
            Assert.Equal(expected >= 0, fitted);
            Assert.Equal(expected, index);
            if (fitted)
            {
                Assert.True(WindowTargetGeometry.TryRegion(window, displays[index], out NormalizedRect alone));
                Assert.Equal(alone, region);
            }
            else
            {
                Assert.Equal(NormalizedRect.Full, region);
            }
        }, iter: PropertyGens.GeometryIterations, threads: PropertyGens.Threads);
    }
}
