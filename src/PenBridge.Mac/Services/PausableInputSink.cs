using PenBridge.Core.Input;
using PenBridge.Mac.Input;

namespace PenBridge.Mac.Services;

/// <summary>
/// The sink handed to <see cref="PenBridge.Core.Net.PenBridgeServer"/>: forwards to the injector unless paused.
/// Pausing releases the injector first, then drops input while the session stays connected.
/// </summary>
/// <remarks>
/// <para>After a resume the injector has seen no proximity enter for the pen that is (maybe) already in range, so
/// events are dropped until a resync point: a <see cref="PenEventKind.ProximityEnter"/> or
/// <see cref="PenEventKind.ToolChanged"/> is forwarded as is, and a hover <see cref="PenEventKind.Move"/> is preceded
/// by a synthesized proximity enter. Contact events (Down/Drag/Up) and buttons are dropped until then, so a stroke in
/// progress at resume time starts injecting after the pen lifts.</para>
/// <para>Gestures likewise: pausing makes the injector cancel an open gesture and stop scroll momentum
/// (<see cref="IReleasableInputSink.ReleaseAll"/>), which also releases a one- or three-finger drag's button. After a
/// resume the phased gestures (scroll, pinch, drag, system swipe/pinch, pointer move/drag) are dropped until a
/// <see cref="GesturePhase.Began"/>, so a gesture in progress at resume time never continues with a dangling
/// Changed/Ended. One-shot events (Tap, SmartZoom, TouchDown, Click) pass at once.</para>
/// <para>It is the sink Core's tracker sees, so it also forwards <see cref="ICursorLocator"/> (Mouse mode anchors at
/// the host cursor, ADR-0032) to the injector, outside the pause lock: reading the cursor posts nothing.</para>
/// <para>Platform-free; a candidate for Core if another head needs it.</para>
/// <para>Hot path: one uncontended lock per event, no allocations.</para>
/// </remarks>
public sealed class PausableInputSink : IInputSink, ICursorLocator
{
    private readonly IReleasableInputSink _inner;
    private readonly Lock _lock = new();
    private bool _paused;
    private bool _needsResync;
    private bool _gestureNeedsBegan;

    public PausableInputSink(IReleasableInputSink inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _paused;
            }
        }
    }

    /// <summary>Pauses (releasing the injector first) or resumes input. Callable from any thread.</summary>
    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            if (_paused == paused)
            {
                return;
            }

            if (paused)
            {
                _inner.ReleaseAll();
            }

            _paused = paused;
            _needsResync = true;
            _gestureNeedsBegan = true;
        }
    }

    /// <summary>Stops scroll momentum in the injector, if it has any (session end). Callable from any thread.</summary>
    public void StopMomentum()
    {
        if (_inner is IMomentumSink momentum)
        {
            lock (_lock)
            {
                momentum.StopMomentum();
            }
        }
    }

    /// <inheritdoc />
    public bool TryGetCursorLocation(out DisplayPoint location)
    {
        if (_inner is ICursorLocator locator)
        {
            return locator.TryGetCursorLocation(out location);
        }

        location = default;
        return false;
    }

    public void OnPenEvent(in PenEvent e)
    {
        lock (_lock)
        {
            if (_paused)
            {
                return;
            }

            if (_needsResync)
            {
                switch (e.Kind)
                {
                    case PenEventKind.ProximityEnter or PenEventKind.ToolChanged:
                        _needsResync = false;
                        break;
                    case PenEventKind.Move:
                        _needsResync = false;
                        _inner.OnPenEvent(e with { Kind = PenEventKind.ProximityEnter, Button = PenButtons.None, PreviousTool = PenTool.None, Synthetic = true });
                        break;
                    default:
                        return;
                }
            }

            _inner.OnPenEvent(e);
        }
    }

    public void OnGestureEvent(in GestureEvent e)
    {
        lock (_lock)
        {
            if (_paused)
            {
                return;
            }

            if (_gestureNeedsBegan && IsPhased(e.Kind))
            {
                if (e.Phase != GesturePhase.Began)
                {
                    return;
                }

                _gestureNeedsBegan = false;
            }

            _inner.OnGestureEvent(e);
        }
    }

    /// <summary>Gesture kinds with Began/Changed/Ended phases (the rest are one-shot events).</summary>
    private static bool IsPhased(GestureKind kind) =>
        kind is GestureKind.Scroll or GestureKind.Pinch or GestureKind.Drag or GestureKind.SystemSwipe or GestureKind.SystemPinch
            or GestureKind.PointerMove or GestureKind.PointerDrag;
}
