using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
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

    private readonly PenBridgeServer _server;
    private readonly PenBridgeServerOptions _options;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly ControlChannel _channel;
    private readonly EndPoint? _remote;
    private readonly IPAddress _address;
    private readonly long _epoch;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[] _deviceIdBytes = [];
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

        // Armed at accept: the whole pre-session phase is bounded from the first byte.
        _handshake = _channel.StartDeadline(_options.HandshakeTimeout);
    }

    public Guid DeviceId { get; private set; }

    public string DeviceName { get; private set; } = string.Empty;

    public SessionInput? Input { get; private set; }

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

        byte[] challenge = RandomNumberGenerator.GetBytes(WireSizes.Challenge);
        await _channel.WriteAsync(new HelloAckMessage(ProtocolVersion.Current, _server.ServerName, ProtocolCapabilities.None, challenge)).ConfigureAwait(false);

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
                OnInputFault);
            input.ForgedDatagramFlood += OnForgedFlood;
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
            _info = new SessionInfo(DeviceId, DeviceName, _remote);
            NetLog.SessionStarted(_log, DeviceId, PeerText.Sanitize(DeviceName));
            _server.RaiseSessionStarted(_info);
            if (_options.PingInterval != Timeout.InfiniteTimeSpan)
            {
                Volatile.Write(ref _pingTimer, _time.CreateTimer(PingCallback, this, _options.PingInterval, _options.PingInterval));
            }

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
                    input.ConfigureSurface(surface.WidthPx, surface.HeightPx);
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
                    await _channel.WriteAsync(new PongMessage(frame.Read<PingMessage>().TimestampUs)).ConfigureAwait(false);
                    break;
                case ControlMessageType.Pong:
                    UpdateRtt(frame.Read<PongMessage>().TimestampUs);
                    break;
                case ControlMessageType.Bye:
                    return SessionEndReason.PeerClosed;
                case ControlMessageType.Error:
                    LogPeerError(frame);
                    return SessionEndReason.PeerError;
                default:
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
            await _channel.WriteAsync(new UdpAckMessage(highestSequence)).ConfigureAwait(false);
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
            await _channel.WriteAsync(new PingMessage(NowUs())).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException or NotSupportedException)
        {
            _channel.Abort();
        }
    }

    private ulong NowUs() => (ulong)(_time.GetElapsedTime(_epoch).Ticks / TimeSpan.TicksPerMicrosecond);

    private void UpdateRtt(ulong sentUs)
    {
        ulong now = NowUs();
        if (sentUs <= now)
        {
            Volatile.Write(ref _rttTicks, (long)(now - sentUs) * TimeSpan.TicksPerMicrosecond);
        }
    }
}
