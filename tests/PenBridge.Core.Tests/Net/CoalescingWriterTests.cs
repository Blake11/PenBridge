using System.Collections.Concurrent;
using PenBridge.Core.Net;
using PenBridge.Core.Tests.Input;

namespace PenBridge.Core.Tests.Net;

/// <summary>The control-path bounds behind the settings sync (ADR-0030): coalesced pushes and the update rate cap.</summary>
public class CoalescingWriterTests
{
    [Fact]
    public async Task Requests_before_enable_are_held_and_drained_once()
    {
        var drains = new ConcurrentQueue<int>();
        var writer = new CoalescingWriter(bits =>
        {
            drains.Enqueue(bits);
            return Task.FromResult(true);
        });

        Assert.Same(Task.CompletedTask, writer.Request(2));
        Assert.Same(Task.CompletedTask, writer.Request(4));
        Assert.Empty(drains);

        await writer.Enable().Bounded();

        Assert.Equal([6], drains);
        Assert.Equal(0, writer.Pending);
    }

    [Fact]
    public async Task A_flood_while_writing_costs_one_more_drain_with_the_union_of_bits()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drains = new ConcurrentQueue<int>();
        var writer = new CoalescingWriter(bits =>
        {
            drains.Enqueue(bits);
            started.TrySetResult();
            return drains.Count == 1 ? release.Task : Task.FromResult(true);
        });
        _ = writer.Enable();

        Task first = writer.Request(1);
        await started.Task.Bounded(); // the first drain is in flight and blocked
        var tasks = new HashSet<Task>();
        for (int i = 0; i < 10_000; i++)
        {
            tasks.Add(writer.Request(i % 2 == 0 ? 2 : 4));
        }

        Assert.Equal(6, writer.Pending); // no queue: one mask, however many requests
        Assert.Equal([first], tasks); // no waiter list: every request shares the running loop's task
        release.SetResult(true);
        await first.Bounded();

        Assert.Equal([1, 6], drains);
        Assert.Equal(0, writer.Pending);
    }

    [Fact]
    public async Task Drains_run_one_at_a_time_and_end_with_the_last_request()
    {
        int running = 0, overlaps = 0, last = 0;
        var writer = new CoalescingWriter(async bits =>
        {
            if (Interlocked.Increment(ref running) > 1)
            {
                Interlocked.Increment(ref overlaps);
            }

            await Task.Yield();
            Volatile.Write(ref last, bits);
            Interlocked.Decrement(ref running);
            return true;
        });
        _ = writer.Enable();

        Task[] requests = [.. Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 2_000; i++)
            {
                _ = writer.Request(1);
            }
        }))];
        await Task.WhenAll(requests).Bounded();
        await writer.Request(8).Bounded();

        Assert.Equal(0, overlaps);
        Assert.NotEqual(0, Volatile.Read(ref last) & 8);
        Assert.Equal(0, writer.Pending);
    }

    [Fact]
    public async Task A_failed_drain_stops_the_writer()
    {
        int drains = 0;
        var writer = new CoalescingWriter(_ => Task.FromResult(Interlocked.Increment(ref drains) > 1));
        await writer.Enable().Bounded();
        await writer.Request(1).Bounded();

        await writer.Request(2).Bounded();

        Assert.Equal(1, drains);
        Assert.Equal(0, writer.Pending);
    }

    [Fact]
    public async Task A_throwing_drain_stops_the_writer_instead_of_wedging_it_and_reports_the_fault()
    {
        int drains = 0;
        var bug = new InvalidOperationException("bug");
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var writer = new CoalescingWriter(
            _ =>
            {
                Interlocked.Increment(ref drains);
                throw bug;
            },
            faults.Enqueue);
        _ = writer.Enable();

        await writer.Request(1).Bounded();
        await writer.Request(1).Bounded();

        Assert.Equal(1, drains);
        Assert.Same(bug, Assert.Single(faults));
    }

    [Fact]
    public async Task A_failed_drain_is_not_a_fault()
    {
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var writer = new CoalescingWriter(_ => Task.FromResult(false), faults.Enqueue);
        _ = writer.Enable();

        await writer.Request(1).Bounded();

        Assert.Empty(faults);
    }

    [Fact]
    public async Task Stop_drops_pending_bits()
    {
        int drains = 0;
        var writer = new CoalescingWriter(_ => Task.FromResult(Interlocked.Increment(ref drains) > 0));
        _ = writer.Request(1);

        writer.Stop();
        await writer.Enable().Bounded();
        await writer.Request(2).Bounded();

        Assert.Equal(0, drains);
        Assert.Equal(0, writer.Pending);
    }

    [Fact]
    public void Token_bucket_allows_a_burst_then_the_rate()
    {
        var time = new FakeTime();
        var bucket = new TokenBucket(time, perSecond: 20, capacity: 20);

        Assert.Equal(20, Take(bucket, 100));
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(1, Take(bucket, 100));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(20, Take(bucket, 100));
        time.Advance(TimeSpan.FromHours(1)); // refill is capped at the burst
        Assert.Equal(20, Take(bucket, 100));
    }

    private static int Take(TokenBucket bucket, int attempts)
    {
        int taken = 0;
        for (int i = 0; i < attempts; i++)
        {
            taken += bucket.TryTake() ? 1 : 0;
        }

        return taken;
    }
}
