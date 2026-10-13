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

    private static SessionInput CreateInput(IInputSink sink, TimeProvider time) =>
        new(new Lock(), sink, time, null, Key, SessionId, IPAddress.Loopback, new DisplayRect(0, 0, 1000, 2000), NormalizedRect.Full, 16, null);

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
                input.ProcessDatagram(scratch, (i & 1) == 0 ? source : foreign); // stale or foreign
            },
            () => input.Counters.Stale + input.Counters.ForeignSource);
    }

    [Fact]
    public void Client_serialize_and_seal_path_is_allocation_free()
    {
        // Mirrors PenBridgeClient.SendLocked: header, payload, seal into one reused buffer.
        using var cipher = new InputCipher(Key, SessionId);
        byte[] buffer = new byte[InputDatagram.MaxSize];
        PenSample[] history = [Pens.Contact(1, 1), Pens.Contact(2, 2), Pens.Contact(3, 3)];
        ulong sequence = 0;
        long sealedCount = 0;

        Alloc.AssertZero(
            200,
            i =>
            {
                PenSample? pen = (i & 1) == 0 ? Pens.Contact((ushort)i, 2) : null;
                new InputDatagramHeader(InputDatagramOptions.None, SessionId, ++sequence).Write(buffer);
                int n = InputPayload.Write(buffer.AsSpan(InputDatagramHeader.Size), (uint)i, pen, [], history);
                cipher.Seal(buffer, n);
                sealedCount++;
            },
            () => sealedCount);
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
    public void Datagrams_from_another_ip_never_reach_decryption()
    {
        using SessionInput input = CreateInput(new CountingSink(), new FakeTime());
        using var sealer = new InputCipher(Key, SessionId);
        byte[] buffer = new byte[InputDatagram.MaxSize];
        int length = Seal(sealer, buffer, 1, 0, Pens.Hover(1, 1), []);

        input.ProcessDatagram(buffer.AsSpan(0, length), new IPEndPoint(IPAddress.Parse("10.1.2.3"), 50000).Serialize());

        Assert.Equal(1, input.Counters.ForeignSource);
        Assert.Equal(0, input.Counters.AuthFailures);
    }

    [Fact]
    public void Ipv4_mapped_peer_addresses_match_plain_ipv4_sources()
    {
        var sink = new CountingSink();
        using var input = new SessionInput(new Lock(), sink, new FakeTime(), null, Key, SessionId, IPAddress.Loopback.MapToIPv6(), new DisplayRect(0, 0, 1, 1), NormalizedRect.Full, 16, null);
        input.ConfigureSurface(100, 100);
        using var sealer = new InputCipher(Key, SessionId);
        byte[] buffer = new byte[InputDatagram.MaxSize];
        int length = Seal(sealer, buffer, 1, 0, Pens.Hover(1, 1), []);

        input.ProcessDatagram(buffer.AsSpan(0, length), new IPEndPoint(IPAddress.Loopback.MapToIPv6(), 4000).Serialize());

        Assert.Equal(1, input.Counters.Accepted);
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
}
