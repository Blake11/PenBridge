using System.Net.Sockets;
using System.Runtime.InteropServices;
using PenBridge.Core.Net;

namespace PenBridge.Mac.Services;

/// <summary>
/// macOS quality of service for "input always wins" (ADR-0038, spec "QoS"): the socket service types Core's socket
/// hooks set, and the thread QoS classes of the input path and Core's mirroring threads.
/// </summary>
/// <remarks>
/// <para><b>Sockets.</b> <c>SO_NET_SERVICE_TYPE</c> tells the stack (and Wi-Fi WMM) what a socket carries:
/// <c>NET_SERVICE_TYPE_VO</c> (voice, DSCP EF) for the UDP input socket, <c>NET_SERVICE_TYPE_VI</c> (interactive
/// video, AF41) for each video socket, on top of the TOS byte Core sets. A failure throws; Core logs it once.</para>
/// <para><b>Threads.</b> The input path (the injector, called on the thread-pool thread that completed the UDP
/// receive) runs at user-interactive QoS: <see cref="PromoteInputThread"/> raises the calling thread once (one
/// thread-static check per event afterwards). Core's video send and probe threads run at user-initiated, like the
/// capture queue, so they never compete with input for a core.</para>
/// </remarks>
internal static partial class Qos
{
    private const int SolSocket = 0xFFFF;
    private const int SoNetServiceType = 0x1116;
    private const int NetServiceTypeVideo = 3; // NET_SERVICE_TYPE_VI
    private const int NetServiceTypeVoice = 4; // NET_SERVICE_TYPE_VO

    /// <summary>qos_class_t values (sys/qos.h).</summary>
    internal const uint UserInteractive = 0x21;
    internal const uint UserInitiated = 0x19;
    internal const uint Default = 0x15;
    internal const uint Utility = 0x11;
    internal const uint Background = 0x09;
    internal const uint Unspecified = 0;

    [ThreadStatic]
    private static bool _inputThreadPromoted;

    /// <summary><see cref="PenBridgeServerOptions.ConfigureInputSocket"/>: interactive voice (DSCP EF).</summary>
    public static void ConfigureInputSocket(Socket socket) => SetServiceType(socket, NetServiceTypeVoice);

    /// <summary><see cref="PenBridgeServerOptions.ConfigureVideoSocket"/>: interactive video (DSCP AF41).</summary>
    public static void ConfigureVideoSocket(Socket socket) => SetServiceType(socket, NetServiceTypeVideo);

    /// <summary><see cref="PenBridgeServerOptions.MirrorThreadStarted"/>: user-initiated, below the input path.</summary>
    public static void OnMirrorThreadStarted(MirrorThreadRole role) => _ = SetCurrentThread(UserInitiated);

    /// <summary>
    /// Raises the calling thread to user-interactive QoS the first time it delivers input (thread-pool threads keep it;
    /// the rest of their work is light, and the heavy mirroring work runs on its own queue and threads). Hot path:
    /// after the first call per thread, one thread-static read.
    /// </summary>
    /// <param name="previous">The thread's class before, when this call raised it.</param>
    /// <returns>True when this call raised the thread (first input on it), for a log line.</returns>
    public static bool PromoteInputThread(out uint previous)
    {
        previous = 0;
        if (_inputThreadPromoted)
        {
            return false;
        }

        _inputThreadPromoted = true;
        previous = qos_class_self();
        return previous != UserInteractive && SetCurrentThread(UserInteractive);
    }

    /// <summary>The calling thread's QoS class.</summary>
    internal static uint CurrentClass() => qos_class_self();

    /// <summary>A QoS class's name for logs.</summary>
    internal static string Name(uint qosClass) => qosClass switch
    {
        UserInteractive => "user-interactive",
        UserInitiated => "user-initiated",
        Default => "default",
        Utility => "utility",
        Background => "background",
        Unspecified => "unspecified",
        _ => "0x" + qosClass.ToString("x", System.Globalization.CultureInfo.InvariantCulture),
    };

    private static bool SetCurrentThread(uint qosClass) => pthread_set_qos_class_self_np(qosClass, 0) == 0;

    private static void SetServiceType(Socket socket, int type)
    {
        Span<byte> value = stackalloc byte[sizeof(int)];
        MemoryMarshal.Write(value, in type);
        socket.SetRawSocketOption(SolSocket, SoNetServiceType, value);
    }

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial uint qos_class_self();
}
