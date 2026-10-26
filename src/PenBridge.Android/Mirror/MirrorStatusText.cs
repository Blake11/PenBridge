using PenBridge.Core.Protocol;

namespace PenBridge.Android.Mirror;

/// <summary>How the pad presents a MirrorState (spec "Control messages" → MirrorState): overlay, reason line, video.</summary>
/// <param name="Overlay">What the state overlay shows over the active area.</param>
/// <param name="Reason">The reason line (local text only, never peer text), or null.</param>
/// <param name="KeepVideo">
/// True: the video stays (streaming, starting, or a pause that shows the last frame under the overlay); false: it is
/// hidden and the pad shows its markers again.
/// </param>
internal readonly record struct MirrorPresentation(MirrorOverlayState Overlay, string? Reason, bool KeepVideo);

internal static class MirrorStatusText
{
    /// <summary>The overlay for no hardware decoder on this phone (checked before MirrorStart).</summary>
    public const string NoDecoder = "This phone has no hardware H.264 or HEVC video decoder.";

    /// <summary>
    /// Maps a state and reason to what the pad shows. Off never shows an overlay (the setting or the phone stopped it).
    /// An unknown state already arrives as Unavailable and an unknown reason as None (Core).
    /// </summary>
    public static MirrorPresentation Describe(MirrorState state, MirrorReason reason) => state switch
    {
        MirrorState.Streaming or MirrorState.Starting => new(MirrorOverlayState.None, null, KeepVideo: true),
        MirrorState.Paused => new(MirrorOverlayState.Paused, PausedReason(reason), KeepVideo: true),
        MirrorState.Throttled => new(MirrorOverlayState.Paused, "Video is paused to keep the pen responsive while the Wi-Fi is busy.", KeepVideo: true),
        MirrorState.Unavailable => new(MirrorOverlayState.Unavailable, UnavailableReason(reason), KeepVideo: false),
        _ => new(MirrorOverlayState.None, null, KeepVideo: false), // Off
    };

    private static string? PausedReason(MirrorReason reason) => reason switch
    {
        MirrorReason.PenBridgePaused => "PenBridge is paused on the Mac.",
        _ => null,
    };

    private static string? UnavailableReason(MirrorReason reason) => reason switch
    {
        MirrorReason.PermissionDenied => "The Mac needs the Screen Recording permission for PenBridge: System Settings ▸ Privacy & Security ▸ Screen Recording.",
        MirrorReason.TlsFallback => "The connection runs without UDP (fallback), so there is no video. It resumes when UDP works again; check the Wi-Fi or a firewall.",
        MirrorReason.NoCommonCodec => "The Mac and this phone have no video codec in common.",
        MirrorReason.CaptureFailed => "The Mac couldn't capture or encode the screen.",
        MirrorReason.PenBridgePaused => "PenBridge is paused on the Mac.",
        _ => "Screen mirroring isn't available right now. Check that it is on in PenBridge on the Mac.",
    };
}
