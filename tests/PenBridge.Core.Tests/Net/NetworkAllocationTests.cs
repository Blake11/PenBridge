using PenBridge.Core.Input;
using PenBridge.Core.Net;
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
