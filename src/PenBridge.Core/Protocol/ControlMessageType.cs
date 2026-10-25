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

    /// <summary>S (1.2, RemoteSettings): every Mac setting.</summary>
    SettingsSnapshot = 0x40,

    /// <summary>C (1.2, RemoteSettings): Mac settings to change.</summary>
    SettingsUpdate = 0x41,

    /// <summary>S (1.2, RemoteSettings): the displays the user can target.</summary>
    DisplayList = 0x42,

    /// <summary>S (1.2, ActiveArea): the phone surface's active area.</summary>
    ActiveArea = 0x43,

    /// <summary>C (1.3, ScreenMirror): the phone wants video.</summary>
    MirrorStart = 0x50,

    /// <summary>C (1.3, ScreenMirror): the phone no longer wants video.</summary>
    MirrorStop = 0x51,

    /// <summary>S (1.3, ScreenMirror): the stream's codec, size, fps and bitrate for a new epoch.</summary>
    MirrorConfig = 0x52,

    /// <summary>S (1.3, ScreenMirror): mirroring state and reason.</summary>
    MirrorState = 0x53,

    /// <summary>C (1.3, ScreenMirror): encode the next frame as a keyframe.</summary>
    KeyframeRequest = 0x54,

    /// <summary>C (1.3, ScreenMirror): receive counters and the last rendered frame.</summary>
    MirrorReport = 0x55,

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
        0x01 or 0x02 or (>= 0x10 and <= 0x16) or (>= 0x20 and <= 0x24) or 0x30 or 0x31 or (>= 0x40 and <= 0x43) or (>= 0x50 and <= 0x55) or 0x70 or 0x7E or 0x7F => true,
        _ => false,
    };

    /// <summary>
    /// The capability both sides must have negotiated before <paramref name="type"/> may be sent or received during a
    /// session (<see cref="ProtocolCapabilities.None"/> for the base messages). Receiving a message whose capability
    /// isn't negotiated is a protocol error.
    /// </summary>
    public static ProtocolCapabilities RequiredCapability(ControlMessageType type) => type switch
    {
        ControlMessageType.SettingsSnapshot or ControlMessageType.SettingsUpdate or ControlMessageType.DisplayList => ProtocolCapabilities.RemoteSettings,
        ControlMessageType.ActiveArea => ProtocolCapabilities.ActiveArea,
        >= ControlMessageType.MirrorStart and <= ControlMessageType.MirrorReport => ProtocolCapabilities.ScreenMirror,
        _ => ProtocolCapabilities.None,
    };

    /// <summary>True when <paramref name="negotiated"/> (the capabilities both sides advertised) includes <see cref="RequiredCapability"/>.</summary>
    public static bool IsNegotiated(ControlMessageType type, ProtocolCapabilities negotiated)
    {
        ProtocolCapabilities required = RequiredCapability(type);
        return (negotiated & required) == required;
    }

    /// <summary>True for the screen-mirroring messages <c>0x50–0x55</c> (1.3, <see cref="ProtocolCapabilities.ScreenMirror"/>).</summary>
    public static bool IsMirror(ControlMessageType type) => type is >= ControlMessageType.MirrorStart and <= ControlMessageType.MirrorReport;
}
