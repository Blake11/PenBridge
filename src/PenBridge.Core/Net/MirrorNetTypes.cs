using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PenBridge.Core.Protocol;

namespace PenBridge.Core.Net;

/// <summary>
/// What a dedicated thread Core starts does (<c>docs/specs/screen-mirroring.md</c> "Thread hooks"). The server and
/// client options' <c>MirrorThreadStarted</c> hook gets it on that thread when it starts, so a head can set the
/// thread's priority with platform APIs (the Mac's QoS class, Android's <c>Process.SetThreadPriority</c>). The video
/// roles belong below the input path; <see cref="InputReceive"/> is the input path (ADR-0038 "input always wins").
/// The name predates the input role; a head must switch on the role rather than treat every call as a video thread.
/// </summary>
public enum MirrorThreadRole
{
    /// <summary>Mac: fragments, seals and paces video datagrams.</summary>
    VideoSend = 1,

    /// <summary>Mac: receives the phone's Probes. Phone: receives video datagrams and reassembles frames.</summary>
    VideoReceive = 2,

    /// <summary>
    /// Mac: receives, decrypts and delivers UDP input datagrams to the sink (one thread per running server, for its
    /// whole life). Give it the highest priority the platform offers (user-interactive QoS on the Mac); keep the hook
    /// quick. Core sets <see cref="Thread.Priority"/> above normal as a portable default.
    /// </summary>
    InputReceive = 3,
}

/// <summary>Raised by <see cref="PenBridgeClient.MirrorStateChanged"/>: the Mac's mirroring state, or the client's own (TLS fallback).</summary>
public sealed class MirrorStateChangedEventArgs(MirrorState state, MirrorReason reason) : EventArgs
{
    public MirrorState State { get; } = state;

    /// <summary>Why, for <see cref="MirrorState.Off"/>, <see cref="MirrorState.Paused"/> and <see cref="MirrorState.Unavailable"/>.</summary>
    public MirrorReason Reason { get; } = reason;
}

/// <summary>Counters of the video path of one session (tests, diagnostics; all monotonic).</summary>
/// <param name="ProbesAccepted">Authenticated Probes (Mac) / frames handed out (phone).</param>
/// <param name="AuthFailures">Datagrams whose tag didn't verify.</param>
/// <param name="Malformed">Wrong size, header, kind or session.</param>
/// <param name="Stale">At or below the highest authenticated sequence, dropped before decryption.</param>
/// <param name="RateLimited">Dropped undecrypted: the session's forged-datagram budget was exhausted (Mac).</param>
/// <param name="EndpointChanges">Times a newer authenticated Probe moved the pinned endpoint (Mac).</param>
/// <param name="FramesSent">Access units fully sent (Mac) / handed to the head (phone).</param>
/// <param name="FramesDropped">Access units dropped unsent while numbered (Mac) / foreign-source datagrams (phone).</param>
/// <param name="FragmentsSent">Video datagrams sent (Mac) / received from the Mac's endpoint (phone).</param>
public readonly record struct MirrorCounters(
    long ProbesAccepted,
    long AuthFailures,
    long Malformed,
    long Stale,
    long RateLimited,
    long EndpointChanges,
    long FramesSent,
    long FramesDropped,
    long FragmentsSent);

/// <summary>
/// The phone side of mirroring on one connection (<see cref="PenBridgeClient.MirrorStats"/>; diagnostics, the SimClient).
/// Monotonic over the connection, across epochs and runs; a reconnect starts again at zero.
/// </summary>
/// <param name="DatagramsReceived">Video datagrams from the Mac's video endpoint (before authentication).</param>
/// <param name="FramesComplete">Access units reassembled and handed to the head (<see cref="PenBridgeClient.VideoFrameReady"/>).</param>
/// <param name="FramesDropped">Frames given up while numbered: incomplete in time, gated before a keyframe, never seen, or no buffer.</param>
/// <param name="FragmentsLost">Fragments of dropped frames that never arrived.</param>
/// <param name="KeyframeRequestsSent">KeyframeRequests written to the Mac.</param>
/// <param name="ProbesSent">Probes sent on the video socket.</param>
/// <param name="ReportsSent">MirrorReports written to the Mac.</param>
/// <param name="Epochs">MirrorConfigs accepted (each starts an epoch).</param>
/// <param name="AuthFailures">Datagrams with a valid header whose tag didn't verify.</param>
/// <param name="Malformed">Datagrams without a valid header.</param>
/// <param name="ForeignDatagrams">Datagrams from another endpoint than the Mac's video port (ignored).</param>
public readonly record struct MirrorClientStats(
    long DatagramsReceived,
    long FramesComplete,
    long FramesDropped,
    long FragmentsLost,
    long KeyframeRequestsSent,
    long ProbesSent,
    long ReportsSent,
    long Epochs,
    long AuthFailures,
    long Malformed,
    long ForeignDatagrams);

/// <summary>DSCP marking and head socket hooks (<c>docs/specs/screen-mirroring.md</c> "QoS").</summary>
internal static class SocketQos
{
    /// <summary>TOS byte for DSCP EF (46): the phone's input socket.</summary>
    public const int Expedited = 0xB8;

    /// <summary>TOS byte for DSCP AF41 (34): the video sockets on both sides.</summary>
    public const int Video = 0x88;

    /// <summary>
    /// Sets <paramref name="tos"/> (when given) and runs the head's <paramref name="hook"/>. Failures are logged once per
    /// <paramref name="warned"/> flag and otherwise ignored: marking is an optimisation, never a reason to fail.
    /// </summary>
    public static void Configure(Socket socket, int? tos, Action<Socket>? hook, ILogger log, ref int warned)
    {
        if (tos is { } value && !TrySetTos(socket, value))
        {
            WarnOnce(log, ref warned, "TOS");
        }

        if (hook is null)
        {
            return;
        }

        try
        {
            hook(socket);
        }
#pragma warning disable CA1031 // A head hook that fails to set a platform option must not break the session.
        catch (Exception)
#pragma warning restore CA1031
        {
            WarnOnce(log, ref warned, "socket hook");
        }
    }

    /// <summary>Sets the IPv4 TOS byte and, on IPv6 sockets, the traffic class. True when at least one took.</summary>
    internal static bool TrySetTos(Socket socket, int tos)
    {
        bool ok = false;
        if (socket.AddressFamily == AddressFamily.InterNetwork || socket is { AddressFamily: AddressFamily.InterNetworkV6, DualMode: true })
        {
            ok |= TrySet(socket, SocketOptionLevel.IP, SocketOptionName.TypeOfService, tos);
        }

        if (socket.AddressFamily == AddressFamily.InterNetworkV6 && Ipv6TrafficClass is { } option)
        {
            // No SocketOptionName exists for it, and on Unix SetSocketOption rejects numbers it doesn't know: raw only.
            Span<byte> value = stackalloc byte[sizeof(int)];
            BitConverter.TryWriteBytes(value, tos);
            try
            {
                socket.SetRawSocketOption(IpProtoIpv6, option, value);
                ok = true;
            }
            catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException)
            {
            }
        }

        return ok;
    }

    /// <summary>The IPv6 traffic class of <paramref name="socket"/> (tests), or -1 where it can't be read.</summary>
    internal static int GetTrafficClass(Socket socket)
    {
        if (Ipv6TrafficClass is not { } option)
        {
            return -1;
        }

        Span<byte> value = stackalloc byte[sizeof(int)];
        return socket.GetRawSocketOption(IpProtoIpv6, option, value) == sizeof(int) ? BitConverter.ToInt32(value) : -1;
    }

    // IPPROTO_IPV6 is 41 everywhere; IPV6_TCLASS differs per OS.
    private const int IpProtoIpv6 = 41;

    private static int? Ipv6TrafficClass =>
        OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() ? 67
        : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS() ? 36
        : OperatingSystem.IsWindows() ? 39
        : null;

    private static bool TrySet(Socket socket, SocketOptionLevel level, SocketOptionName name, int value)
    {
        try
        {
            socket.SetSocketOption(level, name, value);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static void WarnOnce(ILogger log, ref int warned, string what)
    {
        if (Interlocked.Exchange(ref warned, 1) == 0)
        {
            NetLog.QosUnavailable(log, what);
        }
    }
}

/// <summary>
/// Video sockets and the server's input socket are read by a dedicated thread blocked in a synchronous receive.
/// Disposing a UDP socket doesn't unblock that receive on every platform (on macOS the dispose itself waits for it), so
/// the receive thread owns its socket: the stopper only flags it and <see cref="Wake"/>s it, and the thread disposes
/// the socket when it leaves.
/// </summary>
internal static class VideoSockets
{
    /// <summary>How long a receive blocks before it rechecks its stop flag (a backstop for a lost wake datagram).</summary>
    public const int ReceiveTimeoutMs = 1000;

    /// <summary>A bound UDP socket with the receive backstop.</summary>
    public static void PrepareForReceiveThread(Socket socket) => socket.ReceiveTimeout = ReceiveTimeoutMs;

    /// <summary>Sends one byte to the socket's own port, so its blocked receive returns and sees the stop flag.</summary>
    public static void Wake(Socket socket)
    {
        try
        {
            if (socket.LocalEndPoint is not IPEndPoint local)
            {
                return;
            }

            IPAddress target = local.Address.Equals(IPAddress.Any) ? IPAddress.Loopback
                : local.Address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback
                : local.Address;
            socket.SendTo([0], new IPEndPoint(target, local.Port));
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The receive timeout still ends the thread.
        }
    }
}

/// <summary>
/// Starts Core's dedicated threads with the head hook on start: video threads below normal priority, the input receive
/// thread above normal (portable defaults; the hook sets the real priority).
/// </summary>
internal static class MirrorThreads
{
    public static Thread Start(string name, MirrorThreadRole role, Action<MirrorThreadRole>? hook, ILogger log, Action body)
    {
        var thread = new Thread(() => Run(role, hook, log, body))
        {
            IsBackground = true,
            Name = name,
        };
        try
        {
            thread.Priority = role == MirrorThreadRole.InputReceive ? ThreadPriority.AboveNormal : ThreadPriority.BelowNormal;
        }
        catch (Exception ex) when (ex is ThreadStateException or PlatformNotSupportedException)
        {
            // A portable default only; the head hook sets the real priority.
        }

        thread.Start();
        return thread;
    }

    private static void Run(MirrorThreadRole role, Action<MirrorThreadRole>? hook, ILogger log, Action body)
    {
        if (hook is not null)
        {
            try
            {
                hook(role);
            }
#pragma warning disable CA1031 // A failing priority hook leaves the portable default; it must not kill the thread.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                NetLog.Unexpected(log, null, ex);
            }
        }

        try
        {
            body();
        }
#pragma warning disable CA1031 // An escaping exception on a dedicated thread would crash the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            NetLog.Unexpected(log, null, ex);
        }
    }
}
