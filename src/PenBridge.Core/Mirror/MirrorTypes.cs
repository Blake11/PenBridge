using PenBridge.Core.Protocol;

namespace PenBridge.Core.Mirror;

/// <summary>The <c>MirrorQuality</c> setting (key 31): which bitrate preset the planner uses.</summary>
public enum MirrorQuality
{
    /// <summary>6 Mbps target, 2 Mbps floor (at 60 fps and 2.5 MP).</summary>
    Low = 0,

    /// <summary>12 Mbps target, 3 Mbps floor (the default).</summary>
    Balanced = 1,

    /// <summary>24 Mbps target, 6 Mbps floor.</summary>
    High = 2,
}

/// <summary>
/// What the phone asks for in MirrorStart (without its video port, which belongs to the connection): its hardware
/// decoders, the decoder's size limit and the display's highest refresh rate. The client's
/// <c>StartMirror(MirrorRequest)</c> sends it; the server's <c>MirrorRequested</c> event carries it.
/// </summary>
/// <param name="Codecs">Hardware decoders on the phone.</param>
/// <param name="MaxWidthPx">Decoder width limit, 1..65535.</param>
/// <param name="MaxHeightPx">Decoder height limit, 1..65535.</param>
/// <param name="MaxFps">The phone display's highest refresh rate, rounded, 1..255.</param>
public readonly record struct MirrorRequest(MirrorCodecs Codecs, int MaxWidthPx, int MaxHeightPx, int MaxFps)
{
    /// <summary>The request a received MirrorStart carries.</summary>
    public static MirrorRequest FromMessage(in MirrorStartMessage message) =>
        new(message.Codecs, message.MaxWidthPx, message.MaxHeightPx, message.MaxFps);

    /// <summary>The MirrorStart for this request. Throws <see cref="ArgumentOutOfRangeException"/> for a value that doesn't fit the wire.</summary>
    public MirrorStartMessage ToMessage(ushort videoUdpPort)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxWidthPx, 1, nameof(MaxWidthPx));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxWidthPx, ushort.MaxValue, nameof(MaxWidthPx));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHeightPx, 1, nameof(MaxHeightPx));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxHeightPx, ushort.MaxValue, nameof(MaxHeightPx));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFps, 1, nameof(MaxFps));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxFps, byte.MaxValue, nameof(MaxFps));
        ArgumentOutOfRangeException.ThrowIfZero(videoUdpPort);
        return new MirrorStartMessage(Codecs, (ushort)MaxWidthPx, (ushort)MaxHeightPx, (byte)MaxFps, videoUdpPort);
    }
}

/// <summary>
/// The capture source of the current target, as the Mac head reports it (<c>SetMirrorSource(pixelSize, refreshHz)</c>):
/// its size in pixels and its display's refresh rate (All displays: the highest; a variable-rate display: its maximum).
/// </summary>
/// <param name="WidthPx">Source width in pixels, at least 1.</param>
/// <param name="HeightPx">Source height in pixels, at least 1.</param>
/// <param name="RefreshHz">Refresh rate, rounded, at least 1.</param>
public readonly record struct MirrorSource(int WidthPx, int HeightPx, int RefreshHz);

/// <summary>
/// One stream configuration: what a MirrorConfig carries without the Mac's video port. The server raises it in
/// <c>MirrorConfigChanged</c> (the head reconfigures its encoder), the client in <c>MirrorConfigReceived</c> (the head
/// reconfigures its decoder). Fps and bitrate are the epoch's planned values; the input guard runs the epoch at a lower
/// rate without a new configuration (<see cref="MirrorRateChange"/>).
/// </summary>
/// <param name="Epoch">Stream generation, mod 256.</param>
/// <param name="Codec">Codec of the stream.</param>
/// <param name="WidthPx">Width in pixels, even.</param>
/// <param name="HeightPx">Height in pixels, even.</param>
/// <param name="Fps">Frame rate.</param>
/// <param name="BitrateKbps">Average encoder bitrate.</param>
public readonly record struct MirrorStreamConfig(byte Epoch, MirrorCodec Codec, int WidthPx, int HeightPx, int Fps, int BitrateKbps)
{
    /// <summary>The configuration a received MirrorConfig carries.</summary>
    public static MirrorStreamConfig FromMessage(in MirrorConfigMessage message) =>
        new(message.Epoch, message.Codec, message.WidthPx, message.HeightPx, message.Fps, (int)Math.Min(message.BitrateKbps, int.MaxValue));

    /// <summary>The MirrorConfig for this configuration. Throws <see cref="ArgumentOutOfRangeException"/> for a value that doesn't fit the wire.</summary>
    public MirrorConfigMessage ToMessage(ushort videoUdpPort)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(WidthPx, 1, nameof(WidthPx));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(WidthPx, ushort.MaxValue, nameof(WidthPx));
        ArgumentOutOfRangeException.ThrowIfLessThan(HeightPx, 1, nameof(HeightPx));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(HeightPx, ushort.MaxValue, nameof(HeightPx));
        ArgumentOutOfRangeException.ThrowIfLessThan(Fps, 1, nameof(Fps));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Fps, byte.MaxValue, nameof(Fps));
        ArgumentOutOfRangeException.ThrowIfLessThan(BitrateKbps, 1, nameof(BitrateKbps));
        ArgumentOutOfRangeException.ThrowIfZero(videoUdpPort);
        return new MirrorConfigMessage(Epoch, Codec, (ushort)WidthPx, (ushort)HeightPx, (byte)Fps, (uint)BitrateKbps, videoUdpPort);
    }
}

/// <summary>
/// The rate the Mac's encoder and capture run the current epoch at (the server's <c>MirrorRateChanged</c>): the input
/// guard's bitrate and fps, applied in place (no new epoch, no keyframe, nothing sent to the phone).
/// </summary>
/// <param name="Epoch">The epoch it applies to; ignore it for another epoch.</param>
/// <param name="Fps">Frame rate: capture interval and the encoder's expected frame rate.</param>
/// <param name="BitrateKbps">Average encoder bitrate.</param>
public readonly record struct MirrorRateChange(byte Epoch, int Fps, int BitrateKbps);

/// <summary>Where the input guard (<see cref="MirrorRateController"/>) holds the stream right now.</summary>
/// <param name="BitrateKbps">Current bitrate, between the plan's floor and target.</param>
/// <param name="Fps">Current frame rate, the plan's or a halving of it (never below 30 by halving).</param>
/// <param name="Paused">The guard paused the video: the Mac sends <c>MirrorState(Throttled)</c> and no frames.</param>
public readonly record struct MirrorGuardLevel(int BitrateKbps, int Fps, bool Paused);
