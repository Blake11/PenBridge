using PenBridge.Core.Net;
using PenBridge.Core.Protocol;

#pragma warning disable xUnit1051 // Network waits are bounded by Wait.Limit instead of the test token.

namespace PenBridge.Core.Tests.Net;

/// <summary>#200: the host reads the connected phone's surface size from the server.</summary>
public class PhoneSurfaceTests
{
    [Fact]
    public async Task Surface_is_null_before_a_session_then_set_from_the_SurfaceInfo()
    {
        await using var h = new NetHarness();
        Assert.Null(h.Server.PhoneSurface);
        int raised = 0;
        h.Server.PhoneSurfaceChanged += (_, _) => Interlocked.Increment(ref raised);

        await h.ConnectPairedAsync();

        await Wait.Until(() => h.Server.PhoneSurface is not null, "surface published");
        Assert.Equal(new PhoneSurfaceSize(1440, 3088), h.Server.PhoneSurface);
        Assert.Equal(1440d / 3088d, h.Server.PhoneSurface!.Value.AspectRatio);
        await Wait.Until(() => Volatile.Read(ref raised) == 1, "surface event raised");
    }

    [Fact]
    public async Task A_second_SurfaceInfo_updates_the_surface()
    {
        await using var h = new NetHarness();
        PenBridgeClient client = await h.ConnectPairedAsync();
        await Wait.Until(() => h.Server.PhoneSurface is not null, "surface published");

        await client.UpdateSurfaceAsync(new SurfaceInfoMessage(3088, 1440, 500f, 500f, SurfaceOrientation.Landscape, SurfaceCapabilities.None)).Bounded();

        await Wait.Until(() => h.Server.PhoneSurface == new PhoneSurfaceSize(3088, 1440), "surface updated");
    }

    [Fact]
    public async Task Surface_is_cleared_with_an_event_when_the_session_ends()
    {
        await using var h = new NetHarness();
        await h.ConnectPairedAsync();
        await Wait.Until(() => h.Server.PhoneSurface is not null, "surface published");
        int raised = 0;
        h.Server.PhoneSurfaceChanged += (_, _) => Interlocked.Increment(ref raised);

        await h.Server.DisconnectSessionAsync().Bounded();

        await Wait.Until(() => h.Server.PhoneSurface is null, "surface cleared");
        await Wait.Until(() => Volatile.Read(ref raised) == 1, "cleared event raised");
    }

    [Fact]
    public async Task Handlers_run_outside_the_server_lock_so_they_may_call_back()
    {
        await using var h = new NetHarness();
        var seen = new TaskCompletionSource<PhoneSurfaceSize?>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Server.PhoneSurfaceChanged += (_, _) =>
        {
            // Takes the server gate from another thread while the raising thread waits: deadlocks if raised under it.
            PhoneSurfaceSize? value = Task.Run(() => h.Server.PhoneSurface).Wait(Wait.Limit) ? h.Server.PhoneSurface : null;
            _ = h.Server.GetStats();
            seen.TrySetResult(value);
        };

        await h.ConnectPairedAsync();

        PhoneSurfaceSize? observed = await seen.Task.Bounded();
        Assert.Equal(new PhoneSurfaceSize(1440, 3088), observed);
    }
}
