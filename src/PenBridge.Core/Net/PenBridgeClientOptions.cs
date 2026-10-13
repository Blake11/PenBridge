using Microsoft.Extensions.Logging;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Core.Net;

/// <summary>Configuration of a <see cref="PenBridgeClient"/> (the phone side).</summary>
public sealed class PenBridgeClientOptions
{
    /// <summary>Holds the device id and the per-server device keys (Keystore on Android; files for the SimClient).</summary>
    public required ISecretStore SecretStore { get; init; }

    /// <summary>The drawing surface, sent as SurfaceInfo right after SessionStart.</summary>
    public required SurfaceInfoMessage Surface { get; init; }

    /// <summary>Name sent in Hello (truncated to 256 UTF-8 bytes).</summary>
    public string DeviceName { get; init; } = Environment.MachineName;

    /// <summary>From TCP connect until SessionStart (including pairing).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Deadline for each control read before the session starts.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A server silent this long on the control channel is dead (it pings every 2 s).</summary>
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Heartbeat interval; <see cref="Timeout.InfiniteTimeSpan"/> disables pings.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Without a UdpAck this long after the first datagram, input switches to InputFrame over TLS.</summary>
    public TimeSpan UdpAckTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Resend interval while the pen is in contact or touches are active. The spec requires a frame at least every
    /// 50 ms; the default stays below that to absorb timer jitter.
    /// </summary>
    public TimeSpan ContactKeepaliveInterval { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>Resend interval while the pen only hovers (spec: at least every 250 ms).</summary>
    public TimeSpan HoverKeepaliveInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// After the state becomes empty (pen out of range, no touches), the empty frame is repeated this many times at
    /// the contact interval, so a single lost datagram doesn't leave the release to the receiver's watchdog.
    /// </summary>
    public int ReleaseRepeats { get; init; } = 2;

    /// <summary>Optional fault injection: return false to drop a sealed datagram instead of sending it.</summary>
    public DatagramFilter? DatagramFilter { get; init; }

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public ILoggerFactory? LoggerFactory { get; init; }
}
