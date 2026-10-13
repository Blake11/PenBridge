using System.Net;
using System.Text;
using PenBridge.Core.Protocol;
using PenBridge.Core.Security;

namespace PenBridge.Core.Net;

/// <summary>How input currently reaches the server.</summary>
public enum InputTransport : byte
{
    /// <summary>No input has been sent or received yet.</summary>
    None = 0,

    /// <summary>Encrypted UDP datagrams (the normal path).</summary>
    Udp = 1,

    /// <summary>InputFrame messages over the TLS control channel (fallback when UDP is blocked).</summary>
    Tls = 2,
}

/// <summary>Why a client connect or pairing attempt failed (<see cref="PenBridgeConnectException.Failure"/>).</summary>
public enum ConnectFailure
{
    /// <summary>TCP connect or the TLS handshake failed.</summary>
    ConnectionFailed,

    /// <summary>The server closed the connection unexpectedly.</summary>
    ConnectionClosed,

    /// <summary>The server didn't answer in time.</summary>
    Timeout,

    /// <summary>The server violated the protocol.</summary>
    ProtocolError,

    /// <summary>The server speaks a different protocol major ("update the app").</summary>
    IncompatibleVersion,

    /// <summary>The server's certificate doesn't match the pinned or QR fingerprint (possible relay).</summary>
    ServerIdentityMismatch,

    /// <summary>No device key is stored for this server; pair first.</summary>
    NotPaired,

    /// <summary>Wrong PIN/token, or a relay was detected. See <see cref="PenBridgeConnectException.AttemptsLeft"/> (0 = locked out).</summary>
    PairingFailed,

    /// <summary>The pairing window is closed or expired ("Pair new device…" not open on the Mac).</summary>
    PairingExpired,

    /// <summary>Another pairing attempt is running on the server.</summary>
    PairingUnavailable,

    /// <summary>The server rejected reconnect authentication (unknown device or wrong key).</summary>
    AuthFailed,

    /// <summary>Another device has the active session and takeover is off.</summary>
    Busy,

    /// <summary>The server sent an Error with another code (see <see cref="PenBridgeConnectException.PeerErrorCode"/>).</summary>
    PeerError,
}

/// <summary>Thrown by <see cref="PenBridgeClient"/> connect and pair operations. The message never contains peer text or secrets.</summary>
public sealed class PenBridgeConnectException : Exception
{
    public PenBridgeConnectException()
        : this(ConnectFailure.ConnectionFailed)
    {
    }

    public PenBridgeConnectException(string message)
        : base(message)
    {
    }

    public PenBridgeConnectException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PenBridgeConnectException(ConnectFailure failure, int attemptsLeft = 0, ProtocolErrorCode? peerErrorCode = null, Exception? innerException = null)
        : base(Describe(failure), innerException)
    {
        Failure = failure;
        AttemptsLeft = attemptsLeft;
        PeerErrorCode = peerErrorCode;
    }

    public ConnectFailure Failure { get; }

    /// <summary>For <see cref="ConnectFailure.PairingFailed"/>: attempts left with the current PIN (0 = new PIN and lockout).</summary>
    public int AttemptsLeft { get; }

    /// <summary>The code of a received Error message, if any. The peer's text is never surfaced.</summary>
    public ProtocolErrorCode? PeerErrorCode { get; }

    private static string Describe(ConnectFailure failure) => failure switch
    {
        ConnectFailure.ConnectionFailed => "Could not connect to the PenBridge server.",
        ConnectFailure.ConnectionClosed => "The server closed the connection.",
        ConnectFailure.Timeout => "The server did not answer in time.",
        ConnectFailure.ProtocolError => "The server violated the protocol.",
        ConnectFailure.IncompatibleVersion => "Incompatible protocol version; update the Mac or phone app.",
        ConnectFailure.ServerIdentityMismatch => "The server certificate does not match the paired server.",
        ConnectFailure.NotPaired => "This server is not paired.",
        ConnectFailure.PairingFailed => "Pairing failed.",
        ConnectFailure.PairingExpired => "The pairing window is not open.",
        ConnectFailure.PairingUnavailable => "Another pairing attempt is in progress.",
        ConnectFailure.AuthFailed => "The server rejected authentication.",
        ConnectFailure.Busy => "Another device is connected.",
        _ => "The server reported an error.",
    };
}

/// <summary>Why a server session or client connection ended.</summary>
public enum SessionEndReason
{
    /// <summary>The peer sent Bye(Normal) or closed the connection cleanly.</summary>
    PeerClosed,

    /// <summary>The connection broke (reset, I/O error).</summary>
    ConnectionLost,

    /// <summary>The peer was silent longer than the liveness timeout, or a handshake deadline passed.</summary>
    Timeout,

    /// <summary>The peer violated the protocol.</summary>
    ProtocolError,

    /// <summary>The peer sent an Error.</summary>
    PeerError,

    /// <summary>Another device took over the session.</summary>
    TakenOver,

    /// <summary>The device was forgotten (unpaired).</summary>
    Unpaired,

    /// <summary>The server is shutting down.</summary>
    Shutdown,

    /// <summary>Closed locally (<c>DisconnectAsync</c>).</summary>
    LocalDisconnect,

    /// <summary>An unexpected local failure.</summary>
    InternalError,
}

/// <summary>Counters of the server's input path for the active session (all monotonic).</summary>
/// <param name="Accepted">Valid frames handed to the tracker (UDP datagrams and TLS InputFrames).</param>
/// <param name="Stale">Datagrams at or below the highest seen sequence (replays, reordering). Dropped before decryption.</param>
/// <param name="AuthFailures">Datagrams whose AEAD tag didn't verify (tampered or forged).</param>
/// <param name="Malformed">Datagrams with a bad size, header or session id, or an authenticated payload that doesn't parse.</param>
/// <param name="Invalid">Authenticated, well-formed frames rejected by sample validation.</param>
/// <param name="ForeignSource">Datagrams from a source other than the session peer (pinned endpoint).</param>
/// <param name="RateLimited">Datagrams dropped undecrypted because the forged-datagram budget was exhausted.</param>
public readonly record struct InputCounters(
    long Accepted,
    long Stale,
    long AuthFailures,
    long Malformed,
    long Invalid,
    long ForeignSource,
    long RateLimited);

/// <summary>Snapshot for the menu-bar UI (<see cref="PenBridgeServer.GetStats"/>).</summary>
/// <param name="HasSession">True while a device has the active session.</param>
/// <param name="DeviceId">The active device, or <see cref="Guid.Empty"/>.</param>
/// <param name="DeviceName">The active device's name (untrusted peer text, shown as plain data), or null.</param>
/// <param name="Transport">Input transport of the active session.</param>
/// <param name="RoundTripTime">Last measured control-channel RTT (Ping/Pong), or null before the first Pong.</param>
/// <param name="PacketsPerSecond">Accepted input frames per second over the last sampling period.</param>
/// <param name="Counters">Input-path counters of the active session.</param>
public readonly record struct PenBridgeServerStats(
    bool HasSession,
    Guid DeviceId,
    string? DeviceName,
    InputTransport Transport,
    TimeSpan? RoundTripTime,
    double PacketsPerSecond,
    InputCounters Counters);

/// <summary>An authenticated session on the server.</summary>
/// <param name="DeviceId">The paired device.</param>
/// <param name="DeviceName">Name from Hello (untrusted peer text, shown as plain data).</param>
/// <param name="RemoteEndPoint">The phone's control-channel endpoint.</param>
public sealed record SessionInfo(Guid DeviceId, string DeviceName, EndPoint? RemoteEndPoint);

public sealed class SessionStartedEventArgs(SessionInfo session) : EventArgs
{
    public SessionInfo Session { get; } = session;
}

public sealed class SessionEndedEventArgs(SessionInfo session, SessionEndReason reason) : EventArgs
{
    public SessionInfo Session { get; } = session;

    public SessionEndReason Reason { get; } = reason;
}

public sealed class DevicePairedEventArgs(PairedDevice device) : EventArgs
{
    public PairedDevice Device { get; } = device;
}

/// <summary>Raised by <see cref="PenBridgeClient.Disconnected"/>.</summary>
public sealed class ClientDisconnectedEventArgs(SessionEndReason reason, ByeReason? byeReason, ProtocolErrorCode? errorCode) : EventArgs
{
    public SessionEndReason Reason { get; } = reason;

    /// <summary>The server's Bye reason, if it sent one.</summary>
    public ByeReason? ByeReason { get; } = byeReason;

    /// <summary>The server's Error code, if it sent one.</summary>
    public ProtocolErrorCode? ErrorCode { get; } = errorCode;
}

/// <summary>
/// Decides whether the client sends one sealed datagram (return true) or drops it. A fault-injection hook for
/// tests and the SimClient's packet-loss script; it runs on the send path, so it must not allocate.
/// </summary>
public delegate bool DatagramFilter(ReadOnlySpan<byte> datagram);

/// <summary>Helpers for untrusted peer text (Error messages, device names).</summary>
public static class PeerText
{
    /// <summary>Longest sanitized text, in characters.</summary>
    public const int MaxLength = 128;

    /// <summary>
    /// Returns a log/display-safe copy: control, format (bidi override) and surrogate characters become '?', and the
    /// result is truncated to <see cref="MaxLength"/> characters. Never base decisions on peer text.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int length = Math.Min(text.Length, MaxLength);
        var builder = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            char c = text[i];
            System.Globalization.UnicodeCategory category = char.GetUnicodeCategory(c);
            bool unsafeChar = char.IsControl(c)
                || char.IsSurrogate(c)
                || category is System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator
                    or System.Globalization.UnicodeCategory.ParagraphSeparator;
            builder.Append(unsafeChar ? '?' : c);
        }

        return builder.ToString();
    }
}
