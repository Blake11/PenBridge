using PenBridge.Core.Input;

namespace PenBridge.Mac.Input;

/// <summary>One CGEvent the injector should post.</summary>
internal enum TabletAction : byte
{
    ProximityEnter = 1,
    ProximityLeave = 2,
    MouseMoved = 3,
    LeftDown = 4,
    LeftDragged = 5,
    LeftUp = 6,
    RightDown = 7,
    RightDragged = 8,
    RightUp = 9,
}

/// <summary>A planned post: the action, the tool it belongs to, and whether the tip and side button are down after it.</summary>
internal readonly record struct TabletStep(TabletAction Action, PenTool Tool, bool Contact, bool Side);

/// <summary>What the injector has actually posted (not what the phone reports).</summary>
internal struct PostedState
{
    public bool InProximity;
    public PenTool Tool;
    public bool Left;
    public bool Right;
}

/// <summary>
/// Pure translation from Core <see cref="PenEvent"/> edges to the CGEvents to post, tracked against what was
/// actually posted so nothing can stay stuck.
/// </summary>
/// <remarks>
/// <para>Why a separate planner: the rules (Wacom side-button semantics, proximity framing, release on leave) are
/// the part that can be wrong; keeping them side-effect free makes them reviewable and testable without CoreGraphics.</para>
/// <para>Rules:</para>
/// <list type="bullet">
/// <item>Any pointer event first ensures proximity for its tool (posts ProximityEnter when not in proximity, or
/// leave/enter when the tool differs). This also recovers after a pause or a missing Accessibility grant.</item>
/// <item>Down posts LeftDown, Up posts LeftUp, only when the posted left state differs (no unmatched up/down).</item>
/// <item>Side button (Primary): ButtonDown posts RightDown at the current point and ButtonUp posts RightUp, hovering or
/// touching (ADR-0016). Secondary is reserved and ignored.</item>
/// <item>Motion posts LeftDragged while the tip is down, else RightDragged while the side button is down, else
/// MouseMoved. A Drag after the left button was released (pause mid-stroke) becomes a hover move until the next Down.</item>
/// <item>ToolChanged and ProximityLeave (and <see cref="Release"/>) post LeftUp, RightUp and ProximityLeave as needed;
/// ToolChanged then enters with the new tool, so apps switch pen/eraser.</item>
/// </list>
/// </remarks>
internal static class TabletEventPlanner
{
    /// <summary>Upper bound of steps for one event (tool mismatch on Down: LeftUp, RightUp, Leave, Enter, LeftDown).</summary>
    public const int MaxSteps = 6;

    /// <summary>Plans the posts for one pen event and updates <paramref name="state"/>. Returns the step count.</summary>
    public static int Plan(ref PostedState state, PenEventKind kind, PenButtons button, PenTool tool, Span<TabletStep> steps)
    {
        int n = 0;
        switch (kind)
        {
            case PenEventKind.ProximityEnter:
                EnsureProximity(ref state, tool, steps, ref n);
                break;

            case PenEventKind.ToolChanged:
                ReleaseInto(ref state, steps, ref n);
                EnsureProximity(ref state, tool, steps, ref n);
                break;

            case PenEventKind.ProximityLeave:
                ReleaseInto(ref state, steps, ref n);
                break;

            case PenEventKind.Move:
            case PenEventKind.Drag:
                EnsureProximity(ref state, tool, steps, ref n);
                steps[n++] = new TabletStep(MotionAction(state.Left, state.Right), state.Tool, state.Left, state.Right);
                break;

            case PenEventKind.Down:
                EnsureProximity(ref state, tool, steps, ref n);
                if (!state.Left)
                {
                    state.Left = true;
                    steps[n++] = new TabletStep(TabletAction.LeftDown, state.Tool, true, state.Right);
                }
                else
                {
                    steps[n++] = new TabletStep(TabletAction.LeftDragged, state.Tool, true, state.Right);
                }

                break;

            case PenEventKind.Up:
                if (state.Left)
                {
                    state.Left = false;
                    steps[n++] = new TabletStep(TabletAction.LeftUp, state.Tool, false, state.Right);
                }

                break;

            case PenEventKind.ButtonDown when button == PenButtons.Primary:
                EnsureProximity(ref state, tool, steps, ref n);
                if (!state.Right)
                {
                    state.Right = true;
                    steps[n++] = new TabletStep(TabletAction.RightDown, state.Tool, state.Left, state.Right);
                }

                break;

            case PenEventKind.ButtonUp when button == PenButtons.Primary:
                if (state.Right)
                {
                    state.Right = false;
                    steps[n++] = new TabletStep(TabletAction.RightUp, state.Tool, state.Left, state.Right);
                }

                break;

            default:
                // Secondary button (reserved until Phase 5) and unknown kinds: nothing to post.
                break;
        }

        return n;
    }

    /// <summary>Plans LeftUp, RightUp and ProximityLeave for whatever is still posted. Returns the step count.</summary>
    public static int Release(ref PostedState state, Span<TabletStep> steps)
    {
        int n = 0;
        ReleaseInto(ref state, steps, ref n);
        return n;
    }

    /// <summary>The mouse event for motion given the posted button state.</summary>
    public static TabletAction MotionAction(bool left, bool right) =>
        left ? TabletAction.LeftDragged : right ? TabletAction.RightDragged : TabletAction.MouseMoved;

    private static void EnsureProximity(ref PostedState state, PenTool tool, Span<TabletStep> steps, ref int n)
    {
        if (state.InProximity && state.Tool == tool)
        {
            return;
        }

        ReleaseInto(ref state, steps, ref n);
        state.InProximity = true;
        state.Tool = tool;
        steps[n++] = new TabletStep(TabletAction.ProximityEnter, tool, false, state.Right);
    }

    private static void ReleaseInto(ref PostedState state, Span<TabletStep> steps, ref int n)
    {
        if (state.Left)
        {
            state.Left = false;
            steps[n++] = new TabletStep(TabletAction.LeftUp, state.Tool, false, state.Right);
        }

        if (state.Right)
        {
            state.Right = false;
            steps[n++] = new TabletStep(TabletAction.RightUp, state.Tool, false, state.Right);
        }

        if (state.InProximity)
        {
            state.InProximity = false;
            steps[n++] = new TabletStep(TabletAction.ProximityLeave, state.Tool, false, state.Right);
        }
    }
}
