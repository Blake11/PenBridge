using System.Buffers.Binary;
using PenBridge.Core.Mirror;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Mirror;

/// <summary>VideoFragmenter ↔ FrameReassembler (docs/specs/screen-mirroring.md "Video datagram", "Reassembly").</summary>
public sealed class FrameReassemblerTests : IDisposable
{
    private const byte Epoch = 3;

    private readonly FakeTime _time = new();
    private readonly VideoLink _link = new();
    private readonly FrameReassembler _r;

    public FrameReassemblerTests()
    {
        _r = new FrameReassembler(_time);
        _r.Start(Epoch);
    }

    public void Dispose() => _link.Dispose();

    [Fact]
    public void Fragment_counts_follow_the_greedy_layout()
    {
        Assert.Equal(1, VideoFragmenter.GetFragmentCount(1));
        Assert.Equal(1, VideoFragmenter.GetFragmentCount(1152));
        Assert.Equal(2, VideoFragmenter.GetFragmentCount(1153));
        Assert.Equal(2, VideoFragmenter.GetFragmentCount(1152 + 1160));
        Assert.Equal(3, VideoFragmenter.GetFragmentCount(1152 + 1160 + 1));
        Assert.Equal(904, VideoFragmenter.GetFragmentCount(VideoDatagram.MaxFrameLength));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoFragmenter.GetFragmentCount(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoFragmenter.GetFragmentCount(VideoDatagram.MaxFrameLength + 1));
    }

    [Fact]
    public void Fragments_carry_the_header_prefix_and_increasing_sequences()
    {
        byte[] au = VideoLink.AccessUnit(3000, 5);
        List<OpenedFragment> fragments = _link.Send(au, new VideoFrameInfo(Epoch, 5, true, 0xCAFEBABE));
        Assert.Equal(3, fragments.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(VideoDatagramHeader.Fragment(Epoch, true, VideoLink.Session, (ulong)i + 1, 5, (ushort)i, 3), fragments[i].Header);
        }

        Assert.Equal([1160, 1160, 3000 - 1152 - 1160], fragments.Select(f => f.Payload.Length));
        Assert.Equal(0xCAFEBABEu, BinaryPrimitives.ReadUInt32BigEndian(fragments[0].Payload));
        Assert.Equal(3000u, BinaryPrimitives.ReadUInt32BigEndian(fragments[0].Payload.AsSpan(4)));
        Assert.Equal(au[..1152], fragments[0].Payload[8..]);
        Assert.Equal(au[1152..2312], fragments[1].Payload);
        Assert.Equal(au[2312..], fragments[2].Payload);
        Assert.Equal(3UL, _link.Tx.LastSealedSequence);
    }

    [Fact]
    public void Fragmenter_rejects_bad_arguments()
    {
        byte[] d = new byte[VideoDatagram.MaxSize];
        var frame = new VideoFrameInfo(1, 1, false, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoFragmenter.WriteFragment(_link.Tx, [], frame, 0, d));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoFragmenter.WriteFragment(_link.Tx, new byte[10], frame, 1, d));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoFragmenter.WriteFragment(_link.Tx, new byte[10], frame, -1, d));
        Assert.Equal("datagram", Assert.Throws<ArgumentException>(() => VideoFragmenter.WriteFragment(_link.Tx, new byte[10], frame, 0, new byte[24 + 8 + 10 + 15])).ParamName);
        Assert.Equal(24 + 8 + 10 + 16, VideoFragmenter.WriteFragment(_link.Tx, new byte[10], frame, 0, new byte[24 + 8 + 10 + 16]));
        Assert.Throws<ArgumentNullException>(() => VideoFragmenter.WriteFragment(null!, new byte[10], frame, 0, d));
        Assert.Equal("datagram", Assert.Throws<ArgumentException>(() => VideoFragmenter.WriteProbe(_link.Tx, 1, new byte[39])).ParamName);
    }

    [Fact]
    public void Probe_round_trips()
    {
        byte[] d = new byte[VideoDatagram.MaxSize];
        Assert.Equal(VideoDatagram.MinSize, VideoFragmenter.WriteProbe(_link.Tx, 9, d));
        Assert.True(_link.Rx.TryOpen(d.AsSpan(0, VideoDatagram.MinSize), out VideoDatagramHeader header, out int payload));
        Assert.Equal(VideoDatagramHeader.Probe(9, VideoLink.Session, 1), header);
        Assert.Equal(0, payload);
        Assert.Equal(ReassemblyResult.Invalid, _r.Accept(header, [], out _)); // a probe is no fragment
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1152)]
    [InlineData(1153)]
    [InlineData(2312)]
    [InlineData(2313)]
    [InlineData(50_000)]
    [InlineData(VideoDatagram.MaxFrameLength)]
    public void Frames_of_every_size_round_trip(int length)
    {
        byte[] au = VideoLink.AccessUnit(length, 1);
        List<OpenedFragment> fragments = _link.Send(au, new VideoFrameInfo(Epoch, 1, true, 1234));
        VideoFrame frame = DeliverAll(fragments);
        Assert.Equal(au, frame.Span.ToArray());
        Assert.Equal(au, frame.Data.ToArray());
        Assert.Equal((Epoch, 1u, true, 1234u, length), (frame.Epoch, frame.FrameNumber, frame.IsKeyframe, frame.CaptureTimeUs, frame.Length));
        Assert.Equal(_time.GetTimestamp(), frame.CompletedTimestamp);
        frame.Dispose();
        Assert.Equal(0, frame.Length);
        Assert.True(frame.Span.IsEmpty);
        Assert.Equal(1u, _r.FramesComplete);
        Assert.Equal(1u, _r.LastCompleteFrame);
        Assert.False(_r.KeyframeNeeded);
    }

    [Fact]
    public void Reordered_fragments_and_interleaved_frames_complete()
    {
        List<OpenedFragment> key = _link.Send(VideoLink.AccessUnit(5000, 1), new VideoFrameInfo(Epoch, 1, true, 0));
        key.Reverse();
        DeliverAll(key).Dispose();

        byte[] au2 = VideoLink.AccessUnit(4000, 2);
        byte[] au3 = VideoLink.AccessUnit(3000, 3);
        List<OpenedFragment> f2 = _link.Send(au2, new VideoFrameInfo(Epoch, 2, false, 0));
        List<OpenedFragment> f3 = _link.Send(au3, new VideoFrameInfo(Epoch, 3, false, 0));
        Assert.Equal(ReassemblyResult.Buffered, Accept(f2[0]));
        Assert.Equal(ReassemblyResult.Buffered, Accept(f3[1]));
        Assert.Equal(ReassemblyResult.Buffered, Accept(f2[3]));
        Assert.Equal(ReassemblyResult.Buffered, Accept(f3[0]));
        Assert.Equal(ReassemblyResult.Buffered, Accept(f2[2]));
        Assert.Equal(ReassemblyResult.FrameReady, Accept(f2[1], out VideoFrame? frame2));
        Assert.Equal(au2, frame2!.Span.ToArray());
        Assert.Equal(ReassemblyResult.FrameReady, Accept(f3[2], out VideoFrame? frame3));
        Assert.Equal(au3, frame3!.Span.ToArray());
        Assert.Equal((3u, 0u, 0u), (_r.FramesComplete, _r.FramesDropped, _r.FragmentsLost));
    }

    [Fact]
    public void Duplicates_and_late_fragments_change_nothing()
    {
        List<OpenedFragment> f = _link.Send(VideoLink.AccessUnit(3000, 1), new VideoFrameInfo(Epoch, 1, true, 0));
        Assert.Equal(ReassemblyResult.Buffered, Accept(f[0]));
        Assert.Equal(ReassemblyResult.Duplicate, Accept(f[0]));
        Assert.Equal(ReassemblyResult.Buffered, Accept(f[2]));
        Assert.Equal(ReassemblyResult.Duplicate, Accept(f[2]));
        Assert.Equal(ReassemblyResult.FrameReady, Accept(f[1], out VideoFrame? frame));
        frame!.Dispose();
        Assert.Equal(ReassemblyResult.Stale, Accept(f[1]));
        Assert.Equal(ReassemblyResult.Stale, Accept(f[0]));
        Assert.Equal(4UL, _r.DatagramsRejected);
        Assert.Equal((1u, 0u, 0u), (_r.FramesComplete, _r.FramesDropped, _r.FragmentsLost));
    }

    [Fact]
    public void A_lost_fragment_gates_until_a_keyframe_and_requests_one_every_250_ms()
    {
        DeliverAll(Frame(1, true)).Dispose();
        List<OpenedFragment> f2 = Frame(2, false, 3000);
        Accept(f2[0]);
        Accept(f2[2]); // f2[1] lost

        Assert.False(_r.TryTakeKeyframeRequest(out _));
        Assert.Equal(ReassemblyResult.Gated, AcceptAll(Frame(3, false)));
        Assert.True(_r.KeyframeNeeded);
        Assert.Equal((1u, 2u, 1u), (_r.FramesComplete, _r.FramesDropped, _r.FragmentsLost)); // 2 dropped, 3 gated
        Assert.Equal(ReassemblyResult.Stale, Accept(f2[1])); // the late fragment of a dropped frame

        Assert.True(_r.TryTakeKeyframeRequest(out KeyframeRequestMessage request));
        Assert.Equal((Epoch, 1u), (request.Epoch, request.LastCompleteFrame));
        Assert.False(_r.TryTakeKeyframeRequest(out _));
        Assert.Equal(TimeSpan.FromMilliseconds(250), _r.NextDeadline);
        _time.Advance(TimeSpan.FromMilliseconds(249));
        Assert.False(_r.TryTakeKeyframeRequest(out _));
        Assert.Equal(ReassemblyResult.Gated, AcceptAll(Frame(4, false)));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(_r.TryTakeKeyframeRequest(out _));

        VideoFrame key = DeliverAll(Frame(5, true));
        Assert.True(key.IsKeyframe);
        key.Dispose();
        Assert.False(_r.KeyframeNeeded);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_r.TryTakeKeyframeRequest(out _));
        Assert.Null(_r.NextDeadline);
        Assert.Equal(ReassemblyResult.FrameReady, AcceptAll(Frame(6, false), dispose: true));
        Assert.Equal(new MirrorReportMessage(Epoch, 3, 3, 1, 5, 77), _r.CreateReport(5, 77));
    }

    [Fact]
    public void A_frame_never_seen_is_a_loss()
    {
        DeliverAll(Frame(1, true)).Dispose();
        _ = Frame(2, false); // sent, all of it lost
        Assert.Equal(ReassemblyResult.Gated, AcceptAll(Frame(3, false)));
        Assert.Equal((1u, 2u, 1u), (_r.FramesComplete, _r.FramesDropped, _r.FragmentsLost));
        Assert.True(_r.TryTakeKeyframeRequest(out _));
    }

    [Fact]
    public void A_newer_complete_frame_drops_the_older_incomplete_one()
    {
        DeliverAll(Frame(1, true)).Dispose();
        List<OpenedFragment> f2 = Frame(2, false, 5000);
        Accept(f2[0]);
        Assert.Equal(ReassemblyResult.Gated, AcceptAll(Frame(3, false)));
        Assert.Equal(4u, _r.FragmentsLost); // f2[1..4]
        Assert.Equal(ReassemblyResult.Stale, Accept(f2[1]));
    }

    [Fact]
    public void Frames_time_out_100_ms_after_their_first_fragment()
    {
        DeliverAll(Frame(1, true)).Dispose();
        List<OpenedFragment> f2 = Frame(2, false, 3000);
        Accept(f2[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(100), _r.NextDeadline);
        _time.Advance(TimeSpan.FromMilliseconds(99));
        Accept(f2[1]);
        _r.Poll();
        Assert.False(_r.KeyframeNeeded);
        Assert.Equal(TimeSpan.FromMilliseconds(1), _r.NextDeadline);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        _r.Poll();
        Assert.True(_r.KeyframeNeeded);
        Assert.Equal((1u, 1u), (_r.FramesDropped, _r.FragmentsLost));
        Assert.Equal(ReassemblyResult.Stale, Accept(f2[2]));
        Assert.Equal(TimeSpan.Zero, _r.NextDeadline); // the keyframe request is due
        Assert.True(_r.TryTakeKeyframeRequest(out _));
    }

    [Fact]
    public void A_new_epoch_resets_and_waits_for_its_keyframe()
    {
        DeliverAll(Frame(1, true)).Dispose();
        List<OpenedFragment> old = Frame(2, false);
        _r.Start(4);
        Assert.Equal(4, _r.Epoch);
        Assert.Equal((0u, 0u, 0u, 0u), (_r.FramesComplete, _r.FramesDropped, _r.FragmentsLost, _r.LastCompleteFrame));
        Assert.Equal(ReassemblyResult.WrongEpoch, Accept(old[0]));

        // The first request waits one interval for the epoch's first keyframe.
        Assert.True(_r.KeyframeNeeded);
        Assert.False(_r.TryTakeKeyframeRequest(out _));
        Assert.Equal(TimeSpan.FromMilliseconds(250), _r.NextDeadline);

        // A non-keyframe first means the keyframe was lost: request at once.
        Assert.Equal(ReassemblyResult.Gated, AcceptAll(Frame(7, false, epoch: 4)));
        Assert.True(_r.TryTakeKeyframeRequest(out KeyframeRequestMessage request));
        Assert.Equal((4, 0u), (request.Epoch, request.LastCompleteFrame));
        Assert.Equal(ReassemblyResult.FrameReady, AcceptAll(Frame(8, true, epoch: 4), dispose: true));
        Assert.Equal(ReassemblyResult.FrameReady, AcceptAll(Frame(9, false, epoch: 4), dispose: true));
    }

    [Fact]
    public void Without_any_frame_after_start_a_request_follows_after_250_ms()
    {
        _time.Advance(TimeSpan.FromMilliseconds(249));
        Assert.False(_r.TryTakeKeyframeRequest(out _));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(_r.TryTakeKeyframeRequest(out _));
        Assert.False(_r.TryTakeKeyframeRequest(out _));
        _time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.True(_r.TryTakeKeyframeRequest(out _));
    }

    [Fact]
    public void Frames_held_by_the_decoder_limit_new_ones()
    {
        var held = new List<VideoFrame>();
        for (uint n = 1; n <= FrameReassembler.MaxFramesHeld; n++)
        {
            held.Add(DeliverAll(Frame(n, true)));
        }

        Assert.Equal(ReassemblyResult.NoBuffer, AcceptAll(Frame(5, true)));
        Assert.True(_r.KeyframeNeeded);
        held[0].Dispose();
        held[0].Dispose(); // a repeat is ignored
        VideoFrame next = DeliverAll(Frame(6, true));
        Assert.Equal(6u, next.FrameNumber);
        Assert.Equal(VideoLink.AccessUnit(1000, 6), next.Span.ToArray());
        Assert.Equal(VideoLink.AccessUnit(1000, 2), held[1].Span.ToArray()); // the others are untouched
        foreach (VideoFrame f in held)
        {
            f.Dispose();
        }

        next.Dispose();
    }

    [Fact]
    public void A_fifth_frame_in_flight_drops_the_oldest()
    {
        DeliverAll(Frame(1, true)).Dispose();
        var partial = new List<List<OpenedFragment>>();
        for (uint n = 3; n <= 6; n++)
        {
            List<OpenedFragment> f = Frame(n, false, 3000);
            Assert.Equal(ReassemblyResult.Buffered, Accept(f[0]));
            partial.Add(f);
        }

        Assert.Equal(ReassemblyResult.NoBuffer, Accept(Frame(2, false)[0])); // older than all in flight: no room
        Assert.Equal(0u, _r.FramesDropped);
        List<OpenedFragment> f7 = Frame(7, false, 3000);
        Assert.Equal(ReassemblyResult.Buffered, Accept(f7[0]));
        Assert.Equal((1u, 2u), (_r.FramesDropped, _r.FragmentsLost)); // frame 3 went
        Assert.Equal(ReassemblyResult.Stale, Accept(partial[0][1]));
        Assert.Equal(ReassemblyResult.Buffered, Accept(partial[1][1])); // frame 4 is still in flight
    }

    [Fact]
    public void The_pool_evicts_older_frames_for_a_new_one()
    {
        List<OpenedFragment> big1 = Frame(1, true, VideoDatagram.MaxFrameLength);
        List<OpenedFragment> big2 = Frame(2, true, VideoDatagram.MaxFrameLength);
        Assert.Equal(ReassemblyResult.Buffered, Accept(big1[0]));
        Assert.Equal(ReassemblyResult.Buffered, Accept(big2[0])); // two don't fit: frame 1 goes
        Assert.Equal((1u, 903u), (_r.FramesDropped, _r.FragmentsLost));
        Assert.Equal(ReassemblyResult.Stale, Accept(big1[1]));
        VideoFrame frame = DeliverAll(big2.Skip(1));
        Assert.Equal(VideoDatagram.MaxFrameLength, frame.Length);

        // While the decoder holds it, another frame that size has no room and nothing older to evict.
        Assert.Equal(ReassemblyResult.NoBuffer, Accept(Frame(3, true, VideoDatagram.MaxFrameLength)[0]));
        frame.Dispose();
        Assert.Equal(ReassemblyResult.FrameReady, AcceptAll(Frame(4, true, VideoDatagram.MaxFrameLength), dispose: true));
    }

    [Fact]
    public void Invalid_fragments_are_rejected()
    {
        OpenedFragment first = Frame(1, true, 3000)[0];
        VideoDatagramHeader h = first.Header;

        Assert.Equal(ReassemblyResult.Invalid, _r.Accept(h, first.Payload.AsSpan(0, 7), out _)); // no prefix
        Assert.Equal(ReassemblyResult.Invalid, _r.Accept(h, Prefix(0, []), out _));
        Assert.Equal(ReassemblyResult.Invalid, _r.Accept(h, Prefix(VideoDatagram.MaxFrameLength + 1, []), out _));
        Assert.Equal(ReassemblyResult.Invalid, _r.Accept(h, Prefix(3 * 1160, []), out _)); // more than 3 fragments hold
        Assert.Equal(ReassemblyResult.Invalid, _r.Accept(h, Prefix(2000, new byte[1153]), out _)); // too much data
        Assert.Equal(ReassemblyResult.Invalid, _r.Accept(h with { FragmentIndex = 1 }, [], out _)); // empty
        Assert.Equal(ReassemblyResult.Invalid, _r.Accept(h with { FragmentIndex = 1 }, new byte[1161], out _));
        Assert.Equal(ReassemblyResult.Invalid, _r.Accept(h with { FragmentIndex = 3 }, new byte[10], out _)); // inconsistent header

        Assert.Equal(ReassemblyResult.Buffered, _r.Accept(h, first.Payload, out _));
        Assert.Equal(ReassemblyResult.Invalid, _r.Accept(h with { FragmentIndex = 1, FragmentCount = 4 }, new byte[10], out _));
        Assert.Equal(ReassemblyResult.Invalid, _r.Accept(h with { FragmentIndex = 1, Flags = VideoDatagramOptions.None }, new byte[10], out _));
        Assert.Equal(10UL, _r.DatagramsRejected);
    }

    [Fact]
    public void A_frame_length_that_doesnt_match_the_data_drops_the_frame()
    {
        VideoDatagramHeader h = VideoDatagramHeader.Fragment(Epoch, true, VideoLink.Session, 1, 1, 0, 1);
        Assert.Equal(ReassemblyResult.Invalid, _r.Accept(h, Prefix(100, new byte[50]), out _));
        Assert.Equal(1u, _r.FramesDropped);
        Assert.Equal(ReassemblyResult.Stale, _r.Accept(h, Prefix(50, new byte[50]), out _));
    }

    [Fact]
    public void Fragments_that_are_not_full_are_joined()
    {
        byte[] au = VideoLink.AccessUnit(350, 1);
        VideoDatagramHeader h = VideoDatagramHeader.Fragment(Epoch, true, VideoLink.Session, 1, 1, 0, 3);
        Assert.Equal(ReassemblyResult.Buffered, _r.Accept(h with { FragmentIndex = 2 }, au.AsSpan(300, 50), out _));
        Assert.Equal(ReassemblyResult.Buffered, _r.Accept(h, Prefix(350, au[..100]), out _));
        Assert.Equal(ReassemblyResult.FrameReady, _r.Accept(h with { FragmentIndex = 1 }, au.AsSpan(100, 200), out VideoFrame? frame));
        Assert.Equal(au, frame!.Span.ToArray());
        frame.Dispose();
    }

    [Fact]
    public void Inactive_until_started_and_after_stop()
    {
        var r = new FrameReassembler(_time);
        OpenedFragment f = Frame(1, true)[0];
        Assert.False(r.IsActive);
        Assert.Equal(ReassemblyResult.Inactive, r.Accept(f.Header, f.Payload, out _));
        Assert.False(r.TryTakeKeyframeRequest(out _));
        Assert.Null(r.NextDeadline);
        r.Poll();

        OpenedFragment partial = Frame(2, true, 3000)[0];
        Assert.Equal(ReassemblyResult.Buffered, _r.Accept(partial.Header, partial.Payload, out _));
        _r.Stop();
        Assert.False(_r.IsActive);
        Assert.Null(_r.NextDeadline);
        Assert.Equal(ReassemblyResult.Inactive, _r.Accept(f.Header, f.Payload, out _));
        Assert.Throws<ArgumentNullException>(() => new FrameReassembler(null!));
    }

    [Fact]
    public void Streams_with_loss_reorder_and_duplicates_never_show_a_broken_frame()
    {
        var random = new Random(2026);
        var originals = new Dictionary<uint, byte[]>();
        var arrivals = new List<OpenedFragment>();
        for (uint n = 1; n <= 300; n++)
        {
            bool key = n % 30 == 1;
            byte[] au = VideoLink.AccessUnit(key ? 20_000 : random.Next(1, 6_000), n);
            originals[n] = au;
            foreach (OpenedFragment f in _link.Send(au, new VideoFrameInfo(Epoch, n, key, n)))
            {
                double roll = random.NextDouble();
                if (roll < 0.01)
                {
                    continue; // lost
                }

                arrivals.Add(f);
                if (roll > 0.99)
                {
                    arrivals.Add(f); // duplicated (the cipher would drop it; the reassembler must too)
                }
            }
        }

        // Local reordering: swap neighbours now and then.
        for (int i = 1; i < arrivals.Count; i++)
        {
            if (random.NextDouble() < 0.05)
            {
                (arrivals[i - 1], arrivals[i]) = (arrivals[i], arrivals[i - 1]);
            }
        }

        bool needKey = true;
        uint last = 0;
        int ready = 0;
        foreach (OpenedFragment f in arrivals)
        {
            _time.Advance(TimeSpan.FromMilliseconds(0.2));
            if (_r.Accept(f.Header, f.Payload, out VideoFrame? frame) == ReassemblyResult.FrameReady)
            {
                Assert.Equal(originals[frame!.FrameNumber], frame.Span.ToArray());
                Assert.True(frame.FrameNumber > last);
                Assert.True(!needKey || frame.IsKeyframe, "after a loss the decoder gets a keyframe first");
                needKey = false;
                last = frame.FrameNumber;
                ready++;
                frame.Dispose();
            }

            if (_r.KeyframeNeeded)
            {
                needKey = true;
            }
        }

        Assert.InRange(ready, 30, 299);
        Assert.Equal((uint)ready, _r.FramesComplete);
        Assert.InRange(_r.FramesDropped, 1u, 300u);
    }

    private static byte[] Prefix(int frameLength, byte[] data)
    {
        byte[] payload = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)frameLength);
        data.CopyTo(payload, 8);
        return payload;
    }

    private List<OpenedFragment> Frame(uint number, bool key, int length = 1000, byte epoch = Epoch) =>
        _link.Send(VideoLink.AccessUnit(length, number), new VideoFrameInfo(epoch, number, key, number));

    private ReassemblyResult Accept(OpenedFragment f) => _r.Accept(f.Header, f.Payload, out _);

    private ReassemblyResult Accept(OpenedFragment f, out VideoFrame? frame) => _r.Accept(f.Header, f.Payload, out frame);

    private ReassemblyResult AcceptAll(IEnumerable<OpenedFragment> fragments, bool dispose = false)
    {
        ReassemblyResult result = ReassemblyResult.Inactive;
        foreach (OpenedFragment f in fragments)
        {
            result = _r.Accept(f.Header, f.Payload, out VideoFrame? frame);
            if (dispose)
            {
                frame?.Dispose();
            }
        }

        return result;
    }

    private VideoFrame DeliverAll(IEnumerable<OpenedFragment> fragments)
    {
        VideoFrame? ready = null;
        foreach (OpenedFragment f in fragments)
        {
            ReassemblyResult result = _r.Accept(f.Header, f.Payload, out VideoFrame? frame);
            if (frame is not null)
            {
                Assert.Equal(ReassemblyResult.FrameReady, result);
                ready = frame;
            }
        }

        return Assert.IsType<VideoFrame>(ready);
    }
}
