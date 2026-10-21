using System.Net;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Net;

/// <summary>
/// Regression for #107: on a takeover the displaced session's input release (tracker reset: synthetic pen Up/Leave,
/// gesture Cancelled) must reach the sink before any event of the new session, and nothing of the old session may
/// reach the sink afterwards. Fake time does not advance during a takeover, so no watchdog interferes.
/// </summary>
public class TakeoverInputReleaseTests
{
    private static NetHarness CreateTakeoverHarness() => new(o => new PenBridgeServerOptions
    {
        Identity = o.Identity,
        Registry = o.Registry,
        InputSink = o.InputSink,
        BindAddress = o.BindAddress,
        Port = 0,
        TargetDisplay = o.TargetDisplay,
        TimeProvider = o.TimeProvider,
        AllowTakeover = true,
    });

    /// <summary>Two fingers moving down until the recognizer opened a scroll (its Began reached the sink after <paramref name="sinkMark"/>).</summary>
    private static async Task OpenScrollAsync(NetHarness h, PenBridgeClient client, int sinkMark)
    {
        for (int i = 0; i <= 20; i++)
        {
            ushort y = (ushort)(30000 + (i * 1000));
            long accepted = h.Server.GetStats().Counters.Accepted;
            Assert.True(client.SendState(null, [new TouchSample(1, true, 20000, y), new TouchSample(2, true, 40000, y)]));
            await Wait.Until(() => h.Server.GetStats().Counters.Accepted > accepted, "touch frame " + i);
        }

        await Wait.Until(
            () => h.Sink.All()[sinkMark..].Any(e => e is GestureEvent { Kind: GestureKind.Scroll, Phase: GesturePhase.Began }),
            "scroll began");
    }

    [Fact]
    public async Task Takeover_delivers_the_old_release_before_any_new_session_event()
    {
        await using NetHarness h = CreateTakeoverHarness();
        var lifecycle = new List<string>();
        h.Server.SessionEnded += (_, e) => { lock (lifecycle) { lifecycle.Add("ended " + e.Session.DeviceId); } };
        h.Server.SessionStarted += (_, e) => { lock (lifecycle) { lifecycle.Add("started " + e.Session.DeviceId); } };

        PenBridgeClient first = await h.PairAsync();
        await OpenScrollAsync(h, first, 0);
        int mark = h.Sink.All().Length;

        PenBridgeClient second = await h.PairAsync(); // takes over mid-scroll
        await OpenScrollAsync(h, second, mark);

        object[] after = h.Sink.All()[mark..];

        // First the old session's release, then only the new session's TouchDown, Began, Changed…: no late Cancelled.
        GestureEvent release = Assert.IsType<GestureEvent>(after[0]);
        Assert.Equal((GestureKind.Scroll, GesturePhase.Cancelled, true), (release.Kind, release.Phase, release.Synthetic));
        Assert.All(after[1..], e => Assert.True(
            e is GestureEvent { Kind: GestureKind.TouchDown } or GestureEvent { Phase: GesturePhase.Began or GesturePhase.Changed },
            "unexpected after the release: " + e));
        Assert.Contains(after[1..], e => e is GestureEvent { Kind: GestureKind.Scroll, Phase: GesturePhase.Began });

        lock (lifecycle)
        {
            Assert.Equal(["started " + first.DeviceId, "ended " + first.DeviceId, "started " + second.DeviceId], lifecycle);
        }
    }

    [Fact]
    public async Task Late_frames_datagrams_and_the_watchdog_of_a_taken_over_session_reach_no_sink()
    {
        await using NetHarness h = CreateTakeoverHarness();
        byte[]? withheld = null;
        bool capture = false;
        PenBridgeClient first = await h.PairAsync(filter: d =>
        {
            if (!capture)
            {
                return true;
            }

            withheld ??= d.ToArray(); // a genuine sealed datagram of the old session the server never saw
            return false;
        });
        Assert.True(first.SendState(Pens.Contact(2000, 2000), [])); // contact arms the 150 ms pen watchdog
        await Wait.Until(() => h.Sink.PenKinds().Contains(PenEventKind.Down), "first device's Down");
        SessionInput old = h.Server.ActiveInput!;
        capture = true;
        Assert.True(first.SendState(Pens.Contact(2500, 2500), []));
        await Wait.Until(() => Volatile.Read(ref withheld) is not null, "withheld datagram");

        await h.PairAsync(); // takeover; the new device sends nothing
        Assert.NotSame(old, h.Server.ActiveInput);
        PenEvent[] pen = h.Sink.Pen();
        Assert.Equal([PenEventKind.Up, PenEventKind.ProximityLeave], pen[^2..].Select(e => e.Kind));
        int count = h.Sink.All().Length;

        // A late TLS InputFrame and a late, validly sealed UDP datagram (the old session's id and key, a sequence
        // above the highest it accepted), then well past its watchdog deadline.
        byte[] frame = new byte[InputPayload.MaxSize];
        int n = InputPayload.Write(frame, uint.MaxValue / 2, Pens.Contact(3000, 3000), [new TouchSample(1, true, 100, 100)], default);
        old.ProcessInputFrame(frame.AsSpan(0, n));
        old.ProcessDatagram(withheld!, new IPEndPoint(IPAddress.Loopback, 1).Serialize());
        h.Time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, old.Counters.AuthFailures + old.Counters.Malformed + old.Counters.Stale); // dropped as closed, not as invalid

        Assert.Equal(count, h.Sink.All().Length);
    }

    [Fact]
    public async Task A_disconnect_without_takeover_still_releases_an_open_gesture_and_pen()
    {
        await using var h = new NetHarness();
        var ended = new TaskCompletionSource<SessionEndedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Server.SessionEnded += (_, e) => ended.TrySetResult(e);
        PenBridgeClient client = await h.PairAsync();
        await OpenScrollAsync(h, client, 0);

        await client.DisconnectAsync().Bounded();
        await ended.Task.Bounded();

        GestureEvent last = h.Sink.Gestures()[^1];
        Assert.Equal((GestureKind.Scroll, GesturePhase.Cancelled, true), (last.Kind, last.Phase, last.Synthetic));
        Assert.False(h.Server.GetStats().HasSession);

        // A held pen is released by a server-side disconnect too.
        PenBridgeClient again = await h.PairAsync();
        Assert.True(again.SendState(Pens.Contact(2000, 2000), []));
        await Wait.Until(() => h.Sink.PenKinds().Contains(PenEventKind.Down), "Down");
        await h.Server.DisconnectSessionAsync().Bounded();
        PenEvent[] pen = h.Sink.Pen();
        Assert.Equal([PenEventKind.Up, PenEventKind.ProximityLeave], pen[^2..].Select(e => e.Kind));
        Assert.True(pen[^1].Synthetic);
    }
}
