namespace PenBridge.Core.Protocol;

#pragma warning disable CA1028 // Wire enums: storage matches the wire field width.

/// <summary>
/// <c>u8</c> codec mask in MirrorStart: the hardware decoders the phone has (or, on the Mac, its hardware encoders).
/// Unknown bits are preserved and ignored.
/// </summary>
[Flags]
public enum MirrorCodecs : byte
{
    None = 0,

    /// <summary>H.264 / AVC.</summary>
    H264 = 1,

    /// <summary>HEVC / H.265.</summary>
    Hevc = 2,
}

/// <summary><c>u8</c> codec of a stream in MirrorConfig. Any other value is invalid.</summary>
public enum MirrorCodec : byte
{
    /// <summary>H.264 / AVC, Annex-B; keyframes start with SPS/PPS.</summary>
    H264 = 1,

    /// <summary>HEVC / H.265, Annex-B; keyframes start with VPS/SPS/PPS.</summary>
    Hevc = 2,
}

/// <summary><c>u8</c> mirroring state in MirrorState. A reader shows an unknown value as <see cref="Unavailable"/> (<see cref="MirrorStateMessage.State"/>).</summary>
public enum MirrorState : byte
{
    Off = 0,
    Starting = 1,
    Streaming = 2,
    Paused = 3,

    /// <summary>The input guard paused the video (ADR-0038 "input always wins").</summary>
    Throttled = 4,
    Unavailable = 5,
}

/// <summary>
/// <c>u8</c> reason in MirrorState, for <see cref="MirrorState.Off"/>, <see cref="MirrorState.Paused"/> and
/// <see cref="MirrorState.Unavailable"/> (otherwise <see cref="None"/>). Unknown values are tolerated (shown as None).
/// </summary>
public enum MirrorReason : byte
{
    None = 0,

    /// <summary>MirrorEnabled is off.</summary>
    SettingOff = 1,

    /// <summary>The user paused PenBridge on the Mac.</summary>
    PenBridgePaused = 2,

    /// <summary>The Mac has no Screen Recording permission.</summary>
    PermissionDenied = 3,

    /// <summary>The session runs in TLS fallback (no UDP).</summary>
    TlsFallback = 4,

    /// <summary>The phone's decoders and the Mac's encoders have no codec in common.</summary>
    NoCommonCodec = 5,

    /// <summary>Capture or encode failed on the Mac.</summary>
    CaptureFailed = 6,

    /// <summary>The phone sent MirrorStop.</summary>
    PhoneStopped = 7,
}

#pragma warning restore CA1028

/// <summary>Shared rules of the protocol 1.3 mirroring messages (<c>docs/specs/screen-mirroring.md</c>).</summary>
public static class MirrorWire
{
    /// <summary>The codec bits this build knows.</summary>
    public const MirrorCodecs KnownCodecs = MirrorCodecs.H264 | MirrorCodecs.Hevc;

    /// <summary>True for a codec value this build knows.</summary>
    public static bool IsKnown(MirrorCodec codec) => codec is MirrorCodec.H264 or MirrorCodec.Hevc;

    /// <summary>True for a state value this build knows.</summary>
    public static bool IsKnown(MirrorState state) => state <= MirrorState.Unavailable;

    /// <summary>True for a reason value this build knows.</summary>
    public static bool IsKnown(MirrorReason reason) => reason <= MirrorReason.PhoneStopped;

    /// <summary>True when the state carries a reason (Off, Paused, Unavailable); the other states carry <see cref="MirrorReason.None"/>.</summary>
    public static bool HasReason(MirrorState state) => state is MirrorState.Off or MirrorState.Paused or MirrorState.Unavailable;

    /// <summary>The mask bit of one codec.</summary>
    public static MirrorCodecs ToMask(MirrorCodec codec) => codec switch
    {
        MirrorCodec.H264 => MirrorCodecs.H264,
        MirrorCodec.Hevc => MirrorCodecs.Hevc,
        _ => MirrorCodecs.None,
    };

    /// <summary>
    /// What a reader shows for a received <c>u8 state ‖ u8 reason</c> (forward compatible, never an error): an unknown
    /// state becomes <see cref="MirrorState.Unavailable"/> (keeping a known reason); a known state with an unknown reason
    /// keeps its state with <see cref="MirrorReason.None"/>; a reason sent with a state that carries none is ignored.
    /// </summary>
    public static (MirrorState State, MirrorReason Reason) Interpret(byte rawState, byte rawReason)
    {
        var state = (MirrorState)rawState;
        var reason = (MirrorReason)rawReason;
        bool reasonKnown = IsKnown(reason);
        if (!IsKnown(state))
        {
            return (MirrorState.Unavailable, reasonKnown ? reason : MirrorReason.None);
        }

        return HasReason(state) && reasonKnown ? (state, reason) : (state, MirrorReason.None);
    }
}

/// <summary>
/// 0x50 C → S (needs <see cref="ProtocolCapabilities.ScreenMirror"/>). The phone wants video. Payload: <c>u8 codecs ‖
/// u16 maxWidthPx ‖ u16 maxHeightPx ‖ u8 maxFps ‖ u16 videoUdpPort</c>. Sizes, fps and port must be non-zero; unknown
/// codec bits are kept (and ignored by the planner). An empty codec mask is valid: the Mac answers
/// <c>Unavailable/NoCommonCodec</c>.
/// </summary>
public readonly struct MirrorStartMessage : IControlMessage<MirrorStartMessage>
{
    public const int Size = 1 + 2 + 2 + 1 + 2;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for a zero size, fps or port.</summary>
    public MirrorStartMessage(MirrorCodecs codecs, ushort maxWidthPx, ushort maxHeightPx, byte maxFps, ushort videoUdpPort)
    {
        if (!IsValid(maxWidthPx, maxHeightPx, maxFps, videoUdpPort))
        {
            throw new ArgumentOutOfRangeException(nameof(maxWidthPx), "Sizes, fps and port must be non-zero.");
        }

        Codecs = codecs;
        MaxWidthPx = maxWidthPx;
        MaxHeightPx = maxHeightPx;
        MaxFps = maxFps;
        VideoUdpPort = videoUdpPort;
    }

    public static ControlMessageType Type => ControlMessageType.MirrorStart;

    /// <summary>The phone's hardware decoders.</summary>
    public MirrorCodecs Codecs { get; }

    /// <summary>The decoder's largest width in pixels.</summary>
    public ushort MaxWidthPx { get; }

    public ushort MaxHeightPx { get; }

    /// <summary>The display refresh rate, at least 1.</summary>
    public byte MaxFps { get; }

    /// <summary>The phone's video socket port (the source of its Probes).</summary>
    public ushort VideoUdpPort { get; }

    public int PayloadSize => IsValid(MaxWidthPx, MaxHeightPx, MaxFps, VideoUdpPort) ? Size : throw new InvalidOperationException("Invalid MirrorStart.");

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteU8((byte)Codecs);
        w.WriteU16(MaxWidthPx);
        w.WriteU16(MaxHeightPx);
        w.WriteU8(MaxFps);
        w.WriteU16(VideoUdpPort);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out MirrorStartMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadU8(out byte codecs) || !r.TryReadU16(out ushort width) || !r.TryReadU16(out ushort height)
            || !r.TryReadU8(out byte fps) || !r.TryReadU16(out ushort port) || !IsValid(width, height, fps, port))
        {
            return false;
        }

        message = new MirrorStartMessage((MirrorCodecs)codecs, width, height, fps, port);
        return true;
    }

    private static bool IsValid(ushort width, ushort height, byte fps, ushort port) => width > 0 && height > 0 && fps > 0 && port > 0;
}

/// <summary>0x51 C → S (needs <see cref="ProtocolCapabilities.ScreenMirror"/>). Empty payload: the phone no longer wants video.</summary>
public readonly struct MirrorStopMessage : IControlMessage<MirrorStopMessage>
{
    public static ControlMessageType Type => ControlMessageType.MirrorStop;

    public int PayloadSize => 0;

    public int WritePayload(Span<byte> destination) => 0;

    public static bool TryRead(ReadOnlySpan<byte> payload, out MirrorStopMessage message)
    {
        message = default;
        return true;
    }
}

/// <summary>
/// 0x52 S → C (needs <see cref="ProtocolCapabilities.ScreenMirror"/>). The stream of a new epoch. Payload: <c>u8 epoch ‖
/// u8 codec ‖ u16 widthPx ‖ u16 heightPx ‖ u8 fps ‖ u32 bitrateKbps ‖ u16 videoUdpPort</c>. The codec must be known;
/// sizes, fps, bitrate and port non-zero.
/// </summary>
public readonly struct MirrorConfigMessage : IControlMessage<MirrorConfigMessage>
{
    public const int Size = 1 + 1 + 2 + 2 + 1 + 4 + 2;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for an unknown codec or a zero size, fps, bitrate or port.</summary>
    public MirrorConfigMessage(byte epoch, MirrorCodec codec, ushort widthPx, ushort heightPx, byte fps, uint bitrateKbps, ushort videoUdpPort)
    {
        if (!IsValid(codec, widthPx, heightPx, fps, bitrateKbps, videoUdpPort))
        {
            throw new ArgumentOutOfRangeException(nameof(codec), "Known codec; non-zero sizes, fps, bitrate and port.");
        }

        Epoch = epoch;
        Codec = codec;
        WidthPx = widthPx;
        HeightPx = heightPx;
        Fps = fps;
        BitrateKbps = bitrateKbps;
        VideoUdpPort = videoUdpPort;
    }

    public static ControlMessageType Type => ControlMessageType.MirrorConfig;

    /// <summary>Stream generation, mod 256; video datagrams of other epochs are dropped.</summary>
    public byte Epoch { get; }

    public MirrorCodec Codec { get; }

    public ushort WidthPx { get; }

    public ushort HeightPx { get; }

    public byte Fps { get; }

    /// <summary>Average encoder bitrate.</summary>
    public uint BitrateKbps { get; }

    /// <summary>The Mac's video socket port: the phone sends Probes there and accepts video only from it.</summary>
    public ushort VideoUdpPort { get; }

    public int PayloadSize => IsValid(Codec, WidthPx, HeightPx, Fps, BitrateKbps, VideoUdpPort) ? Size : throw new InvalidOperationException("Invalid MirrorConfig.");

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, PayloadSize, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteU8(Epoch);
        w.WriteU8((byte)Codec);
        w.WriteU16(WidthPx);
        w.WriteU16(HeightPx);
        w.WriteU8(Fps);
        w.WriteU32(BitrateKbps);
        w.WriteU16(VideoUdpPort);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out MirrorConfigMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadU8(out byte epoch) || !r.TryReadU8(out byte codec) || !r.TryReadU16(out ushort width) || !r.TryReadU16(out ushort height)
            || !r.TryReadU8(out byte fps) || !r.TryReadU32(out uint bitrate) || !r.TryReadU16(out ushort port)
            || !IsValid((MirrorCodec)codec, width, height, fps, bitrate, port))
        {
            return false;
        }

        message = new MirrorConfigMessage(epoch, (MirrorCodec)codec, width, height, fps, bitrate, port);
        return true;
    }

    private static bool IsValid(MirrorCodec codec, ushort width, ushort height, byte fps, uint bitrate, ushort port) =>
        MirrorWire.IsKnown(codec) && width > 0 && height > 0 && fps > 0 && bitrate > 0 && port > 0;
}

/// <summary>
/// 0x53 S → C (needs <see cref="ProtocolCapabilities.ScreenMirror"/>). Payload: <c>u8 state ‖ u8 reason</c>. Any values
/// are accepted (forward compatible); <see cref="State"/> and <see cref="Reason"/> interpret them
/// (<see cref="MirrorWire.Interpret"/>), <see cref="RawState"/> and <see cref="RawReason"/> keep the bytes.
/// </summary>
public readonly struct MirrorStateMessage : IControlMessage<MirrorStateMessage>
{
    public const int Size = 2;

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> for an unknown state or reason, or a reason with a state that
    /// carries none (Starting, Streaming, Throttled).
    /// </summary>
    public MirrorStateMessage(MirrorState state, MirrorReason reason)
    {
        if (!MirrorWire.IsKnown(state) || !MirrorWire.IsKnown(reason) || (reason != MirrorReason.None && !MirrorWire.HasReason(state)))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), "Known state and reason; a reason only with Off, Paused or Unavailable.");
        }

        RawState = (byte)state;
        RawReason = (byte)reason;
    }

    private MirrorStateMessage(byte rawState, byte rawReason)
    {
        RawState = rawState;
        RawReason = rawReason;
    }

    public static ControlMessageType Type => ControlMessageType.MirrorState;

    /// <summary>The state byte as received.</summary>
    public byte RawState { get; }

    /// <summary>The reason byte as received.</summary>
    public byte RawReason { get; }

    /// <summary>The state to show: unknown values read as <see cref="MirrorState.Unavailable"/>.</summary>
    public MirrorState State => MirrorWire.Interpret(RawState, RawReason).State;

    /// <summary>The reason to show (<see cref="MirrorReason.None"/> for states that carry none or an unknown reason).</summary>
    public MirrorReason Reason => MirrorWire.Interpret(RawState, RawReason).Reason;

    public int PayloadSize => Size;

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Size, nameof(destination));
        destination[0] = RawState;
        destination[1] = RawReason;
        return Size;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out MirrorStateMessage message)
    {
        message = default;
        if (payload.Length < Size)
        {
            return false;
        }

        message = new MirrorStateMessage(payload[0], payload[1]);
        return true;
    }
}

/// <summary>
/// 0x54 C → S (needs <see cref="ProtocolCapabilities.ScreenMirror"/>). Encode the next frame as a keyframe. Payload:
/// <c>u8 epoch ‖ u32 lastCompleteFrame</c> (the last frame the phone reassembled in that epoch, 0 before the first).
/// </summary>
public readonly struct KeyframeRequestMessage(byte epoch, uint lastCompleteFrame) : IControlMessage<KeyframeRequestMessage>
{
    public const int Size = 1 + 4;

    public static ControlMessageType Type => ControlMessageType.KeyframeRequest;

    public byte Epoch { get; } = epoch;

    public uint LastCompleteFrame { get; } = lastCompleteFrame;

    public int PayloadSize => Size;

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Size, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteU8(Epoch);
        w.WriteU32(LastCompleteFrame);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out KeyframeRequestMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadU8(out byte epoch) || !r.TryReadU32(out uint frame))
        {
            return false;
        }

        message = new KeyframeRequestMessage(epoch, frame);
        return true;
    }
}

/// <summary>
/// 0x55 C → S (needs <see cref="ProtocolCapabilities.ScreenMirror"/>), every 500 ms while streaming. Payload: <c>u8 epoch ‖
/// u32 framesComplete ‖ u32 framesDropped ‖ u32 fragmentsLost ‖ u32 lastRenderedFrame ‖ u64 lastRenderedAtUs</c>. The
/// counters count since the epoch's MirrorConfig and wrap; <c>lastRenderedAtUs</c> is the phone's Ping clock.
/// </summary>
public readonly struct MirrorReportMessage(
    byte epoch,
    uint framesComplete,
    uint framesDropped,
    uint fragmentsLost,
    uint lastRenderedFrame,
    ulong lastRenderedAtUs) : IControlMessage<MirrorReportMessage>
{
    public const int Size = 1 + 4 + 4 + 4 + 4 + 8;

    public static ControlMessageType Type => ControlMessageType.MirrorReport;

    public byte Epoch { get; } = epoch;

    public uint FramesComplete { get; } = framesComplete;

    public uint FramesDropped { get; } = framesDropped;

    public uint FragmentsLost { get; } = fragmentsLost;

    public uint LastRenderedFrame { get; } = lastRenderedFrame;

    public ulong LastRenderedAtUs { get; } = lastRenderedAtUs;

    public int PayloadSize => Size;

    public int WritePayload(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Size, nameof(destination));
        var w = new WireWriter(destination);
        w.WriteU8(Epoch);
        w.WriteU32(FramesComplete);
        w.WriteU32(FramesDropped);
        w.WriteU32(FragmentsLost);
        w.WriteU32(LastRenderedFrame);
        w.WriteU64(LastRenderedAtUs);
        return w.Written;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out MirrorReportMessage message)
    {
        message = default;
        var r = new WireReader(payload);
        if (!r.TryReadU8(out byte epoch) || !r.TryReadU32(out uint complete) || !r.TryReadU32(out uint dropped)
            || !r.TryReadU32(out uint lost) || !r.TryReadU32(out uint rendered) || !r.TryReadU64(out ulong renderedAt))
        {
            return false;
        }

        message = new MirrorReportMessage(epoch, complete, dropped, lost, rendered, renderedAt);
        return true;
    }
}
