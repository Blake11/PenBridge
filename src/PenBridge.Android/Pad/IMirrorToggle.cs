namespace PenBridge.Android.Pad;

/// <summary>The pad menu's screen-mirroring quick toggle (ADR-0038): writes the Mac's MirrorEnabled setting. UI thread.</summary>
internal interface IMirrorToggle
{
    /// <summary>The session offers mirroring (protocol 1.3 with ScreenMirror on both sides); otherwise the item is hidden.</summary>
    bool IsAvailable { get; }

    /// <summary>MirrorEnabled in the Mac's latest settings snapshot.</summary>
    bool IsEnabled { get; }

    /// <summary>Sends MirrorEnabled to the Mac; the next snapshot is the truth.</summary>
    void SetEnabled(bool enabled);
}
