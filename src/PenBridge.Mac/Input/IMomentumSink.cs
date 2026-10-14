namespace PenBridge.Mac.Input;

/// <summary>
/// An input sink that may keep posting after the input stops (scroll momentum). The session's end stops it: the
/// recognizer's session-end cancel only reaches gestures that are still active, not a coast after the fingers lifted.
/// </summary>
public interface IMomentumSink
{
    /// <summary>Stops a scroll coast at once (posting its end). Idempotent, any thread.</summary>
    void StopMomentum();
}
