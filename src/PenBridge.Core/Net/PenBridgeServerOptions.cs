using System.Net;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Diagnostics;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Core.Net;

/// <summary>Configuration of a <see cref="PenBridgeServer"/>. Timeouts follow docs/specs/phase-1-core.md.</summary>
public sealed class PenBridgeServerOptions
{
    /// <summary>The TLS identity (its fingerprint is what phones pin). Owned by the caller.</summary>
    public required ServerIdentity Identity { get; init; }

    /// <summary>Paired devices and their keys.</summary>
    public required IPairedDeviceRegistry Registry { get; init; }

    /// <summary>Receives pen and gesture events; called under the server's input lock, so it must not block.</summary>
    public required IInputSink InputSink { get; init; }

    /// <summary>Name sent in HelloAck (truncated to 256 UTF-8 bytes).</summary>
    public string ServerName { get; init; } = Environment.MachineName;

    /// <summary>Local address for both sockets. The default <see cref="IPAddress.IPv6Any"/> is bound dual-mode (IPv4 + IPv6).</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.IPv6Any;

    /// <summary>First port tried for TCP and UDP (ADR-0011). 0 binds an ephemeral port (tests).</summary>
    public int Port { get; init; } = ProtocolInfo.DefaultPort;

    /// <summary>How many following ports are tried when <see cref="Port"/> is taken (47821–47829).</summary>
    public int PortFallbackCount { get; init; } = 9;

    /// <summary>Initial "allow takeover" setting (ADR-0010); changeable through <see cref="PenBridgeServer.AllowTakeover"/>.</summary>
    public bool AllowTakeover { get; init; }

    /// <summary>Target display in global display points. The default unit square yields normalized 0..1 positions.</summary>
    public DisplayRect TargetDisplay { get; init; } = new(0, 0, 1, 1);

    /// <summary>Sub-region of <see cref="TargetDisplay"/> in fractions.</summary>
    public NormalizedRect TargetRegion { get; init; } = NormalizedRect.Full;

    /// <summary>Host put into QR payloads; null picks the first non-loopback IPv4 address.</summary>
    public string? AdvertisedHost { get; init; }

    /// <summary>Pre-auth deadline: from accept until SessionStart, excluding pairing (which has <see cref="PairingTimeout"/> and is followed by a fresh pre-auth deadline for Auth).</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Deadline for each control read before the session starts.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>From the phone's PairRound1 to PairResult; holds the single pairing slot at most this long.</summary>
    public TimeSpan PairingTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>A session peer silent this long on the control channel is dead (it pings every 2 s).</summary>
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Heartbeat interval; <see cref="Timeout.InfiniteTimeSpan"/> disables pings.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Concurrent connections that haven't authenticated yet; more are closed on accept.</summary>
    public int MaxPendingConnections { get; init; } = 8;

    /// <summary>Concurrent unauthenticated connections from one remote IP, so one host can't take every pending slot.</summary>
    public int MaxPendingConnectionsPerAddress { get; init; } = 2;

    /// <summary>Tests only: the exact ports to try, replacing <see cref="Port"/> + <see cref="PortFallbackCount"/>.</summary>
    internal IReadOnlyList<int>? PortCandidates { get; init; }

    /// <summary>
    /// Advertise <see cref="ProtocolCapabilities.FingerTouches"/> (ADR-0026): when the phone advertises it too, it sends
    /// a lone finger as raw touches and the session's recognizer emits <see cref="GestureKind.PointerMove"/>,
    /// <see cref="GestureKind.Click"/> and <see cref="GestureKind.PointerDrag"/> (a relative trackpad cursor). Off by
    /// default: turn it on only once <see cref="InputSink"/> posts those kinds, or one-finger input does nothing.
    /// </summary>
    public bool FingerTouches { get; init; }

    /// <summary>Advertise <see cref="ProtocolCapabilities.RemoteSettings"/> (ADR-0030); off until the head handles <see cref="PenBridgeServer.SettingsUpdateRequested"/>.</summary>
    public bool RemoteSettings { get; init; }

    /// <summary>Advertise <see cref="ProtocolCapabilities.ActiveArea"/> (ADR-0031); off until the head's mapping uses the active area.</summary>
    public bool ActiveArea { get; init; }

    /// <summary>
    /// Capabilities sent in HelloAck: <see cref="ProtocolCapabilities.SampleDedupe"/>, plus
    /// <see cref="ProtocolCapabilities.FingerTouches"/>, <see cref="ProtocolCapabilities.RemoteSettings"/> and
    /// <see cref="ProtocolCapabilities.ActiveArea"/> when their options are set. Tests override it to act
    /// as an older server.
    /// </summary>
    internal ProtocolCapabilities Capabilities
    {
        get => _capabilities ?? (ProtocolCapabilities.SampleDedupe | (FingerTouches ? ProtocolCapabilities.FingerTouches : ProtocolCapabilities.None)
            | (RemoteSettings ? ProtocolCapabilities.RemoteSettings : ProtocolCapabilities.None) | (ActiveArea ? ProtocolCapabilities.ActiveArea : ProtocolCapabilities.None));
        init => _capabilities = value;
    }

    private readonly ProtocolCapabilities? _capabilities;

    /// <summary>Sustained rate of failed datagram decryptions tolerated per session (bursts up to twice this).</summary>
    public int MaxAuthFailuresPerSecond { get; init; } = 16;

    /// <summary>
    /// The initial input settings (ADR-0031..0033: stylus mode, side button, pressure curve, gestures) applied to every
    /// session; the defaults reproduce the behaviour without settings. Change them at run time through the server.
    /// </summary>
    public InputSettings InputSettings { get; init; } = InputSettings.Default;

    /// <summary>Watchdog and history options for the tracker.</summary>
    public PenStateTrackerOptions? TrackerOptions { get; init; }

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Debug latency capture (off when null, the default): input frames, client Pings and server Ping round trips go
    /// into this preallocated ring buffer, timestamped with <see cref="TimeProvider"/>. Nothing is logged per frame.
    /// </summary>
    public LatencyRecorder? LatencyRecorder { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }
}
