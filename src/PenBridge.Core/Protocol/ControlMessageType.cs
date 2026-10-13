namespace PenBridge.Core.Protocol;

/// <summary>
/// <c>u8</c> control-message type. C = phone → Mac, S = Mac → phone. Types <c>0xF0–0xFF</c> are reserved and
/// ignored by readers; any other unknown type is a protocol violation.
/// </summary>
public enum ControlMessageType : byte
{
    /// <summary>C: version, deviceId, deviceName, capabilities.</summary>
    Hello = 0x01,

    /// <summary>S: version, serverName, capabilities, challenge.</summary>
    HelloAck = 0x02,

    /// <summary>C: J-PAKE round 1 + secret kind.</summary>
    ClientPairRound1 = 0x10,

    /// <summary>S: J-PAKE round 1.</summary>
    ServerPairRound1 = 0x11,

    /// <summary>C: J-PAKE round 2.</summary>
    ClientPairRound2 = 0x12,

    /// <summary>S: J-PAKE round 2.</summary>
    ServerPairRound2 = 0x13,

    /// <summary>C: key-confirmation MAC.</summary>
    ClientPairConfirm = 0x14,

    /// <summary>S: key-confirmation MAC.</summary>
    ServerPairConfirm = 0x15,

    /// <summary>S: pairing outcome.</summary>
    PairResult = 0x16,

    /// <summary>C: deviceId, HMAC, clientNonce.</summary>
    Auth = 0x20,

    /// <summary>S: udpPort, sessionId, serverNonce.</summary>
    SessionStart = 0x21,

    /// <summary>C: surface size, DPI, orientation, flags.</summary>
    SurfaceInfo = 0x22,

    /// <summary>S: highest UDP sequence received so far.</summary>
    UdpAck = 0x23,

    /// <summary>C: input payload over TLS (fallback mode).</summary>
    InputFrame = 0x24,

    /// <summary>Both: heartbeat request.</summary>
    Ping = 0x30,

    /// <summary>Both: heartbeat reply.</summary>
    Pong = 0x31,

    /// <summary>Both: orderly close.</summary>
    Bye = 0x70,

    /// <summary>S: another session is active.</summary>
    Busy = 0x7E,

    /// <summary>Both: error code + message.</summary>
    Error = 0x7F,
}

/// <summary>Classification helpers for raw type bytes.</summary>
public static class ControlMessageTypes
{
    /// <summary>First type of the reserved, always-ignored range.</summary>
    public const byte ReservedFirst = 0xF0;

    /// <summary>True for <c>0xF0–0xFF</c>: readers skip these frames.</summary>
    public static bool IsReserved(byte type) => type >= ReservedFirst;

    /// <summary>True when <paramref name="type"/> is a message this build understands.</summary>
    public static bool IsKnown(byte type) => type switch
    {
        0x01 or 0x02 or (>= 0x10 and <= 0x16) or (>= 0x20 and <= 0x24) or 0x30 or 0x31 or 0x70 or 0x7E or 0x7F => true,
        _ => false,
    };
}
