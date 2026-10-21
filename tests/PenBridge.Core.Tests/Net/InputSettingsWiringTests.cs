using System.Collections.Concurrent;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;

#pragma warning disable xUnit1051 // Network waits are bounded by Wait.Limit instead of the test token.

namespace PenBridge.Core.Tests.Net;

/// <summary>The server's input settings and mapping target reach the session input, and its active area the phone (ADR-0031..0033).</summary>
public class InputSettingsWiringTests
{
    private static readonly InputSettings Mouse = InputSettings.Default with { StylusMode = StylusMode.Mouse };

    private static NetHarness Harness(InputSettings? settings = null, bool activeArea = true) => new(o => new PenBridgeServerOptions
    {
        Identity = o.Identity,
        Registry = o.Registry,
        InputSink = o.InputSink,
        ServerName = o.ServerName,
        BindAddress = o.BindAddress,
        Port = o.Port,
        TargetDisplay = o.TargetDisplay,
        TimeProvider = o.TimeProvider,
        RemoteSettings = true,
        ActiveArea = activeArea,
        InputSettings = settings ?? InputSettings.Default,
    });

    [Fact]
    public async Task The_phone_gets_the_active_area_after_surface_info_and_on_target_and_mode_changes()
    {
        await using NetHarness h = Harness();
        var areas = new ConcurrentQueue<NormalizedRect>();
        PenBridgeClient client = h.CreateClient();
        client.ActiveAreaReceived += (_, e) => areas.Enqueue(e.Area);
        h.Server.OpenPairingWindow();
        await client.PairWithPinAsync("127.0.0.1", h.Port, h.Server.CurrentPin!).Bounded();
        await h.WaitForSurfaceAsync();

        // SurfaceInfo: the portrait 1:2 display letterboxes the taller phone surface vertically.
        // The connect-time push (before SurfaceInfo) may arrive first: wait for the letterboxed area itself.
        NormalizedRect letterboxed = h.Server.ActiveInput!.ActiveArea;
        await Wait.Until(() => client.LastActiveArea is { } a && Near(letterboxed, a), "the first active area");
        AssertArea(letterboxed, client.LastActiveArea!.Value);
        Assert.True(client.LastActiveArea.Value.Height < 1);

        // A wide target: a horizontal band.
        h.Server.SetTarget(MappingTarget.ForDisplay(new DisplayRect(0, 0, 2000, 1000)));
        NormalizedRect band = h.Server.ActiveInput.ActiveArea;
        Assert.True(band.Height < 0.5);
        await Wait.Until(() => Near(client.LastActiveArea!.Value, band), "the area of the new target");

        // Mouse mode: the whole surface.
        h.Server.SetInputSettings(Mouse);
        Assert.Same(Mouse, h.Server.InputSettings);
        await Wait.Until(() => client.LastActiveArea == NormalizedRect.Full, "the mouse-mode area");
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Settings_changed_mid_session_reach_the_tracker()
    {
        await using NetHarness h = Harness();
        PenBridgeClient client = await h.PairAsync();

        // Threshold 0.5: a contact at about 0.46 pressure is hover.
        h.Server.SetInputSettings(InputSettings.Default with { ClickThreshold = 0.5 });
        Assert.Equal(0.5, h.Server.ActiveInput!.Settings.ClickThreshold);
        Assert.True(client.SendState(Pens.Hover(1000, 1000), []));
        Assert.True(client.SendState(Pens.Contact(1100, 1000, 30000), []));
        Assert.True(client.SendState(Pens.Contact(1200, 1000, 30000), []));
        await Wait.Until(() => h.Server.GetStats().Counters.Accepted == 3, "all three frames applied");
        Assert.DoesNotContain(PenEventKind.Down, h.Sink.PenKinds());

        // Back to the defaults (between strokes, so immediately): the same contact is a Down.
        h.Server.SetInputSettings(InputSettings.Default);
        Assert.True(client.SendState(Pens.Contact(1300, 1000, 30000), []));
        await Wait.Until(() => h.Sink.PenKinds().Contains(PenEventKind.Down), "a pen down");
    }

    [Fact]
    public async Task Initial_settings_apply_to_every_session()
    {
        await using NetHarness h = Harness(Mouse);
        PenBridgeClient client = await h.PairAsync();

        Assert.Same(Mouse, h.Server.ActiveInput!.Settings);
        await Wait.Until(() => client.LastActiveArea == NormalizedRect.Full, "the mouse-mode area");
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Server.SetInputSettings(InputSettings.Default with { MouseSpeed = 99 }));
        Assert.Same(Mouse, h.Server.InputSettings);
    }

    [Fact]
    public async Task A_phone_without_the_capability_gets_no_active_area()
    {
        await using NetHarness h = Harness();
        int received = 0;
        PenBridgeClient client = await h.PairAsync(configure: o => new PenBridgeClientOptions
        {
            SecretStore = o.SecretStore,
            Surface = o.Surface,
            DeviceName = o.DeviceName,
            TimeProvider = o.TimeProvider,
            Capabilities = ProtocolCapabilities.SampleDedupe | ProtocolCapabilities.FingerTouches,
        });
        client.ActiveAreaReceived += (_, _) => Interlocked.Increment(ref received);

        h.Server.SetTarget(MappingTarget.ForDisplay(new DisplayRect(0, 0, 2000, 1000)));
        h.Server.SetInputSettings(Mouse);
        Assert.Equal(NormalizedRect.Full, h.Server.ActiveInput!.ActiveArea);
        await h.SettleAsync();
        Assert.Null(client.LastActiveArea);
        Assert.Equal(0, received);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task A_server_without_the_capability_sends_no_active_area()
    {
        await using NetHarness h = Harness(activeArea: false);
        PenBridgeClient client = await h.PairAsync();

        h.Server.SetTarget(MappingTarget.ForDisplay(new DisplayRect(0, 0, 2000, 1000)));
        await h.SettleAsync();
        Assert.Null(client.LastActiveArea);
        Assert.True(client.IsConnected);
    }

    private static bool Near(NormalizedRect a, NormalizedRect b) =>
        Math.Abs(a.X - b.X) <= 2.0 / 65535 && Math.Abs(a.Y - b.Y) <= 2.0 / 65535
        && Math.Abs(a.Width - b.Width) <= 2.0 / 65535 && Math.Abs(a.Height - b.Height) <= 2.0 / 65535;

    private static void AssertArea(NormalizedRect expected, NormalizedRect actual) =>
        Assert.True(Near(expected, actual), $"expected {expected}, got {actual}");
}
