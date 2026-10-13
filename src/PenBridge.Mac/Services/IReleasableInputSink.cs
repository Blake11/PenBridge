using PenBridge.Core.Input;

namespace PenBridge.Mac.Services;

/// <summary>
/// An <see cref="IInputSink"/> that can release everything it currently holds (pen contact, right button,
/// tablet proximity). <see cref="ServerHost"/> calls <see cref="ReleaseAll"/> when input is paused.
/// </summary>
/// <remarks>
/// <see cref="ServerHost"/> serializes <see cref="ReleaseAll"/> with <see cref="IInputSink.OnPenEvent"/> and
/// <see cref="IInputSink.OnGestureEvent"/> (one lock in <see cref="PausableInputSink"/>), so implementations never see
/// them concurrently. After <see cref="ReleaseAll"/>, the next event the sink receives is a proximity enter
/// (real or synthesized), never a dangling drag or up.
/// </remarks>
public interface IReleasableInputSink : IInputSink
{
    /// <summary>Posts any outstanding up / right-up / proximity leave so nothing stays stuck. Idempotent.</summary>
    void ReleaseAll();
}
