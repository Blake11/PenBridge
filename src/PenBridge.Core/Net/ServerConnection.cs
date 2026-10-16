using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using SecretKind = PenBridge.Core.Security.PairingSecretKind;

namespace PenBridge.Core.Net;

/// <summary>
/// One phone connection on the server: TLS handshake, Hello/HelloAck, optional pairing, Auth, then the session loop.
/// Every read has a deadline (<see cref="PenBridgeServerOptions.ReadTimeout"/> before the session,
/// <see cref="PenBridgeServerOptions.PeerTimeout"/> during it); the whole pre-session phase is bounded by
/// <see cref="PenBridgeServerOptions.HandshakeTimeout"/> and pairing by <see cref="PenBridgeServerOptions.PairingTimeout"/>.
/// Any failure closes and disposes the connection.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001", Justification = "RunAsync owns the channel and disposes it in its finally block.")]
internal sealed class ServerConnection
{
    private static readonly TimerCallback PingCallback = static state => _ = ((ServerConnection)state!).PingAsync();

    /// <summary>SettingsUpdate messages a session may send per second, sustained (ADR-0030; a slider sends far fewer).</summary>
    internal const int SettingsUpdatesPerSecond = 20;

    /// <summary>SettingsUpdate messages a session may send in a burst.</summary>
    internal const int SettingsUpdateBurst = 20;

    /// <summary>Dropped SettingsUpdate messages are logged at most this often.</summary>
    private static readonly TimeSpan ThrottleLogInterval = TimeSpan.FromSeconds(10);

    // Push bits, drained in this order: the display list before the snapshot that may name one of its displays.
    internal const int PushDisplays = 1;
    internal const int PushSnapshot = 2;
    internal const int PushActiveArea = 4;

    private readonly PenBridgeServer _server;
    private readonly PenBridgeServerOptions _options;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly ControlChannel _channel;
    private readonly EndPoint? _remote;
    private readonly IPAddress _address;
    private readonly long _epoch;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CoalescingWriter _pushes; // server → phone settings, display list and active area; latest state wins
    private readonly TokenBucket _settingsUpdates; // read loop only
    private readonly Lock _areaLock = new();
    private NormalizedRect? _activeArea; // latest requested; guarded by _areaLock
    private NormalizedRect? _activeAreaSent; // only touched by the push writer
    private int _resendArea; // set per SurfaceInfo (any thread); the writer consumes it to drop the dedupe once
    // The last list and snapshot sent, by their (immutable, per-send) arrays; only touched by the push writer.
    private IReadOnlyList<DisplayListEntry>? _displaysSent;
    private IReadOnlyList<SettingEntry>? _snapshotSent;
    private long _settingsUpdatesDropped;
    private long _throttleLoggedAt = -1; // read loop only: when dropped updates were last logged
    private byte[] _deviceIdBytes = [];
    private ProtocolCapabilities _negotiated; // advertised by both sides (set from Hello)
    private ITimer? _handshake;
    private ITimer? _pingTimer;
    private SessionInfo? _info;
    private long _rttTicks = -1;
    private int _requestedReason;
    private bool _authenticated;
    private bool _sessionStarted;
    private SessionEndReason _refusalReason = SessionEndReason.ProtocolError;

    public ServerConnection(PenBridgeServer server, Socket socket, IPEndPoint remote, IPAddress address)
    {
        _server = server;
        _options = server.Options;
        _log = server.Log;
        _time = _options.TimeProvider;
        _remote = remote;
        _address = address;
        socket.NoDelay = true;
        socket.SendTimeout = (int)ControlChannel.SyncSendTimeout.TotalMilliseconds;
        _channel = new ControlChannel(socket, _time);
        _epoch = _time.GetTimestamp();
        _pushes = new CoalescingWriter(DrainPushesAsync, OnPushFault);
        _settingsUpdates = new TokenBucket(_time, SettingsUpdatesPerSecond, SettingsUpdateBurst);

        // Armed at accept: the whole pre-session phase is bounded from the first byte.
        _handshake = _channel.StartDeadline(_options.HandshakeTimeout);
    }

    public Guid DeviceId { get; private set; }

    public string DeviceName { get; private set; } = string.Empty;

    public SessionInput? Input { get; private set; }

    /// <summary>SettingsUpdate messages dropped by the per-session rate cap.</summary>
    internal long SettingsUpdatesDropped => Interlocked.Read(ref _settingsUpdatesDropped);

    /// <summary>The server → phone push writer (for tests).</summary>
    internal CoalescingWriter Pushes => _pushes;

    /// <summary>True once the control channel was aborted (for tests).</summary>
    internal bool IsChannelAborted => _channel.IsAborted;

    /// <summary>Test hook: awaited by the push writer after it took its bits and before it reads the state to send.</summary>
    internal Func<int, Task>? BeforePushDrain { get; set; }

    public TimeSpan? RoundTripTime
    {
        get
        {
            long ticks = Volatile.Read(ref _rttTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    public void Start() => _ = RunAsync();

    /// <summary>Closes the connection (Bye if a session is running). Completes when the connection is fully torn down.</summary>
    /// <remarks>
    /// The requested reason is recorded before anything is sent, so however the connection then ends (the peer
    /// closing after it read the Bye, a failed read, the abort) it is reported with <paramref name="reason"/>. The Bye
    /// is followed by a half-close and a bounded wait for the peer to close first: closing while the peer's frames
    /// are still unread would reset the connection, and a reset can discard the Bye before the peer reads it.
    /// </remarks>
    public async Task CloseAsync(ByeReason bye, SessionEndReason reason)
    {
        if (Interlocked.CompareExchange(ref _requestedReason, (int)reason + 1, 0) == 0)
        {
            // Release the input first so the next session starts from a clean state.
            Input?.Shutdown();
            _pushes.Stop();

            // No more pings: a write after the half-close would fail and abort, cutting the linger short.
            Volatile.Read(ref _pingTimer)?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (Volatile.Read(ref _sessionStarted)
                && await _channel.TryWriteFarewellAsync(new ByeMessage(bye), shutdownSend: true).ConfigureAwait(false))
            {
                // The session loop drains (and ignores) the peer's last frames until the peer closes its side.
                await _done.Task.WaitAsync(ControlChannel.LingerTimeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            _channel.Abort();
        }

        await _done.Task.ConfigureAwait(false);
    }

    private SessionEndReason? RequestedReason
    {
        get
        {
            int value = Volatile.Read(ref _requestedReason);
            return value == 0 ? null : (SessionEndReason)(value - 1);
        }
    }

    private async Task RunAsync()
    {
        NetLog.ConnectionAccepted(_log, _remote);
        SessionEndReason reason = SessionEndReason.ConnectionLost;
        try
        {
            await _channel.Ssl.AuthenticateAsServerAsync(_server.TlsOptions, _channel.Token).ConfigureAwait(false);
            if (!await HandshakeAsync().ConfigureAwait(false))
            {
                reason = RequestedReason ?? _refusalReason;
                return;
            }

            await _handshake!.DisposeAsync().ConfigureAwait(false);
            _handshake = null;
            SessionEndReason ended = await SessionLoopAsync().ConfigureAwait(false);

            // A close we requested wins over how the stream then ended (e.g. the peer closing after our Bye).
            reason = RequestedReason ?? ended;
        }
        catch (OperationCanceledException)
        {
            reason = RequestedReason ?? (_channel.TimedOut ? SessionEndReason.Timeout : SessionEndReason.Shutdown);
            if (reason == SessionEndReason.Timeout)
            {
                await _channel.TryWriteFarewellAsync(new ErrorMessage(ProtocolErrorCode.Timeout, "Timed out.")).ConfigureAwait(false);
            }
        }
        catch (ProtocolException ex)
        {
            reason = RequestedReason ?? SessionEndReason.ProtocolError;
            NetLog.ProtocolViolation(_log, _remote, ex.Code);
            await _channel.TryWriteFarewellAsync(new ErrorMessage(ex.Code, "Protocol violation.")).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or ObjectDisposedException)
        {
            reason = RequestedReason ?? (_channel.TimedOut ? SessionEndReason.Timeout : SessionEndReason.ConnectionLost);
        }
#pragma warning disable CA1031 // Last-resort guard: one bad connection must not take down the server.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            reason = SessionEndReason.InternalError;
            NetLog.Unexpected(_log, _remote, ex);
            await _channel.TryWriteFarewellAsync(new ErrorMessage(ProtocolErrorCode.InternalError, "Internal error.")).ConfigureAwait(false);
        }
        finally
        {
            if (_handshake is not null)
            {
                await _handshake.DisposeAsync().ConfigureAwait(false);
            }

            if (_pingTimer is not null)
            {
                await _pingTimer.DisposeAsync().ConfigureAwait(false);
            }

            Input?.Shutdown();
            _pushes.Stop();
            if (!_authenticated)
            {
                _server.ReleasePending(_address);
            }

            _server.OnConnectionEnded(this);
            await _channel.DisposeAsync().ConfigureAwait(false);
            NetLog.ConnectionClosed(_log, _remote, reason);
            if (_sessionStarted && _info is not null)
            {
                NetLog.SessionEnded(_log, DeviceId, reason);
                _server.RaiseSessionEnded(_info, reason);
            }

            _done.TrySetResult();
        }
    }

    /// <summary>Hello → (pairing) → Auth → SessionStart. False when the connection was refused in an orderly way.</summary>
    private async Task<bool> HandshakeAsync()
    {
        HelloMessage hello = (await ReadAsync().ConfigureAwait(false)).Read<HelloMessage>();
        if (!hello.Version.IsCompatibleWith(ProtocolVersion.Current))
        {
            NetLog.IncompatibleVersion(_log, _remote, hello.Version);
            await _channel.TryWriteFarewellAsync(new ErrorMessage(ProtocolErrorCode.IncompatibleVersion, "Unsupported protocol major version.")).ConfigureAwait(false);
            return false;
        }

        _deviceIdBytes = hello.DeviceId.ToArray();
        DeviceId = new Guid(_deviceIdBytes, bigEndian: true);
        DeviceName = hello.DeviceName;
        _negotiated = hello.Capabilities & _options.Capabilities;

        byte[] challenge = RandomNumberGenerator.GetBytes(WireSizes.Challenge);
        await _channel.WriteAsync(new HelloAckMessage(ProtocolVersion.Current, _server.ServerName, _options.Capabilities, challenge)).ConfigureAwait(false);

        ReceivedControlFrame next = await ReadAsync().ConfigureAwait(false);
        if (next.Type == ControlMessageType.ClientPairRound1)
        {
            if (!await PairAsync(next.Read<ClientPairRound1Message>(), challenge).ConfigureAwait(false))
            {
                return false;
            }

            next = await ReadAsync().ConfigureAwait(false);
        }

        return await AuthenticateAsync(next.Read<AuthMessage>(), challenge).ConfigureAwait(false);
    }

    private async Task<bool> PairAsync(ClientPairRound1Message round1, byte[] challenge)
    {
        var kind = (SecretKind)(byte)round1.SecretKind;
        PairingAttemptStart start = _server.Pins.TryBeginAttempt(kind, out PinAttempt? attempt);
        NetLog.PairingStart(_log, round1.SecretKind, DeviceId, start.ToString());
        switch (start)
        {
            case PairingAttemptStart.Started:
                break;
            case PairingAttemptStart.LockedOut:
                await _channel.WriteAsync(PairResultMessage.Failed(0)).ConfigureAwait(false);
                return false;
            case PairingAttemptStart.Busy:
                await _channel.WriteAsync(new ErrorMessage(ProtocolErrorCode.PairingUnavailable, "Another pairing attempt is in progress.")).ConfigureAwait(false);
                return false;
            default: // WindowClosed, Expired: no strike.
                await _channel.WriteAsync(PairResultMessage.Expired()).ConfigureAwait(false);
                return false;
        }

        // Pairing has its own deadline instead of the pre-auth one; Auth afterwards gets a fresh pre-auth deadline.
        if (_handshake is not null)
        {
            await _handshake.DisposeAsync().ConfigureAwait(false);
        }

        _handshake = null;
        ITimer deadline = _channel.StartDeadline(_options.PairingTimeout);
        PairingSession session = PairingSession.ForServer(attempt!, DeviceId, challenge, _server.Fingerprint);
        try
        {
            byte[] serverRound1 = session.CreateRound1();
            if (!session.TryProcessPeerRound1(round1.Round1.Span))
            {
                return await RefusePairingAsync(session).ConfigureAwait(false);
            }

            await _channel.WriteAsync(new ServerPairRound1Message(serverRound1)).ConfigureAwait(false);

            ClientPairRound2Message round2 = (await ReadAsync().ConfigureAwait(false)).Read<ClientPairRound2Message>();
            if (!session.TryProcessPeerRound2(round2.Round2.Span))
            {
                return await RefusePairingAsync(session).ConfigureAwait(false);
            }

            await _channel.WriteAsync(new ServerPairRound2Message(session.CreateRound2())).ConfigureAwait(false);

            ClientPairConfirmMessage confirm = (await ReadAsync().ConfigureAwait(false)).Read<ClientPairConfirmMessage>();
            if (!session.TryVerifyPeerConfirm(confirm.Mac.Span))
            {
                return await RefusePairingAsync(session).ConfigureAwait(false);
            }

            // The attempt completed with Ok inside TryVerifyPeerConfirm; only now may the server confirm.
            byte[] deviceKey = new byte[KeySchedule.KeyLength];
            try
            {
                session.CopyDeviceKey(deviceKey);
                _server.Registry.Add(DeviceId, DeviceName, deviceKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(deviceKey);
            }

            await _channel.WriteAsync(new ServerPairConfirmMessage(session.CreateConfirm())).ConfigureAwait(false);
            await _channel.WriteAsync(PairResultMessage.Ok()).ConfigureAwait(false);
            _handshake = _channel.StartDeadline(_options.HandshakeTimeout);
            NetLog.Paired(_log, DeviceId, PeerText.Sanitize(DeviceName));
            foreach (PairedDevice device in _server.Registry.List())
            {
                if (device.DeviceId == DeviceId)
                {
                    _server.RaiseDevicePaired(device);
                    break;
                }
            }

            return true;
        }
        finally
        {
            // Completes an unfinished attempt as failed (a strike), e.g. on timeout or disconnect.
            session.Dispose();
            await deadline.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> RefusePairingAsync(PairingSession session)
    {
        session.Dispose();
        PairingResult result = session.AttemptResult ?? new PairingResult(PairingOutcome.Failed, 0);
        NetLog.PairingFailed(_log, DeviceId, result.Outcome.ToString(), result.AttemptsLeft);
        PairResultMessage message = result.Outcome == PairingOutcome.Expired
            ? PairResultMessage.Expired()
            : PairResultMessage.Failed((byte)Math.Clamp(result.AttemptsLeft, 0, byte.MaxValue));
        await _channel.WriteAsync(message).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> AuthenticateAsync(AuthMessage auth, byte[] challenge)
    {
        byte[] deviceKey = new byte[KeySchedule.KeyLength];
        byte[] inputKey = new byte[KeySchedule.KeyLength];
        try
        {
            bool ok = auth.DeviceId.Span.SequenceEqual(_deviceIdBytes)
                && _server.Registry.TryGetDeviceKey(DeviceId, deviceKey)
                && ReconnectAuth.Verify(deviceKey, challenge, auth.ClientNonce.Span, _server.Fingerprint, auth.Hmac.Span);
            if (!ok)
            {
                NetLog.AuthFailed(_log, DeviceId);
                await _channel.TryWriteFarewellAsync(new ErrorMessage(ProtocolErrorCode.AuthFailed, "Authentication failed.")).ConfigureAwait(false);
                return false;
            }

            _server.Registry.Touch(DeviceId);
            _authenticated = true;
            _server.ReleasePending(_address);

            switch (_server.TryActivate(this, out ServerConnection? previous))
            {
                case ActivationResult.Busy:
                    NetLog.Busy(_log, DeviceId);
                    _refusalReason = SessionEndReason.PeerClosed;
                    await _channel.TryWriteFarewellAsync(default(BusyMessage)).ConfigureAwait(false);
                    return false;
                case ActivationResult.Unpaired:
                    NetLog.AuthFailed(_log, DeviceId);
                    _refusalReason = SessionEndReason.Unpaired;
                    await _channel.TryWriteFarewellAsync(new ErrorMessage(ProtocolErrorCode.AuthFailed, "Authentication failed.")).ConfigureAwait(false);
                    return false;
            }

            if (previous is not null)
            {
                // Completes after the old connection released its input and raised SessionEnded, so both precede this
                // session's input and SessionStarted. PublishInput enforces the input half on its own as well.
                await previous.CloseAsync(ByeReason.TakenOver, SessionEndReason.TakenOver).ConfigureAwait(false);
            }

            uint sessionId = _server.NextSessionId();
            byte[] serverNonce = RandomNumberGenerator.GetBytes(WireSizes.Nonce);
            KeySchedule.DeriveInputKey(deviceKey, auth.ClientNonce.Span, serverNonce, sessionId, inputKey);
            var input = new SessionInput(
                _server.InputLock,
                _options.InputSink,
                _time,
                _options.TrackerOptions,
                inputKey,
                sessionId,
                _options.TargetDisplay,
                _options.TargetRegion,
                _options.MaxAuthFailuresPerSecond,
                OnUdpAck,
                OnInputFault,
                _options.LatencyRecorder,
                _negotiated);
            input.ForgedDatagramFlood += OnForgedFlood;
            input.ActiveAreaChanged += SendActiveArea; // only marks the push dirty (any thread, no _gate)
            Input = input;
            if (!_server.PublishInput(this, input) || RequestedReason is not null)
            {
                // Taken over (or closed) while starting.
                input.Shutdown();
                _refusalReason = RequestedReason ?? SessionEndReason.TakenOver;
                return false;
            }

            // Flag the session before SessionStart can reach the client: a close from here on must send Bye, or a
            // client that already holds its SessionStart would only see the stream end. The write lock orders the
            // two frames; a Bye that wins it just fails the client's connect.
            Volatile.Write(ref _sessionStarted, true);
            await _channel.WriteAsync(new SessionStartMessage((ushort)_server.Port, sessionId, serverNonce)).ConfigureAwait(false);
            _info = new SessionInfo(DeviceId, DeviceName, _remote, _negotiated);
            NetLog.SessionStarted(_log, DeviceId, PeerText.Sanitize(DeviceName));
            _server.RaiseSessionStarted(_info);
            if (_options.PingInterval != Timeout.InfiniteTimeSpan)
            {
                Volatile.Write(ref _pingTimer, _time.CreateTimer(PingCallback, this, _options.PingInterval, _options.PingInterval));
            }

            // From here on the server's pushes reach this session: everything negotiated goes out once with the latest
            // state (display list, snapshot, active area), then whatever changes. Not awaited: the read loop starts now.
            _ = _pushes.Enable();
            _ = RequestPush(PushDisplays | PushSnapshot | PushActiveArea);

            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(deviceKey);
            CryptographicOperations.ZeroMemory(inputKey);
        }
    }

    private async Task<SessionEndReason> SessionLoopAsync()
    {
        SessionInput input = Input!;
        while (true)
        {
            if (await _channel.ReadAsync(_options.PeerTimeout).ConfigureAwait(false) is not { } frame)
            {
                return SessionEndReason.PeerClosed;
            }

            if (RequestedReason is not null)
            {
                // Closing (Bye sent, send side shut): drain until the peer closes, act on nothing.
                continue;
            }

            switch (frame.Type)
            {
                case ControlMessageType.SurfaceInfo:
                    SurfaceInfoMessage surface = frame.Read<SurfaceInfoMessage>();
                    input.ConfigureSurface(surface.WidthPx, surface.HeightPx, surface.Capabilities, surface.XDpi, surface.YDpi);
                    _server.SetPhoneSurface(this, surface.WidthPx, surface.HeightPx);
                    if ((_negotiated & ProtocolCapabilities.ActiveArea) != 0)
                    {
                        // The phone resets its outline on every surface change and waits for the area, even an unchanged one.
                        Volatile.Write(ref _resendArea, 1);
                        _ = RequestPush(PushActiveArea);
                    }

                    break;
                case ControlMessageType.InputFrame:
                    bool wasTls = input.Transport == InputTransport.Tls;
                    input.ProcessInputFrame(frame.Payload.Span);
                    if (!wasTls && input.Transport == InputTransport.Tls)
                    {
                        NetLog.ServerTlsFallback(_log, DeviceId);
                    }

                    break;
                case ControlMessageType.Ping:
                    ulong pingUs = frame.Read<PingMessage>().TimestampUs;
                    _options.LatencyRecorder?.RecordClientPing(input.SessionId, pingUs, _time.GetTimestamp());
                    _ = await _channel.TryWriteAsync(new PongMessage(pingUs)).ConfigureAwait(false); // false: closing, Bye was the last frame
                    break;
                case ControlMessageType.Pong:
                    UpdateRtt(frame.Read<PongMessage>().TimestampUs, input.SessionId);
                    break;
                case ControlMessageType.SettingsUpdate when (_negotiated & ProtocolCapabilities.RemoteSettings) != 0:
                    OnSettingsUpdate(frame);
                    break;
                case ControlMessageType.Bye:
                    return SessionEndReason.PeerClosed;
                case ControlMessageType.Error:
                    LogPeerError(frame);
                    return SessionEndReason.PeerError;
                default:
                    // Includes the settings messages without their capability, and the server-to-phone ones.
                    throw new ProtocolException(ProtocolErrorCode.UnexpectedMessage, "Unexpected control message during a session.");
            }
        }
    }

    private async ValueTask<ReceivedControlFrame> ReadAsync()
    {
        ReceivedControlFrame frame = await _channel.ReadRequiredAsync(_options.ReadTimeout).ConfigureAwait(false);
        if (frame.Type == ControlMessageType.Error)
        {
            LogPeerError(frame);
            throw new IOException("The peer reported an error.");
        }

        if (frame.Type == ControlMessageType.Bye)
        {
            throw new IOException("The peer closed the connection.");
        }

        return frame;
    }

    private void LogPeerError(ReceivedControlFrame frame)
    {
        if (ErrorMessage.TryRead(frame.Payload.Span, out ErrorMessage error))
        {
            NetLog.PeerError(_log, error.Code, PeerText.Sanitize(error.Message));
        }
    }

    /// <summary>
    /// Hands a SettingsUpdate to the server, at most <see cref="SettingsUpdatesPerSecond"/> per second (burst
    /// <see cref="SettingsUpdateBurst"/>). Excess updates are dropped unparsed and counted, not answered, and don't end
    /// the session: the phone's settings UI recovers with the next snapshot, and the session's cost stays bounded
    /// either way (one handler call per token, and the replies coalesce in <see cref="_pushes"/>).
    /// </summary>
    private void OnSettingsUpdate(ReceivedControlFrame frame)
    {
        if (!_settingsUpdates.TryTake())
        {
            long dropped = Interlocked.Increment(ref _settingsUpdatesDropped);
            long now = _time.GetTimestamp();
            if (_throttleLoggedAt < 0 || _time.GetElapsedTime(_throttleLoggedAt, now) >= ThrottleLogInterval)
            {
                _throttleLoggedAt = now;
                NetLog.SettingsUpdatesThrottled(_log, DeviceId, SettingsUpdatesPerSecond, dropped);
            }

            return;
        }

        SettingsUpdateMessage update = frame.Read<SettingsUpdateMessage>();
        NetLog.SettingsUpdateReceived(_log, DeviceId, update.Entries.Count);
        if (update.Entries.Count > 0)
        {
            _server.RaiseSettingsUpdateRequested(_info!, update.Entries);
        }
    }

    /// <summary>
    /// Marks server → phone state dirty (<see cref="PushDisplays"/>, <see cref="PushSnapshot"/>,
    /// <see cref="PushActiveArea"/>); bits the session didn't negotiate are ignored. Only queues: one writer sends the
    /// latest state once SessionStart went out, so any number of calls costs at most one write per bit in flight.
    /// Never throws; the task completes when that state is written or the connection failed.
    /// </summary>
    internal Task RequestPush(int bits)
    {
        if ((_negotiated & ProtocolCapabilities.RemoteSettings) == 0)
        {
            bits &= ~(PushDisplays | PushSnapshot);
        }

        if ((_negotiated & ProtocolCapabilities.ActiveArea) == 0)
        {
            bits &= ~PushActiveArea;
        }

        return bits == 0 ? Task.CompletedTask : _pushes.Request(bits);
    }

    /// <summary>Queues the active area for this session (ADR-0031); only the latest one is sent, and only with <see cref="ProtocolCapabilities.ActiveArea"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A non-finite area.</exception>
    internal void SendActiveArea(NormalizedRect area)
    {
        _ = ActiveAreaMessage.FromNormalized(area); // validates before anything is queued
        if ((_negotiated & ProtocolCapabilities.ActiveArea) == 0)
        {
            return;
        }

        lock (_areaLock)
        {
            _activeArea = area;
        }

        _ = RequestPush(PushActiveArea);
    }

    /// <summary>The push writer's drain: the latest display list, snapshot and active area, in that order. False after a failed write (the connection is aborted).</summary>
    /// <remarks>
    /// The state is read after the bits were taken, so a snapshot read here may be newer than the bits say and name a
    /// display of a list whose bit is still pending: a snapshot always goes out behind the list read with it. Each
    /// list or snapshot is sent once (every send copies its entries into a new array, compared by reference), so the pending bit that follows sends nothing again.
    /// </remarks>
    private async Task<bool> DrainPushesAsync(int bits)
    {
        if (RequestedReason is not null)
        {
            return false;
        }

        try
        {
            if (BeforePushDrain is { } hook)
            {
                await hook(bits).ConfigureAwait(false);
            }

            (SettingsSnapshotMessage? snapshot, DisplayListMessage? displays) = _server.SettingsState;
            if ((bits & (PushDisplays | PushSnapshot)) != 0 && displays is { } d && !ReferenceEquals(d.Displays, _displaysSent))
            {
                if (!await TryPushAsync(d).ConfigureAwait(false))
                {
                    return false;
                }

                _displaysSent = d.Displays;
            }

            if ((bits & PushSnapshot) != 0 && snapshot is { } s && !ReferenceEquals(s.Entries, _snapshotSent))
            {
                if (!await TryPushAsync(s).ConfigureAwait(false))
                {
                    return false;
                }

                _snapshotSent = s.Entries;
            }

            if ((bits & PushActiveArea) != 0)
            {
                NormalizedRect? area;
                lock (_areaLock)
                {
                    area = _activeArea;
                }

                bool force = Interlocked.Exchange(ref _resendArea, 0) != 0;
                if (area is { } a && (force || a != _activeAreaSent))
                {
                    if (!await TryPushAsync(ActiveAreaMessage.FromNormalized(a)).ConfigureAwait(false))
                    {
                        return false;
                    }

                    _activeAreaSent = a;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException or NotSupportedException)
        {
            _channel.Abort();
            return false;
        }
    }

    /// <summary>
    /// One push write; false once the connection is closing. A drain may be mid-way when <see cref="CloseAsync"/>
    /// sends Bye and half-closes: a write after that would fail and abort, cutting the linger short.
    /// </summary>
    private async ValueTask<bool> TryPushAsync<T>(T message)
        where T : IControlMessage<T> =>
        RequestedReason is null && await _channel.TryWriteAsync(message).ConfigureAwait(false);

    /// <summary>The push writer died on an exception its drain doesn't expect (a bug): log it and end the session.</summary>
    private void OnPushFault(Exception exception)
    {
        NetLog.Unexpected(_log, _remote, exception);
        _ = CloseAsync(ByeReason.Normal, SessionEndReason.InternalError);
    }

    private void OnUdpAck(ulong highestSequence) => _ = SendUdpAckAsync(highestSequence);

    private void OnInputFault(Exception exception)
    {
        NetLog.InputFault(_log, DeviceId, exception);
        _ = CloseAsync(ByeReason.Normal, SessionEndReason.InternalError);
    }

    private void OnForgedFlood() => NetLog.ForgedFlood(_log);

    private async Task SendUdpAckAsync(ulong highestSequence)
    {
        try
        {
            _ = await _channel.TryWriteAsync(new UdpAckMessage(highestSequence)).ConfigureAwait(false); // false: closing, Bye was the last frame
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException or NotSupportedException)
        {
            _channel.Abort();
        }
    }

    private async Task PingAsync()
    {
        if (RequestedReason is not null)
        {
            // Closing: covers a ping timer created or firing while CloseAsync stops it.
            return;
        }

        try
        {
            _ = await _channel.TryWriteAsync(new PingMessage(NowUs())).ConfigureAwait(false); // false: closing, Bye was the last frame
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException or NotSupportedException)
        {
            _channel.Abort();
        }
    }

    private ulong NowUs() => (ulong)(_time.GetElapsedTime(_epoch).Ticks / TimeSpan.TicksPerMicrosecond);

    private void UpdateRtt(ulong sentUs, uint sessionId)
    {
        long received = _time.GetTimestamp();
        ulong now = (ulong)(_time.GetElapsedTime(_epoch, received).Ticks / TimeSpan.TicksPerMicrosecond);
        if (sentUs <= now)
        {
            Volatile.Write(ref _rttTicks, (long)(now - sentUs) * TimeSpan.TicksPerMicrosecond);
            if (_options.LatencyRecorder is { } latency)
            {
                // The Ping went out at _epoch + sentUs (Ping timestamps are microseconds since the epoch).
                long sent = _epoch + (long)((double)sentUs * _time.TimestampFrequency / 1e6);
                latency.RecordServerRtt(sessionId, sent, received);
            }
        }
    }
}
