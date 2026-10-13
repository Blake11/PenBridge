using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;
using SecretKind = PenBridge.Core.Security.PairingSecretKind;
using WireSecretKind = PenBridge.Core.Protocol.PairingSecretKind;

namespace PenBridge.Core.Net;

/// <summary>
/// The phone-side network engine: connects to a <see cref="PenBridgeServer"/>, pairs (PIN or QR token) or
/// authenticates with the stored <c>K_dev</c> and pinned fingerprint, then streams input with
/// <see cref="SendState(in PenSample?, ReadOnlySpan{TouchSample}, ReadOnlySpan{PenSample})"/>.
/// </summary>
/// <remarks>
/// <para>Send rules: every call sends the complete current state at once. While the pen is in contact or touches
/// are active, the state is resent every <see cref="PenBridgeClientOptions.ContactKeepaliveInterval"/>; while it
/// only hovers, every <see cref="PenBridgeClientOptions.HoverKeepaliveInterval"/>. Earlier samples batched by the
/// platform go into <c>history</c> (oldest first; only the newest 8 are kept). If no UdpAck arrives within
/// <see cref="PenBridgeClientOptions.UdpAckTimeout"/> of the first datagram, input switches to InputFrame over TLS.</para>
/// <para>Threads: <c>SendState</c> may be called from any thread (one lock serializes it with the keepalive and
/// fallback timers). The UDP send path is allocation-free. A background loop reads the control channel; timers
/// come from the injected <see cref="TimeProvider"/>.</para>
/// </remarks>
public sealed class PenBridgeClient : IAsyncDisposable
{
    private static readonly TimerCallback KeepaliveCallback = static s => ((Connection)s!).Owner.OnKeepalive((Connection)s);
    private static readonly TimerCallback AckTimeoutCallback = static s => ((Connection)s!).Owner.OnAckTimeout((Connection)s);
    private static readonly TimerCallback ProbeCallback = static s => ((Connection)s!).Owner.OnProbe((Connection)s);
    private static readonly TimerCallback PingCallback = static s => _ = PingAsync((Connection)s!);

    private readonly PenBridgeClientOptions _options;
    private readonly ClientCredentialStore _credentials;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly byte[] _deviceIdBytes = new byte[WireSizes.DeviceId];
    private readonly string _deviceName;
    private readonly Lock _sendLock = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private Connection? _connection;
    private int _disposed;

    public PenBridgeClient(PenBridgeClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.SecretStore);
        _ = options.Surface.PayloadSize; // throws for an invalid/default surface
        _options = options;
        _time = options.TimeProvider;
        _log = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger("PenBridge.Client");
        _credentials = new ClientCredentialStore(options.SecretStore);
        DeviceId = _credentials.GetOrCreateDeviceId();
        DeviceId.TryWriteBytes(_deviceIdBytes, bigEndian: true, out _);
        _deviceName = WireString.Truncate(options.DeviceName ?? string.Empty);
    }

    /// <summary>Raised (on a thread-pool thread) when an established session ends.</summary>
    public event EventHandler<ClientDisconnectedEventArgs>? Disconnected;

    public Guid DeviceId { get; }

    public ClientCredentialStore Credentials => _credentials;

    public bool IsConnected => Volatile.Read(ref _connection) is { Closed: false };

    /// <summary>Udp once the server acknowledged a datagram, Tls after the fallback, None before either.</summary>
    public InputTransport Transport
    {
        get
        {
            lock (_sendLock)
            {
                return _connection is not { } c ? InputTransport.None
                    : c.Transport == InputTransport.Tls ? InputTransport.Tls
                    : c.UdpAcked ? InputTransport.Udp
                    : InputTransport.None;
            }
        }
    }

    /// <summary>Last control-channel RTT, or null.</summary>
    public TimeSpan? RoundTripTime
    {
        get
        {
            long ticks = Volatile.Read(ref _connection) is { } c ? Volatile.Read(ref c.RttTicks) : -1;
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    /// <summary>The connected server's certificate fingerprint, or null.</summary>
    public byte[]? ServerFingerprint => Volatile.Read(ref _connection)?.Fingerprint.ToArray();

    /// <summary>The connected server's name (untrusted peer text), or null.</summary>
    public string? ServerName => Volatile.Read(ref _connection)?.ServerName;

    /// <summary>The negotiated TLS version of the current connection (ADR-0013: 1.2 or 1.3).</summary>
    public SslProtocols NegotiatedTlsProtocol => Volatile.Read(ref _connection)?.Protocol ?? SslProtocols.None;

    /// <summary>Fallback frames replaced by a newer state because the TLS writer was stalled (tests, diagnostics).</summary>
    internal long FallbackFramesDropped
    {
        get
        {
            lock (_sendLock)
            {
                return _connection?.FallbackDropped ?? 0;
            }
        }
    }

    /// <summary>Wall-clock age of the control write in progress, or null (tests).</summary>
    internal TimeSpan? ControlWriteInFlightFor => Volatile.Read(ref _connection)?.Channel.WriteInFlightFor;

    /// <summary>UdpAcks received on this connection (tests, diagnostics).</summary>
    internal long AcksReceived => Volatile.Read(ref _connection) is { } c ? Interlocked.Read(ref c.AcksReceived) : 0;

    /// <summary>Datagrams sealed so far on this connection (tests, diagnostics).</summary>
    internal long DatagramsSealed => Volatile.Read(ref _connection)?.DatagramsSealed ?? 0;

    /// <summary>Pairs with the 6-digit PIN shown on the Mac, then authenticates and starts the session.</summary>
    public Task PairWithPinAsync(string host, int port, string pin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (pin.Length != PinIssuer.PinLength || !pin.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("The PIN must be 6 digits.", nameof(pin));
        }

        return ConnectCoreAsync(host, port, pinnedFingerprint: null, SecretKind.Pin, Encoding.ASCII.GetBytes(pin), cancellationToken);
    }

    /// <summary>Pairs with a scanned QR payload (the TLS certificate must match its fingerprint).</summary>
    public Task PairWithQrAsync(PairingQrPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.Version.IsCompatibleWith(ProtocolVersion.Current))
        {
            throw new PenBridgeConnectException(ConnectFailure.IncompatibleVersion);
        }

        return ConnectCoreAsync(payload.Host, payload.Port, payload.Fingerprint.ToArray(), SecretKind.QrToken, payload.Token.ToArray(), cancellationToken);
    }

    /// <summary>Parses QR text and pairs with it.</summary>
    public Task PairWithQrAsync(string qrText, CancellationToken cancellationToken = default)
    {
        if (!PairingQrPayload.TryParse(qrText, out PairingQrPayload? payload))
        {
            throw new ArgumentException("Not a valid PenBridge pairing QR code.", nameof(qrText));
        }

        return PairWithQrAsync(payload!, cancellationToken);
    }

    /// <summary>Reconnects to a paired server: the certificate must match <paramref name="serverFingerprint"/>.</summary>
    public Task ConnectAsync(string host, int port, ReadOnlyMemory<byte> serverFingerprint, CancellationToken cancellationToken = default)
    {
        if (serverFingerprint.Length != CertificateFingerprint.Length)
        {
            throw new ArgumentException("Fingerprint must be 32 bytes.", nameof(serverFingerprint));
        }

        if (!_credentials.IsPaired(serverFingerprint.Span))
        {
            throw new PenBridgeConnectException(ConnectFailure.NotPaired);
        }

        return ConnectCoreAsync(host, port, serverFingerprint.ToArray(), pairKind: null, secret: null, cancellationToken);
    }

    /// <summary>Sends the current state now (pen if in range, all active touches).</summary>
    public bool SendState(in PenSample? pen, ReadOnlySpan<TouchSample> touches) => SendState(pen, touches, default);

    /// <summary>
    /// Sends the complete current state now, with earlier batched pen samples (oldest first; the newest 8 are kept),
    /// and schedules keepalives. Returns false when not connected. Throws for invalid samples or duplicate pointer ids.
    /// </summary>
    public bool SendState(in PenSample? pen, ReadOnlySpan<TouchSample> touches, ReadOnlySpan<PenSample> history)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(touches.Length, InputPayload.MaxTouches, nameof(touches));
        if (history.Length > InputPayload.MaxHistory)
        {
            history = history[^InputPayload.MaxHistory..];
        }

        Validate(pen, touches, history);
        lock (_sendLock)
        {
            if (_connection is not { Closed: false } c)
            {
                return false;
            }

            c.LastPen = pen;
            touches.CopyTo(c.LastTouches);
            c.LastTouchCount = touches.Length;
            bool active = IsActive(c);
            if (!active && c.WasActive)
            {
                c.RepeatsLeft = _options.ReleaseRepeats;
            }

            c.WasActive = active;
            bool sent = SendLocked(c, pen, touches, history);
            ArmKeepaliveLocked(c);
            return sent;
        }
    }

    /// <summary>Sends a new SurfaceInfo (e.g. after rotation).</summary>
    public async Task UpdateSurfaceAsync(SurfaceInfoMessage surface)
    {
        _ = surface.PayloadSize;
        Connection c = Volatile.Read(ref _connection) ?? throw new InvalidOperationException("Not connected.");
        await c.Channel.WriteAsync(surface).ConfigureAwait(false);
    }

    /// <summary>Sends Bye(Normal) (best effort, bounded) and closes the session.</summary>
    public async Task DisconnectAsync()
    {
        if (Volatile.Read(ref _connection) is not { } c)
        {
            return;
        }

        c.LocalClose = true;
        await c.Channel.TryWriteFarewellAsync(new ByeMessage(ByeReason.Normal)).ConfigureAwait(false);
        c.Channel.AbortHard();
        await c.Completed.Task.ConfigureAwait(false);
    }

    /// <summary>Closes at once: aborts the socket before taking any lock, so a stalled server can't delay it.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _connection) is { } c)
        {
            c.LocalClose = true;
            c.Channel.AbortHard();
            await c.Completed.Task.ConfigureAwait(false);
        }
    }

    /// <summary>Sends raw bytes on the session's UDP socket (tests: replay/tamper from the pinned source).</summary>
    internal void SendRawDatagram(ReadOnlySpan<byte> datagram)
    {
        lock (_sendLock)
        {
            _connection?.Udp.Send(datagram, SocketFlags.None);
        }
    }

    private static void Validate(in PenSample? pen, ReadOnlySpan<TouchSample> touches, ReadOnlySpan<PenSample> history)
    {
        if (pen.HasValue && !InputSampleCodec.IsValid(pen.GetValueOrDefault()))
        {
            throw new ArgumentException("Pen sample has out-of-range values.", nameof(pen));
        }

        foreach (ref readonly PenSample h in history)
        {
            if (!InputSampleCodec.IsValid(h))
            {
                throw new ArgumentException("History sample has out-of-range values.", nameof(history));
            }
        }

        for (int i = 1; i < touches.Length; i++)
        {
            for (int j = 0; j < i; j++)
            {
                if (touches[i].PointerId == touches[j].PointerId)
                {
                    throw new ArgumentException("Duplicate touch pointer id.", nameof(touches));
                }
            }
        }
    }

    private static bool InContact(Connection c) =>
        c.LastTouchCount > 0 || (c.LastPen is { } p && (p.State & PenState.Contact) != 0);

    private static bool IsActive(Connection c) =>
        InContact(c) || (c.LastPen is { } p && (p.State & PenState.InRange) != 0);

    private bool SendLocked(Connection c, in PenSample? pen, ReadOnlySpan<TouchSample> touches, ReadOnlySpan<PenSample> history)
    {
        uint timeUs = unchecked((uint)(_time.GetElapsedTime(c.Start).Ticks / TimeSpan.TicksPerMicrosecond));
        if (c.Transport == InputTransport.Tls)
        {
            // Never blocks: the frame goes into the bounded queue drained by the fallback writer.
            Span<byte> frame = c.ReserveFallbackSlot(out int slot);
            int n = InputPayload.Write(frame[ControlFrame.HeaderSize..], timeUs, pen, touches, history);
            ControlFrame.WriteHeader(frame, ControlMessageType.InputFrame, n);
            c.CommitFallbackSlot(slot, ControlFrame.HeaderSize + n);
            return true;
        }

        SendDatagramLocked(c, timeUs, pen, touches, history);
        if (!c.AckArmed)
        {
            // Fallback rule: the highest sequence sent when the timer is armed must be acked within the timeout.
            // The server acks at most 1/s but always sends a trailing ack for the last datagram of a burst, so a
            // working path answers within about 1 s plus the RTT, also when the stream stops.
            c.AckArmed = true;
            c.AwaitedSequence = c.Sequence;
            c.AckTimer.Change(_options.UdpAckTimeout, Timeout.InfiniteTimeSpan);
        }

        return true;
    }

    private void SendDatagramLocked(Connection c, uint timeUs, in PenSample? pen, ReadOnlySpan<TouchSample> touches, ReadOnlySpan<PenSample> history)
    {
        Span<byte> datagram = c.Datagram;
        new InputDatagramHeader(InputDatagramOptions.None, c.SessionId, ++c.Sequence).Write(datagram);
        int payloadLength = InputPayload.Write(datagram[InputDatagramHeader.Size..], timeUs, pen, touches, history);
        int total = c.Cipher.Seal(datagram, payloadLength);
        c.DatagramsSealed++;
        DatagramFilter? filter = _options.DatagramFilter;
        if (filter is not null && !filter(datagram[..total]))
        {
            return;
        }

        try
        {
            c.Udp.Send(datagram[..total], SocketFlags.None);
        }
        catch (SocketException)
        {
            // Lossy by design (e.g. ICMP unreachable on a connected UDP socket); the ack fallback covers a dead path.
        }
    }

    private void ArmKeepaliveLocked(Connection c)
    {
        TimeSpan due = InContact(c) ? _options.ContactKeepaliveInterval
            : IsActive(c) ? _options.HoverKeepaliveInterval
            : c.RepeatsLeft > 0 ? _options.ContactKeepaliveInterval
            : Timeout.InfiniteTimeSpan;
        c.Keepalive.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void OnKeepalive(Connection c)
    {
        lock (_sendLock)
        {
            if (c.Closed)
            {
                return;
            }

            if (!IsActive(c))
            {
                if (c.RepeatsLeft <= 0)
                {
                    return;
                }

                c.RepeatsLeft--;
            }

            SendLocked(c, c.LastPen, c.LastTouches.AsSpan(0, c.LastTouchCount), default);
            ArmKeepaliveLocked(c);
        }
    }

    private void OnAckTimeout(Connection c)
    {
        lock (_sendLock)
        {
            if (c.Closed || !c.AckArmed || c.Transport == InputTransport.Tls || c.AckedSequence >= c.AwaitedSequence)
            {
                return;
            }

            c.AckArmed = false;
            c.UdpAcked = false;
            c.Transport = InputTransport.Tls;
            NetLog.ClientTlsFallback(_log, _options.UdpAckTimeout);
            c.StartFallbackWriter();
            c.ProbeTimer.Change(_options.UdpProbeInterval, _options.UdpProbeInterval);

            // Deliver the current state over the new path right away.
            SendLocked(c, c.LastPen, c.LastTouches.AsSpan(0, c.LastTouchCount), default);
            ArmKeepaliveLocked(c);
        }
    }

    /// <summary>In fallback: sends the current state as one UDP datagram; an ack covering it switches back to UDP.</summary>
    private void OnProbe(Connection c)
    {
        lock (_sendLock)
        {
            if (c.Closed || c.Transport != InputTransport.Tls)
            {
                return;
            }

            uint timeUs = unchecked((uint)(_time.GetElapsedTime(c.Start).Ticks / TimeSpan.TicksPerMicrosecond));
            SendDatagramLocked(c, timeUs, c.LastPen, c.LastTouches.AsSpan(0, c.LastTouchCount), default);
            c.ProbeSequence = c.Sequence;
        }
    }

    private void OnUdpAck(Connection c, ulong highestSequence)
    {
        Interlocked.Increment(ref c.AcksReceived);
        lock (_sendLock)
        {
            if (c.Closed)
            {
                return;
            }

            if (c.Transport == InputTransport.Tls)
            {
                // Only an ack covering the latest probe proves the path works again (not a late pre-fallback ack).
                if (c.ProbeSequence == 0 || highestSequence < c.ProbeSequence)
                {
                    return;
                }

                c.Transport = InputTransport.Udp;
                c.ProbeTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                NetLog.ClientUdpRestored(_log);
            }

            c.UdpAcked = true;
            c.AckedSequence = Math.Max(c.AckedSequence, highestSequence);
            if (c.AckedSequence >= c.Sequence)
            {
                // Everything sent so far is acknowledged: nothing to wait for until the next send.
                c.AckArmed = false;
                c.AckTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            else
            {
                // Newer datagrams are in flight: wait for them (the server's trailing ack covers the last one).
                c.AckArmed = true;
                c.AwaitedSequence = c.Sequence;
                c.AckTimer.Change(_options.UdpAckTimeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private static async Task PingAsync(Connection c)
    {
        try
        {
            await c.Channel.WriteAsync(new PingMessage(c.NowUs())).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException or NotSupportedException)
        {
            c.Channel.Abort();
        }
    }

    private async Task ConnectCoreAsync(string host, int port, byte[]? pinnedFingerprint, SecretKind? pairKind, byte[]? secret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _connection) is not null)
            {
                throw new InvalidOperationException("Already connected.");
            }

            await HandshakeAsync(host, port, pinnedFingerprint, pairKind, secret, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (secret is not null)
            {
                CryptographicOperations.ZeroMemory(secret);
            }

            _connectGate.Release();
        }
    }

    private async Task HandshakeAsync(string host, int port, byte[]? pinned, SecretKind? pairKind, byte[]? secret, CancellationToken cancellationToken)
    {
        Socket socket = await ConnectSocketAsync(host, port, cancellationToken).ConfigureAwait(false);
        var channel = new ControlChannel(socket, _time);
        ITimer deadline = channel.StartDeadline(_options.ConnectTimeout);
        CancellationTokenRegistration registration = cancellationToken.Register(static s => ((ControlChannel)s!).Abort(), channel);
        byte[] deviceKey = new byte[KeySchedule.KeyLength];
        byte[] inputKey = new byte[KeySchedule.KeyLength];
        Socket? udp = null;
        Connection? connection = null;
        try
        {
            var tls = new SslClientAuthenticationOptions
            {
                TargetHost = host,
#pragma warning disable CA5398 // ADR-0013: TLS 1.2+ is a deliberate floor, not a pinned version.
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
#pragma warning restore CA5398
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,

                // Self-signed server: trust comes from the pinned fingerprint, or (unpinned PIN pairing) from the
                // J-PAKE confirmation, which binds the fingerprint seen here.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && (pinned is null || CertificateFingerprint.Matches(certificate, pinned)),
            };
            try
            {
                await channel.Ssl.AuthenticateAsClientAsync(tls, channel.Token).ConfigureAwait(false);
            }
            catch (AuthenticationException ex)
            {
                throw new PenBridgeConnectException(pinned is null ? ConnectFailure.ConnectionFailed : ConnectFailure.ServerIdentityMismatch, innerException: ex);
            }

            byte[] fingerprint = CertificateFingerprint.Compute(channel.Ssl.RemoteCertificate!);
            await channel.WriteAsync(new HelloMessage(ProtocolVersion.Current, _deviceIdBytes, _deviceName, ProtocolCapabilities.None)).ConfigureAwait(false);
            HelloAckMessage ack = (await ReadHandshakeAsync(channel).ConfigureAwait(false)).Read<HelloAckMessage>();
            if (!ack.Version.IsCompatibleWith(ProtocolVersion.Current))
            {
                await channel.TryWriteFarewellAsync(new ErrorMessage(ProtocolErrorCode.IncompatibleVersion, "Unsupported protocol major version.")).ConfigureAwait(false);
                throw new PenBridgeConnectException(ConnectFailure.IncompatibleVersion);
            }

            byte[] challenge = ack.Challenge.ToArray();
            if (pairKind is { } kind)
            {
                await PairAsync(channel, kind, secret!, challenge, fingerprint, deviceKey).ConfigureAwait(false);
                _credentials.SaveDeviceKey(fingerprint, deviceKey);
                NetLog.ClientPaired(_log, PeerText.Sanitize(ack.ServerName));
            }
            else if (!_credentials.TryGetDeviceKey(fingerprint, deviceKey))
            {
                throw new PenBridgeConnectException(ConnectFailure.NotPaired);
            }

            byte[] clientNonce = RandomNumberGenerator.GetBytes(WireSizes.Nonce);
            byte[] proof = new byte[ReconnectAuth.ProofLength];
            ReconnectAuth.ComputeProof(deviceKey, challenge, clientNonce, fingerprint, proof);
            await channel.WriteAsync(new AuthMessage(_deviceIdBytes, proof, clientNonce)).ConfigureAwait(false);
            SessionStartMessage start = (await ReadHandshakeAsync(channel).ConfigureAwait(false)).Read<SessionStartMessage>();
            KeySchedule.DeriveInputKey(deviceKey, clientNonce, start.ServerNonce.Span, start.SessionId, inputKey);

            IPAddress server = ((IPEndPoint)socket.RemoteEndPoint!).Address;
            if (server.IsIPv4MappedToIPv6)
            {
                server = server.MapToIPv4();
            }

            udp = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
            {
                SendTimeout = (int)ControlChannel.SyncSendTimeout.TotalMilliseconds,
            };
            udp.Connect(new IPEndPoint(server, start.UdpPort));
            connection = new Connection(this, channel, udp, new InputCipher(inputKey, start.SessionId), start.SessionId, fingerprint, ack.ServerName, channel.Ssl.SslProtocol, _time, _options.FallbackQueueCapacity);

            await channel.WriteAsync(_options.Surface).ConfigureAwait(false);
            await deadline.DisposeAsync().ConfigureAwait(false);
            lock (_sendLock)
            {
                _connection = connection;
            }

            if (_options.PingInterval != Timeout.InfiniteTimeSpan)
            {
                connection.PingTimer = _time.CreateTimer(PingCallback, connection, _options.PingInterval, _options.PingInterval);
            }

            NetLog.ClientConnected(_log, PeerText.Sanitize(ack.ServerName), connection.Protocol);
            _ = ReadLoopAsync(connection);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await FailAsync().ConfigureAwait(false);
            throw new PenBridgeConnectException(channel.TimedOut ? ConnectFailure.Timeout : ConnectFailure.ConnectionClosed);
        }
        catch (ProtocolException ex)
        {
            await channel.TryWriteFarewellAsync(new ErrorMessage(ex.Code, "Protocol violation.")).ConfigureAwait(false);
            await FailAsync().ConfigureAwait(false);
            throw new PenBridgeConnectException(ConnectFailure.ProtocolError, innerException: ex);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            await FailAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new PenBridgeConnectException(channel.TimedOut ? ConnectFailure.Timeout : ConnectFailure.ConnectionClosed, innerException: ex);
        }
        catch
        {
            await FailAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(deviceKey);
            CryptographicOperations.ZeroMemory(inputKey);
        }

        async Task FailAsync()
        {
            await deadline.DisposeAsync().ConfigureAwait(false);
            if (connection is not null)
            {
                await connection.DisposeResourcesAsync().ConfigureAwait(false);
            }
            else
            {
                udp?.Dispose();
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<Socket> ConnectSocketAsync(string host, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            SendTimeout = (int)ControlChannel.SyncSendTimeout.TotalMilliseconds,
        };
        if (_options.TcpSendBufferSize is { } size)
        {
            socket.SendBufferSize = size;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using ITimer deadline = _time.CreateTimer(
            static s =>
            {
                try
                {
                    ((CancellationTokenSource)s!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            },
            cts,
            _options.ConnectTimeout,
            Timeout.InfiniteTimeSpan);
        try
        {
            await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return socket;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new PenBridgeConnectException(ConnectFailure.Timeout);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new PenBridgeConnectException(ConnectFailure.ConnectionFailed, innerException: ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task PairAsync(ControlChannel channel, SecretKind kind, byte[] secret, byte[] challenge, byte[] fingerprint, byte[] deviceKey)
    {
        using var session = new PairingSession(PairingRole.Client, kind, secret, DeviceId, challenge, fingerprint);
        await channel.WriteAsync(new ClientPairRound1Message(session.CreateRound1(), (WireSecretKind)(byte)kind)).ConfigureAwait(false);

        ServerPairRound1Message round1 = (await ReadHandshakeAsync(channel).ConfigureAwait(false)).Read<ServerPairRound1Message>();
        if (!session.TryProcessPeerRound1(round1.Round1.Span))
        {
            throw new PenBridgeConnectException(ConnectFailure.PairingFailed);
        }

        await channel.WriteAsync(new ClientPairRound2Message(session.CreateRound2())).ConfigureAwait(false);
        ServerPairRound2Message round2 = (await ReadHandshakeAsync(channel).ConfigureAwait(false)).Read<ServerPairRound2Message>();
        if (!session.TryProcessPeerRound2(round2.Round2.Span))
        {
            throw new PenBridgeConnectException(ConnectFailure.PairingFailed);
        }

        await channel.WriteAsync(new ClientPairConfirmMessage(session.CreateConfirm())).ConfigureAwait(false);

        // A wrong secret or a relay makes the server answer PairResult(failed) here (thrown by ReadHandshakeAsync).
        ServerPairConfirmMessage confirm = (await ReadHandshakeAsync(channel).ConfigureAwait(false)).Read<ServerPairConfirmMessage>();
        if (!session.TryVerifyPeerConfirm(confirm.Mac.Span))
        {
            throw new PenBridgeConnectException(ConnectFailure.PairingFailed);
        }

        PairResultMessage result = (await ReadHandshakeAsync(channel).ConfigureAwait(false)).Read<PairResultMessage>();
        if (result.Status != PairResultStatus.Ok)
        {
            throw new PenBridgeConnectException(ConnectFailure.ProtocolError);
        }

        session.CopyDeviceKey(deviceKey);
    }

    /// <summary>Reads a handshake frame, converting Error, Busy, Bye and failed PairResults into exceptions.</summary>
    private async ValueTask<ReceivedControlFrame> ReadHandshakeAsync(ControlChannel channel)
    {
        ReceivedControlFrame frame = await channel.ReadAsync(_options.ReadTimeout).ConfigureAwait(false)
            ?? throw new PenBridgeConnectException(ConnectFailure.ConnectionClosed);
        switch (frame.Type)
        {
            case ControlMessageType.Error:
                if (!ErrorMessage.TryRead(frame.Payload.Span, out ErrorMessage error))
                {
                    throw new PenBridgeConnectException(ConnectFailure.ProtocolError);
                }

                throw new PenBridgeConnectException(
                    error.Code switch
                    {
                        ProtocolErrorCode.AuthFailed => ConnectFailure.AuthFailed,
                        ProtocolErrorCode.PairingUnavailable => ConnectFailure.PairingUnavailable,
                        ProtocolErrorCode.IncompatibleVersion => ConnectFailure.IncompatibleVersion,
                        ProtocolErrorCode.Timeout => ConnectFailure.Timeout,
                        _ => ConnectFailure.PeerError,
                    },
                    peerErrorCode: error.Code);
            case ControlMessageType.Busy:
                throw new PenBridgeConnectException(ConnectFailure.Busy);
            case ControlMessageType.Bye:
                throw new PenBridgeConnectException(ConnectFailure.ConnectionClosed);
            case ControlMessageType.PairResult:
                PairResultMessage result = frame.Read<PairResultMessage>();
                return result.Status switch
                {
                    PairResultStatus.Ok => frame,
                    PairResultStatus.Expired => throw new PenBridgeConnectException(ConnectFailure.PairingExpired),
                    _ => throw new PenBridgeConnectException(ConnectFailure.PairingFailed, result.AttemptsLeft),
                };
            default:
                return frame;
        }
    }

    private async Task ReadLoopAsync(Connection c)
    {
        SessionEndReason reason = SessionEndReason.ConnectionLost;
        ByeReason? bye = null;
        ProtocolErrorCode? errorCode = null;
        try
        {
            bool running = true;
            while (running)
            {
                if (await c.Channel.ReadAsync(_options.PeerTimeout).ConfigureAwait(false) is not { } frame)
                {
                    reason = SessionEndReason.PeerClosed;
                    break;
                }

                switch (frame.Type)
                {
                    case ControlMessageType.UdpAck:
                        OnUdpAck(c, frame.Read<UdpAckMessage>().HighestSequence);
                        break;
                    case ControlMessageType.Ping:
                        await c.Channel.WriteAsync(new PongMessage(frame.Read<PingMessage>().TimestampUs)).ConfigureAwait(false);
                        break;
                    case ControlMessageType.Pong:
                        c.UpdateRtt(frame.Read<PongMessage>().TimestampUs);
                        break;
                    case ControlMessageType.Bye:
                        bye = frame.Read<ByeMessage>().Reason;
                        reason = bye switch
                        {
                            ByeReason.TakenOver => SessionEndReason.TakenOver,
                            ByeReason.Unpaired => SessionEndReason.Unpaired,
                            ByeReason.Shutdown => SessionEndReason.Shutdown,
                            _ => SessionEndReason.PeerClosed,
                        };
                        if (bye == ByeReason.Unpaired)
                        {
                            // The Mac forgot this device; the stored key can never authenticate again.
                            _credentials.Forget(c.Fingerprint);
                        }

                        running = false;
                        break;
                    case ControlMessageType.Error:
                        if (ErrorMessage.TryRead(frame.Payload.Span, out ErrorMessage error))
                        {
                            errorCode = error.Code;
                            NetLog.PeerError(_log, error.Code, PeerText.Sanitize(error.Message));
                        }

                        reason = SessionEndReason.PeerError;
                        running = false;
                        break;
                    default:
                        throw new ProtocolException(ProtocolErrorCode.UnexpectedMessage, "Unexpected control message during a session.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = c.LocalClose ? SessionEndReason.LocalDisconnect : c.Channel.TimedOut ? SessionEndReason.Timeout : SessionEndReason.ConnectionLost;
        }
        catch (ProtocolException ex)
        {
            reason = SessionEndReason.ProtocolError;
            await c.Channel.TryWriteFarewellAsync(new ErrorMessage(ex.Code, "Protocol violation.")).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            reason = c.LocalClose ? SessionEndReason.LocalDisconnect : SessionEndReason.ConnectionLost;
        }
        finally
        {
            lock (_sendLock)
            {
                c.Closed = true;
                if (ReferenceEquals(_connection, c))
                {
                    _connection = null;
                }
            }

            await c.DisposeResourcesAsync().ConfigureAwait(false);
            NetLog.ClientDisconnected(_log, reason);
            c.Completed.TrySetResult();
            try
            {
                Disconnected?.Invoke(this, new ClientDisconnectedEventArgs(reason, bye, errorCode));
            }
#pragma warning disable CA1031 // A faulty UI handler must not crash the read loop's thread.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    /// <summary>State of one connection. Mutable fields are guarded by the client's send lock unless noted.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001", Justification = "The signal SemaphoreSlim never allocates a wait handle; resources are released by DisposeResourcesAsync.")]
    private sealed class Connection
    {
        public Connection(PenBridgeClient owner, ControlChannel channel, Socket udp, InputCipher cipher, uint sessionId, byte[] fingerprint, string serverName, SslProtocols protocol, TimeProvider time, int fallbackCapacity)
        {
            _slots = new byte[Math.Max(1, fallbackCapacity)][];
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = new byte[FrameSize];
            }

            _lengths = new int[_slots.Length];
            Owner = owner;
            Channel = channel;
            Udp = udp;
            Cipher = cipher;
            SessionId = sessionId;
            Fingerprint = fingerprint;
            ServerName = serverName;
            Protocol = protocol;
            Time = time;
            Start = time.GetTimestamp();
            Keepalive = time.CreateTimer(KeepaliveCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            AckTimer = time.CreateTimer(AckTimeoutCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            ProbeTimer = time.CreateTimer(ProbeCallback, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        private const int FrameSize = ControlFrame.HeaderSize + InputPayload.MaxSize;

        private readonly byte[][] _slots;
        private readonly int[] _lengths;
        private readonly byte[] _writerBuffer = new byte[FrameSize];
        private readonly SemaphoreSlim _signal = new(0);
        private int _head;
        private int _count;
        private Task? _writer;

        /// <summary>Fallback frames dropped because the queue was full (a newer full state replaced them).</summary>
        public long FallbackDropped { get; private set; }

        /// <summary>
        /// Under the send lock: the buffer for the next fallback frame. When the queue is full (the server stopped
        /// reading), every queued frame is dropped: each frame is the complete state, so only the newest matters.
        /// </summary>
        public Span<byte> ReserveFallbackSlot(out int slot)
        {
            if (_count == _slots.Length)
            {
                FallbackDropped += _count;
                _count = 0;
            }

            slot = (_head + _count) % _slots.Length;
            return _slots[slot];
        }

        /// <summary>Under the send lock: queues the frame written into <paramref name="slot"/> and wakes the writer.</summary>
        public void CommitFallbackSlot(int slot, int length)
        {
            _lengths[slot] = length;
            _count++;
            _signal.Release();
        }

        /// <summary>Under the send lock: starts the single background writer once.</summary>
        public void StartFallbackWriter() => _writer ??= Task.Run(WriteFallbackAsync);

        private bool TryDequeue(out int length)
        {
            lock (Owner._sendLock)
            {
                if (_count == 0 || Closed)
                {
                    length = 0;
                    return false;
                }

                length = _lengths[_head];
                _slots[_head].AsSpan(0, length).CopyTo(_writerBuffer);
                _head = (_head + 1) % _slots.Length;
                _count--;
                return true;
            }
        }

        private async Task WriteFallbackAsync()
        {
            try
            {
                while (true)
                {
                    await _signal.WaitAsync(Channel.Token).ConfigureAwait(false);
                    while (TryDequeue(out int length))
                    {
                        await Channel.WriteFrameAsync(_writerBuffer.AsMemory(0, length)).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException or NotSupportedException)
            {
                Channel.Abort();
            }
        }

        public PenBridgeClient Owner { get; }

        public ControlChannel Channel { get; }

        public Socket Udp { get; }

        public InputCipher Cipher { get; }

        public uint SessionId { get; }

        public byte[] Fingerprint { get; }

        public string ServerName { get; }

        public SslProtocols Protocol { get; }

        public TimeProvider Time { get; }

        public long Start { get; }

        public ITimer Keepalive { get; }

        public ITimer AckTimer { get; }

        public ITimer ProbeTimer { get; }

        public ITimer? PingTimer { get; set; }

        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[] Datagram { get; } = new byte[InputDatagram.MaxSize];

        public TouchSample[] LastTouches { get; } = new TouchSample[InputPayload.MaxTouches];

#pragma warning disable SA1401, CA1051 // Hot-path state of a private class; fields keep it simple and ref-friendly.
        public PenSample? LastPen;
        public int LastTouchCount;
        public bool WasActive;
        public int RepeatsLeft;
        public ulong Sequence;
        public ulong ProbeSequence;
        public ulong AwaitedSequence;
        public ulong AckedSequence;
        public long AcksReceived;
        public long DatagramsSealed;
        public InputTransport Transport = InputTransport.Udp;
        public bool UdpAcked;
        public bool AckArmed;
        public volatile bool Closed;
        public volatile bool LocalClose;
        public long RttTicks = -1;
#pragma warning restore SA1401, CA1051

        public ulong NowUs() => (ulong)(Time.GetElapsedTime(Start).Ticks / TimeSpan.TicksPerMicrosecond);

        public void UpdateRtt(ulong sentUs)
        {
            ulong now = NowUs();
            if (sentUs <= now)
            {
                Volatile.Write(ref RttTicks, (long)(now - sentUs) * TimeSpan.TicksPerMicrosecond);
            }
        }

        public async ValueTask DisposeResourcesAsync()
        {
            await Keepalive.DisposeAsync().ConfigureAwait(false);
            await AckTimer.DisposeAsync().ConfigureAwait(false);
            await ProbeTimer.DisposeAsync().ConfigureAwait(false);
            if (PingTimer is not null)
            {
                await PingTimer.DisposeAsync().ConfigureAwait(false);
            }

            lock (Owner._sendLock)
            {
                Closed = true;
                Cipher.Dispose();
                Udp.Dispose();
            }

            await Channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
