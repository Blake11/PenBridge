using PenBridge.Core.Protocol;

namespace PenBridge.Android.Mirror;

/// <summary>MIME types and names for Core's <see cref="MirrorCodec"/> (the MirrorConfig <c>codec</c>, spec "Control messages").</summary>
internal static class MirrorCodecExtensions
{
    public const string MimeH264 = "video/avc";
    public const string MimeHevc = "video/hevc";

    public static string Mime(this MirrorCodec codec) => codec == MirrorCodec.Hevc ? MimeHevc : MimeH264;

    public static MirrorCodecs Flag(this MirrorCodec codec) => codec == MirrorCodec.Hevc ? MirrorCodecs.Hevc : MirrorCodecs.H264;

    public static string Name(this MirrorCodec codec) => codec == MirrorCodec.Hevc ? "HEVC" : "H.264";
}

/// <summary>
/// One stream configuration from the Mac (the MirrorConfig fields the decoder needs). A new epoch with the same codec
/// and size keeps the running decoder; a codec or size change reconfigures it.
/// </summary>
/// <param name="Codec">Stream codec.</param>
/// <param name="Width">Coded width in pixels (even, ≥ 2).</param>
/// <param name="Height">Coded height in pixels (even, ≥ 2).</param>
/// <param name="Fps">Frame rate the Mac sends at (the decoder's operating rate hint).</param>
internal readonly record struct MirrorVideoConfig(MirrorCodec Codec, int Width, int Height, int Fps);
