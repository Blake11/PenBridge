using PenBridge.Core.Net;

namespace PenBridge.Android.Services;

/// <summary>Coarse phase of the <see cref="ClientHost"/>.</summary>
public enum ClientHostStatus
{
    /// <summary>Not started (app in the background) or nothing to do.</summary>
    Idle,

    /// <summary>Browsing for Macs; no session wanted yet.</summary>
    Discovering,

    /// <summary>Connecting/authenticating, or waiting to retry (<see cref="ClientHostState.IsReconnecting"/>).</summary>
    Connecting,

    /// <summary>Pairing with a PIN or QR token.</summary>
    Pairing,

    /// <summary>Session established; see <see cref="ClientHostState.Transport"/> and <see cref="ClientHostState.RoundTripTime"/>.</summary>
    Connected,

    /// <summary>The last attempt or session failed and will not be retried automatically.</summary>
    Error,
}

/// <summary>Why the host is in <see cref="ClientHostStatus.Error"/> (or why it is reconnecting).</summary>
public enum ClientErrorKind
{
    None,

    /// <summary>TCP/TLS connect failed or the server closed the connection.</summary>
    Unreachable,

    /// <summary>The server didn't answer in time, or the session timed out.</summary>
    Timeout,

    /// <summary>The established session dropped.</summary>
    ConnectionLost,

    /// <summary>Different protocol major: update the Mac or phone app.</summary>
    IncompatibleVersion,

    /// <summary>The server's certificate doesn't match the pinned/QR fingerprint (possible relay).</summary>
    IdentityMismatch,

    /// <summary>No device key for this Mac: pair first.</summary>
    NotPaired,

    /// <summary>Wrong PIN/token (see <see cref="ClientHostState.AttemptsLeft"/>).</summary>
    PairingFailed,

    /// <summary>Too many wrong PINs: the Mac shows a new PIN and refuses attempts for a while.</summary>
    PairingLockedOut,

    /// <summary>"Pair new device…" isn't open on the Mac, or the PIN expired.</summary>
    PairingExpired,

    /// <summary>Another pairing attempt is in progress on the Mac.</summary>
    PairingUnavailable,

    /// <summary>The Mac rejected this device's key (it was forgotten there): pair again.</summary>
    AuthFailed,

    /// <summary>Another device has the active session.</summary>
    Busy,

    /// <summary>Another device took over the session.</summary>
    TakenOver,

    /// <summary>The Mac forgot this device; the local key was deleted.</summary>
    Unpaired,

    /// <summary>The server violated the protocol or reported an error.</summary>
    ProtocolError,

    /// <summary>Invalid user input (PIN format, QR payload, host/port).</summary>
    InvalidInput,
}

/// <summary>
/// Immutable snapshot published by <see cref="ClientHost.StateChanged"/> (main thread). Value equality, so consumers
/// can skip redundant renders. <see cref="MacName"/> is untrusted peer text, already sanitized; <see cref="Message"/>
/// is local text only (peer error text is never surfaced).
/// </summary>
public sealed record ClientHostState(
    ClientHostStatus Status,
    string? MacName = null,
    InputTransport Transport = InputTransport.None,
    TimeSpan? RoundTripTime = null,
    bool IsReconnecting = false,
    ClientErrorKind Error = ClientErrorKind.None,
    string? Message = null,
    int AttemptsLeft = 0)
{
    public static ClientHostState Idle { get; } = new(ClientHostStatus.Idle);

    public bool IsConnected => Status == ClientHostStatus.Connected;
}
