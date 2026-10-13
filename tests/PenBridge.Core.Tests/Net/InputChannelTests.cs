using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using PenBridge.Core.Input;
using PenBridge.Core.Net;

#pragma warning disable xUnit1051 // Network waits are bounded by Wait.Limit instead of the test token.

namespace PenBridge.Core.Tests.Net;

/// <summary>The UDP input channel over loopback: drops, rate limiting, watchdog and the TLS fallback.</summary>
public class InputChannelTests
{
    private static byte[] WithSequence(byte[] datagram, ulong sequence)
    {
        byte[] copy = (byte[])datagram.Clone();
        BinaryPrimitives.WriteUInt64BigEndian(copy.AsSpan(8), sequence);
        return copy;
    }

    [Fact]
    public async Task Tampered_replayed_old_and_foreign_datagrams_are_dropped_without_events()
    {
        await using var h = new NetHarness();
        var captured = new List<byte[]>();
        PenBridgeClient client = await h.PairAsync(filter: d =>
        {
            lock (captured)
            {
                captured.Add(d.ToArray());
            }

            return true;
        });
        EndToEndTests.DrawStroke(client);
        await EndToEndTests.AssertStrokeAsync(h.Sink);
        int events = h.Sink.Pen().Length;
        InputCounters before = h.Server.GetStats().Counters;
        byte[] last;
        byte[] old;
        lock (captured)
        {
            last = captured[^1];
            old = captured[1];
        }

        client.SendRawDatagram(last); // exact replay
        client.SendRawDatagram(old); // older sequence
        client.SendRawDatagram(WithSequence(last, 1000)); // header (AAD) changed: tag fails
        byte[] flipped = WithSequence(last, 1001);
        flipped[20] ^= 0x01; // and a ciphertext bit flipped
        client.SendRawDatagram(flipped);
        client.SendRawDatagram(last.AsSpan(0, 20)); // truncated
        using (var foreign = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            foreign.SendTo(WithSequence(last, 2000), new IPEndPoint(IPAddress.Loopback, h.Port));
        }

        await Wait.Until(
            () =>
            {
                InputCounters c = h.Server.GetStats().Counters;
                return c.Stale == before.Stale + 2 && c.AuthFailures == before.AuthFailures + 2
                    && c.Malformed == before.Malformed + 1 && c.ForeignSource == before.ForeignSource + 1;
            },
            "all bad datagrams counted");
        Assert.Equal(before.Accepted, h.Server.GetStats().Counters.Accepted);
        Assert.Equal(events, h.Sink.Pen().Length);

        // The session still works afterwards.
        client.SendState(Pens.Hover(500, 500), []);
        await Wait.Until(() => h.Sink.Pen().Length == events + 1, "a fresh valid datagram is accepted");
        Assert.Equal(PenEventKind.ProximityEnter, h.Sink.Pen()[^1].Kind);
    }

    [Fact]
    public async Task Forged_datagrams_beyond_the_budget_are_dropped_undecrypted()
    {
        await using var h = new NetHarness();
        byte[]? first = null;
        PenBridgeClient client = await h.PairAsync(filter: d =>
        {
            first ??= d.ToArray();
            return true;
        });
        client.SendState(Pens.Hover(100, 100), []);
        await Wait.Until(() => h.Server.GetStats().Counters.Accepted == 1, "first datagram");

        // In batches, so a small socket receive buffer (Linux CI) can't drop any of them.
        for (ulong i = 0; i < 100; i++)
        {
            client.SendRawDatagram(WithSequence(first!, 10_000 + i));
            if (i % 10 == 9)
            {
                long expected = (long)i + 1;
                await Wait.Until(() => h.Server.GetStats().Counters is { } c && c.AuthFailures + c.RateLimited == expected, "batch handled");
            }
        }

        await Wait.Until(
            () => h.Server.GetStats().Counters is { } c && c.AuthFailures + c.RateLimited == 100,
            "all forged datagrams handled");
        InputCounters counters = h.Server.GetStats().Counters;
        int burst = 2 * new PenBridgeServerOptions { Identity = h.Identity, Registry = h.Registry, InputSink = h.Sink }.MaxAuthFailuresPerSecond;
        Assert.Equal(burst, counters.AuthFailures); // bounded decryption work
        Assert.Equal(100 - burst, counters.RateLimited);

        // The budget refills over time, so the genuine peer gets through again.
        h.Time.Advance(TimeSpan.FromSeconds(1));
        client.SendState(Pens.Hover(200, 200), []);
        await Wait.Until(() => h.Server.GetStats().Counters.Accepted >= 2, "genuine datagram after refill");
    }

    [Fact]
    public async Task Watchdog_synthesizes_pen_up_and_leave_after_150_ms_of_silence_in_contact()
    {
        await using var h = new NetHarness();
        var blocked = new StrongBox<bool>(false);
        PenBridgeClient client = await h.PairAsync(filter: _ => !blocked.Value);
        client.SendState(Pens.Contact(3000, 3000), []);
        await Wait.Until(() => h.Server.GetStats().Counters.Accepted == 1, "contact frame"); // read under the input lock: watchdog armed

        blocked.Value = true; // the link dies: keepalives are lost
        h.Time.Advance(TimeSpan.FromMilliseconds(149));
        Assert.DoesNotContain(PenEventKind.Up, h.Sink.PenKinds());

        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        PenEvent[] events = h.Sink.Pen();
        Assert.Equal([PenEventKind.ProximityEnter, PenEventKind.Down, PenEventKind.Up, PenEventKind.ProximityLeave], events.Select(e => e.Kind));
        Assert.True(events[2].Synthetic);
        Assert.True(events[3].Synthetic);
    }

    [Fact]
    public async Task Watchdog_synthesizes_leave_after_1_s_of_silence_while_hovering()
    {
        await using var h = new NetHarness();
        var blocked = new StrongBox<bool>(false);
        PenBridgeClient client = await h.PairAsync(filter: _ => !blocked.Value);
        client.SendState(Pens.Hover(3000, 3000), []);
        await Wait.Until(() => h.Server.GetStats().Counters.Accepted == 1, "hover frame");

        blocked.Value = true;
        h.Time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal([PenEventKind.ProximityEnter], h.Sink.PenKinds());

        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal([PenEventKind.ProximityEnter, PenEventKind.ProximityLeave], h.Sink.PenKinds());
        Assert.True(h.Sink.Pen()[^1].Synthetic);
    }

    [Fact]
    public async Task Keepalives_keep_a_held_pen_down()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.PairAsync();
        client.SendState(Pens.Contact(3000, 3000), []);
        await Wait.Until(() => h.Server.GetStats().Counters.Accepted == 1, "contact frame");

        for (int i = 0; i < 10; i++)
        {
            long accepted = h.Server.GetStats().Counters.Accepted;
            h.Time.Advance(TimeSpan.FromMilliseconds(40)); // one contact keepalive
            await Wait.Until(() => h.Server.GetStats().Counters.Accepted > accepted, "keepalive " + i);
        }

        Assert.Equal([PenEventKind.ProximityEnter, PenEventKind.Down], h.Sink.PenKinds());
    }

    [Fact]
    public async Task Input_falls_back_to_tls_when_udp_is_blocked()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.PairAsync(filter: _ => false); // UDP blocked entirely
        client.SendState(Pens.Hover(1000, 1000), []);
        Assert.Equal(InputTransport.None, client.Transport);

        h.Time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.NotEqual(InputTransport.Tls, client.Transport);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(InputTransport.Tls, client.Transport);
        await Wait.Until(() => h.Sink.PenKinds().Contains(PenEventKind.ProximityEnter), "state delivered over TLS");
        client.SendState(null, []);
        await Wait.Until(() => h.Sink.PenKinds().Contains(PenEventKind.ProximityLeave), "leave over TLS");
        h.Sink.Clear();

        EndToEndTests.DrawStroke(client);
        await EndToEndTests.AssertStrokeAsync(h.Sink);
        PenBridgeServerStats stats = h.Server.GetStats();
        Assert.Equal(InputTransport.Tls, stats.Transport);
        Assert.Equal(0, stats.Counters.AuthFailures);
    }

    [Fact]
    public async Task Two_finger_touches_reach_the_gesture_recognizer()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.PairAsync();
        for (int i = 0; i <= 20; i++)
        {
            ushort y = (ushort)(30000 + (i * 1000));
            client.SendState(null, [new TouchSample(1, true, 20000, y), new TouchSample(2, true, 40000, y)]);
            await Wait.Until(() => h.Server.GetStats().Counters.Accepted == i + 1, "touch frame " + i);
        }

        client.SendState(null, []);
        await Wait.Until(() => h.Sink.Gestures().Any(g => g.Phase == GesturePhase.Ended), "scroll ended");
        GestureEvent[] gestures = h.Sink.Gestures();
        Assert.All(gestures, g => Assert.Equal(GestureKind.Scroll, g.Kind));
        Assert.Equal(GesturePhase.Began, gestures[0].Phase);
        Assert.Empty(h.Sink.Pen());
    }
}
