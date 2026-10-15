namespace PenBridge.Core.Protocol;

#pragma warning disable CA1028 // Wire enums: storage matches the wire field width.

/// <summary><c>u8</c> reason carried by <see cref="ControlMessageType.Bye"/>. Unknown values are accepted.</summary>
public enum ByeReason : byte
{
    Normal = 0,
    TakenOver = 1,
    Unpaired = 2,
    Shutdown = 3,
}

/// <summary><c>u8</c> source of the J-PAKE shared secret in the phone's PairRound1.</summary>
public enum PairingSecretKind : byte
{
    Pin = 1,
    QrToken = 2,
}

/// <summary><c>u8</c> status carried by PairResult.</summary>
public enum PairResultStatus : byte
{
    Ok = 0,
    Failed = 1,
    Expired = 2,
}

/// <summary><c>u8</c> orientation of the phone drawing surface in SurfaceInfo.</summary>
public enum SurfaceOrientation : byte
{
    Portrait = 0,
    Landscape = 1,
}

/// <summary>
/// <c>u32</c> capability flags exchanged in Hello/HelloAck. A peer advertises what it supports; a feature is
/// used only when both sides advertise it. Unknown bits are preserved and ignored.
/// </summary>
[Flags]
public enum ProtocolCapabilities : uint
{
    None = 0,

    /// <summary>
    /// Since 1.1. Server: it skips the history samples of an input frame whose <c>timeUs</c> doesn't advance past the
    /// previous frame's, so a resent frame never applies its samples twice. Client: it may resend a frame that carried
    /// a click (contact then release within the frame) with that frame's own <c>timeUs</c> as its release repeats.
    /// The client resends only when the server advertised this bit.
    /// </summary>
    SampleDedupe = 1,

    /// <summary>
    /// Since 1.1 (ADR-0026). Server: its gesture recognizer handles a lone finger as a relative trackpad cursor
    /// (<c>GestureKind.PointerMove</c>, <c>Click</c>, <c>PointerDrag</c>). Client: in cursor finger mode it sends a lone
    /// finger as raw touches instead of absolute hover samples, and makes a touch lift survive one lost datagram: the
    /// last touch state is sent once more before a lift unless a keepalive already resent it, and the release
    /// repeats after a lift resend the lift frame with its own <c>timeUs</c>. Used only when both sides advertise it;
    /// otherwise the phone keeps ADR-0022's absolute cursor.
    /// </summary>
    FingerTouches = 2,
}

/// <summary><c>u32</c> flags describing the phone surface in SurfaceInfo. Unknown bits are preserved and ignored.</summary>
[Flags]
public enum SurfaceCapabilities : uint
{
    None = 0,

    /// <summary>The phone reports a finger as a pen-like sample; pressure is synthetic.</summary>
    FingerFallback = 1,
}

/// <summary><c>u8</c> flags in the input datagram header. None are defined yet; unknown bits are ignored.</summary>
[Flags]
public enum InputDatagramOptions : byte
{
    None = 0,
}

#pragma warning restore CA1028
