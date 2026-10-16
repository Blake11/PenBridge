using PenBridge.Core.Input;
using PenBridge.Core.Protocol;

namespace PenBridge.Android.Pad;

/// <summary>
/// Where the pad sends its input and reads its connection status from. Implemented by the client host (P3-A
/// <c>ClientHost</c>, adapted by P3-C) on top of <c>PenBridgeClient</c>.
/// </summary>
public interface IPadOutput
{
    /// <summary>Current connection status shown by the pad's status dot and menu.</summary>
    PadConnectionState ConnectionState { get; }

    /// <summary>Raised (on any thread) after <see cref="ConnectionState"/> changed. The pad re-reads the property.</summary>
    event EventHandler? ConnectionStateChanged;

    /// <summary>
    /// The current session negotiated <c>ProtocolCapabilities.FingerTouches</c> (ADR-0026: a lone finger goes out as
    /// raw touches and the Mac moves the cursor relatively). False when not connected. Read on the UI thread; changes
    /// raise <see cref="ConnectionStateChanged"/>.
    /// </summary>
    bool FingerTouches { get; }

    /// <summary>
    /// The part of the surface that maps onto the Mac's whole target (ADR-0031), in the current surface orientation;
    /// the whole surface without one. Read on the UI thread.
    /// </summary>
    NormalizedRect ActiveArea { get; }

    /// <summary>Raised on the UI thread after <see cref="ActiveArea"/> changed. The pad re-reads the property.</summary>
    event EventHandler? ActiveAreaChanged;

    /// <summary>
    /// Sends the complete current input state. Same contract as <c>PenBridgeClient.SendState</c>: earlier batched pen
    /// samples in <paramref name="history"/> (oldest first, at most 8). Called from the pad's sender thread only: never
    /// on the UI thread and never concurrently. Returns false when not connected (the pad keeps going).
    /// </summary>
    bool SendState(in PenSample? pen, ReadOnlySpan<TouchSample> touches, ReadOnlySpan<PenSample> history);

    /// <summary>
    /// The drawing surface changed (first layout, or an orientation change applied between strokes). Called on the UI
    /// thread and must not block: send SurfaceInfo asynchronously when connected (<c>UpdateSurfaceAsync</c>) and keep
    /// the latest value as the surface for the next session start.
    /// </summary>
    void UpdateSurface(SurfaceInfoMessage surface);
}
