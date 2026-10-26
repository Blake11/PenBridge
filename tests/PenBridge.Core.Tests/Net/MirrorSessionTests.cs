using System.Diagnostics;
using PenBridge.Core.Diagnostics;
using PenBridge.Core.Input;
using PenBridge.Core.Mirror;
using PenBridge.Core.Net;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using PenBridge.Core.Settings;

namespace PenBridge.Core.Tests.Net;

/// <summary>Mirroring session rules: capability gating, state composition, teardown, fallback, the guard, rate limits.</summary>
public class MirrorSessionTests
{
    public static TheoryData<int> PhoneMessages => [0x50, 0x51, 0x54, 0x55];

    [Theory]
    [MemberData(nameof(PhoneMessages))]
    public async Task Mirroring_messages_without_the_capability_are_a_protocol_error(int type)
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, screenMirror: false));
        Assert.Equal(ProtocolCapabilities.None, rig.Client.NegotiatedCapabilities & ProtocolCapabilities.ScreenMirror);
        Assert.False(rig.Client.StartMirror(MirrorRig.Request));
        var ended = new TaskCompletionSource<ClientDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Client.Disconnected += (_, e) => ended.TrySetResult(e);

        await SendAsync(rig.Client, (ControlMessageType)type);

        Assert.Equal(ProtocolErrorCode.UnexpectedMessage, (await ended.Task.Bounded()).ErrorCode);
        Assert.Empty(rig.Requested.All());
    }

    [Theory]
    [InlineData(0x52)]
    [InlineData(0x53)]
    public async Task A_phone_sending_the_macs_mirroring_messages_is_a_protocol_error(int type)
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        var ended = new TaskCompletionSource<ClientDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Client.Disconnected += (_, e) => ended.TrySetResult(e);

        if (type == 0x52)
        {
            await rig.Client.SendRawControlAsync(new MirrorConfigMessage(1, MirrorCodec.H264, 100, 100, 60, 1000, 5000));
        }
        else
        {
            await rig.Client.SendRawControlAsync(new MirrorStateMessage(MirrorState.Streaming, MirrorReason.None));
        }

        Assert.Equal(ProtocolErrorCode.UnexpectedMessage, (await ended.Task.Bounded()).ErrorCode);
    }

    [Fact]
    public async Task A_mirroring_message_before_session_start_is_a_protocol_error()
    {
        await using var h = new NetHarness(o => MirrorRig.ServerOptions(o));
        await using RawClient raw = await RawClient.ConnectTlsAsync(h.Port);
        await raw.HelloAsync(Guid.NewGuid(), capabilities: ProtocolCapabilities.ScreenMirror);
        await raw.WriteAsync(MirrorRig.Request.ToMessage(5000));

        ReceivedControlFrame? frame = await raw.ReadAsync();

        Assert.Equal(ProtocolErrorCode.UnexpectedMessage, frame!.Value.Read<ErrorMessage>().Code);
        Assert.Null(await raw.ReadAsync());
    }

    [Fact]
    public async Task The_phone_sees_starting_until_the_head_is_ready_and_no_common_codec_as_unavailable()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync(ready: false);
        int states = rig.ClientStates.Mark();
        Assert.True(rig.Client.StartMirror(MirrorRig.Request));
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Starting, MirrorReason.None)), "Starting while the head decides");
        await rig.Requested.WaitFor(0, 1, "the head was asked to capture");
        Assert.Equal(MirrorRig.Request, rig.Requested.All()[0]);

        rig.Server.SetMirrorState(MirrorState.Streaming);
        rig.Server.SetMirrorSource(2000, 3000, 60);
        await rig.H.SettleAsync();
        Assert.Equal((MirrorState.Starting, MirrorReason.None), rig.Server.MirrorStatus); // encoders still unknown
        Assert.Empty(rig.ClientConfigs.All());

        rig.Server.SetMirrorEncoders(MirrorCodecs.None);
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Unavailable, MirrorReason.NoCommonCodec)), "no common codec");

        rig.Server.SetMirrorEncoders(MirrorCodecs.H264);
        MirrorStreamConfig config = (await rig.ClientConfigs.WaitFor(0, 1, "a stream once a codec fits"))[0];
        Assert.Equal(MirrorCodec.H264, config.Codec);
        await rig.ClientStates.WaitFor(states, got => got[^1] == (MirrorState.Streaming, MirrorReason.None), "Streaming after the config");
    }

    [Fact]
    public async Task The_heads_pause_reaches_the_phone_and_resuming_starts_a_new_epoch()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        MirrorStreamConfig first = await rig.StartAsync();
        int states = rig.ClientStates.Mark();
        rig.Server.SetMirrorState(MirrorState.Paused, MirrorReason.PenBridgePaused);
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Paused, MirrorReason.PenBridgePaused)), "Paused");
        rig.Server.SendVideoFrame(MirrorRig.AccessUnit(3000, 1, true), true, rig.NowCaptureUs);
        await rig.H.SettleAsync();
        Assert.Equal(0, rig.Sender.FramesSent);
        Assert.Empty(rig.Frames.All());

        int configs = rig.ClientConfigs.Mark();
        rig.Server.SetMirrorState(MirrorState.Streaming);
        MirrorStreamConfig second = (await rig.ClientConfigs.WaitFor(configs, 1, "a new epoch on resume"))[0];
        Assert.Equal(first with { Epoch = second.Epoch }, second);
        Assert.NotEqual(first.Epoch, second.Epoch);
    }

    [Fact]
    public async Task Settings_from_the_snapshot_gate_and_shape_the_stream()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, remoteSettings: true));
        var values = new MacSettingValues();
        values.ApplyUpdate([SettingEntry.Bool(MacSettingKey.MirrorEnabled, true)], []);
        await rig.Server.SendSettingsSnapshotAsync(values.ToEntries());
        MirrorStreamConfig balanced = await rig.StartAsync();

        int states = rig.ClientStates.Mark();
        values.ApplyUpdate([SettingEntry.Bool(MacSettingKey.MirrorEnabled, false)], []);
        await rig.Server.SendSettingsSnapshotAsync(values.ToEntries());
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Off, MirrorReason.SettingOff)), "Off/SettingOff");

        int configs = rig.ClientConfigs.Mark();
        values.ApplyUpdate(
            [
                SettingEntry.Bool(MacSettingKey.MirrorEnabled, true),
                SettingEntry.Int(MacSettingKey.MirrorQuality, (int)MirrorQuality.Low),
                SettingEntry.Int(MacSettingKey.MirrorFps, 30),
            ],
            []);
        await rig.Server.SendSettingsSnapshotAsync(values.ToEntries());
        MirrorStreamConfig low = (await rig.ClientConfigs.WaitFor(configs, 1, "the Low, 30 fps stream"))[0];
        Assert.Equal(30, low.Fps);
        Assert.InRange(low.BitrateKbps, 1, balanced.BitrateKbps - 1);
    }

    [Fact]
    public async Task Dropping_hevc_from_the_encoders_falls_back_to_h264_in_a_new_epoch()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        MirrorStreamConfig hevc = await rig.StartAsync();
        Assert.Equal(MirrorCodec.Hevc, hevc.Codec);
        int configs = rig.ClientConfigs.Mark();
        rig.Server.SetMirrorEncoders(MirrorCodecs.H264);
        MirrorStreamConfig h264 = (await rig.ClientConfigs.WaitFor(configs, 1, "the H.264 epoch"))[0];
        Assert.Equal(MirrorCodec.H264, h264.Codec);
        Assert.NotEqual(hevc.Epoch, h264.Epoch);
        Assert.InRange(h264.BitrateKbps, hevc.BitrateKbps + 1, int.MaxValue); // H.264 × 1.4
    }

    [Fact]
    public void Throttled_and_inconsistent_head_states_are_rejected()
    {
        var options = new PenBridgeServerOptions
        {
            Identity = NetHarness.SharedServerIdentity,
            Registry = new PairedDeviceRegistry(new InMemorySecretStore()),
            InputSink = new ConcurrentSink(),
        };
        var server = new PenBridgeServer(options);
        Assert.Equal("state", Assert.Throws<ArgumentOutOfRangeException>(() => server.SetMirrorState(MirrorState.Throttled)).ParamName);
        Assert.Equal("state", Assert.Throws<ArgumentOutOfRangeException>(() => server.SetMirrorState(MirrorState.Streaming, MirrorReason.CaptureFailed)).ParamName);
        Assert.Equal("refreshHz", Assert.Throws<ArgumentOutOfRangeException>(() => server.SetMirrorSource(1, 1, 0)).ParamName);
    }

    [Fact]
    public async Task Stop_mirror_stops_the_run_and_a_new_start_brings_a_new_epoch()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        MirrorStreamConfig first = await rig.StartAsync();
        VideoSender sender = rig.Sender;
        int states = rig.ClientStates.Mark();

        rig.Client.StopMirror();

        await rig.Stopped.WaitFor(0, 1, "the head was told to stop");
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Off, MirrorReason.PhoneStopped)), "Off/PhoneStopped");
        await Wait.Until(() => sender.IsStopped && rig.Mirror.Sender is null, "the Mac's run stopped");
        Assert.Equal(0, rig.Client.Mirror!.Port);
        rig.Server.SendVideoFrame(MirrorRig.AccessUnit(3000, 1, true), true, rig.NowCaptureUs); // dropped, no run

        MirrorStreamConfig second = await rig.StartAsync();
        Assert.NotEqual(first.Epoch, second.Epoch);
        Assert.Equal(2, rig.Requested.Count);
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 2, true), true);
        Assert.Equal(second.Epoch, (await rig.Frames.WaitFor(0, 1, "video on the new run"))[0].Epoch);
    }

    [Fact]
    public async Task Bye_stops_the_video()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        await rig.StartAsync();
        VideoSender sender = rig.Sender;

        await rig.Client.DisconnectAsync().Bounded();

        await rig.Stopped.WaitFor(0, 1, "the head was told to stop");
        await Wait.Until(() => sender.IsStopped, "the run stopped");
    }

    [Fact]
    public async Task A_takeover_stops_the_old_sessions_video_before_the_new_one_asks()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, allowTakeover: true));
        await rig.StartAsync();
        VideoSender sender = rig.Sender;
        var events = new EventLog<string>();
        rig.Server.MirrorStopped += (_, _) => events.Add("stopped");
        rig.Server.MirrorRequested += (_, _) => events.Add("requested");

        PenBridgeClient other = await rig.H.ConnectPairedAsync();
        await rig.Stopped.WaitFor(0, 1, "the old session's video stopped");
        await Wait.Until(() => sender.IsStopped, "the old run stopped");
        Assert.True(other.StartMirror(MirrorRig.Request));
        await events.WaitFor(0, 2, "stop, then the new request");
        Assert.Equal(["stopped", "requested"], events.All());
    }

    [Fact]
    public async Task Tls_fallback_makes_mirroring_unavailable_until_udp_is_back()
    {
        bool drop = false;
        await using MirrorRig rig = await MirrorRig.CreateAsync(phone: MirrorRig.Phone(input: _ => !Volatile.Read(ref drop)));
        await rig.StartAsync();
        VideoSender sender = rig.Sender;
        int states = rig.ClientStates.Mark();

        Volatile.Write(ref drop, true);
        Assert.True(rig.Client.SendState(Pens.Hover(100, 100), []));
        await Wait.Step(rig.H.Time, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500));
        Assert.Equal(InputTransport.Tls, rig.Client.Transport);
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Unavailable, MirrorReason.TlsFallback)), "Unavailable/TlsFallback");
        await rig.Stopped.WaitFor(0, 1, "the head was told to stop");
        await Wait.Until(() => sender.IsStopped, "the run stopped in fallback");

        // UDP works again: the next probe datagram is acked, the phone asks for video again.
        int configs = rig.ClientConfigs.Mark();
        Volatile.Write(ref drop, false);
        await Wait.Step(rig.H.Time, TimeSpan.FromSeconds(11), TimeSpan.FromMilliseconds(500));
        await Wait.Until(() => rig.Client.Transport == InputTransport.Udp, "the phone is back on UDP");

        // A TLS keepalive read after the probe may have put the server back in fallback (restore skipped); the next UDP
        // datagram restores it again.
        Assert.True(rig.Client.SendState(Pens.Hover(120, 100), []));
        await rig.ClientConfigs.WaitFor(configs, 1, "a new epoch once UDP is back");
        Assert.Equal(2, rig.Requested.Count);
    }

    [Fact]
    public async Task A_udp_restore_posted_before_a_tls_frame_does_not_resume_video_in_fallback()
    {
        bool drop = false;
        await using MirrorRig rig = await MirrorRig.CreateAsync(phone: MirrorRig.Phone(input: _ => !Volatile.Read(ref drop)));
        await rig.StartAsync();
        ServerMirror mirror = rig.Mirror;
        SessionInput input = rig.Server.ActiveInput!;
        VideoSender sender = rig.Sender;
        Volatile.Write(ref drop, true);
        Assert.True(rig.Client.SendState(Pens.Hover(100, 100), []));
        await Wait.Step(rig.H.Time, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500));
        Assert.True(rig.Client.SendState(Pens.Hover(110, 100), [])); // over TLS now
        await Wait.Until(() => input.Transport == InputTransport.Tls && mirror.InTlsFallback, "the server is in fallback");
        await Wait.Until(() => sender.IsStopped, "the run stopped in fallback");
        await rig.H.SettleAsync();
        int requested = rig.Requested.Count;
        int configs = rig.ClientConfigs.Mark();

        // A UDP restore posted earlier runs only now, after a TLS frame flipped the input back.
        mirror.RestoreUdp();
        await rig.H.SettleAsync();
        Assert.True(mirror.InTlsFallback);
        Assert.Equal(requested, rig.Requested.Count);
        Assert.Empty(rig.ClientConfigs.Since(configs));
        Assert.True(sender.IsStopped);
    }

    [Fact]
    public async Task A_video_socket_that_cannot_open_makes_mirroring_unavailable_and_input_goes_on()
    {
        // The head's hook disposes the socket, so reading its port fails: a stand-in for a bind failure.
        await using MirrorRig rig = await MirrorRig.CreateAsync(phone: MirrorRig.Phone(videoSocket: s => s.Dispose()));
        int states = rig.ClientStates.Mark();
        Assert.True(rig.Client.StartMirror(MirrorRig.Request));
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Unavailable, MirrorReason.None)), "Unavailable/None");
        await rig.H.SettleAsync();
        Assert.Equal(0, rig.Requested.Count); // no MirrorStart without a port
        Assert.Equal(0, rig.Client.Mirror!.Port);

        SessionInput input = rig.Server.ActiveInput!;
        long accepted = input.Accepted;
        Assert.True(rig.Client.SendState(Pens.Hover(100, 100), []));
        await Wait.Until(() => input.Accepted > accepted, "input still flows");
        Assert.True(rig.Client.IsConnected);
    }

    [Fact]
    public async Task Udp_coming_back_opens_the_video_socket_off_the_read_loop_and_a_failure_keeps_the_session()
    {
        bool drop = false;
        int hooks = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        void Hook(System.Net.Sockets.Socket socket)
        {
            if (Interlocked.Increment(ref hooks) == 1)
            {
                return; // the first run opens normally
            }

            entered.Set();
            release.Wait(TestContext.Current.CancellationToken); // a slow head hook...
            socket.Dispose(); // ...that then breaks the socket
        }

        await using MirrorRig rig = await MirrorRig.CreateAsync(phone: MirrorRig.Phone(input: _ => !Volatile.Read(ref drop), videoSocket: Hook));
        await rig.StartAsync();
        int states = rig.ClientStates.Mark();
        Volatile.Write(ref drop, true);
        Assert.True(rig.Client.SendState(Pens.Hover(100, 100), []));
        await Wait.Step(rig.H.Time, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500));
        Assert.Equal(InputTransport.Tls, rig.Client.Transport);
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Unavailable, MirrorReason.TlsFallback)), "Unavailable/TlsFallback");

        // UDP is back: the restore path opens the run, and the hook blocks while the session keeps going.
        Volatile.Write(ref drop, false);
        await Wait.Step(rig.H.Time, TimeSpan.FromSeconds(11), TimeSpan.FromMilliseconds(500));
        await Wait.Until(() => rig.Client.Transport == InputTransport.Udp, "the probe's ack restored UDP");
        Assert.True(entered.Wait(Wait.Limit, TestContext.Current.CancellationToken), "the restore path opened a socket");
        SessionInput input = rig.Server.ActiveInput!;
        long accepted = input.Accepted;
        Assert.True(rig.Client.SendState(Pens.Hover(200, 200), [])); // the send lock is free
        await Wait.Until(() => input.Accepted > accepted, "input flows while the hook blocks");
        await rig.H.SettleAsync(); // the read loop is free too

        states = rig.ClientStates.Mark();
        release.Set();
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Unavailable, MirrorReason.None)), "Unavailable/None");
        accepted = input.Accepted;
        Assert.True(rig.Client.SendState(Pens.Hover(300, 300), []));
        await Wait.Until(() => input.Accepted > accepted, "input still flows after the failure");
        await rig.H.SettleAsync();
        Assert.True(rig.Client.IsConnected);
        Assert.Equal(1, rig.Requested.Count); // the failed run never asked the Mac
    }

    [Fact]
    public async Task An_input_loss_during_contact_wakes_the_guard_and_steps_the_video_down()
    {
        int dropNext = 0;
        await using MirrorRig rig = await MirrorRig.CreateAsync(phone: MirrorRig.Phone(input: _ => Interlocked.Exchange(ref dropNext, 0) == 0));
        MirrorStreamConfig full = await rig.StartAsync();
        SessionInput input = rig.Server.ActiveInput!;
        for (int i = 0; i < 3; i++)
        {
            long accepted = input.Accepted;
            rig.Client.SendState(Pens.Contact((ushort)(1000 + i), 1000), []);
            await Wait.Until(() => input.Accepted > accepted, "a contact frame arrived");
        }

        Volatile.Write(ref dropNext, 1);
        rig.Client.SendState(Pens.Contact(2000, 1000), []); // lost
        long before = input.Accepted;
        rig.Client.SendState(Pens.Contact(2001, 1000), []); // a sequence gap in contact
        await Wait.Until(() => input.Accepted > before, "the frame after the gap arrived");
        Assert.True(rig.Mirror.Guard.IsDegraded);

        // The wake only fires the guard's timer (due now); fake time runs it.
        int configs = rig.ClientConfigs.Mark();
        rig.H.Time.Advance(TimeSpan.Zero);
        MirrorStreamConfig stepped = (await rig.ClientConfigs.WaitFor(configs, 1, "the guard stepped the bitrate down"))[0];
        Assert.Equal(full.Fps, stepped.Fps);
        Assert.InRange(stepped.BitrateKbps, 1, full.BitrateKbps - 1);
        Assert.NotEqual(full.Epoch, stepped.Epoch);
    }

    [Fact]
    public async Task Keyframe_requests_are_folded_to_one_per_250_ms_and_old_epochs_are_ignored()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        MirrorStreamConfig config = await rig.StartAsync();
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 1, true), true); // the phone stops waiting for a keyframe
        await rig.Frames.WaitFor(0, 1, "the keyframe");
        int keyframes = rig.Keyframes.Mark();

        for (int i = 0; i < 3; i++)
        {
            await rig.Client.SendRawControlAsync(new KeyframeRequestMessage(config.Epoch, 1));
        }

        await rig.Client.SendRawControlAsync(new KeyframeRequestMessage(unchecked((byte)(config.Epoch - 1)), 1));
        await rig.H.SettleAsync(); // all read before fake time moves (a request read mid-advance would arm late)
        await rig.Keyframes.WaitFor(keyframes, 1, "the first request is honoured at once");
        rig.H.Time.Advance(ServerMirror.KeyframeInterval - TimeSpan.FromMilliseconds(1));
        await rig.H.SettleAsync();
        Assert.Single(rig.Keyframes.Since(keyframes));

        rig.H.Time.Advance(TimeSpan.FromMilliseconds(1));
        await rig.Keyframes.WaitFor(keyframes, 2, "the folded requests, once, after the window");
        rig.H.Time.Advance(ServerMirror.KeyframeInterval);
        await rig.Client.SendRawControlAsync(new KeyframeRequestMessage(unchecked((byte)(config.Epoch - 1)), 1));
        await rig.H.SettleAsync();
        Assert.Equal(2, rig.Keyframes.Since(keyframes).Length);
    }

    [Fact]
    public async Task Mirror_reports_beyond_four_per_second_are_dropped()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        MirrorStreamConfig config = await rig.StartAsync();
        for (int i = 0; i < 10; i++)
        {
            await rig.Client.SendRawControlAsync(new MirrorReportMessage(config.Epoch, 0, 0, 0, 0, 0));
        }

        await rig.H.SettleAsync();
        Assert.Equal(10 - ServerMirror.ReportsPerSecond, rig.Mirror.ReportsDropped);
    }

    [Fact]
    public async Task The_mac_pings_faster_while_streaming()
    {
        var latency = new LatencyRecorder(4096);
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, latency: latency));
        await Wait.Step(rig.H.Time, TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(250));
        await Wait.Until(() => CountRtts(latency), n => n == 2, "two round trips at the idle 2 s interval");

        await rig.StartAsync();
        int start = CountRtts(latency);
        await Wait.Step(rig.H.Time, TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(250));
        await Wait.Until(() => CountRtts(latency) - start, n => n >= 8, "a round trip every 500 ms while streaming");
        Assert.InRange(CountRtts(latency) - start, 8, 9);
        Assert.NotNull(rig.Mirror.Guard.RttBaseline);

        rig.Client.StopMirror();
        await rig.Stopped.WaitFor(0, 1, "stopped");
        await rig.H.SettleAsync();
        int stopped = CountRtts(latency);
        await Wait.Step(rig.H.Time, TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(250));
        await Wait.Until(() => CountRtts(latency) - stopped, n => n >= 2, "back to the 2 s interval");
        Assert.InRange(CountRtts(latency) - stopped, 2, 3);

        static int CountRtts(LatencyRecorder r) => r.Snapshot().Count(x => x.Kind == LatencyRecordKind.ServerRtt);
    }

    [Fact]
    public async Task A_rendered_frame_becomes_a_video_latency_row()
    {
        var latency = new LatencyRecorder(4096);
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, latency: latency));
        await rig.StartAsync();
        long captured = rig.H.Time.GetTimestamp();
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 1, true), true);
        await rig.Frames.WaitFor(0, 1, "the frame");
        ulong rendered = rig.Client.NowUs + 1234;
        rig.Client.ReportFrameRendered(1, rendered);

        rig.H.Time.Advance(ClientMirror.ReportInterval);
        LatencyRecord row = (await Wait.Until(
            () => latency.Snapshot().Where(r => r.Kind == LatencyRecordKind.Video).ToArray(),
            got => got.Length == 1,
            "the report produced a video row"))[0];
        Assert.Equal(rig.Server.ActiveInput!.SessionId, row.SessionId);
        Assert.Equal(rendered, row.ClientUs);
        Assert.Equal(captured, row.Server);
        Assert.Equal(rig.H.Time.GetTimestamp(), row.ServerEnd);
    }

    [Fact]
    public async Task A_render_of_an_older_epoch_never_meets_the_new_epochs_capture_times()
    {
        var latency = new LatencyRecorder(4096);
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, latency: latency));
        MirrorStreamConfig first = await rig.StartAsync();
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 1, true), true);
        await rig.SendAsync(MirrorRig.AccessUnit(800, 2, false), false);
        await rig.Frames.WaitFor(0, 2, "the first epoch's frames");
        rig.Client.ReportFrameRendered(first.Epoch, 2, rig.Client.NowUs);

        // A new epoch (a guard step or a new source) restarts the numbers before the next report.
        int configs = rig.ClientConfigs.Mark();
        rig.Server.SetMirrorSource(1000, 1500, 60);
        MirrorStreamConfig second = (await rig.ClientConfigs.WaitFor(configs, 1, "the second epoch"))[0];
        await rig.WaitReleasedAsync();
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 3, true), true);
        await rig.SendAsync(MirrorRig.AccessUnit(800, 4, false), false);
        await rig.Frames.WaitFor(2, 2, "the second epoch's frames 1 and 2");
        rig.Client.ReportFrameRendered(5, rig.Client.NowUs); // current epoch, but not handed out yet
        rig.Client.ReportFrameRendered(first.Epoch, 2, rig.Client.NowUs); // late, from the old epoch (the latest call)

        long reports = rig.Client.MirrorStats.ReportsSent;
        rig.H.Time.Advance(ClientMirror.ReportInterval);
        await Wait.Until(() => rig.Client.MirrorStats.ReportsSent > reports, "a MirrorReport");
        await rig.H.SettleAsync();
        Assert.DoesNotContain(latency.Snapshot(), r => r.Kind == LatencyRecordKind.Video);

        // A render of this epoch counts, also for a number the old epoch already reported.
        ulong rendered = rig.Client.NowUs;
        rig.Client.ReportFrameRendered(second.Epoch, 2, rendered);
        rig.H.Time.Advance(ClientMirror.ReportInterval);
        LatencyRecord row = (await Wait.Until(
            () => latency.Snapshot().Where(r => r.Kind == LatencyRecordKind.Video).ToArray(),
            got => got.Length == 1,
            "the current epoch's render produced a row"))[0];
        Assert.Equal(rendered, row.ClientUs);
    }

    [Fact]
    public async Task Video_streams_while_input_flows_without_reordering_or_loss()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        await rig.StartAsync();
        SessionInput input = rig.Server.ActiveInput!;
        using var done = new CancellationTokenSource();
        Task video = Task.Run(async () =>
        {
            for (int n = 0; !done.IsCancellationRequested; n++)
            {
                rig.Server.SendVideoFrame(MirrorRig.AccessUnit(n % 30 == 0 ? 60_000 : 20_000, n, n % 30 == 0), n % 30 == 0, rig.NowCaptureUs);
                await Task.Delay(8, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        const int Frames = 300;
        var latencies = new double[Frames];
        for (int i = 0; i < Frames; i++)
        {
            long accepted = input.Accepted;
            long start = Stopwatch.GetTimestamp();
            Assert.True(rig.Client.SendState(Pens.Contact((ushort)(1000 + i), 1000), []));
            await Wait.Until(() => input.Accepted > accepted, "the input frame arrived");
            latencies[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        await done.CancelAsync();
        await video;
        Assert.Equal(Frames, input.Accepted);
        Assert.Equal(0, input.Counters.Stale);
        Assert.InRange(rig.Sender.FramesSent, 10, long.MaxValue);
        Array.Sort(latencies);
        TestContext.Current.TestOutputHelper?.WriteLine($"input p50 {latencies[Frames / 2]:F2} ms, p95 {latencies[Frames * 95 / 100]:F2} ms while {rig.Sender.FramesSent} video frames streamed");
        Assert.InRange(latencies[Frames * 95 / 100], 0, 100); // loopback; a generous bound for loaded CI machines
    }

    private static Task SendAsync(PenBridgeClient client, ControlMessageType type) => type switch
    {
        ControlMessageType.MirrorStart => client.SendRawControlAsync(MirrorRig.Request.ToMessage(5000)),
        ControlMessageType.MirrorStop => client.SendRawControlAsync(default(MirrorStopMessage)),
        ControlMessageType.KeyframeRequest => client.SendRawControlAsync(new KeyframeRequestMessage(1, 0)),
        _ => client.SendRawControlAsync(new MirrorReportMessage(1, 0, 0, 0, 0, 0)),
    };
}
