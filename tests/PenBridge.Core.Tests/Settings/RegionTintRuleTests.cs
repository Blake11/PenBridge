using PenBridge.Core.Input;
using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Settings;

/// <summary>The tint outside the active region (ADR-0035): when it shows and where its hole is.</summary>
public class RegionTintRuleTests
{
    private static readonly NormalizedRect Part = new(0.25, 0.1, 0.5, 0.4);

    private static readonly RegionTintState Shown = new(
        Connected: true,
        Paused: false,
        Enabled: true,
        OverlayOpen: false,
        SingleDisplay: true,
        Region: Part,
        Mode: StylusMode.Pen);

    [Fact]
    public void Shows_when_every_condition_holds() => Assert.True(RegionTintRule.IsVisible(Shown));

    [Fact]
    public void Hidden_without_a_session() => Assert.False(RegionTintRule.IsVisible(Shown with { Connected = false }));

    [Fact]
    public void Hidden_while_paused() => Assert.False(RegionTintRule.IsVisible(Shown with { Paused = true }));

    [Fact]
    public void Hidden_when_the_setting_is_off() => Assert.False(RegionTintRule.IsVisible(Shown with { Enabled = false }));

    [Fact]
    public void Hidden_while_the_overlay_is_open() => Assert.False(RegionTintRule.IsVisible(Shown with { OverlayOpen = true }));

    [Fact]
    public void Hidden_for_all_displays() => Assert.False(RegionTintRule.IsVisible(Shown with { SingleDisplay = false }));

    [Fact]
    public void Hidden_in_mouse_mode() => Assert.False(RegionTintRule.IsVisible(Shown with { Mode = StylusMode.Mouse }));

    [Fact]
    public void Hidden_for_the_full_display()
    {
        Assert.False(RegionTintRule.IsVisible(Shown with { Region = NormalizedRect.Full }));

        // Within the epsilon still counts as full; just past it doesn't.
        Assert.False(RegionTintRule.IsVisible(Shown with { Region = new NormalizedRect(1e-7, 0, 1 - 1e-7, 1) }));
        Assert.True(RegionTintRule.IsVisible(Shown with { Region = new NormalizedRect(0, 0, 0.999, 1) }));
    }

    [Fact]
    public void Hole_is_scaled_and_flipped_to_a_bottom_left_origin()
    {
        // Top 10 % .. 50 % of a 1000 × 500 view: y up, so the hole spans 250 .. 450 from the bottom.
        Assert.Equal(new ViewRect(250, 250, 500, 200), RegionTintRule.HoleInView(Part, 1000, 500));
    }

    [Fact]
    public void Hole_of_edge_regions()
    {
        Assert.Equal(new ViewRect(0, 0, 800, 600), RegionTintRule.HoleInView(NormalizedRect.Full, 800, 600));

        // The top-left quarter sits at the top of a y-up view; the bottom-right quarter at its origin's row.
        Assert.Equal(new ViewRect(0, 300, 400, 300), RegionTintRule.HoleInView(new NormalizedRect(0, 0, 0.5, 0.5), 800, 600));
        Assert.Equal(new ViewRect(400, 0, 400, 300), RegionTintRule.HoleInView(new NormalizedRect(0.5, 0.5, 0.5, 0.5), 800, 600));
    }
}
