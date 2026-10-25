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
using PenBridge.Core.Mirror;
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
/// platform go into <c>history</c> (oldest first; only the newest 8 are kept). When the state becomes empty it is
/// repeated <see cref="PenBridgeClientOptions.ReleaseRepeats"/> times; a frame that ended with a whole click is
/// resent as it was instead, if the server dedupes samples (#92). With <see cref="ProtocolCapabilities.FingerTouches"/>
/// negotiated (ADR-0026) a touch lift survives one lost datagram: a frame that lifts a touch contact is preceded by
/// the previous state once more unless a keepalive already resent it, and the repeats after a lift that emptied the
/// state resend that frame with its own time. If no UdpAck arrives within
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
    private int _qosWarned;

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

    /// <summary>
    /// Raised on the read-loop thread when the Mac sends its settings (<see cref="ProtocolCapabilities.RemoteSettings"/>,
    /// ADR-0030): after SessionStart, after every change and in reply to <see cref="UpdateSettingsAsync"/>. Redraw from
    /// it (<see cref="Settings.MacSettingValues.FromEntries"/>); it is the truth. Handlers must be quick.
    /// </summary>
    public event EventHandler<SettingsSnapshotEventArgs>? SettingsSnapshotReceived;

    /// <summary>Raised on the read-loop thread when the Mac sends the displays it can target (before its first snapshot and on changes).</summary>
    public event EventHandler<DisplayListEventArgs>? DisplayListReceived;

    /// <summary>Raised on the read-loop thread when the Mac sends the surface's active area (<see cref="ProtocolCapabilities.ActiveArea"/>, ADR-0031).</summary>
    public event EventHandler<ActiveAreaEventArgs>? ActiveAreaReceived;

    /// <summary>
    /// Raised on the read-loop thread with every MirrorConfig (a new epoch): reconfigure the decoder for this codec and
    /// size. Video of the epoch follows on <see cref="VideoFrameReady"/>, starting with a keyframe. Handlers must be quick.
    /// </summary>
    public event EventHandler<MirrorStreamConfig>? MirrorConfigReceived;

    /// <summary>
    /// Raised with the Mac's MirrorState (read-loop thread), and with Unavailable/TlsFallback when this client falls
    /// back to TLS while mirroring (thread pool), or with Unavailable/None when the phone's video socket can't open
    /// (thread pool; logged once; call <see cref="StartMirror"/> again to retry). Anything but Streaming means no video
    /// until the next MirrorConfig.
    /// </summary>
    public event EventHandler<MirrorStateChangedEventArgs>? MirrorStateChanged;

    /// <summary>
    /// Raised on Core's video receive thread with each reassembled access unit, in order, keyframe first after every
    /// epoch start or loss. The frame is a handle to pooled memory: the handler owns it and must
    /// <see cref="VideoFrame.Dispose"/> it once queued to the decoder (or copied); until then it holds reassembly memory,
    /// and with four frames held new ones are dropped. A repeated Dispose is ignored. Without a handler, or when the
    /// handler throws, Core disposes the frame. Handlers must not block (queue to the decoder thread).
    /// </summary>
    public event EventHandler<VideoFrame>? VideoFrameReady;

    public Guid DeviceId { get; }

    /// <summary>
    /// The client's Ping clock in microseconds since this connection's session start (0 when not connected): the clock
    /// of <see cref="ReportFrameRendered"/>'s <c>renderedAtUs</c>, so the Mac can relate renders to its captures.
    /// </summary>
    public ulong NowUs => Volatile.Read(ref _connection)?.NowUs() ?? 0;

    /// <summary>The mirroring configuration of the current epoch, or null.</summary>
    public MirrorStreamConfig? MirrorConfig => Volatile.Read(ref _connection)?.Mirror?.Config;

    /// <summary>
    /// Mirroring counters of the current connection: datagrams, frames complete and dropped, KeyframeRequests, Probes
    /// and MirrorReports sent (default when not connected or mirroring wasn't negotiated). Thread-safe; a snapshot.
    /// </summary>
    public MirrorClientStats MirrorStats => Volatile.Read(ref _connection)?.Mirror?.Stats ?? default;

    /// <summary>Video-path counters of the current connection (tests, diagnostics).</summary>
    internal MirrorCounters MirrorCounters => Volatile.Read(ref _connection)?.Mirror?.Counters ?? default;

    /// <summary>The mirroring of the current connection (tests).</summary>
    internal ClientMirror? Mirror => Volatile.Read(ref _connection)?.Mirror;

    /// <summary>
    /// Asks the Mac for video (MirrorStart; protocol 1.3, <see cref="ProtocolCapabilities.ScreenMirror"/>): opens a video
    /// socket and its receive thread on the thread pool (MirrorStart goes out once it has a port), then follows MirrorConfig and MirrorState on its own (Probes, KeyframeRequests,
    /// MirrorReports). Call it again with new limits to send another MirrorStart. In TLS fallback it waits for UDP
    /// (raising Unavailable/TlsFallback). False, without sending, when not connected or the Mac doesn't offer mirroring.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A size or fps outside the wire range (1..65535, 1..255).</exception>
    public bool StartMirror(MirrorRequest request)
    {
        _ = request.ToMessage(1);
        if (Volatile.Read(ref _connection) is not { Closed: false, Mirror: { } mirror })
        {
            return false;
        }

        mirror.Start(request);
        return true;
    }

    /// <summary>Stops asking for video (MirrorStop) and closes the video socket. No-op when not mirroring.</summary>
    public void StopMirror() => Volatile.Read(ref _connection)?.Mirror?.Stop();

    /// <summary>
    /// The head rendered frame <paramref name="frameNumber"/> of epoch <paramref name="epoch"/>
    /// (<see cref="VideoFrame.Epoch"/>) at <paramref name="renderedAtUs"/> (<see cref="NowUs"/>); reported to the Mac in
    /// the next MirrorReport (video latency). A frame of another epoch than the current one is ignored, and a new epoch
    /// forgets the previous render, so a frame number never meets the wrong epoch's capture time. Thread-safe, cheap.
    /// </summary>
    public void ReportFrameRendered(byte epoch, uint frameNumber, ulong renderedAtUs) =>
        Volatile.Read(ref _connection)?.Mirror?.ReportFrameRendered(epoch, frameNumber, renderedAtUs);

    /// <summary>
    /// <see cref="ReportFrameRendered(byte, uint, ulong)"/> for the current epoch. A number above the last frame handed
    /// out in that epoch is ignored; prefer the epoch overload, which also catches a late render of an older epoch.
    /// </summary>
    public void ReportFrameRendered(uint frameNumber, ulong renderedAtUs) =>
        Volatile.Read(ref _connection)?.Mirror?.ReportFrameRendered(frameNumber, renderedAtUs);

    /// <summary>
    /// Asks the Mac for a keyframe because the phone's decoder lost frames on its own (its queue overflowed, it
    /// restarted). Like a network loss: no frame but a keyframe is handed out next, and a KeyframeRequest for the current
    /// epoch goes out now and every 250 ms until one arrives. No-op when not mirroring. Thread-safe, non-blocking.
    /// </summary>
    public void RequestKeyframe() => Volatile.Read(ref _connection)?.Mirror?.RequestKeyframe();

    internal void RaiseMirrorConfig(MirrorStreamConfig config) => RaiseRemote(MirrorConfigReceived, config);

    internal void RaiseMirrorState(MirrorState state, MirrorReason reason) =>
        RaiseRemote(MirrorStateChanged, new MirrorStateChangedEventArgs(state, reason));

    /// <summary>Hands a frame to the head; disposes it when nobody takes it or the handler throws.</summary>
    internal void RaiseVideoFrame(VideoFrame frame)
    {
        if (VideoFrameReady is not { } handler)
        {
            frame.Dispose();
            return;
        }

        try
        {
            handler(this, frame);
        }
#pragma warning disable CA1031 // A faulty decoder handler must not stop the video thread.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            frame.Dispose();
            NetLog.Unexpected(_log, null, ex);
        }
    }

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

    /// <summary>The Mac's latest settings snapshot on this connection, or null (not connected, not negotiated, or none yet).</summary>
    public IReadOnlyList<SettingEntry>? LastSettingsSnapshot => Volatile.Read(ref _connection)?.Remote.Snapshot;

    /// <summary>The Mac's latest display list on this connection, or null.</summary>
    public IReadOnlyList<DisplayListEntry>? LastDisplayList => Volatile.Read(ref _connection)?.Remote.Displays;

    /// <summary>The latest active area on this connection, or null (then the whole surface is active).</summary>
    public NormalizedRect? LastActiveArea => Volatile.Read(ref _connection)?.Remote.ActiveArea;

    /// <summary>Capabilities the connected server advertised in HelloAck (None when not connected).</summary>
    public ProtocolCapabilities ServerCapabilities => Volatile.Read(ref _connection)?.ServerCapabilities ?? ProtocolCapabilities.None;

    /// <summary>
    /// Capabilities both sides advertised for the current connection (None when not connected). Configure the input
    /// path from it: with <see cref="ProtocolCapabilities.FingerTouches"/>, build the <see cref="InputFilter"/> with
    /// <see cref="InputFilterOptions.FingerTouches"/> (ADR-0026). Fixed for a connection; re-read after reconnecting.
    /// </summary>
    public ProtocolCapabilities NegotiatedCapabilities => Volatile.Read(ref _connection)?.Negotiated ?? ProtocolCapabilities.None;

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

            bool hadContactTouch = HasContactTouch(c);
            if (c.LastStateSends == 1 && (c.Negotiated & ProtocolCapabilities.FingerTouches) != 0
                && c.Transport == InputTransport.Udp && LiftsContact(c, touches))
            {
                // The state before a lift went out only once: send it again first, so one lost datagram can't hide
                // a quick tap's only contact frame from the server (an identical full state changes nothing there).
                SendLocked(c, NextTimeUsLocked(c), c.LastPen, c.LastTouches.AsSpan(0, c.LastTouchCount), default);
            }

            c.LastPen = pen;
            touches.CopyTo(c.LastTouches);
            c.LastTouchCount = touches.Length;
            c.LastStateSends = 1;
            bool active = IsActive(c);
            if (!active && c.WasActive)
            {
                c.RepeatsLeft = _options.ReleaseRepeats;
            }

            c.WasActive = active;
            uint timeUs = NextTimeUsLocked(c);
            c.HasReleaseTime = !active && hadContactTouch && history.IsEmpty && c.RepeatsLeft > 0
                && (c.Negotiated & ProtocolCapabilities.FingerTouches) != 0;
            c.ReleaseTimeUs = timeUs;
            if (RecordClickLocked(c, active, timeUs, pen, history))
            {
                // A whole click whose finger was held back since it landed: nothing active was sent before it.
                c.RepeatsLeft = _options.ReleaseRepeats;
                if (c.RepeatsLeft <= 0)
                {
                    c.ClickHistoryCount = 0; // no repeats configured: nothing will resend it
                }
            }

            bool sent = SendLocked(c, timeUs, pen, touches, history);
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

    /// <summary>
    /// Asks the Mac to change settings (ADR-0030). It applies the valid entries and answers with a snapshot
    /// (<see cref="SettingsSnapshotReceived"/>). False, without sending, when not connected or the session didn't negotiate
    /// <see cref="ProtocolCapabilities.RemoteSettings"/>, or when the write failed (the connection then closes).
    /// </summary>
    /// <exception cref="ArgumentException">Not 1..<see cref="SettingsWire.MaxEntries"/> entries.</exception>
    public async Task<bool> UpdateSettingsAsync(IReadOnlyList<SettingEntry> entries)
    {
        var message = new SettingsUpdateMessage(entries);
        if (Volatile.Read(ref _connection) is not { Closed: false } c || (c.Negotiated & ProtocolCapabilities.RemoteSettings) == 0)
        {
            return false;
        }

        try
        {
            await c.Channel.WriteAsync(message).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException or NotSupportedException)
        {
            c.Channel.Abort();
            return false;
        }
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

    /// <summary>Writes any control message on the session's channel (tests: protocol violations after SessionStart).</summary>
    internal async Task SendRawControlAsync<T>(T message)
        where T : IControlMessage<T>
    {
        Connection c = Volatile.Read(ref _connection) ?? throw new InvalidOperationException("Not connected.");
        await c.Channel.WriteAsync(message).ConfigureAwait(false);
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

    /// <summary>
    /// The pen or a touch is in contact. A touch withdrawn by the phone filter (present, <c>Contact=false</c>, ADR-0024)
    /// is not: after a platform cancel no further input event may come, so its state must not be resent forever.
    /// </summary>
    private static bool InContact(Connection c) =>
        HasContactTouch(c) || (c.LastPen is { } p && (p.State & PenState.Contact) != 0);

    /// <summary>A touch in contact in the last sent state is gone from <paramref name="touches"/> or no longer in contact.</summary>
    private static bool LiftsContact(Connection c, ReadOnlySpan<TouchSample> touches)
    {
        for (int i = 0; i < c.LastTouchCount; i++)
        {
            TouchSample old = c.LastTouches[i];
            if (!old.Contact)
            {
                continue;
            }

            bool still = false;
            foreach (ref readonly TouchSample t in touches)
            {
                still |= t.PointerId == old.PointerId && t.Contact;
            }

            if (!still)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasContactTouch(Connection c)
    {
        for (int i = 0; i < c.LastTouchCount; i++)
        {
            if (c.LastTouches[i].Contact)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsActive(Connection c) =>
        InContact(c) || (c.LastPen is { } p && (p.State & PenState.InRange) != 0);

    /// <summary>
    /// The frame time for a new frame: µs since the session start, strictly after the previous new frame's (wrapping),
    /// so the server's sample dedupe never mistakes a new frame's history for a resend.
    /// </summary>
    private static uint NextTimeUsLocked(Connection c)
    {
        uint timeUs = unchecked((uint)(c.Time.GetElapsedTime(c.Start).Ticks / TimeSpan.TicksPerMicrosecond));
        if (c.HasTime && unchecked((int)(timeUs - c.LastTimeUs)) <= 0)
        {
            timeUs = unchecked(c.LastTimeUs + 1);
        }

        c.HasTime = true;
        c.LastTimeUs = timeUs;
        return timeUs;
    }

    /// <summary>
    /// Keeps a frame that ends the input with a whole click (a contact sample in its history, then the empty state: a
    /// finger tap or double-tap replay) so the release repeats resend it as it was
    /// (<see cref="ProtocolCapabilities.SampleDedupe"/>): a lost click datagram is recovered, and the server applies
    /// its samples once. Only if the server advertised the capability; any other frame drops the kept click.
    /// </summary>
    private static bool RecordClickLocked(Connection c, bool active, uint timeUs, in PenSample? pen, ReadOnlySpan<PenSample> history)
    {
        c.ClickHistoryCount = 0;
        if (active || history.IsEmpty || !pen.HasValue
            || (c.ServerCapabilities & ProtocolCapabilities.SampleDedupe) == 0)
        {
            return false;
        }

        bool contact = false;
        foreach (ref readonly PenSample h in history)
        {
            contact |= (h.State & PenState.Contact) != 0;
        }

        if (contact)
        {
            history.CopyTo(c.ClickHistory);
            c.ClickHistoryCount = history.Length;
            c.ClickTimeUs = timeUs;
        }

        return contact;
    }

    /// <summary>
    /// The time and history for resending the unchanged current state (keepalive, release repeat, fallback switch,
    /// probe): while a click frame is kept, that frame as it was, so no frame moves the server's dedupe watermark past
    /// the click before its resend lands; after a touch lift that emptied the state (FingerTouches), that frame's own
    /// time, so a repeat standing in for a lost lift gives the server the real lift time (a tap stays a tap);
    /// otherwise a new time and no history.
    /// </summary>
    private static uint CurrentFrameLocked(Connection c, out ReadOnlySpan<PenSample> history)
    {
        c.LastStateSends++;
        if (c.ClickHistoryCount > 0)
        {
            history = c.ClickHistory.AsSpan(0, c.ClickHistoryCount);
            return c.ClickTimeUs;
        }

        if (c.HasReleaseTime)
        {
            history = default;
            return c.ReleaseTimeUs;
        }

        history = default;
        return NextTimeUsLocked(c);
    }

    private bool SendLocked(Connection c, uint timeUs, in PenSample? pen, ReadOnlySpan<TouchSample> touches, ReadOnlySpan<PenSample> history)
    {
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

            uint timeUs = CurrentFrameLocked(c, out ReadOnlySpan<PenSample> history);
            SendLocked(c, timeUs, c.LastPen, c.LastTouches.AsSpan(0, c.LastTouchCount), history);
            if (c.RepeatsLeft <= 0)
            {
                c.ClickHistoryCount = 0; // the resend window ends with the repeats; a much later click would surprise
                c.HasReleaseTime = false;
            }

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
            c.Mirror?.OnTransport(tls: true); // no video in fallback; only records and posts

            // Deliver the current state over the new path right away (a pending click frame as it was).
            uint timeUs = CurrentFrameLocked(c, out ReadOnlySpan<PenSample> history);
            SendLocked(c, timeUs, c.LastPen, c.LastTouches.AsSpan(0, c.LastTouchCount), history);
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

            uint timeUs = CurrentFrameLocked(c, out ReadOnlySpan<PenSample> history);
            SendDatagramLocked(c, timeUs, c.LastPen, c.LastTouches.AsSpan(0, c.LastTouchCount), history);
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
                c.Mirror?.OnTransport(tls: false);
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
            await channel.WriteAsync(new HelloMessage(ProtocolVersion.Current, _deviceIdBytes, _deviceName, _options.Capabilities)).ConfigureAwait(false);
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
            SocketQos.Configure(udp, SocketQos.Expedited, _options.ConfigureInputSocket, _log, ref _qosWarned);
            ProtocolCapabilities negotiated = ack.Capabilities & _options.Capabilities;
            connection = new Connection(this, channel, udp, new InputCipher(inputKey, start.SessionId), start.SessionId, fingerprint, ack.ServerName, channel.Ssl.SslProtocol, _time, _options.FallbackQueueCapacity)
            {
                ServerCapabilities = ack.Capabilities,
                Negotiated = negotiated,
                Mirror = (negotiated & ProtocolCapabilities.ScreenMirror) != 0
                    ? CreateMirror(channel, server, deviceKey, clientNonce, start)
                    : null,
            };

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

    private ClientMirror CreateMirror(ControlChannel channel, IPAddress server, byte[] deviceKey, byte[] clientNonce, SessionStartMessage start)
    {
        Span<byte> keys = stackalloc byte[2 * KeySchedule.KeyLength];
        try
        {
            KeySchedule.DeriveVideoKey(deviceKey, clientNonce, start.ServerNonce.Span, start.SessionId, VideoKeyDirection.ServerToClient, keys[..KeySchedule.KeyLength]);
            KeySchedule.DeriveVideoKey(deviceKey, clientNonce, start.ServerNonce.Span, start.SessionId, VideoKeyDirection.ClientToServer, keys[KeySchedule.KeyLength..]);
            return new ClientMirror(this, channel, server, _options, _log, keys[..KeySchedule.KeyLength], keys[KeySchedule.KeyLength..], start.SessionId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keys);
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
                    case ControlMessageType.SettingsSnapshot when (c.Negotiated & ProtocolCapabilities.RemoteSettings) != 0:
                        IReadOnlyList<SettingEntry> snapshot = frame.Read<SettingsSnapshotMessage>().Entries;
                        c.Remote.Snapshot = snapshot;
                        RaiseRemote(SettingsSnapshotReceived, new SettingsSnapshotEventArgs(snapshot));
                        break;
                    case ControlMessageType.DisplayList when (c.Negotiated & ProtocolCapabilities.RemoteSettings) != 0:
                        IReadOnlyList<DisplayListEntry> displays = frame.Read<DisplayListMessage>().Displays;
                        c.Remote.Displays = displays;
                        RaiseRemote(DisplayListReceived, new DisplayListEventArgs(displays));
                        break;
                    case ControlMessageType.ActiveArea when (c.Negotiated & ProtocolCapabilities.ActiveArea) != 0:
                        NormalizedRect area = frame.Read<ActiveAreaMessage>().ToNormalizedRect();
                        c.Remote.ActiveArea = area;
                        RaiseRemote(ActiveAreaReceived, new ActiveAreaEventArgs(area));
                        break;
                    case ControlMessageType.MirrorConfig when c.Mirror is not null:
                        c.Mirror.OnConfig(frame.Read<MirrorConfigMessage>());
                        break;
                    case ControlMessageType.MirrorState when c.Mirror is not null:
                        MirrorStateMessage mirrorState = frame.Read<MirrorStateMessage>();
                        c.Mirror.OnState(mirrorState.State, mirrorState.Reason);
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

    private void RaiseRemote<T>(EventHandler<T>? handler, T args)
    {
        try
        {
            handler?.Invoke(this, args);
        }
#pragma warning disable CA1031 // A faulty UI handler must not end the session.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            NetLog.Unexpected(_log, null, ex);
        }
    }

    /// <summary>What the Mac last told this connection (written by the read loop, read by any thread).</summary>
    private sealed class RemoteState
    {
        private readonly Lock _lock = new();
        private IReadOnlyList<SettingEntry>? _snapshot;
        private IReadOnlyList<DisplayListEntry>? _displays;
        private NormalizedRect? _activeArea;

        public IReadOnlyList<SettingEntry>? Snapshot
        {
            get => Volatile.Read(ref _snapshot);
            set => Volatile.Write(ref _snapshot, value);
        }

        public IReadOnlyList<DisplayListEntry>? Displays
        {
            get => Volatile.Read(ref _displays);
            set => Volatile.Write(ref _displays, value);
        }

        public NormalizedRect? ActiveArea
        {
            get
            {
                lock (_lock)
                {
                    return _activeArea;
                }
            }

            set
            {
                lock (_lock)
                {
                    _activeArea = value;
                }
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

        public PenSample[] ClickHistory { get; } = new PenSample[InputPayload.MaxHistory];

        public RemoteState Remote { get; } = new();

        public ProtocolCapabilities ServerCapabilities { get; init; }

        /// <summary>Advertised by both sides.</summary>
        public ProtocolCapabilities Negotiated { get; init; }

        /// <summary>Screen mirroring of this connection, or null when <see cref="ProtocolCapabilities.ScreenMirror"/> wasn't negotiated.</summary>
        public ClientMirror? Mirror { get; init; }

#pragma warning disable SA1401, CA1051 // Hot-path state of a private class; fields keep it simple and ref-friendly.
        public PenSample? LastPen;
        public int LastTouchCount;
        public bool WasActive;
        public int RepeatsLeft;
        public int ClickHistoryCount;
        public uint ClickTimeUs;
        public int LastStateSends; // how often the current state went out (1 = only by SendState itself)
        public bool HasReleaseTime;
        public uint ReleaseTimeUs;
        public uint LastTimeUs;
        public bool HasTime;
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

            Mirror?.Dispose();

            await Channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
