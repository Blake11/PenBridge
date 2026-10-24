using PenBridge.Core.Mirror;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using PenBridge.Core.Tests.Input;

namespace PenBridge.Core.Tests.Mirror;

/// <summary>The video hot path (fragment, seal, open, reassemble) and the input-path guard hook allocate nothing.</summary>
[Collection(AllocationGroup.Name)]
public sealed class MirrorAllocationTests : IDisposable
{
    private readonly VideoLink _link = new();

    public void Dispose() => _link.Dispose();

    [Fact]
    public void Fragment_seal_open_and_reassemble_do_not_allocate()
    {
        var time = new FakeTime();
        var reassembler = new FrameReassembler(time);
        reassembler.Start(1);
        byte[][] units = [VideoLink.AccessUnit(700, 1), VideoLink.AccessUnit(6_000, 2), VideoLink.AccessUnit(30_000, 3)];
        byte[] datagram = new byte[VideoDatagram.MaxSize];
        uint frameNumber = 0;
        long ready = 0;
        long gated = 0;
        long requests = 0;
        Alloc.AssertZero(1_000, i =>
        {
            bool key = i % 10 == 0;
            byte[] au = key ? units[2] : units[i % 2];
            var frame = new VideoFrameInfo(1, ++frameNumber, key, (uint)i);
            int count = VideoFragmenter.GetFragmentCount(au.Length);
            for (int f = 0; f < count; f++)
            {
                int length = VideoFragmenter.WriteFragment(_link.Tx, au, frame, f, datagram);
                if (i % 7 == 3 && f == count - 1)
                {
                    continue; // lost: drops and gating run too
                }

                if (!_link.Rx.TryOpen(datagram.AsSpan(0, length), out VideoDatagramHeader header, out int payload))
                {
                    continue;
                }

                switch (reassembler.Accept(header, datagram.AsSpan(VideoDatagramHeader.Size, payload), out VideoFrame? done))
                {
                    case ReassemblyResult.FrameReady:
                        ready++;
                        done!.Dispose();
                        break;
                    case ReassemblyResult.Gated:
                        gated++;
                        break;
                }
            }

            time.Advance(TimeSpan.FromMilliseconds(5));
            if (reassembler.TryTakeKeyframeRequest(out _))
            {
                requests++;
            }

            reassembler.Poll();
            _ = reassembler.NextDeadline;
            _ = reassembler.CreateReport(frameNumber, (ulong)i);
        }, () => ready, () => gated, () => requests);
    }

    [Fact]
    public void Probes_do_not_allocate()
    {
        byte[] key = new byte[32];
        using var tx = new VideoCipher(key, 9);
        using var rx = new VideoCipher(key, 9);
        byte[] datagram = new byte[VideoDatagram.MaxSize];
        long opened = 0;
        Alloc.AssertZero(2_000, i =>
        {
            int length = VideoFragmenter.WriteProbe(tx, (byte)i, datagram);
            if (rx.TryOpen(datagram.AsSpan(0, length), out VideoDatagramHeader header, out _) && header.Kind == VideoDatagramKind.Probe)
            {
                opened++;
            }
        }, () => opened);
    }

    [Fact]
    public void The_input_guard_hook_and_the_pacer_do_not_allocate()
    {
        var time = new FakeTime();
        var guard = new MirrorRateController(time);
        guard.Start(new MirrorStreamPlan(MirrorCodec.Hevc, 1920, 1080, 60, 12_000, 3_000));
        var pacer = new VideoPacer(time);
        pacer.Configure(12_000, 60);
        uint timeUs = 0;
        long calls = 0;
        Alloc.AssertZero(5_000, i =>
        {
            time.Advance(TimeSpan.FromMilliseconds(i % 50 == 0 ? 60 : 4));
            timeUs += 4_000;
            guard.OnInputDatagram(i % 20 != 0, timeUs);
            if (i % 30 == 0)
            {
                pacer.BeginFrame(12 * 1200, i % 60 == 0);
            }

            if (pacer.GetDelay() == TimeSpan.Zero)
            {
                pacer.OnSent(1200);
            }

            calls++;
        }, () => calls);
    }
}
