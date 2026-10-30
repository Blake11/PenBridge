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

    [Theory]
    [InlineData(MirrorState.Paused, MirrorReason.PenBridgePaused)]
    [InlineData(MirrorState.Unavailable, MirrorReason.CaptureFailed)]
    public async Task The_heads_pause_reaches_the_phone_and_resuming_starts_a_new_epoch(MirrorState paused, MirrorReason reason)
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        MirrorStreamConfig first = await rig.StartAsync();
        int states = rig.ClientStates.Mark();
        rig.Server.SetMirrorState(paused, reason);
        await rig.ClientStates.WaitFor(states, got => got.Contains((paused, reason)), "paused");
        rig.Server.SendVideoFrame(MirrorRig.AccessUnit(3000, 1, true), true, rig.NowCaptureUs);
        await rig.H.SettleAsync();
        Assert.Equal(0, rig.Sender.FramesSent);
        Assert.Empty(rig.Frames.All());

        int configs = rig.ClientConfigs.Mark();
        int raised = rig.ServerConfigs.Mark();
        rig.Server.SetMirrorState(MirrorState.Streaming);
        MirrorStreamConfig second = (await rig.ClientConfigs.WaitFor(configs, 1, "a new epoch on resume"))[0];
        Assert.Equal(first with { Epoch = second.Epoch }, second);
        Assert.NotEqual(first.Epoch, second.Epoch);

        // The head is told again (MirrorConfigChanged), and the new epoch starts with a keyframe: a delta is discarded.
        Assert.Equal([second], await rig.ServerConfigs.WaitFor(raised, 1, "MirrorConfigChanged on resume"));
        await rig.WaitReleasedAsync();
        rig.Server.SendVideoFrame(MirrorRig.AccessUnit(3000, 2, false), false, rig.NowCaptureUs);
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 3, true), true);
        ReceivedFrame frame = (await rig.Frames.WaitFor(0, 1, "the new epoch's first frame"))[0];
        Assert.Equal((second.Epoch, VideoDatagram.FirstFrameNumber, true), (frame.Epoch, frame.Number, frame.Keyframe));
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
    public async Task A_repeated_mirror_start_with_the_same_plan_starts_a_new_epoch_for_the_head()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync();
        MirrorStreamConfig first = await rig.StartAsync();
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 1, true), true);
        await rig.Frames.WaitFor(0, 1, "the first epoch streams");
        int raised = rig.ServerConfigs.Mark();
        int frames = rig.Frames.Mark();

        MirrorStreamConfig second = await rig.StartAsync(); // the same request, nothing else changed
        Assert.Equal(first with { Epoch = second.Epoch }, second);
        Assert.NotEqual(first.Epoch, second.Epoch);
        Assert.Equal([second], await rig.ServerConfigs.WaitFor(raised, 1, "MirrorConfigChanged for the repeated start"));
        await rig.Requested.WaitFor(0, 2, "every MirrorStart is a MirrorRequested");

        rig.Server.SendVideoFrame(MirrorRig.AccessUnit(3000, 2, false), false, rig.NowCaptureUs); // discarded: keyframe first
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 3, true), true);
        ReceivedFrame frame = (await rig.Frames.WaitFor(frames, 1, "the new epoch's first frame"))[0];
        Assert.Equal((second.Epoch, VideoDatagram.FirstFrameNumber, true), (frame.Epoch, frame.Number, frame.Keyframe));
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
    public async Task Requests_still_queued_for_the_head_at_a_takeover_end_in_a_stop_and_late_frames_are_dropped()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, allowTakeover: true));
        var events = new EventLog<string>();
        using var gate = new ManualResetEventSlim(false);
        int blocked = 0;
        rig.Server.MirrorRequested += (_, _) =>
        {
            events.Add("requested");
            if (Interlocked.Exchange(ref blocked, 1) == 0)
            {
                gate.Wait(Wait.Limit); // the head is busy: later events queue behind this one
            }
        };
        rig.Server.MirrorConfigChanged += (_, _) => events.Add("config");
        rig.Server.MirrorStopped += (_, _) => events.Add("stopped");

        // Stop and ask again while the head still handles the first request: a second request is pending at the takeover.
        int states = rig.ClientStates.Mark();
        Assert.True(rig.Client.StartMirror(MirrorRig.Request));
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Streaming, MirrorReason.None)), "Streaming");
        states = rig.ClientStates.Mark();
        rig.Client.StopMirror();
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Off, MirrorReason.PhoneStopped)), "Off");
        states = rig.ClientStates.Mark();
        Assert.True(rig.Client.StartMirror(MirrorRig.Request));
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Streaming, MirrorReason.None)), "Streaming again");
        ServerMirror old = rig.Mirror;
        VideoSender oldSender = rig.Sender;

        PenBridgeClient other = await rig.H.ConnectPairedAsync();
        await Wait.Until(() => oldSender.IsStopped, "the old run stopped");
        gate.Set();
        await events.WaitFor(0, 6, "the old session's events");
        await rig.H.SettleAsync();
        Assert.Equal(["requested", "config", "stopped", "requested", "config", "stopped"], events.All());
        Assert.Equal((MirrorState.Off, MirrorReason.None), rig.Server.MirrorStatus); // the new session asked nothing

        // Late frames from a head that hasn't seen the stop yet are dropped: through the server, the old session, and with no session.
        byte[] keyframe = MirrorRig.AccessUnit(3000, 1, true);
        rig.Server.SendVideoFrame(keyframe, true, rig.NowCaptureUs);
        old.SendVideoFrame(keyframe, true, rig.NowCaptureUs);
        await other.DisconnectAsync().Bounded();
        await Wait.Until(() => rig.Server.ActiveConnection is null, "no session");
        rig.Server.SendVideoFrame(keyframe, true, rig.NowCaptureUs); // every drop is synchronous: nothing to settle
        Assert.Equal(0, oldSender.FramesSent);
        Assert.Empty(rig.Frames.All());
        Assert.Equal(6, events.All().Length);
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

        // No more input: a TLS keepalive the Mac read after the probe would have skipped the restore, so the phone
        // follows the probe's ack with one UDP datagram on its own.
        await rig.ClientConfigs.WaitFor(configs, 1, "a new epoch once UDP is back");
        await rig.Requested.WaitFor(0, 2, "the head was asked again"); // raised by the event drainer, maybe after the config

        // A TLS keepalive read after the probe but newer than it may flap the session once more (stop, then ask again
        // on the follow-up datagram); either way it ends streaming, with every request but the last one stopped.
        await Wait.Until(
            () => !rig.Mirror.InTlsFallback && rig.Requested.Count == rig.Stopped.Count + 1
                && rig.Server.MirrorStatus == (MirrorState.Streaming, MirrorReason.None),
            "streaming again");
    }

    [Fact]
    public async Task An_idle_phone_follows_the_restoring_probe_with_one_udp_datagram()
    {
        bool drop = false;
        int passed = 0;
        await using MirrorRig rig = await MirrorRig.CreateAsync(phone: MirrorRig.Phone(input: _ =>
        {
            if (Volatile.Read(ref drop))
            {
                return false;
            }

            Interlocked.Increment(ref passed);
            return true;
        }));
        await rig.StartAsync();
        Volatile.Write(ref drop, true);
        Assert.True(rig.Client.SendState(null, [])); // out of range: no keepalives, only the release repeats
        await Wait.Step(rig.H.Time, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500));
        Assert.Equal(InputTransport.Tls, rig.Client.Transport);
        await Wait.Until(() => rig.Mirror.InTlsFallback, "the Mac is in fallback");

        int mark = Volatile.Read(ref passed);
        Volatile.Write(ref drop, false);
        await Wait.Step(rig.H.Time, TimeSpan.FromSeconds(11), TimeSpan.FromMilliseconds(500));
        await Wait.Until(() => rig.Client.Transport == InputTransport.Udp, "the phone is back on UDP");
        // Without new input: the probe, then the datagram after its ack (an ack wait may add more under load).
        await Wait.Until(() => Volatile.Read(ref passed) >= mark + 2, "the probe and one datagram after its ack");
        await Wait.Until(() => !rig.Mirror.InTlsFallback, "the Mac left fallback");
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

        // The failed run never asked the Mac: it ends where the phone's MirrorStop left it, every request balanced by a
        // stop. Not an exact count: a UDP datagram the Mac's receive thread takes after the fallback's TLS frame (same
        // timeUs on fake time, so not stale) restores UDP on the Mac before the phone's MirrorStop is read, which
        // repeats the request (Requested, then Stopped) without the phone asking.
        await Wait.Until(() => rig.Server.MirrorStatus == (MirrorState.Off, MirrorReason.PhoneStopped), "the Mac stays stopped");
        await Wait.Until(() => rig.Requested.Count == rig.Stopped.Count, "every request is matched by a stop");
        Assert.InRange(rig.Requested.Count, 1, 2);
    }

    [Fact]
    public async Task An_input_loss_during_contact_wakes_the_guard_and_steps_the_rate_down_in_the_same_epoch()
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
        int raised = rig.ServerConfigs.Mark();
        int rates = rig.ServerRates.Mark();
        // The wake arms it after the input lock is released, so it may not be armed yet: advance until it ran.
        await Wait.Until(
            () =>
            {
                rig.H.Time.Advance(TimeSpan.Zero);
                return rig.ServerRates.Since(rates).Length > 0;
            },
            "the guard stepped the bitrate down");
        MirrorRateChange stepped = rig.ServerRates.Since(rates)[0];
        Assert.Equal(full.Epoch, stepped.Epoch);
        Assert.Equal(full.Fps, stepped.Fps);
        Assert.InRange(stepped.BitrateKbps, 1, full.BitrateKbps - 1);

        // A live change: the epoch and the phone's configuration stay, the pacing follows.
        await rig.H.SettleAsync();
        Assert.Equal(full, rig.Mirror.Config);
        Assert.Empty(rig.ClientConfigs.Since(configs));
        Assert.Empty(rig.ServerConfigs.Since(raised));
        Assert.Equal((stepped.BitrateKbps, stepped.Fps), rig.Sender.PacingRate);
    }

    [Fact]
    public async Task A_guard_pause_holds_the_epoch_and_the_resume_continues_it_from_a_keyframe()
    {
        // No server pings: on fake time their RTT reads 0 (healthy), which would end the degradation the test drives.
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, pingInterval: Timeout.InfiniteTimeSpan));
        MirrorStreamConfig config = await rig.StartAsync();
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 1, true), true);
        await rig.Frames.WaitFor(0, 1, "frame 1");
        int configs = rig.ClientConfigs.Mark();
        int raised = rig.ServerConfigs.Mark();
        int rates = rig.ServerRates.Mark();
        int states = rig.ClientStates.Mark();

        rig.Mirror.OnRtt(TimeSpan.FromMilliseconds(10));
        for (int i = 0; i < 200 && rig.Server.MirrorStatus.State != MirrorState.Throttled; i++)
        {
            rig.Mirror.OnRtt(TimeSpan.FromMilliseconds(30)); // stays degraded
            await Wait.Step(rig.H.Time, TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal((MirrorState.Throttled, MirrorReason.None), rig.Server.MirrorStatus);
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Throttled, MirrorReason.None)), "the phone sees Throttled");
        MirrorRateChange[] steps = rig.ServerRates.Since(rates);
        Assert.NotEmpty(steps);
        Assert.All(steps, step => Assert.Equal(config.Epoch, step.Epoch)); // every step in the running epoch
        Assert.Equal(new MirrorGuardLevel(steps[^1].BitrateKbps, steps[^1].Fps, true), rig.Mirror.Guard.Level); // paused at the bottom
        Assert.Empty(rig.ServerConfigs.Since(raised));
        long sent = rig.Sender.FramesSent;
        rig.Server.SendVideoFrame(MirrorRig.AccessUnit(3000, 2, true), true, rig.NowCaptureUs);
        await rig.H.SettleAsync();
        Assert.Equal(sent, rig.Sender.FramesSent); // paused: nothing goes out

        // Healthy for 2 s: the same epoch resumes with a forced keyframe, no MirrorConfig.
        int keyframes = rig.Keyframes.Mark();
        states = rig.ClientStates.Mark();
        rig.Mirror.OnRtt(TimeSpan.FromMilliseconds(10));
        await Wait.Step(rig.H.Time, MirrorRateController.HealthyBeforeRecovery);
        await rig.Keyframes.WaitFor(keyframes, 1, "the resume asks the head for a keyframe");
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Streaming, MirrorReason.None)), "the phone sees Streaming");
        Assert.Equal(config, rig.Mirror.Config);
        Assert.Equal(new MirrorGuardLevel(steps[^1].BitrateKbps, steps[^1].Fps, false), rig.Mirror.Guard.Level); // at the floor

        rig.Server.SendVideoFrame(MirrorRig.AccessUnit(3000, 3, false), false, rig.NowCaptureUs); // before the keyframe: discarded
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 4, true), true);
        ReceivedFrame frame = (await rig.Frames.WaitFor(1, 1, "the resumed frame"))[0];
        Assert.Equal((config.Epoch, VideoDatagram.FirstFrameNumber + 1, true), (frame.Epoch, frame.Number, frame.Keyframe));
        await rig.H.SettleAsync();
        Assert.Empty(rig.ClientConfigs.Since(configs));
        Assert.Empty(rig.ServerConfigs.Since(raised));
    }

    [Fact]
    public async Task The_loss_a_pause_cuts_does_not_count_against_the_resumed_epoch()
    {
        uint dropFrame = 0;
        DatagramFilter filter = d => !VideoDatagramHeader.TryRead(d, out VideoDatagramHeader h) || h.FrameNumber != Volatile.Read(ref dropFrame);
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, videoFilter: filter, pingInterval: Timeout.InfiniteTimeSpan));
        MirrorStreamConfig config = await rig.StartAsync();
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 1, true), true);
        await rig.Frames.WaitFor(0, 1, "frame 1");

        // Frame 2 never reaches the phone (as if the pause cut it): the phone only sees the gap once the resume keyframe
        // (frame 3) arrives, and counts it as lost then.
        Volatile.Write(ref dropFrame, VideoDatagram.FirstFrameNumber + 1);
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 2, false), false);
        await PauseAsync(rig);
        MirrorGuardLevel bottom = rig.Mirror.Guard.Level;
        await Wait.Step(rig.H.Time, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100)); // the phone reports during the pause

        int rates = rig.ServerRates.Mark();
        await ResumeAsync(rig);
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 3, true), true);
        ReceivedFrame resumed = (await rig.Frames.WaitFor(1, 1, "the resume keyframe"))[0];
        Assert.Equal(VideoDatagram.FirstFrameNumber + 2, resumed.Number);
        Assert.True(rig.Client.MirrorStats.FragmentsLost > 0); // the phone counted frame 2 with the keyframe
        long reports = rig.Client.MirrorStats.ReportsSent;

        // The reports after the keyframe carry that loss; they must not degrade the guard, so recovery goes on one step
        // per second (resumed ~ now) instead of waiting out a loss signal and 2 s healthy.
        for (int i = 0; i < 40 && (i < 15 || rig.Client.MirrorStats.ReportsSent < reports + 2); i++)
        {
            await Wait.Step(rig.H.Time, TimeSpan.FromMilliseconds(100));
            Assert.False(rig.Mirror.Guard.IsDegraded, $"degraded {(i + 1) * 100} ms after the keyframe");
        }

        long sentReports = rig.Client.MirrorStats.ReportsSent;
        Assert.True(sentReports >= reports + 2);
        await Wait.Until(() => rig.Mirror.ReportsReceived >= sentReports, "the Mac took every report sent so far");
        await rig.H.SettleAsync();
        Assert.False(rig.Mirror.Guard.IsDegraded); // the loss signal would last 1 s: fake time stood still since
        Assert.Contains(rig.ServerRates.Since(rates), r => r.Epoch == config.Epoch && (r.Fps > bottom.Fps || r.BitrateKbps > bottom.BitrateKbps));
    }

    [Fact]
    public async Task A_new_mirror_start_during_a_guard_pause_does_not_hold_the_old_epoch()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, pingInterval: Timeout.InfiniteTimeSpan));
        MirrorStreamConfig config = await rig.StartAsync();
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 1, true), true);
        await rig.Frames.WaitFor(0, 1, "frame 1");
        await PauseAsync(rig);

        int states = rig.ClientStates.Mark();
        int configs = rig.ClientConfigs.Mark();
        Assert.True(rig.Client.StartMirror(MirrorRig.Request)); // the phone starts over
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Throttled, MirrorReason.None)), "still Throttled");
        Assert.True(rig.Mirror.Guard.Level.Paused);
        Assert.Null(rig.Mirror.Config);

        await ResumeAsync(rig);
        MirrorStreamConfig next = (await rig.ClientConfigs.WaitFor(configs, 1, "a new epoch"))[0];
        Assert.NotEqual(config.Epoch, next.Epoch);
        Assert.Equal(next, rig.Mirror.Config);
    }

    [Fact]
    public async Task A_plan_change_during_a_guard_pause_resumes_in_a_new_epoch()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, pingInterval: Timeout.InfiniteTimeSpan));
        MirrorStreamConfig config = await rig.StartAsync();
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 1, true), true);
        await rig.Frames.WaitFor(0, 1, "frame 1");
        await PauseAsync(rig);

        int configs = rig.ClientConfigs.Mark();
        rig.Mirror.OnSurface((ushort)(config.WidthPx / 2), (ushort)(config.HeightPx / 2)); // a smaller surface: a new size
        await rig.H.SettleAsync();
        Assert.Empty(rig.ClientConfigs.Since(configs)); // nothing while paused

        await ResumeAsync(rig);
        MirrorStreamConfig next = (await rig.ClientConfigs.WaitFor(configs, 1, "a new epoch"))[0];
        Assert.NotEqual(config.Epoch, next.Epoch);
        Assert.True(next.WidthPx < config.WidthPx);
    }

    [Fact]
    public async Task Keyframe_requests_during_a_guard_pause_are_ignored_and_the_resume_keyframe_answers_a_late_one()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, pingInterval: Timeout.InfiniteTimeSpan));
        MirrorStreamConfig config = await rig.StartAsync();
        await rig.SendAsync(MirrorRig.AccessUnit(3000, 1, true), true);
        await rig.Frames.WaitFor(0, 1, "frame 1");
        await PauseAsync(rig);

        int keyframes = rig.Keyframes.Mark();
        rig.Mirror.OnKeyframeRequest(new KeyframeRequestMessage(config.Epoch, 1));
        await Wait.Step(rig.H.Time, ServerMirror.KeyframeInterval * 2, TimeSpan.FromMilliseconds(50));
        Assert.Empty(rig.Keyframes.Since(keyframes)); // paused: nothing to encode

        await ResumeAsync(rig);
        await rig.Keyframes.WaitFor(keyframes, 1, "the resume keyframe");
        rig.Mirror.OnKeyframeRequest(new KeyframeRequestMessage(config.Epoch, 1)); // sent before that keyframe arrived
        await Wait.Step(rig.H.Time, ServerMirror.KeyframeInterval * 2, TimeSpan.FromMilliseconds(50));
        Assert.Single(rig.Keyframes.Since(keyframes)); // no second keyframe

        rig.Mirror.OnKeyframeRequest(new KeyframeRequestMessage(config.Epoch, 1)); // a later loss is answered as usual
        await rig.Keyframes.WaitFor(keyframes, 2, "a keyframe for the later request");
    }

    /// <summary>Degrades the guard (high RTT) until it pauses the video (pings must be off).</summary>
    private static async Task PauseAsync(MirrorRig rig)
    {
        int states = rig.ClientStates.Mark();
        rig.Mirror.OnRtt(TimeSpan.FromMilliseconds(10));
        for (int i = 0; i < 200 && rig.Server.MirrorStatus.State != MirrorState.Throttled; i++)
        {
            rig.Mirror.OnRtt(TimeSpan.FromMilliseconds(30));
            await Wait.Step(rig.H.Time, TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal((MirrorState.Throttled, MirrorReason.None), rig.Server.MirrorStatus);
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Throttled, MirrorReason.None)), "the phone sees Throttled");
    }

    /// <summary>A healthy RTT, then <see cref="MirrorRateController.HealthyBeforeRecovery"/>: the guard resumes.</summary>
    private static async Task ResumeAsync(MirrorRig rig)
    {
        int states = rig.ClientStates.Mark();
        rig.Mirror.OnRtt(TimeSpan.FromMilliseconds(10));
        await Wait.Step(rig.H.Time, MirrorRateController.HealthyBeforeRecovery, TimeSpan.FromMilliseconds(100));
        await rig.ClientStates.WaitFor(states, got => got.Contains((MirrorState.Streaming, MirrorReason.None)), "the phone sees Streaming");
        Assert.False(rig.Mirror.Guard.Level.Paused);
    }

    [Fact]
    public async Task An_epoch_that_starts_below_the_plan_carries_the_plan_and_the_head_gets_the_rate_just_before()
    {
        await using MirrorRig rig = await MirrorRig.CreateAsync(o => MirrorRig.ServerOptions(o, pingInterval: Timeout.InfiniteTimeSpan));
        var order = new EventLog<string>();
        rig.Server.MirrorConfigChanged += (_, c) => order.Add($"config {c.Epoch}");
        rig.Server.MirrorRateChanged += (_, r) => order.Add($"rate {r.Epoch}");
        MirrorStreamConfig full = await rig.StartAsync();
        int rates = rig.ServerRates.Mark();
        rig.Mirror.OnRtt(TimeSpan.FromMilliseconds(10));
        rig.Mirror.OnRtt(TimeSpan.FromMilliseconds(30));
        rig.Mirror.OnRtt(TimeSpan.FromMilliseconds(30)); // degraded: one step at once
        MirrorRateChange stepped = (await rig.ServerRates.WaitFor(rates, 1, "a step"))[0];

        // A repeated MirrorStart starts a new epoch while the guard holds the stream below the plan.
        int configs = rig.ClientConfigs.Mark();
        int raised = rig.ServerConfigs.Mark();
        rates = rig.ServerRates.Mark();
        int marks = order.Mark();
        Assert.True(rig.Client.StartMirror(MirrorRig.Request));
        MirrorStreamConfig next = (await rig.ClientConfigs.WaitFor(configs, 1, "the new epoch"))[0];
        Assert.Equal(full with { Epoch = next.Epoch }, next); // the planned fps and bitrate, not the guard's
        Assert.Equal([next], await rig.ServerConfigs.WaitFor(raised, 1, "the head's config"));
        Assert.Equal([stepped with { Epoch = next.Epoch }], await rig.ServerRates.WaitFor(rates, 1, "the head's rate"));
        Assert.Equal([$"rate {next.Epoch}", $"config {next.Epoch}"], await order.WaitFor(marks, 2, "both head events, the rate first"));
        Assert.Equal((stepped.BitrateKbps, stepped.Fps), rig.Sender.PacingRate);
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

        // At least 300 input frames, and on until 10 video frames went out meanwhile (a count, not a duration: a slow
        // machine streams fewer frames per input frame).
        const int Frames = 300;
        const int VideoFrames = 10;
        var latencies = new double[Frames];
        long began = Stopwatch.GetTimestamp();
        int i = 0;
        for (; i < Frames || rig.Sender.FramesSent < VideoFrames; i++)
        {
            Assert.True(Stopwatch.GetElapsedTime(began) < Wait.Limit + Wait.Limit, $"only {rig.Sender.FramesSent} video frames streamed after {i} input frames");
            long accepted = input.Accepted;
            long start = Stopwatch.GetTimestamp();
            Assert.True(rig.Client.SendState(Pens.Contact((ushort)(1000 + (i % 1000)), 1000), []));
            await Wait.Until(() => input.Accepted > accepted, "the input frame arrived");
            if (i < Frames)
            {
                latencies[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
        }

        await done.CancelAsync();
        await video;
        Assert.Equal(i, input.Accepted);
        Assert.Equal(0, input.Counters.Stale);
        Assert.InRange(rig.Sender.FramesSent, VideoFrames, long.MaxValue);
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
