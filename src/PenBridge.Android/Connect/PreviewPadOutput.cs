#if DEBUG
using PenBridge.Android.Pad;
using PenBridge.Core.Input;
using PenBridge.Core.Protocol;

namespace PenBridge.Android.Connect;

/// <summary>
/// Debug-only pad output that sends nothing: lets Settings open the pad without a session, to check and screenshot the
/// surface look on the emulator (no Mac or pairing needed).
/// </summary>
internal sealed class PreviewPadOutput : IPadOutput
{
    public PadConnectionState ConnectionState => PadConnectionState.Disconnected;

    public bool FingerTouches => false;

    public event EventHandler? ConnectionStateChanged
    {
        add { }
        remove { }
    }

    public bool SendState(in PenSample? pen, ReadOnlySpan<TouchSample> touches, ReadOnlySpan<PenSample> history) => true;

    public void UpdateSurface(SurfaceInfoMessage surface)
    {
    }
}
#endif
