using System.Runtime.CompilerServices;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Tests.Input;
using static PenBridge.Core.Input.PenEventKind;

#pragma warning disable xUnit1051 // Network waits are bounded by Wait.Limit instead of the test token.

namespace PenBridge.Core.Tests.Net;

/// <summary>
/// #92: a finger tap travels as one datagram (hover, contact, release). With <see cref="ProtocolCapabilities.SampleDedupe"/>
/// the release repeats resend that whole frame at its own time, so a lost tap still clicks, and only once.
/// </summary>
public class ClickResendTests
{
    private static readonly PenEventKind[] OneClick = [ProximityEnter, Down, Up, ProximityLeave];
    private static readonly TimeSpan Repeat = TimeSpan.FromMilliseconds(40);

    internal static PenSample Finger(PenState state) =>
        new(30000, 20000, state.HasFlag(PenState.Contact) ? (ushort)0x8000 : (ushort)0, 0, 0, 0, PenTool.FingerFallback, state, PenButtons.None);

    internal static readonly PenSample[] TapHistory = [Finger(PenState.InRange), Finger(PenState.InRange | PenState.Contact)];

    /// <summary>Sends a tap frame as <see cref="InputFilter"/> replays it: hover, contact, then out of range.</summary>
    private static void SendTap(PenBridgeClient client) => client.SendState(Finger(PenState.None), [], TapHistory);

    /// <summary>A datagram filter that drops the datagrams whose 1-based index is in <paramref name="drop"/>.</summary>
    private static DatagramFilter Dropping(params int[] drop)
    {
        int n = 0;
        return _ => Array.IndexOf(drop, Interlocked.Increment(ref n)) < 0;
    }

    private static async Task RunRepeatsAsync(NetHarness h, PenBridgeClient client, long expectAccepted)
    {
        for (int i = 0; i < 4; i++)
        {
            h.Time.Advance(Repeat);
        }

        await Wait.Until(() => h.Server.GetStats().Counters.Accepted == expectAccepted, "datagrams delivered");
        Assert.Equal(0, client.FallbackFramesDropped);
    }

    [Fact]
    public async Task Server_and_client_negotiate_sample_dedupe()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.ConnectPairedAsync();
        Assert.Equal(ProtocolCapabilities.SampleDedupe, client.ServerCapabilities);
    }

    [Fact]
    public async Task A_lost_tap_datagram_is_recovered_by_the_release_repeats_as_one_click()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.ConnectPairedAsync(filter: Dropping(1));
        SendTap(client);

        await RunRepeatsAsync(h, client, expectAccepted: 2);
        Assert.Equal(3, client.DatagramsSealed); // the tap and its two repeats, then quiet
        Assert.Equal(OneClick, h.Sink.PenKinds());
        Assert.All(h.Sink.Pen(), e => Assert.True(e.TimestampUs < Repeat.TotalMicroseconds)); // at the tap's own time
    }

    [Fact]
    public async Task Without_loss_the_repeats_add_no_second_click()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.ConnectPairedAsync();
        SendTap(client);

        await RunRepeatsAsync(h, client, expectAccepted: 3);
        Assert.Equal(OneClick, h.Sink.PenKinds());
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 1 })]    // first tap lost
    [InlineData(new[] { 4 })]    // second tap lost
    [InlineData(new[] { 1, 4 })] // both lost
    public async Task A_double_tap_keeps_click_state_two(int[] drop)
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.ConnectPairedAsync(filter: Dropping(drop));
        SendTap(client);
        await RunRepeatsAsync(h, client, expectAccepted: 3 - (drop.Contains(1) ? 1 : 0));
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        SendTap(client);
        await RunRepeatsAsync(h, client, expectAccepted: 6 - drop.Length);

        Assert.Equal([.. OneClick, .. OneClick], h.Sink.PenKinds());
        long[] counts = ClickCounts(h.Sink.Pen());
        Assert.Equal([1L, 2L], counts);
    }

    [Fact]
    public async Task A_lost_tap_after_cursor_movement_clicks_once()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.ConnectPairedAsync(filter: Dropping(2));
        client.SendState(Finger(PenState.InRange), []); // the cursor moved there (active), then the tap
        SendTap(client);

        await RunRepeatsAsync(h, client, expectAccepted: 3);
        Assert.Equal([ProximityEnter, Down, Up, ProximityLeave], h.Sink.PenKinds());
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 1, 4 })] // both tap datagrams lost
    public async Task An_input_filter_double_tap_survives_lost_datagrams(int[] drop)
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.ConnectPairedAsync(filter: Dropping(drop));
        var filter = new InputFilter();
        var touches = new TouchSample[InputFilter.MaxTouches];
        var finger = new RawTouch(1, true, false, 30000, 20000, 0.05f);

        void Feed(int ms, params RawTouch[] raw)
        {
            InputFilterResult r = filter.Process(TimeSpan.FromMilliseconds(ms), null, raw, touches);
            if (r.HasPen)
            {
                client.SendState(r.Pen, touches.AsSpan(0, r.TouchCount), filter.ReplaySamples[..r.ReplayCount]);
            }
        }

        Feed(0, finger);
        Feed(50); // a quick tap: replayed as one frame
        for (int i = 0; i < 3; i++)
        {
            h.Time.Advance(Repeat);
        }

        Feed(150, finger);
        Feed(200); // second tap within the double-tap window
        await RunRepeatsAsync(h, client, expectAccepted: 6 - drop.Length);

        Assert.Equal([.. OneClick, .. OneClick], h.Sink.PenKinds());
        Assert.Equal([1L, 2L], ClickCounts(h.Sink.Pen()));
    }

    [Fact]
    public async Task A_tls_fallback_between_a_lost_tap_and_its_repeats_delivers_the_tap()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.ConnectPairedAsync(filter: _ => false); // UDP lost from here on
        client.SendState(Finger(PenState.InRange), []); // arms the 2 s ack deadline
        h.Time.Advance(TimeSpan.FromMilliseconds(1980));
        SendTap(client); // lost; its first repeat is due after the fallback

        h.Time.Advance(TimeSpan.FromMilliseconds(20)); // the ack deadline: the switch to TLS sends the kept click frame
        Assert.Equal(InputTransport.Tls, client.Transport);
        await Wait.Until(() => h.Sink.PenKinds().Length == 4, "click delivered over TLS");
        for (int i = 0; i < 2; i++)
        {
            long accepted = h.Server.GetStats().Counters.Accepted;
            h.Time.Advance(Repeat);
            await Wait.Until(() => h.Server.GetStats().Counters.Accepted > accepted, "repeat " + i);
        }

        Assert.Equal(OneClick, h.Sink.PenKinds());
    }

    [Fact]
    public async Task A_probe_while_a_click_is_pending_resends_it_and_keeps_the_watermark()
    {
        await using var h = new NetHarness();
        var blocked = new StrongBox<bool>(true);
        PenBridgeClient client = await h.ConnectPairedAsync(filter: _ => !blocked.Value);
        client.SendState(Finger(PenState.InRange) with { X = 1000 }, []);
        await Wait.Step(h.Time, TimeSpan.FromSeconds(2)); // fallback to TLS; the first probe is due 10 s later
        Assert.Equal(InputTransport.Tls, client.Transport);
        await Wait.Step(h.Time, TimeSpan.FromMilliseconds(9980));
        blocked.Value = false;

        SendTap(client); // over TLS; its first repeat is due after the probe
        await Wait.Until(() => h.Sink.PenKinds().Contains(Down), "tap delivered over TLS");
        uint tapTime = h.Server.ActiveInput!.LastTimeUs;

        h.Time.Advance(TimeSpan.FromMilliseconds(20)); // the probe
        await Wait.Until(() => client.Transport == InputTransport.Udp, "switched back to UDP after the probe ack");
        Assert.Equal(tapTime, h.Server.ActiveInput!.LastTimeUs); // the probe was the click frame, not a newer time
        for (int i = 0; i < 2; i++)
        {
            long accepted = h.Server.GetStats().Counters.Accepted;
            h.Time.Advance(Repeat);
            await Wait.Until(() => h.Server.GetStats().Counters.Accepted > accepted, "repeat " + i);
        }

        Assert.Equal(1, h.Sink.PenKinds().Count(k => k == Down));
        Assert.Equal(tapTime, h.Server.ActiveInput!.LastTimeUs);
    }

    [Fact]
    public async Task The_kept_click_ends_with_the_repeats()
    {
        await using var h = new NetHarness();
        var blocked = new StrongBox<bool>(true);
        PenBridgeClient client = await h.ConnectPairedAsync(filter: _ => !blocked.Value);
        SendTap(client);
        await Wait.Step(h.Time, TimeSpan.FromSeconds(2)); // repeats done (lost), then the fallback sends a new frame
        Assert.Equal(InputTransport.Tls, client.Transport);
        await Wait.Until(() => h.Server.GetStats().Counters.Accepted == 1, "fallback frame delivered");
        Assert.Empty(h.Sink.Pen()); // no click seconds late: the resend window was the repeats
    }

    [Fact]
    public async Task An_older_server_gets_todays_repeats_without_samples()
    {
        await using var h = new NetHarness(o => WithCapabilities(o, ProtocolCapabilities.None));
        PenBridgeClient client = await h.ConnectPairedAsync(filter: Dropping(2));
        Assert.Equal(ProtocolCapabilities.None, client.ServerCapabilities);
        client.SendState(Finger(PenState.InRange), []); // the cursor moves, then taps
        SendTap(client);

        await RunRepeatsAsync(h, client, expectAccepted: 3);
        Assert.Equal(4, client.DatagramsSealed);
        // Today's behaviour: the repeats carry only the empty state, so the lost click stays lost.
        Assert.Equal([ProximityEnter, ProximityLeave], h.Sink.PenKinds());
    }

    [Fact]
    public async Task An_older_server_gets_no_repeats_for_a_held_tap()
    {
        await using var h = new NetHarness(o => WithCapabilities(o, ProtocolCapabilities.None));
        PenBridgeClient client = await h.ConnectPairedAsync();
        SendTap(client); // a quick tap: nothing active was sent before it, so no release repeats (as before #92)

        await RunRepeatsAsync(h, client, expectAccepted: 1);
        Assert.Equal(1, client.DatagramsSealed);
        Assert.Equal(OneClick, h.Sink.PenKinds());
    }

    [Fact]
    public async Task An_older_server_without_loss_clicks_once()
    {
        await using var h = new NetHarness(o => WithCapabilities(o, ProtocolCapabilities.None));
        PenBridgeClient client = await h.ConnectPairedAsync();
        client.SendState(Finger(PenState.InRange), []);
        SendTap(client);

        await RunRepeatsAsync(h, client, expectAccepted: 4);
        Assert.Equal(OneClick, h.Sink.PenKinds());
    }

    [Fact]
    public async Task New_input_after_a_tap_stops_its_resend()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.ConnectPairedAsync(filter: Dropping(1));
        SendTap(client);
        client.SendState(Finger(PenState.InRange) with { X = 1000 }, []);

        for (int i = 0; i < 4; i++)
        {
            h.Time.Advance(Repeat); // below the 200 ms hover keepalive
        }

        Assert.Equal(2, client.DatagramsSealed); // the tap and the hover: no click resend after newer input
        await Wait.Until(() => h.Server.GetStats().Counters.Accepted == 1, "hover delivered");
        Assert.Equal([ProximityEnter], h.Sink.PenKinds());
    }

    private static PenBridgeServerOptions WithCapabilities(PenBridgeServerOptions o, ProtocolCapabilities capabilities) => new()
    {
        Identity = o.Identity,
        Registry = o.Registry,
        InputSink = o.InputSink,
        ServerName = o.ServerName,
        BindAddress = o.BindAddress,
        Port = o.Port,
        TargetDisplay = o.TargetDisplay,
        TimeProvider = o.TimeProvider,
        Capabilities = capabilities,
    };

    /// <summary>The click count the Mac injector would post for each Down (finger slop 0, 500 ms interval).</summary>
    private static long[] ClickCounts(PenEvent[] events)
    {
        var state = default(ClickState);
        var counts = new List<long>();
        foreach (PenEvent e in events)
        {
            if (e.Kind == Down)
            {
                counts.Add(TabletEventMath.NextClickCount(
                    ref state, e.TimestampUs * 1000, e.Position, 500_000_000, TabletEventMath.ClickSlopFor(e.Sample.Tool, 6)));
            }
        }

        return [.. counts];
    }
}
