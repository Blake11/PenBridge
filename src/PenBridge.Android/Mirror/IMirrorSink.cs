using PenBridge.Core.Mirror;
using PenBridge.Core.Protocol;

namespace PenBridge.Android.Mirror;

/// <summary>
/// Receives the current session's screen-mirroring events from <c>ClientHost</c> on Core's threads. Every method must
/// return quickly and never block: they run on the session's read loop or the video receive thread.
/// </summary>
internal interface IMirrorSink
{
    /// <summary>A new epoch (MirrorConfig), on the read-loop thread, before any of its video can arrive.</summary>
    void OnMirrorConfig(MirrorStreamConfig config);

    /// <summary>The Mac's MirrorState, or the client's own Unavailable/TlsFallback (read loop or thread pool).</summary>
    void OnMirrorState(MirrorState state, MirrorReason reason);

    /// <summary>
    /// One reassembled access unit, on the video receive thread. The caller disposes <paramref name="frame"/> when this
    /// returns, so copy what is needed (the decoder's Feed does). Allocation-free.
    /// </summary>
    void OnVideoFrame(in VideoFrame frame);
}
