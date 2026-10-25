using PenBridge.Core.Diagnostics;
using PenBridge.Core.Mirror;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Tests.Net;

/// <summary>A frame the phone handed to its "decoder": a copy of the handle's fields and bytes (the handle is disposed at once).</summary>
internal readonly record struct ReceivedFrame(byte Epoch, uint Number, bool Keyframe, uint CaptureTimeUs, byte[] Data);

/// <summary>
/// A mirroring session on loopback: a server offering ScreenMirror with a ready "head" (encoders, source, Streaming)
/// and a paired phone, with every mirroring event of both sides logged.
/// </summary>
internal sealed class MirrorRig : IAsyncDisposable
{
    /// <summary>What the test phone asks for: both codecs, a large decoder, a 120 Hz panel.</summary>
    public static readonly MirrorRequest Request = new(MirrorCodecs.H264 | MirrorCodecs.Hevc, 4096, 4096, 120);

    private MirrorRig(NetHarness harness) => H = harness;

    public NetHarness H { get; }

    public PenBridgeServer Server => H.Server;

    public PenBridgeClient Client { get; private set; } = null!;

    public EventLog<MirrorRequest> Requested { get; } = new();

    public EventLog<int> Stopped { get; } = new();

    public EventLog<MirrorStreamConfig> ServerConfigs { get; } = new();

    public EventLog<int> Keyframes { get; } = new();

    public EventLog<MirrorStreamConfig> ClientConfigs { get; } = new();

    public EventLog<(MirrorState State, MirrorReason Reason)> ClientStates { get; } = new();

    public EventLog<ReceivedFrame> Frames { get; } = new();

    public ServerMirror Mirror => Server.ActiveConnection?.Mirror ?? throw new InvalidOperationException("No mirroring session.");

    public VideoSender Sender => Mirror.Sender ?? throw new InvalidOperationException("No video run.");

    /// <summary>Server options with ScreenMirror on (and whatever <paramref name="more"/> sets).</summary>
    public static PenBridgeServerOptions ServerOptions(
        PenBridgeServerOptions o,
        bool screenMirror = true,
        DatagramFilter? videoFilter = null,
        LatencyRecorder? latency = null,
        bool allowTakeover = false,
        bool remoteSettings = false) => new()
        {
            Identity = o.Identity,
            Registry = o.Registry,
            InputSink = o.InputSink,
            ServerName = o.ServerName,
            BindAddress = o.BindAddress,
            Port = o.Port,
            TargetDisplay = o.TargetDisplay,
            TimeProvider = o.TimeProvider,
            ScreenMirror = screenMirror,
            VideoDatagramFilter = videoFilter,
            LatencyRecorder = latency,
            AllowTakeover = allowTakeover,
            RemoteSettings = remoteSettings,
        };

    /// <summary>A phone with optional input and Probe filters.</summary>
    public static Func<PenBridgeClientOptions, PenBridgeClientOptions> Phone(DatagramFilter? input = null, DatagramFilter? probes = null) => o => new()
    {
        SecretStore = o.SecretStore,
        Surface = o.Surface,
        DeviceName = o.DeviceName,
        DatagramFilter = input ?? o.DatagramFilter,
        TimeProvider = o.TimeProvider,
        VideoDatagramFilter = probes,
    };

    /// <summary>
    /// A connected rig. With <paramref name="ready"/> the head is ready (encoders, source, Streaming) before the phone
    /// connects; <see cref="StartAsync"/> then gets the stream going.
    /// </summary>
    public static async Task<MirrorRig> CreateAsync(
        Func<PenBridgeServerOptions, PenBridgeServerOptions>? server = null,
        Func<PenBridgeClientOptions, PenBridgeClientOptions>? phone = null,
        bool ready = true,
        bool logFrames = true)
    {
        var rig = new MirrorRig(new NetHarness(server ?? (o => ServerOptions(o))));
        PenBridgeServer s = rig.Server;
        s.MirrorRequested += (_, r) => rig.Requested.Add(r);
        s.MirrorStopped += (_, _) => rig.Stopped.Add(0);
        s.MirrorConfigChanged += (_, c) => rig.ServerConfigs.Add(c);
        s.KeyframeRequested += (_, _) => rig.Keyframes.Add(0);
        if (ready)
        {
            rig.MakeHeadReady();
        }

        rig.Client = await rig.H.ConnectPairedAsync(configure: phone, subscribe: c => rig.Subscribe(c, logFrames));
        return rig;
    }

    public void MakeHeadReady()
    {
        Server.SetMirrorEncoders(MirrorCodecs.H264 | MirrorCodecs.Hevc);
        Server.SetMirrorSource(2000, 3000, 60);
        Server.SetMirrorState(MirrorState.Streaming);
    }

    public void Subscribe(PenBridgeClient client, bool logFrames = true)
    {
        client.MirrorConfigReceived += (_, c) => ClientConfigs.Add(c);
        client.MirrorStateChanged += (_, e) => ClientStates.Add((e.State, e.Reason));
        if (!logFrames)
        {
            return; // Core disposes frames nobody takes
        }

        client.VideoFrameReady += (_, f) =>
        {
            Frames.Add(new ReceivedFrame(f.Epoch, f.FrameNumber, f.IsKeyframe, f.CaptureTimeUs, f.Span.ToArray()));
            f.Dispose();
        };
    }

    /// <summary>
    /// Asks for video and waits until the stream is up: the phone has the epoch's MirrorConfig and Streaming, and the
    /// Mac pinned the phone's endpoint from the epoch's Probe and released the epoch. Returns the configuration.
    /// </summary>
    public async Task<MirrorStreamConfig> StartAsync()
    {
        int configs = ClientConfigs.Mark();
        int states = ClientStates.Mark();
        Assert.True(Client.StartMirror(Request));
        MirrorStreamConfig config = (await ClientConfigs.WaitFor(configs, 1, "the phone got a MirrorConfig"))[^1];
        await ClientStates.WaitFor(states, got => got.Contains((MirrorState.Streaming, MirrorReason.None)), "the phone got Streaming");
        await WaitReleasedAsync();
        return config;
    }

    /// <summary>Waits until the current epoch may go out to a pinned endpoint (its Probe arrived).</summary>
    public Task WaitReleasedAsync() =>
        Wait.Until(() => Mirror.Sender is { Target: not null, IsReleased: true }, "the Mac pinned the phone and released the epoch");

    /// <summary>Capture time "now" in host-monotonic µs on the server clock's scale.</summary>
    public long NowCaptureUs => (long)(H.Time.GetTimestamp() / (H.Time.TimestampFrequency / 1e6));

    /// <summary>A fake access unit: an Annex-B start code, a NAL header byte and a pattern of <paramref name="length"/> bytes in total.</summary>
    public static byte[] AccessUnit(int length, int seed, bool keyframe)
    {
        byte[] au = new byte[length];
        for (int i = 0; i < au.Length; i++)
        {
            au[i] = (byte)((i * 31) + seed);
        }

        au[0] = 0;
        au[1] = 0;
        au[2] = 0;
        au[3] = 1;
        au[4] = keyframe ? (byte)0x67 : (byte)0x41; // H.264 SPS / non-IDR slice
        return au;
    }

    /// <summary>Sends one access unit and waits until the send thread finished it (so the next one isn't dropped).</summary>
    public async Task SendAsync(byte[] accessUnit, bool keyframe)
    {
        long sent = Sender.FramesSent;
        Server.SendVideoFrame(accessUnit, keyframe, NowCaptureUs);
        await Wait.Until(() => Sender.FramesSent > sent, "the Mac sent the frame");
    }

    public ValueTask DisposeAsync() => H.DisposeAsync();
}
