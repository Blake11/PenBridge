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
