using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using PenBridge.Core.Input;
using PenBridge.Core.Net;

namespace PenBridge.Core.Tests.Net;

/// <summary>
/// The server's dedicated input thread (#320, ADR-0038 QoS): UDP receive → decrypt → sink on one thread the head's
/// <see cref="PenBridgeServerOptions.MirrorThreadStarted"/> hook sees as <see cref="MirrorThreadRole.InputReceive"/>,
/// and a shutdown that never hangs on the socket it owns.
/// </summary>
public class InputReceiveThreadTests
{
    /// <summary>The harness's options with a thread hook and, optionally, another sink.</summary>
    internal static PenBridgeServerOptions WithHook(PenBridgeServerOptions o, Action<MirrorThreadRole>? hook, IInputSink? sink = null) => new()
    {
        Identity = o.Identity,
        Registry = o.Registry,
        InputSink = sink ?? o.InputSink,
        ServerName = o.ServerName,
        BindAddress = o.BindAddress,
        Port = o.Port,
        TargetDisplay = o.TargetDisplay,
        TimeProvider = o.TimeProvider,
        MirrorThreadStarted = hook,
    };

    [Fact]
    public async Task Udp_input_reaches_the_sink_on_the_hooked_input_receive_thread()
    {
        var hooked = new ConcurrentQueue<(MirrorThreadRole Role, int ThreadId, bool Pool, string? Name)>();
        var sink = new ThreadRecordingSink();
        await using var h = new NetHarness(o => WithHook(
            o,
            role => hooked.Enqueue((role, Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.Name)),
            sink));
        await Wait.Until(() => !hooked.IsEmpty, "the input thread called the hook");

        PenBridgeClient client = await h.ConnectPairedAsync();
        EndToEndTests.DrawStroke(client);
        await Wait.Until(() => sink.Kinds().Contains(PenEventKind.ProximityLeave), "the stroke went through");

        (MirrorThreadRole role, int threadId, bool pool, string? name) = Assert.Single(hooked); // once per server, not per session
        Assert.Equal(MirrorThreadRole.InputReceive, role);
        Assert.False(pool, "a dedicated thread, not the pool");
        Assert.Equal("PenBridge input receive", name);
        Assert.All(sink.Threads(), id => Assert.Equal(threadId, id));
    }

    [Fact]
    public async Task A_throwing_hook_leaves_the_input_thread_running()
    {
        int calls = 0;
        var sink = new ThreadRecordingSink();
        await using var h = new NetHarness(o => WithHook(
            o,
            _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("head priority API failed");
            },
            sink));

        PenBridgeClient client = await h.ConnectPairedAsync();
        EndToEndTests.DrawStroke(client);
        await Wait.Until(() => sink.Kinds().Contains(PenEventKind.ProximityLeave), "input still flows");
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Disposing_an_idle_server_ends_the_input_thread_and_frees_its_port()
    {
        await using var h = new NetHarness();
        int port = h.Port;
        Task exited = h.Server.InputThreadExited;
        Assert.False(exited.IsCompleted);

        await h.Server.DisposeAsync().AsTask().Bounded(); // a hang fails here
        Assert.True(exited.IsCompletedSuccessfully, "dispose waits for the input thread");
        AssertUdpPortFree(port);
    }

    [Fact]
    public async Task Disposing_while_input_streams_ends_the_input_thread()
    {
        await using var h = new NetHarness();
        int port = h.Port;
        PenBridgeClient client = await h.ConnectPairedAsync();
        EndToEndTests.DrawStroke(client);
        await EndToEndTests.AssertStrokeAsync(h.Sink);

        // Keep datagrams coming while the server shuts down: the thread must still see its stop flag.
        using var stop = new CancellationTokenSource();
        Task pump = Task.Run(() =>
        {
            for (ushort i = 0; !stop.IsCancellationRequested; i++)
            {
                client.SendState(Pens.Hover((ushort)(1000 + (i % 2000)), 1000), []);
                Thread.Yield();
            }
        }, TestContext.Current.CancellationToken);
        await Wait.Until(() => h.Server.InputThreadDatagrams > 20, "datagrams flow");

        await h.Server.DisposeAsync().AsTask().Bounded();
        Assert.True(h.Server.InputThreadExited.IsCompletedSuccessfully);
        await stop.CancelAsync();
        await pump.Bounded();
        AssertUdpPortFree(port);
    }

    [Fact]
    public async Task The_input_socket_receives_with_the_stop_backstop()
    {
        // A lost wake datagram must not leave dispose waiting forever: the blocking receive times out and rechecks.
        await using var h = new NetHarness();
        Assert.Equal(VideoSockets.ReceiveTimeoutMs, h.Server.InputSocketReceiveTimeoutMs);
    }

    private static void AssertUdpPortFree(int port)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, port)); // throws AddressAlreadyInUse while the server's socket lives
    }

    /// <summary>Records each pen event's kind and the managed thread that delivered it.</summary>
    private sealed class ThreadRecordingSink : IInputSink
    {
        private readonly Lock _lock = new();
        private readonly List<(PenEventKind Kind, int ThreadId)> _events = [];

        public void OnPenEvent(in PenEvent e)
        {
            lock (_lock)
            {
                _events.Add((e.Kind, Environment.CurrentManagedThreadId));
            }
        }

        public void OnGestureEvent(in GestureEvent e)
        {
        }

        public PenEventKind[] Kinds()
        {
            lock (_lock)
            {
                return [.. _events.Select(x => x.Kind)];
            }
        }

        public int[] Threads()
        {
            lock (_lock)
            {
                return [.. _events.Select(x => x.ThreadId)];
            }
        }
    }
}
