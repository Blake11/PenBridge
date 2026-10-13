using PenBridge.Core.Net;

namespace PenBridge.Android.Pad;

/// <summary>Connection phase shown by the pad overlay.</summary>
public enum PadConnectionKind : byte
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
}

/// <summary>What the pad overlay renders: the phase, plus the input transport and latency while connected.</summary>
/// <param name="Kind">Connection phase.</param>
/// <param name="Transport">Input transport while connected (<see cref="InputTransport.None"/> before the first input).</param>
/// <param name="RttMs">Last measured control round-trip time in milliseconds, if known.</param>
public readonly record struct PadConnectionState(PadConnectionKind Kind, InputTransport Transport = InputTransport.None, int? RttMs = null)
{
    public static PadConnectionState Disconnected => default;

    public static PadConnectionState Connecting => new(PadConnectionKind.Connecting);

    public static PadConnectionState Reconnecting => new(PadConnectionKind.Reconnecting);

    public static PadConnectionState Connected(InputTransport transport, int? rttMs) => new(PadConnectionKind.Connected, transport, rttMs);
}
