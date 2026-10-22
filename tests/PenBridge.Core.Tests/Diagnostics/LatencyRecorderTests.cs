using System.Net;
using System.Net.Sockets;
using PenBridge.Core.Diagnostics;
using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using PenBridge.Core.Tests.Input;
using PenBridge.Core.Tests.Net;

namespace PenBridge.Core.Tests.Diagnostics;

/// <summary>The ring buffer, the dump, and the capture wired into the server's input path.</summary>
[Collection(AllocationGroup.Name)]
public class LatencyRecorderTests
{
    private const uint SessionId = 0xC0FFEE01;
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private sealed class CountingSink : IInputSink
    {
        public long Pen;

        public void OnPenEvent(in PenEvent e) => Pen++;

        public void OnGestureEvent(in GestureEvent e)
        {
        }
    }

    [Fact]
    public void Ring_keeps_the_newest_records_oldest_first()
    {
        var recorder = new LatencyRecorder(4);
        for (uint i = 1; i <= 6; i++)
        {
            recorder.RecordInput(1, i, i * 10, i * 10 + 1, LatencyFrameInfo.None);
        }

        Assert.Equal(6, recorder.Total);
        Assert.Equal(4, recorder.Count);
        Assert.Equal([3UL, 4UL, 5UL, 6UL], recorder.Snapshot().Select(r => r.ClientUs));

        Assert.Equal(4, recorder.Snapshot(clear: true).Length);
        Assert.Empty(recorder.Snapshot());
        recorder.RecordInput(1, 1, 1, 1, LatencyFrameInfo.None);
        recorder.Clear();
        Assert.Equal(0, recorder.Count);
        recorder.RecordClientPing(2, 99, 1234);
        LatencyRecord ping = Assert.Single(recorder.Snapshot());
        Assert.Equal(new LatencyRecord(LatencyRecordKind.ClientPing, LatencyFrameInfo.None, 2, 99, 1234, 1234), ping);
    }

    [Fact]
    public void Ring_below_capacity_snapshots_in_order()
    {
        var recorder = new LatencyRecorder(8);
        recorder.RecordServerRtt(1, 100, 250);
        recorder.RecordInput(1, 5, 300, 310, LatencyFrameInfo.PenContact);

        LatencyRecord[] all = recorder.Snapshot();

        Assert.Equal(2, all.Length);
        Assert.Equal(LatencyRecordKind.ServerRtt, all[0].Kind);
        Assert.Equal(250, all[0].ServerEnd);
        Assert.Equal(LatencyRecordKind.Input, all[1].Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyRecorder(0));
    }

    [Fact]
    public void Recording_is_allocation_free()
    {
        var recorder = new LatencyRecorder(256);
        Alloc.AssertZero(
            1000,
            i =>
            {
                switch (i % 3)
                {
                    case 0:
                        recorder.RecordInput(1, (uint)i, i, i + 1, LatencyFrameInfo.PenContact);
                        break;
                    case 1:
                        recorder.RecordClientPing(1, (ulong)i, i);
                        break;
                    default:
                        recorder.RecordServerRtt(1, i, i + 5);
                        break;
                }
            },
            () => recorder.Total);
    }

    [Fact]
    public void Datagram_path_with_capture_on_is_allocation_free_and_records_each_frame()
    {
        var time = new FakeTime();
        var sink = new CountingSink();
        var recorder = new LatencyRecorder(64);
        using var input = new SessionInput(new Lock(), sink, time, null, Key, SessionId, new DisplayRect(0, 0, 1000, 2000), NormalizedRect.Full, 16, null, null, recorder);
        input.ConfigureSurface(1440, 3088);
        SocketAddress source = new IPEndPoint(IPAddress.Loopback, 50000).Serialize();

        const int PerPass = 200;
        const int Total = (8 * PerPass) + 1;
        using var sealer = new InputCipher(Key, SessionId);
        byte[][] datagrams = new byte[Total][];
        int[] lengths = new int[Total];
        for (int i = 0; i < Total; i++)
        {
            datagrams[i] = new byte[InputDatagram.MaxSize];
            PenSample pen = i % 10 < 7 ? Pens.Contact((ushort)(1000 + i), 1000) : Pens.Hover((ushort)(1000 + i), 1000);
            new InputDatagramHeader(InputDatagramOptions.None, SessionId, (ulong)i + 1).Write(datagrams[i]);
            int length = InputPayload.Write(datagrams[i].AsSpan(InputDatagramHeader.Size), (uint)(i * 4000), pen, [], default);
            lengths[i] = sealer.Seal(datagrams[i], length);
        }

        input.ProcessDatagram(datagrams[0].AsSpan(0, lengths[0]), source); // pins the endpoint (allocates once)

        Alloc.AssertZero(
            PerPass,
            i => input.ProcessDatagram(datagrams[i + 1].AsSpan(0, lengths[i + 1]), source),
            () => recorder.Total);

        Assert.Equal(input.Accepted, recorder.Total);
        LatencyRecord last = recorder.Snapshot()[^1];
        Assert.Equal(LatencyRecordKind.Input, last.Kind);
        Assert.Equal(SessionId, last.SessionId);
        Assert.Equal((ulong)((input.Accepted - 1) * 4000), last.ClientUs); // AssertZero may stop before the last datagram
        Assert.Equal(time.GetTimestamp(), last.Server);
        Assert.Equal(LatencyFrameInfo.PenInRange, last.Flags & ~LatencyFrameInfo.PenContact);
    }

    [Fact]
    public void Frames_before_surface_info_are_not_recorded()
    {
        var recorder = new LatencyRecorder(8);
        using var input = new SessionInput(new Lock(), new CountingSink(), new FakeTime(), null, Key, SessionId, new DisplayRect(0, 0, 1, 1), NormalizedRect.Full, 16, null, null, recorder);
        using var sealer = new InputCipher(Key, SessionId);
        byte[] buffer = new byte[InputDatagram.MaxSize];
        new InputDatagramHeader(InputDatagramOptions.None, SessionId, 1).Write(buffer);
        int length = sealer.Seal(buffer, InputPayload.Write(buffer.AsSpan(InputDatagramHeader.Size), 0, Pens.Hover(1, 1), [], default));

        input.ProcessDatagram(buffer.AsSpan(0, length), new IPEndPoint(IPAddress.Loopback, 1).Serialize());

        Assert.Equal(0, recorder.Total);
    }

    [Fact]
    public void Dump_writes_the_summary_then_one_row_per_record()
    {
        LatencyRecord[] records =
        [
            new(LatencyRecordKind.ClientPing, LatencyFrameInfo.None, 1, 1_000, 10_000_000, 10_000_000),
            new(LatencyRecordKind.ServerRtt, LatencyFrameInfo.None, 1, 0, 11_000_000, 11_400_000),
            new(LatencyRecordKind.Input, LatencyFrameInfo.PenInRange | LatencyFrameInfo.PenContact, 1, 2_000, 13_000_000, 13_500_000),
        ];
        var writer = new StringWriter();

        LatencyDump.Write(writer, records, 1_000_000_000);

        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(LatencyDump.Header, lines[0]);
        Assert.Contains(lines, l => l.StartsWith("# session 00000001: inputs=1 client_pings=1 round_trips=1", StringComparison.Ordinal));

        // One-way 0 → θ = −1000 µs − 200 µs (half the 400 µs RTT); the frame then took 2000 + 200 + 500 µs.
        Assert.Contains("# pen_down_to_inject: count=1 p50_ms=2.700 p95_ms=2.700 max_ms=2.700 min_ms=2.700", lines);
        Assert.Contains("# send_to_receive: count=1 p50_ms=2.200 p95_ms=2.200 max_ms=2.200 min_ms=2.200", lines);
        int columns = Array.IndexOf(lines, LatencyDump.Columns);
        Assert.InRange(columns, 1, int.MaxValue);
        Assert.Equal("input\t1\t6\t2000\t3000.0\t3500.0", lines[columns + 3]);
        Assert.Equal(columns + 4, lines.Length);
    }

    [Fact]
    public async Task Loopback_session_captures_inputs_pings_and_round_trips()
    {
        var recorder = new LatencyRecorder(1024);
        await using var h = new NetHarness(o => new PenBridgeServerOptions
        {
            Identity = o.Identity,
            Registry = o.Registry,
            InputSink = o.InputSink,
            ServerName = o.ServerName,
            BindAddress = o.BindAddress,
            Port = o.Port,
            TargetDisplay = o.TargetDisplay,
            TimeProvider = o.TimeProvider,
            LatencyRecorder = recorder,
        });
        PenBridgeClient client = await h.PairAsync();
        uint session = h.Server.ActiveInput!.SessionId;

        // Both sides ping every 2 s; one fake-time step per second lets each round cross the loopback.
        await Wait.Step(h.Time, TimeSpan.FromSeconds(4));
        await Wait.Until(() => Count(recorder, LatencyRecordKind.ClientPing) >= 2 && Count(recorder, LatencyRecordKind.ServerRtt) >= 2, "pings recorded");
        // The stroke of EndToEndTests.DrawStroke, one frame per fake millisecond: each frame is applied before the clock
        // moves, and frame times never need the client's same-instant bump.
        PenSample?[] stroke =
        [
            Pens.Hover(1000, 1000), Pens.Contact(1000, 1000),
            .. Enumerable.Range(1, 5).Select(i => (PenSample?)Pens.Contact((ushort)(1000 + (i * 500)), 1000, (ushort)(20000 + (i * 1000)))),
            Pens.Hover(4000, 1000), null,
        ];
        foreach (PenSample? pen in stroke)
        {
            long accepted = h.Server.GetStats().Counters.Accepted;
            Assert.True(client.SendState(pen, []));
            await Wait.Until(() => h.Server.GetStats().Counters.Accepted > accepted, "frame applied");
            h.Time.Advance(TimeSpan.FromMilliseconds(1));
        }

        await EndToEndTests.AssertStrokeAsync(h.Sink);

        LatencyRecord[] records = recorder.Snapshot();
        Assert.All(records, r => Assert.Equal(session, r.SessionId));
        Assert.InRange(Count(recorder, LatencyRecordKind.Input), 7, int.MaxValue);

        // One shared fake clock that doesn't move during I/O: every corrected latency is exactly zero.
        LatencyReport report = LatencyAnalyzer.Analyze(records, h.Time.TimestampFrequency);
        SessionClock clock = Assert.Single(report.Sessions);
        Assert.True(clock.HasOffset);
        Assert.Equal(0, clock.MinRttUs);
        Assert.Equal(1, report.PenDown.Count);
        Assert.Equal(0, report.Inject.MinUs, 1);
        Assert.Equal(0, report.Inject.MaxUs, 1);
        Assert.Equal(0, report.UnmeasuredInputs);
    }

    private static int Count(LatencyRecorder recorder, LatencyRecordKind kind) => recorder.Snapshot().Count(r => r.Kind == kind);
}
