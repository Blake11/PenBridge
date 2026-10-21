using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Tests.Input;

#pragma warning disable xUnit1051 // Network waits are bounded by Wait.Limit instead of the test token.

namespace PenBridge.Core.Tests.Net;

/// <summary>
/// ADR-0026: <see cref="ProtocolCapabilities.FingerTouches"/> negotiation, the server's one-finger recognizer per
/// session, and a one-finger tap that survives any one lost datagram (the echo before a lift, and release repeats at
/// the lift's own time).
/// </summary>
public class FingerTouchesTests
{
    private static readonly TimeSpan Repeat = TimeSpan.FromMilliseconds(40);
    internal static readonly TouchSample[] Down = [new(1, true, 30000, 20000)];
    internal static readonly TouchSample[] Up = [];

    internal static NetHarness Harness(bool fingerTouches = true) => new(o => new PenBridgeServerOptions
    {
        Identity = o.Identity,
        Registry = o.Registry,
        InputSink = o.InputSink,
        ServerName = o.ServerName,
        BindAddress = o.BindAddress,
        Port = o.Port,
        TargetDisplay = o.TargetDisplay,
        TimeProvider = o.TimeProvider,
        FingerTouches = fingerTouches,
    });

    /// <summary>A phone with finger fallback on (the default Android setting), as SurfaceInfo declares it.</summary>
    private static readonly SurfaceInfoMessage FallbackSurface =
        new(1440, 3088, 500f, 500f, SurfaceOrientation.Portrait, SurfaceCapabilities.FingerFallback);

    internal static PenBridgeClientOptions Phone(PenBridgeClientOptions o) => new()
    {
        SecretStore = o.SecretStore,
        Surface = FallbackSurface,
        DeviceName = o.DeviceName,
        DatagramFilter = o.DatagramFilter,
        TimeProvider = o.TimeProvider,
    };

    private static PenBridgeClientOptions OlderPhone(PenBridgeClientOptions o) => new()
    {
        SecretStore = o.SecretStore,
        Surface = FallbackSurface,
        DeviceName = o.DeviceName,
        DatagramFilter = o.DatagramFilter,
        TimeProvider = o.TimeProvider,
        Capabilities = ProtocolCapabilities.SampleDedupe,
    };

    /// <summary>Drops the datagram whose 1-based index equals <paramref name="drop"/>.Value (0: none).</summary>
    private static DatagramFilter Dropping(StrongBoxInt drop)
    {
        int n = 0;
        return _ => Interlocked.Increment(ref n) != drop.Value;
    }

    private static int[] ClickCounts(NetHarness h) =>
        [.. h.Sink.Gestures().Where(g => g.Kind == GestureKind.Click).Select(g => g.ClickCount)];

    /// <summary>Advances fake time one repeat at a time, waiting each step for the datagrams sent so far.</summary>
    private static async Task StepAsync(NetHarness h, PenBridgeClient client, StrongBoxInt drop, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            h.Time.Advance(Repeat);
            await DeliveredAsync(h, client, drop);
        }
    }

    private static Task DeliveredAsync(NetHarness h, PenBridgeClient client, StrongBoxInt drop)
    {
        long sent = client.DatagramsSealed;
        long expected = sent - (drop.Value > 0 && drop.Value <= sent ? 1 : 0);
        return Wait.Until(() => h.Server.GetStats().Counters.Accepted == expected, "datagrams delivered");
    }

    [Fact]
    public async Task A_default_server_doesnt_offer_it_so_nothing_changes()
    {
        await using NetHarness h = Harness(fingerTouches: false);
        var sessions = new List<SessionInfo>();
        h.Server.SessionStarted += (_, e) => sessions.Add(e.Session);
        PenBridgeClient client = await h.ConnectPairedAsync(configure: Phone);

        Assert.Equal(ProtocolCapabilities.SampleDedupe, client.ServerCapabilities);
        Assert.Equal(ProtocolCapabilities.SampleDedupe, client.NegotiatedCapabilities);
        Assert.Equal(ProtocolCapabilities.SampleDedupe, Assert.Single(sessions).Capabilities);

        var drop = new StrongBoxInt();
        client.SendState(null, Down);
        await DeliveredAsync(h, client, drop);
        client.SendState(null, Up);
        await StepAsync(h, client, drop, 3);
        Assert.Equal(4, client.DatagramsSealed); // touch, lift, two repeats: no echo
        Assert.Empty(ClickCounts(h)); // a lone finger is no gesture without it
    }

    [Fact]
    public async Task Both_sides_negotiate_it_and_the_session_clicks_on_a_one_finger_tap()
    {
        await using NetHarness h = Harness();
        var sessions = new List<SessionInfo>();
        h.Server.SessionStarted += (_, e) => sessions.Add(e.Session);
        PenBridgeClient client = await h.ConnectPairedAsync(configure: Phone);

        ProtocolCapabilities both = ProtocolCapabilities.SampleDedupe | ProtocolCapabilities.FingerTouches;
        Assert.Equal(both, client.ServerCapabilities);
        Assert.Equal(both, client.NegotiatedCapabilities);
        Assert.Equal(both, Assert.Single(sessions).Capabilities);

        var drop = new StrongBoxInt();
        client.SendState(null, Down);
        await DeliveredAsync(h, client, drop);
        client.SendState(null, Up);
        await StepAsync(h, client, drop, 3);
        Assert.Equal(5, client.DatagramsSealed); // touch, echo, lift, two repeats
        Assert.Equal([1], ClickCounts(h));
    }

    [Fact]
    public async Task An_older_phone_gets_no_one_finger_gestures()
    {
        await using NetHarness h = Harness();
        PenBridgeClient client = await h.ConnectPairedAsync(configure: OlderPhone);
        Assert.Equal(ProtocolCapabilities.SampleDedupe, client.NegotiatedCapabilities);

        var drop = new StrongBoxInt();
        client.SendState(null, Down); // e.g. an older phone with finger fallback off
        await DeliveredAsync(h, client, drop);
        client.SendState(null, Up);
        await StepAsync(h, client, drop, 3);
        Assert.Equal(4, client.DatagramsSealed);
        Assert.Empty(ClickCounts(h));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)] // the only contact frame
    [InlineData(2)] // the echo
    [InlineData(3)] // the lift
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)] // second tap's contact
    [InlineData(7)]
    [InlineData(8)] // second tap's lift
    [InlineData(9)]
    [InlineData(10)]
    public async Task A_double_tap_neither_loses_nor_doubles_a_click_when_one_datagram_is_lost(int lost)
    {
        await using NetHarness h = Harness();
        var drop = new StrongBoxInt { Value = lost };
        PenBridgeClient client = await h.ConnectPairedAsync(filter: Dropping(drop), configure: Phone);

        client.SendState(null, Down);
        await DeliveredAsync(h, client, drop);
        h.Time.Advance(TimeSpan.FromMilliseconds(30)); // a quick tap: no keepalive resent its contact
        client.SendState(null, Up);
        await StepAsync(h, client, drop, 3); // repeats, then quiet

        client.SendState(null, Down); // 150 ms after the first lift
        await DeliveredAsync(h, client, drop);
        h.Time.Advance(TimeSpan.FromMilliseconds(30));
        client.SendState(null, Up);
        await StepAsync(h, client, drop, 3);

        Assert.Equal(10, client.DatagramsSealed);
        Assert.Equal([1, 2], ClickCounts(h));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_lost_lift_keeps_its_time_so_a_tap_near_the_limit_still_clicks(bool loseLift)
    {
        await using NetHarness h = Harness();
        var drop = new StrongBoxInt();
        PenBridgeClient client = await h.ConnectPairedAsync(filter: Dropping(drop), configure: Phone);

        client.SendState(null, Down);
        await DeliveredAsync(h, client, drop);
        await StepAsync(h, client, drop, 7); // 280 ms down: contact keepalives
        h.Time.Advance(TimeSpan.FromMilliseconds(10));
        await DeliveredAsync(h, client, drop);
        if (loseLift)
        {
            drop.Value = (int)client.DatagramsSealed + 1; // no echo here: the keepalives already resent the contact
        }

        client.SendState(null, Up); // 290 ms: a tap; its first repeat comes 40 ms later
        await StepAsync(h, client, drop, 3);
        Assert.Equal([1], ClickCounts(h));
    }

    [Fact]
    public async Task A_phone_with_finger_fallback_off_gets_no_cursor_until_its_surface_says_on()
    {
        await using NetHarness h = Harness();
        PenBridgeClient client = await h.ConnectPairedAsync(); // NetHarness.Surface: SurfaceCapabilities.None
        Assert.True((client.NegotiatedCapabilities & ProtocolCapabilities.FingerTouches) != 0);

        var drop = new StrongBoxInt();
        client.SendState(null, Down);
        await DeliveredAsync(h, client, drop);
        client.SendState(null, Up);
        await StepAsync(h, client, drop, 3);
        Assert.Empty(ClickCounts(h));

        await client.UpdateSurfaceAsync(FallbackSurface); // the setting turned on mid-session
        await Wait.Until(() => h.Server.ActiveInput!.PhoneFingerFallback, "SurfaceInfo applied");
        client.SendState(null, Down);
        await DeliveredAsync(h, client, drop);
        client.SendState(null, Up);
        await StepAsync(h, client, drop, 3);
        Assert.Equal([1], ClickCounts(h));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)] // the frame with both fingers: its echo before the first lift still shows both
    public async Task A_staggered_two_finger_tap_with_a_lost_datagram_is_a_right_click_not_a_click(int lost)
    {
        await using NetHarness h = Harness();
        var drop = new StrongBoxInt { Value = lost };
        PenBridgeClient client = await h.ConnectPairedAsync(filter: Dropping(drop), configure: Phone);
        client.SendState(null, [new TouchSample(1, true, 30000, 20000), new TouchSample(2, true, 36000, 20000)]);
        await DeliveredAsync(h, client, drop);
        client.SendState(null, [new TouchSample(2, true, 36000, 20000)]);
        await DeliveredAsync(h, client, drop);
        client.SendState(null, Up);
        await StepAsync(h, client, drop, 14); // past the held tap's double-tap window

        GestureEvent[] gestures = h.Sink.Gestures();
        Assert.Single(gestures, g => g.Kind == GestureKind.Tap);
        Assert.DoesNotContain(gestures, g => g.Kind is GestureKind.Click or GestureKind.PointerMove);
    }

    [Fact]
    public void The_negotiated_value_overrides_the_configured_recognizer_option()
    {
        var on = new PenStateTrackerOptions { Gestures = new GestureRecognizerOptions { OneFingerCursor = true, DragGain = 2 } };
        PenStateTrackerOptions off = SessionInput.WithNegotiated(on, ProtocolCapabilities.SampleDedupe);
        Assert.False(off.Gestures.OneFingerCursor);
        Assert.Equal(2, off.Gestures.DragGain);
        Assert.True(SessionInput.WithNegotiated(null, ProtocolCapabilities.FingerTouches).Gestures.OneFingerCursor);
        Assert.Same(on, SessionInput.WithNegotiated(on, ProtocolCapabilities.FingerTouches));
    }

    private sealed class StrongBoxInt
    {
        public int Value;
    }
}
