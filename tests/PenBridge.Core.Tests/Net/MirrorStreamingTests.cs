using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PenBridge.Core.Mirror;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Net;

/// <summary>Loopback end to end: video from the Mac's send thread to the phone's VideoFrameReady (protocol 1.3).</summary>
public class MirrorStreamingTests
{
    [Fact]
    public async Task Frames_arrive_in_order_with_their_bytes_and_capture_times()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        MirrorStreamConfig config = await rig.StartAsync();
        Assert.Equal(config, rig.ServerConfigs.All()[^1]);
        Assert.Equal(MirrorCodec.Hevc, config.Codec);

        var sent = new List<byte[]>();
        for (int i = 0; i < 10; i++)
        {
            // A keyframe spans many fragments; the rest are one or two.
            byte[] au = MirrorRig.AccessUnit(i == 0 ? 40_000 : 600 + (i * 300), i, keyframe: i == 0);
            sent.Add(au);
            await rig.SendAsync(au, keyframe: i == 0);
        }

        ReceivedFrame[] frames = await rig.Frames.WaitFor(0, 10, "the phone got every frame");
        Assert.Equal(Enumerable.Range(1, 10).Select(n => (uint)n), frames.Select(f => f.Number));
        Assert.All(frames, f => Assert.Equal(config.Epoch, f.Epoch));
        Assert.Equal([true, .. Enumerable.Repeat(false, 9)], frames.Select(f => f.Keyframe));
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(sent[i], frames[i].Data);
        }

        // Capture times are on the session (Ping) clock: microseconds since the connection's start.
        uint sessionNow = rig.Mirror.ToSessionUs(rig.NowCaptureUs);
        Assert.Equal(sessionNow, frames[^1].CaptureTimeUs);
        Assert.Equal(10, rig.Sender.FramesSent);
        Assert.Equal(0, rig.Client.MirrorCounters.AuthFailures);
    }

    [Fact]
    public async Task Access_units_before_the_epochs_first_keyframe_are_discarded_unnumbered()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        await rig.StartAsync();
        rig.Server.SendVideoFrame(MirrorRig.AccessUnit(500, 1, false), false, rig.NowCaptureUs);
        rig.Server.SendVideoFrame(MirrorRig.AccessUnit(500, 2, false), false, rig.NowCaptureUs);
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 3, true), true);
        await rig.SendAsync(MirrorRig.AccessUnit(500, 4, false), false);

        ReceivedFrame[] frames = await rig.Frames.WaitFor(0, 2, "keyframe and follower");
        Assert.Equal([(1u, true), (2u, false)], frames.Select(f => (f.Number, f.Keyframe)));
        Assert.Equal(0, rig.Sender.FramesDropped);
    }

    [Fact]
    public async Task A_lost_frame_makes_the_phone_request_a_keyframe_and_the_next_keyframe_recovers()
    {
        int dropFrame = 0;
        DatagramFilter filter = d => !VideoDatagramHeader.TryRead(d, out VideoDatagramHeader h) || h.FrameNumber != (uint)Volatile.Read(ref dropFrame);
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, videoFilter: filter));
        await rig.StartAsync();
        await rig.SendAsync(MirrorRig.AccessUnit(5000, 1, true), true);
        await rig.SendAsync(MirrorRig.AccessUnit(800, 2, false), false);
        await rig.Frames.WaitFor(0, 2, "the first two frames");

        int keyframes = rig.Keyframes.Mark();
        Volatile.Write(ref dropFrame, 3);
        await rig.SendAsync(MirrorRig.AccessUnit(800, 3, false), false); // lost on the wire
        await rig.SendAsync(MirrorRig.AccessUnit(800, 4, false), false); // completes after a gap: gated, request due at once
        await rig.Keyframes.WaitFor(keyframes, 1, "the Mac's head was asked for a keyframe");
        await rig.H.SettleAsync();
        Assert.Equal(2, rig.Frames.Count); // frame 4 was not handed to the decoder

        await rig.SendAsync(MirrorRig.AccessUnit(800, 5, false), false); // still gated
        await rig.SendAsync(MirrorRig.AccessUnit(6000, 6, true), true);
        ReceivedFrame[] frames = await rig.Frames.WaitFor(0, 3, "the keyframe recovers the stream");
        Assert.Equal([1u, 2u, 6u], frames.Select(f => f.Number));
        Assert.True(frames[^1].Keyframe);
    }

    [Fact]
    public async Task The_phones_mirror_stats_count_frames_drops_requests_probes_and_reports_across_epochs()
    {
        int dropFrame = 0;
        DatagramFilter filter = d => !VideoDatagramHeader.TryRead(d, out VideoDatagramHeader h) || h.FrameNumber != (uint)Volatile.Read(ref dropFrame);
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, videoFilter: filter));
        Assert.Equal(default, rig.Client.MirrorStats);
        await rig.StartAsync();

        // A keyframe of about 100 fragments keeps the one lost fragment under the guard's 2 % loss threshold, so the
        // MirrorReport below doesn't step the guard down (its own epoch would race the SetMirrorSource epoch).
        await rig.SendAsync(MirrorRig.AccessUnit(120_000, 1, true), true);
        await rig.Frames.WaitFor(0, 1, "the keyframe");

        int keyframes = rig.Keyframes.Mark();
        Volatile.Write(ref dropFrame, 2);
        await rig.SendAsync(MirrorRig.AccessUnit(800, 2, false), false); // lost
        await rig.SendAsync(MirrorRig.AccessUnit(800, 3, false), false); // gated after the gap
        await rig.Keyframes.WaitFor(keyframes, 1, "a KeyframeRequest");
        MirrorClientStats stats = await Wait.Until(() => rig.Client.MirrorStats, s => s.KeyframeRequestsSent == 1, "the request counted");
        Assert.Equal(1, stats.FramesComplete);
        Assert.Equal(2, stats.FramesDropped); // the lost frame and the gated one
        Assert.Equal(1, stats.Epochs);
        Assert.True(stats.ProbesSent >= 1, "the epoch's Probe");
        Assert.True(stats.DatagramsReceived >= 2);
        Assert.Equal(0, stats.ReportsSent);

        MirrorGuardLevel level = rig.Mirror.Guard.Level;
        rig.H.Time.Advance(ClientMirror.ReportInterval);
        await Wait.Until(() => rig.Client.MirrorStats.ReportsSent == 1, "a MirrorReport counted");
        await rig.H.SettleAsync();
        Assert.Equal(level, rig.Mirror.Guard.Level); // the report's loss stayed under the threshold: no guard epoch

        // A new epoch restarts the reassembler's counters; the stats keep the totals.
        int configs = rig.ClientConfigs.Mark();
        rig.Server.SetMirrorSource(1000, 1500, 60); // a new size, a new epoch
        await rig.ClientConfigs.WaitFor(configs, 1, "the second epoch");
        stats = rig.Client.MirrorStats;
        Assert.Equal(2, stats.Epochs);
        Assert.Equal(2, stats.FramesDropped);
        Assert.Equal(1, stats.FramesComplete);
    }

    [Fact]
    public async Task A_new_epoch_holds_its_first_keyframe_until_the_epochs_probe()
    {
        bool blockProbes = false;
        byte[]? probe = null;
        DatagramFilter filter = d =>
        {
            if (!Volatile.Read(ref blockProbes))
            {
                return true;
            }

            probe ??= d.ToArray();
            return false;
        };
        await using MirrorRig rig = await MirrorRig.CreateAsync(phone: MirrorRig.Phone(probes: filter));
        MirrorStreamConfig first = await rig.StartAsync();
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 1, true), true);
        await rig.Frames.WaitFor(0, 1, "the first epoch streams");

        // A new source refresh rate replans: a new epoch whose Probe doesn't arrive yet.
        Volatile.Write(ref blockProbes, true);
        int configs = rig.ClientConfigs.Mark();
        rig.Server.SetMirrorSource(2000, 3000, 30);
        MirrorStreamConfig second = (await rig.ClientConfigs.WaitFor(configs, 1, "the second epoch's MirrorConfig"))[0];
        Assert.NotEqual(first.Epoch, second.Epoch);
        Assert.Equal(30, second.Fps);

        rig.Server.SendVideoFrame(MirrorRig.AccessUnit(3000, 2, true), true, rig.NowCaptureUs);
        rig.Server.SendVideoFrame(MirrorRig.AccessUnit(700, 3, false), false, rig.NowCaptureUs);
        await rig.H.SettleAsync();
        Assert.False(rig.Sender.IsReleased);
        Assert.Equal(1, rig.Sender.FramesSent);

        // The epoch's Probe, delivered late from the phone's socket and well within the wait, releases frame 1 and its follower.
        Assert.NotNull(probe);
        Assert.Equal(second.Epoch, probe[2]);
        rig.Client.Mirror!.SendRaw(probe);
        ReceivedFrame[] frames = await rig.Frames.WaitFor(1, 2, "the held keyframe and its follower");
        Assert.Equal(new[] { (second.Epoch, 1u, true), (second.Epoch, 2u, false) }, frames.Select(f => (f.Epoch, f.Number, f.Keyframe)));
    }

    [Fact]
    public async Task A_new_epoch_without_its_probe_goes_out_after_the_wait()
    {
        bool blockProbes = false;
        await using MirrorRig rig = await MirrorRig.CreateAsync(phone: MirrorRig.Phone(probes: _ => !Volatile.Read(ref blockProbes)));
        await rig.StartAsync();
        Volatile.Write(ref blockProbes, true);
        int configs = rig.ClientConfigs.Mark();
        rig.Server.SetMirrorSource(1000, 1500, 60); // smaller source: a new size, a new epoch
        MirrorStreamConfig second = (await rig.ClientConfigs.WaitFor(configs, 1, "the second epoch"))[0];
        rig.Server.SendVideoFrame(MirrorRig.AccessUnit(3000, 1, true), true, rig.NowCaptureUs);
        await rig.H.SettleAsync();

        rig.H.Time.Advance(VideoDatagram.EpochProbeWait - TimeSpan.FromMilliseconds(1));
        await rig.H.SettleAsync();
        Assert.False(rig.Sender.IsReleased);

        rig.H.Time.Advance(TimeSpan.FromMilliseconds(1));
        ReceivedFrame frame = (await rig.Frames.WaitFor(0, 1, "the keyframe after the wait"))[0];
        Assert.Equal((second.Epoch, 1u, true), (frame.Epoch, frame.Number, frame.Keyframe));
    }

    [Fact]
    public async Task Frames_dropped_unsent_still_take_numbers_so_the_phone_sees_the_gap()
    {
        bool blockProbes = false;
        await using MirrorRig rig = await MirrorRig.CreateAsync(phone: MirrorRig.Phone(probes: _ => !Volatile.Read(ref blockProbes)));
        await rig.StartAsync();
        Volatile.Write(ref blockProbes, true);
        int configs = rig.ClientConfigs.Mark();
        rig.Server.SetMirrorSource(2000, 3000, 30);
        await rig.ClientConfigs.WaitFor(configs, 1, "the held epoch");

        // Held: frame 1 and seven followers queue; frames 9 and 10 don't fit and are dropped, numbered.
        rig.Server.SendVideoFrame(MirrorRig.AccessUnit(3000, 0, true), true, rig.NowCaptureUs);
        for (int i = 1; i < 10; i++)
        {
            rig.Server.SendVideoFrame(MirrorRig.AccessUnit(400, i, false), false, rig.NowCaptureUs);
        }

        Assert.Equal(2, rig.Sender.FramesDropped);
        int keyframes = rig.Keyframes.Mark();
        rig.H.Time.Advance(VideoDatagram.EpochProbeWait);
        await rig.Frames.WaitFor(0, VideoSender.QueueCapacity, "the held frames");
        await Wait.Until(() => rig.Sender.FramesSent == VideoSender.QueueCapacity, "the backlog went out");
        await rig.SendAsync(MirrorRig.AccessUnit(400, 11, false), false); // frame 11 after the gap of 9 and 10

        await rig.Keyframes.WaitFor(keyframes, 1, "the gap made the phone ask for a keyframe");
        Assert.Equal(Enumerable.Range(1, VideoSender.QueueCapacity).Select(n => (uint)n), rig.Frames.All().Select(f => f.Number));
    }

    [Fact]
    public async Task Probes_pin_the_endpoint_and_forged_or_replayed_ones_do_not_move_it()
    {
        byte[]? held = null;
        bool hold = false;
        DatagramFilter probes = d =>
        {
            if (!Volatile.Read(ref hold))
            {
                return true;
            }

            held = d.ToArray(); // a genuine Probe that never reached the Mac
            return false;
        };
        await using MirrorRig rig = await MirrorRig.CreateAsync(phone: MirrorRig.Phone(probes: probes));
        await rig.StartAsync();
        SocketAddress home = rig.Sender.Target!;
        int port = rig.Mirror.VideoPort;
        using var attacker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        attacker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var mac = new IPEndPoint(IPAddress.Loopback, port);

        // A forged Probe (valid header, bad tag), a garbage datagram and a fragment-kind datagram.
        byte[] forged = new byte[VideoDatagram.MinSize];
        VideoDatagramHeader.Probe(rig.Client.MirrorConfig!.Value.Epoch, SessionIdOf(rig), 1_000_000).Write(forged);
        attacker.SendTo(forged, mac);
        attacker.SendTo(new byte[] { 1, 2, 3 }, mac);
        await Wait.Until(() => rig.Mirror.Counters is { AuthFailures: 1, Malformed: 1 }, "the forgery and the garbage were counted");

        // A genuine Probe replayed from elsewhere is stale (the phone's own copy got there first).
        Volatile.Write(ref hold, true);
        rig.H.Time.Advance(ClientMirror.ProbeInterval);
        await Wait.Until(() => held is not null, "the phone produced a Probe");
        byte[] fresh = held!;
        Volatile.Write(ref hold, false);
        rig.H.Time.Advance(ClientMirror.ProbeInterval); // the next genuine Probe (higher sequence) from home
        await Wait.Until(() => rig.Mirror.Counters.ProbesAccepted >= 2, "the phone's next Probe arrived");
        attacker.SendTo(fresh, mac);
        await Wait.Until(() => rig.Mirror.Counters.Stale >= 1, "the older genuine Probe is stale");
        Assert.Equal(home, rig.Sender.Target);
        Assert.Equal(0, rig.Mirror.Counters.EndpointChanges);

        // An authenticated Probe with a higher sequence from a new source re-pins (the phone roamed).
        Volatile.Write(ref hold, true);
        held = null;
        rig.H.Time.Advance(ClientMirror.ProbeInterval);
        await Wait.Until(() => held is not null, "the phone produced another Probe");
        attacker.SendTo(held!, mac);
        await Wait.Until(() => rig.Mirror.Counters.EndpointChanges == 1, "the newer Probe moved the endpoint");
        Assert.NotEqual(home, rig.Sender.Target);
    }

    [Fact]
    public async Task A_flood_of_forged_probes_is_capped_by_the_sessions_forged_datagram_budget()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        await rig.StartAsync();
        using var attacker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var mac = new IPEndPoint(IPAddress.Loopback, rig.Mirror.VideoPort);
        byte[] forged = new byte[VideoDatagram.MinSize];
        for (int i = 0; i < 100; i++)
        {
            VideoDatagramHeader.Probe(1, SessionIdOf(rig), 1_000_000 + (ulong)i).Write(forged);
            attacker.SendTo(forged, mac);
        }

        // Fake time doesn't refill the bucket: 32 decryptions (the burst), the rest undecrypted.
        MirrorCounters counters = await Wait.Until(() => rig.Mirror.Counters, c => c.AuthFailures + c.RateLimited == 100, "every forgery was handled");
        Assert.Equal(32, counters.AuthFailures);
        Assert.Equal(68, counters.RateLimited);
    }

    [Fact]
    public async Task The_phone_accepts_video_only_from_the_macs_video_endpoint()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        await rig.StartAsync();
        using var other = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        other.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        other.SendTo(new byte[VideoDatagram.MinSize], new IPEndPoint(IPAddress.Loopback, rig.Client.Mirror!.Port));
        await Wait.Until(() => rig.Client.MirrorCounters.FramesDropped == 1, "the foreign datagram was dropped by source");
        Assert.Equal(0, rig.Client.MirrorCounters.AuthFailures);
    }

    internal static uint SessionIdOf(MirrorRig rig) => rig.Server.ActiveInput!.SessionId;
}
