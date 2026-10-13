namespace PenBridge.Core.Protocol;

/// <summary>
/// <c>u16</c> code carried by the <see cref="ControlMessageType.Error"/> message. Values are append-only;
/// readers must accept values they don't know (a newer minor may add codes).
/// </summary>
#pragma warning disable CA1028 // Wire enum: storage matches the u16 wire field.
public enum ProtocolErrorCode : ushort
#pragma warning restore CA1028
{
    /// <summary>Malformed frame or message (bad length, truncated payload, invalid field).</summary>
    ProtocolViolation = 1,

    /// <summary>A non-reserved message type this peer doesn't know.</summary>
    UnknownMessageType = 2,

    /// <summary>A frame longer than <see cref="ControlFrame.MaxLength"/>.</summary>
    FrameTooLarge = 3,

    /// <summary>A known message that isn't valid in the current connection state or direction.</summary>
    UnexpectedMessage = 4,

    /// <summary>The peer's protocol major differs.</summary>
    IncompatibleVersion = 5,

    /// <summary>Auth HMAC didn't verify, or the device is unknown.</summary>
    AuthFailed = 6,

    /// <summary>No pairing is possible right now (no active PIN/token, or another attempt is running).</summary>
    PairingUnavailable = 7,

    /// <summary>The peer didn't answer in time.</summary>
    Timeout = 8,

    /// <summary>Unexpected failure on the sending side.</summary>
    InternalError = 9,
}
