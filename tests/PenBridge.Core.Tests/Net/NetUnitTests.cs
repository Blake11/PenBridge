using System.Net;
using System.Net.Sockets;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using PenBridge.Core.Tests.Input;

#pragma warning disable xUnit1051 // Network waits are bounded by Wait.Limit instead of the test token.

namespace PenBridge.Core.Tests.Net;

/// <summary>The server input path without sockets (<see cref="SessionInput"/>), allocation checks and small helpers.</summary>
public class NetUnitTests
{
    private const uint SessionId = 0xC0FFEE01;
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly IPEndPoint Peer = new(IPAddress.Loopback, 50000);

    private sealed class CountingSink : IInputSink
    {
        public long Pen;
        public long Gestures;

        public void OnPenEvent(in PenEvent e) => Pen++;

        public void OnGestureEvent(in GestureEvent e) => Gestures++;
    }

    private static SessionInput CreateInput(IInputSink sink, TimeProvider time, Action<ulong>? onAck = null, Action<Exception>? onFault = null) =>
        new(new Lock(), sink, time, null, Key, SessionId, new DisplayRect(0, 0, 1000, 2000), NormalizedRect.Full, 16, onAck, onFault);

    private static int Seal(InputCipher cipher, byte[] buffer, ulong sequence, uint timeUs, in PenSample? pen, ReadOnlySpan<TouchSample> touches)
    {
        new InputDatagramHeader(InputDatagramOptions.None, SessionId, sequence).Write(buffer);
        int length = InputPayload.Write(buffer.AsSpan(InputDatagramHeader.Size), timeUs, pen, touches, default);
        return cipher.Seal(buffer, length);
    }

    [Fact]
    public void Datagram_receive_decrypt_track_path_is_allocation_free()
    {
        var time = new FakeTime();
        var sink = new CountingSink();
        using SessionInput input = CreateInput(sink, time);
        input.ConfigureSurface(1440, 3088);
        SocketAddress source = Peer.Serialize();

        const int PerPass = 200;
        const int Total = (8 * PerPass) + 1; // AssertZero runs 3 warm-up + up to 5 measured passes
        using var sealer = new InputCipher(Key, SessionId);
        byte[][] datagrams = new byte[Total][];
        int[] lengths = new int[Total];
        TouchSample[] two = [new(1, true, 20000, 30000), new(2, true, 40000, 30000)];
        for (int i = 0; i < Total; i++)
        {
            datagrams[i] = new byte[InputDatagram.MaxSize];

            // Cycle hover → contact (drag) → lift → leave → two-finger touches, so every branch runs each pass.
            int phase = i % 20;
            PenSample? pen = phase switch
            {
                < 2 => Pens.Hover((ushort)(1000 + i), 1000),
                < 12 => Pens.Contact((ushort)(1000 + i), (ushort)(1000 + phase), (ushort)(20000 + phase)),
                < 14 => Pens.Hover((ushort)(1000 + i), 1000),
                _ => null,
            };
            ReadOnlySpan<TouchSample> touches = phase >= 15 ? two.Select(t => t with { Y = (ushort)(30000 + (phase * 2000)) }).ToArray() : [];
            lengths[i] = Seal(sealer, datagrams[i], (ulong)i + 1, (uint)(i * 4000), pen, touches);
        }

        input.ProcessDatagram(datagrams[0].AsSpan(0, lengths[0]), source); // pins the endpoint (allocates once)

        Alloc.AssertZero(
            PerPass,
            i => input.ProcessDatagram(datagrams[i + 1].AsSpan(0, lengths[i + 1]), source),
            () => input.Accepted,
            () => sink.Pen);
        Assert.Equal(0, input.Counters.AuthFailures);
        Assert.True(sink.Gestures > 0);
    }

    [Fact]
    public void Dropped_datagrams_are_allocation_free_too()
    {
        var time = new FakeTime();
        using SessionInput input = CreateInput(new CountingSink(), time);
        input.ConfigureSurface(1440, 3088);
        SocketAddress source = Peer.Serialize();
        SocketAddress foreign = new IPEndPoint(IPAddress.Loopback, 50001).Serialize();
        using var sealer = new InputCipher(Key, SessionId);
        byte[] valid = new byte[InputDatagram.MaxSize];
        int length = Seal(sealer, valid, 1, 0, Pens.Hover(1, 1), []);
        byte[] copy = valid.AsSpan(0, length).ToArray();
        input.ProcessDatagram(valid.AsSpan(0, length), source);
        byte[] scratch = new byte[length];

        Alloc.AssertZero(
            100,
            i =>
            {
                copy.CopyTo(scratch);
                input.ProcessDatagram(scratch, (i & 1) == 0 ? source : foreign); // stale, from the pinned or another source
            },
            () => input.Counters.Stale);
    }

    [Fact]
    public async Task Client_send_state_is_allocation_free_once_connected()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.PairAsync();
        PenSample[] history = [Pens.Contact(1, 1), Pens.Contact(2, 2)];
        TouchSample[] none = [];

        Alloc.AssertZero(
            100,
            i => client.SendState(Pens.Contact((ushort)(i % 60000), 2000), none, (i & 1) == 0 ? history : default),
            () => client.DatagramsSealed);
    }

    [Fact]
    public void Datagrams_before_surface_info_are_dropped_by_the_tracker_but_count_as_valid()
    {
        var sink = new CountingSink();
        using SessionInput input = CreateInput(sink, new FakeTime());
        using var sealer = new InputCipher(Key, SessionId);
        byte[] buffer = new byte[InputDatagram.MaxSize];
        int length = Seal(sealer, buffer, 1, 0, Pens.Hover(1, 1), []);

        input.ProcessDatagram(buffer.AsSpan(0, length), Peer.Serialize());

        Assert.Equal(0, sink.Pen);
        Assert.False(input.IsSurfaceConfigured);
    }

    [Fact]
    public void Only_an_authenticated_newer_datagram_moves_the_pinned_endpoint()
    {
        var sink = new CountingSink();
        using SessionInput input = CreateInput(sink, new FakeTime());
        input.ConfigureSurface(1440, 3088);
        using var sealer = new InputCipher(Key, SessionId);
        SocketAddress home = Peer.Serialize();
        SocketAddress roamed = new IPEndPoint(IPAddress.Parse("10.1.2.3"), 40000).Serialize();
        byte[] buffer = new byte[InputDatagram.MaxSize];
        int length = Seal(sealer, buffer, 1, 0, Pens.Hover(1, 1), []);
        input.ProcessDatagram(buffer.AsSpan(0, length), home);
        byte[] replay = buffer.AsSpan(0, length).ToArray();

        // A forged or replayed datagram from elsewhere doesn't move it.
        byte[] forged = replay.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(forged.AsSpan(8), 99);
        input.ProcessDatagram(forged, roamed);
        input.ProcessDatagram(replay.ToArray(), roamed);
        Assert.Equal(0, input.Counters.EndpointChanges);

        // A genuine newer one does (the phone roamed); then the old source is just another endpoint.
        length = Seal(sealer, buffer, 2, 10, Pens.Hover(2, 2), []);
        input.ProcessDatagram(buffer.AsSpan(0, length), roamed);
        Assert.Equal(1, input.Counters.EndpointChanges);
        Assert.Equal(2, input.Counters.Accepted);
        Assert.Equal(1, input.Counters.AuthFailures);
        Assert.Equal(1, input.Counters.Stale);
    }

    [Fact]
    public void UdpAck_is_rate_limited_to_1_per_s_with_a_trailing_ack_for_the_last_datagram()
    {
        var time = new FakeTime();
        var acks = new List<ulong>();
        using SessionInput input = CreateInput(new CountingSink(), time, onAck: acks.Add);
        input.ConfigureSurface(100, 100);
        using var sealer = new InputCipher(Key, SessionId);
        byte[] buffer = new byte[InputDatagram.MaxSize];
        for (ulong seq = 1; seq <= 30; seq++)
        {
            int length = Seal(sealer, buffer, seq, (uint)(seq * 100_000), Pens.Hover(1, 1), []);
            input.ProcessDatagram(buffer.AsSpan(0, length), Peer.Serialize());
            time.Advance(TimeSpan.FromMilliseconds(100));
        }

        // Immediate ack for the first, then one trailing ack per second covering the newest sequence (incl. the last).
        Assert.Equal([1UL, 10UL, 20UL, 30UL], acks);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(4, acks.Count); // nothing new to ack
    }

    [Fact]
    public void Frames_older_than_the_last_accepted_are_dropped_across_transports()
    {
        var sink = new ConcurrentSink();
        using SessionInput input = CreateInput(sink, new FakeTime());
        input.ConfigureSurface(100, 100);
        using var sealer = new InputCipher(Key, SessionId);
        byte[] buffer = new byte[InputDatagram.MaxSize];
        int length = Seal(sealer, buffer, 1, 5000, Pens.Contact(1, 1), []);
        input.ProcessDatagram(buffer.AsSpan(0, length), Peer.Serialize());
        Assert.Equal(InputTransport.Udp, input.Transport);

        // A late fallback frame from before the UDP one is ignored; a newer one is applied.
        byte[] frame = new byte[InputPayload.MaxSize];
        int n = InputPayload.Write(frame, 4000, Pens.Hover(9, 9), [], default);
        input.ProcessInputFrame(frame.AsSpan(0, n));
        Assert.Equal(1, input.Counters.Stale);
        n = InputPayload.Write(frame, 6000, Pens.Hover(9, 9), [], default);
        input.ProcessInputFrame(frame.AsSpan(0, n));
        Assert.Equal(InputTransport.Tls, input.Transport);
        Assert.Equal([PenEventKind.ProximityEnter, PenEventKind.Down, PenEventKind.Up], sink.PenKinds());
    }

    private sealed class ThrowingSink(Func<PenEvent, bool> throwOn) : IInputSink
    {
        public int Calls;

        public void OnPenEvent(in PenEvent e)
        {
            Calls++;
            if (throwOn(e))
            {
                throw new InvalidOperationException("sink failure");
            }
        }

        public void OnGestureEvent(in GestureEvent e)
        {
        }
    }

    [Fact]
    public void A_throwing_sink_faults_the_session_once_without_escaping_datagram_or_watchdog_paths()
    {
        var time = new FakeTime();
        var faults = new List<Exception>();
        var sink = new ThrowingSink(e => e.Synthetic);
        using SessionInput input = CreateInput(sink, time, onFault: faults.Add);
        input.ConfigureSurface(100, 100);
        using var sealer = new InputCipher(Key, SessionId);
        byte[] buffer = new byte[InputDatagram.MaxSize];
        int length = Seal(sealer, buffer, 1, 0, Pens.Contact(1, 1), []);
        input.ProcessDatagram(buffer.AsSpan(0, length), Peer.Serialize());

        time.Advance(TimeSpan.FromMilliseconds(150)); // watchdog → synthetic Up → sink throws on the timer callback

        Assert.Single(faults);
        int calls = sink.Calls;
        length = Seal(sealer, buffer, 2, 10, Pens.Contact(2, 2), []);
        input.ProcessDatagram(buffer.AsSpan(0, length), Peer.Serialize()); // faulted: ignored
        input.Shutdown(); // no release into the broken sink
        Assert.Equal(calls, sink.Calls);
        Assert.Single(faults);

        var direct = new ThrowingSink(_ => true);
        var faults2 = new List<Exception>();
        using SessionInput second = CreateInput(direct, time, onFault: faults2.Add);
        second.ConfigureSurface(100, 100);
        using var sealer2 = new InputCipher(Key, SessionId);
        length = Seal(sealer2, buffer, 1, 0, Pens.Hover(1, 1), []);
        second.ProcessDatagram(buffer.AsSpan(0, length), Peer.Serialize()); // must not throw
        Assert.Single(faults2);
    }

    [Fact]
    public void Shutdown_releases_an_active_pen_and_ignores_later_datagrams()
    {
        var sink = new ConcurrentSink();
        SessionInput input = CreateInput(sink, new FakeTime());
        input.ConfigureSurface(1440, 3088);
        using var sealer = new InputCipher(Key, SessionId);
        byte[] buffer = new byte[InputDatagram.MaxSize];
        int length = Seal(sealer, buffer, 1, 0, Pens.Contact(5, 5), []);
        input.ProcessDatagram(buffer.AsSpan(0, length), Peer.Serialize());

        input.Shutdown();
        length = Seal(sealer, buffer, 2, 10, Pens.Contact(6, 6), []);
        input.ProcessDatagram(buffer.AsSpan(0, length), Peer.Serialize());

        Assert.Equal([PenEventKind.ProximityEnter, PenEventKind.Down, PenEventKind.Up, PenEventKind.ProximityLeave], sink.PenKinds());
    }

    [Fact]
    public void Validation_rejects_history_without_a_pen_and_out_of_range_history()
    {
        var payload = default(InputPayload);
        payload.SetHistory([Pens.Contact(1, 1)]);
        Assert.False(SessionInput.IsValid(payload)); // history but no current pen

        payload.Pen = Pens.Contact(2, 2);
        Assert.True(SessionInput.IsValid(payload));

        payload.SetHistory([new PenSample(1, 1, 0, 0, 0, 0, PenTool.Pen, PenState.None, PenButtons.None)]);
        Assert.False(SessionInput.IsValid(payload)); // a history sample out of range

        payload.SetHistory([]);
        payload.Pen = new PenSample(1, 1, 0, 0, 0, 0, PenTool.None, PenState.InRange, PenButtons.None);
        Assert.False(SessionInput.IsValid(payload)); // in range without a tool

        payload.Pen = new PenSample(1, 1, 0, 0, 0, 0, PenTool.None, PenState.None, PenButtons.None);
        Assert.True(SessionInput.IsValid(payload)); // out-of-range sample: tool irrelevant
    }

    [Fact]
    public void Client_credentials_persist_device_id_and_per_server_keys()
    {
        var store = new InMemorySecretStore();
        var a = new ClientCredentialStore(store);
        Guid id = a.GetOrCreateDeviceId();
        Assert.Equal(id, new ClientCredentialStore(store).GetOrCreateDeviceId());

        byte[] fp1 = Enumerable.Repeat((byte)1, 32).ToArray();
        byte[] fp2 = Enumerable.Repeat((byte)2, 32).ToArray();
        a.SaveDeviceKey(fp1, Key);
        Assert.True(a.IsPaired(fp1));
        Assert.False(a.IsPaired(fp2));
        byte[] key = new byte[32];
        Assert.True(a.TryGetDeviceKey(fp1, key));
        Assert.Equal(Key, key);

        a.Forget(fp1);
        Assert.False(a.IsPaired(fp1));
        Assert.Throws<ArgumentException>(() => a.SaveDeviceKey(fp1, new byte[5]));
    }

    [Fact]
    public void Peer_text_is_sanitized_and_truncated()
    {
        Assert.Equal("ok?bad?", PeerText.Sanitize("ok\u001bbad‮"));
        Assert.Equal(PeerText.MaxLength, PeerText.Sanitize(new string('x', 300)).Length);
        Assert.Equal(string.Empty, PeerText.Sanitize(null));
    }

    [Fact]
    public void Client_rejects_invalid_samples_before_touching_state()
    {
        var client = new PenBridgeClient(new PenBridgeClientOptions { SecretStore = new InMemorySecretStore(), Surface = NetHarness.Surface });
        Assert.False(client.SendState(Pens.Hover(1, 1), [])); // not connected
        Assert.Throws<ArgumentException>(() => client.SendState(new PenSample(1, 1, 0, 9001, 0, 0, PenTool.Pen, PenState.InRange, PenButtons.None), []));
        Assert.Throws<ArgumentException>(() => client.SendState(null, [new TouchSample(1, true, 1, 1), new TouchSample(1, true, 2, 2)]));
        Assert.Throws<ArgumentException>(() => { _ = client.PairWithPinAsync("127.0.0.1", 1, "12a456"); });
    }

    [Fact]
    public async Task Session_ids_are_unique_per_server()
    {
        await using var h = new NetHarness();
        var ids = new HashSet<uint>();
        for (int i = 0; i < 1000; i++)
        {
            Assert.True(ids.Add(h.Server.NextSessionId()));
        }

        Assert.DoesNotContain(0u, ids);
    }

    [Fact]
    public void Ipv6_peers_share_a_pending_limit_per_64_prefix()
    {
        var server = new PenBridgeServer(new PenBridgeServerOptions
        {
            Identity = NetHarness.SharedServerIdentity,
            Registry = new PairedDeviceRegistry(new InMemorySecretStore()),
            InputSink = new ConcurrentSink(),
        });

        Assert.True(server.TryAdmit(IPAddress.Parse("2001:db8:1:2::1")));
        Assert.True(server.TryAdmit(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")));
        Assert.False(server.TryAdmit(IPAddress.Parse("2001:db8:1:2::ffff"))); // same /64, other address
        Assert.True(server.TryAdmit(IPAddress.Parse("2001:db8:1:3::1"))); // another /64
        Assert.True(server.TryAdmit(IPAddress.Parse("192.0.2.1")));
        Assert.True(server.TryAdmit(IPAddress.Parse("192.0.2.2"))); // IPv4 stays per address

        server.ReleasePending(IPAddress.Parse("2001:db8:1:2::9")); // releases the /64's slot
        Assert.True(server.TryAdmit(IPAddress.Parse("2001:db8:1:2::ffff")));
        Assert.Equal(IPAddress.Parse("192.0.2.1"), PenBridgeServer.PendingKey(IPAddress.Parse("::ffff:192.0.2.1")));
    }
}
