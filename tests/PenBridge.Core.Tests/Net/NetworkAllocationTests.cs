using PenBridge.Core.Input;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Tests.Input;

namespace PenBridge.Core.Tests.Net;

/// <summary>
/// The zero-allocation assertions over a live session. Kept apart from <see cref="ClickResendTests"/> and
/// <see cref="FingerTouchesTests"/> so those network classes run in parallel; only these run in the serial
/// <see cref="AllocationGroup"/>.
/// </summary>
[Collection(AllocationGroup.Name)]
public class NetworkAllocationTests
{
    [Fact]
    public async Task Sending_and_keeping_a_click_is_allocation_free()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.ConnectPairedAsync();
        PenSample gone = ClickResendTests.Finger(PenState.None);
        PenSample[] tap = ClickResendTests.TapHistory;
        PenSample[] hover = [ClickResendTests.Finger(PenState.InRange)];

        Alloc.AssertZero(
            100,
            i => client.SendState(gone, [], (i & 1) == 0 ? tap : hover),
            () => client.DatagramsSealed);
    }

    [Fact]
    public async Task Sending_video_frames_is_allocation_free_for_the_head()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync(logFrames: false);
        await rig.StartAsync();
        byte[] keyframe = MirrorRig.AccessUnit(30_000, 1, true);
        byte[] delta = MirrorRig.AccessUnit(30_000, 2, false);
        PenBridgeServer server = rig.Server;
        long ran = 0;

        Alloc.AssertZero(
            200,
            i =>
            {
                bool key = i % 10 == 0;
                server.SendVideoFrame(key ? keyframe : delta, key, rig.NowCaptureUs); // queued or dropped, never blocking
                ran++;
            },
            () => ran);
        await Wait.Until(() => rig.Sender.FramesSent > 0, "frames went out meanwhile");
    }

    [Fact]
    public async Task Receiving_video_datagrams_is_allocation_free_for_the_phone()
    {
        const int PerPass = 50;
        const int Total = 8 * PerPass; // AssertZero runs 3 warm-up + up to 5 measured passes
        byte[][] captured = new byte[Total][];
        int[] lengths = new int[Total];
        for (int i = 0; i < Total; i++)
        {
            captured[i] = new byte[VideoDatagram.MaxSize];
        }

        int capture = 0;
        int count = 0;
        DatagramFilter filter = d =>
        {
            if (Volatile.Read(ref capture) == 0)
            {
                return true;
            }

            int i = Interlocked.Increment(ref count) - 1;
            if (i < Total)
            {
                d.CopyTo(captured[i]);
                lengths[i] = d.Length;
            }

            return false; // held back: the test hands it to the phone itself
        };
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, videoFilter: filter), logFrames: false);
        await rig.StartAsync();
        Volatile.Write(ref capture, 1);
        for (int i = 0; i < Total; i++)
        {
            await rig.SendAsync(MirrorRig.AccessUnit(500, i, i == 0), i == 0); // one fragment each
        }

        ClientMirror phone = rig.Client.Mirror!;
        byte[] scratch = new byte[VideoDatagram.MaxSize];
        Alloc.AssertZero(
            PerPass,
            i =>
            {
                captured[i].AsSpan(0, lengths[i]).CopyTo(scratch); // opened in place
                phone.Process(scratch.AsSpan(0, lengths[i]));
            },
            () => rig.Client.MirrorCounters.ProbesAccepted);
        Assert.Equal(0, rig.Client.MirrorCounters.AuthFailures);
    }

    [Fact]
    public async Task Tapping_with_the_echo_is_allocation_free()
    {
        await using NetHarness h = FingerTouchesTests.Harness();
        PenBridgeClient client = await h.ConnectPairedAsync(configure: FingerTouchesTests.Phone);
        TouchSample[] down = FingerTouchesTests.Down;
        TouchSample[] up = FingerTouchesTests.Up;
        Alloc.AssertZero(
            200,
            i => client.SendState(null, (i & 1) == 0 ? down : up),
            () => client.DatagramsSealed);
    }
}
