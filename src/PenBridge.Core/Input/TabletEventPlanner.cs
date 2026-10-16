namespace PenBridge.Core.Input;

/// <summary>One host input event a tablet injector should post (macOS: a CGEvent; other hosts: their equivalent).</summary>
public enum TabletAction : byte
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
    /// <summary>Middle (other, button 2) mouse down (<see cref="SideButtonAction.MiddleClick"/>).</summary>
    MiddleDown = 10,
    MiddleDragged = 11,
    MiddleUp = 12,
}

/// <summary>
/// A planned post: the action, the tool it belongs to, and whether the tip and side button are down after it.
/// <paramref name="ClickState"/>: the click state to post when the planner decides it
/// (<see cref="SideButtonAction.DoubleClick"/>: 1 then 2); 0 lets the injector count clicks as before.
/// </summary>
public readonly record struct TabletStep(TabletAction Action, PenTool Tool, bool Contact, bool Side, int ClickState = 0);

/// <summary>What the injector has actually posted (not what the phone reports). Only the planner changes it.</summary>
public struct TabletPostedState
{
    /// <summary>A proximity enter was posted without its leave.</summary>
    public bool InProximity { readonly get; internal set; }

    /// <summary>The tool of the posted proximity.</summary>
    public PenTool Tool { readonly get; internal set; }

    /// <summary>A left (tip) down was posted without its up.</summary>
    public bool Left { readonly get; internal set; }

    /// <summary>A right (side button) down was posted without its up.</summary>
    public bool Right { readonly get; internal set; }

    /// <summary>A middle (side button, <see cref="SideButtonAction.MiddleClick"/>) down was posted without its up.</summary>
    public bool Middle { readonly get; internal set; }

    /// <summary>The tip is down in a middle-drag stroke (the side button was held at contact-down with MiddleClick).</summary>
    public bool MiddleStroke { readonly get; internal set; }

    /// <summary>A middle down was ever planned, so <see cref="TabletEventPlanner.Repair"/> releases the middle button too.</summary>
    public bool MiddleUsed { readonly get; internal set; }

    /// <summary>
    /// The tip is down in a right-drag stroke: the side button was held at contact-down, so the right button carries
    /// the stroke instead of the left one (ADR-0022).
    /// </summary>
    public bool RightStroke { readonly get; internal set; }

    /// <summary>The tip is down as far as the host knows (a left stroke or a right-drag stroke).</summary>
    public readonly bool Contact => Left || RightStroke || MiddleStroke;

    /// <summary>A side-button mouse button (right or middle) is down.</summary>
    public readonly bool Side => Right || Middle;
}

/// <summary>
/// Pure translation from <see cref="PenEvent"/> edges to the host input events to post, tracked against what was
/// actually posted so nothing can stay stuck.
/// </summary>
/// <remarks>
/// <para>Why a separate planner: the rules (Wacom side-button semantics, proximity framing, release on leave) are
/// the part that can be wrong; keeping them side-effect free in Core makes them testable without a host. Allocation-free.</para>
/// <para>Rules:</para>
/// <list type="bullet">
/// <item>Any pointer event first ensures proximity for its tool (posts ProximityEnter when not in proximity, or
/// leave/enter when the tool differs). This also recovers after a pause or a missing host permission (e.g. macOS Accessibility).</item>
/// <item>Down posts LeftDown, Up posts LeftUp, only when the posted left state differs (no unmatched up/down).</item>
/// <item>Side button (Primary) while hovering, per <see cref="SideButtonAction"/> (ADR-0032): RightClick posts RightDown
/// on ButtonDown and RightUp on ButtonUp (ADR-0016); MiddleClick the same with the middle button; DoubleClick posts
/// LeftDown/LeftUp with click state 1 then 2 on ButtonDown; None and Eraser post nothing (the tracker turns Eraser into a
/// tool change). A ButtonUp releases whichever side button is posted, whatever the action now. Secondary is reserved
/// and ignored.</item>
/// <item>Right-drag (ADR-0022): when the side button is held at contact-down (the event's held buttons include
/// Primary) and the action is RightClick, the stroke is a right-button stroke: Down posts RightDown if needed (never
/// LeftDown), motion posts RightDragged and Up posts RightUp. MiddleClick makes it a middle-button stroke the same
/// way; the other actions leave it a left stroke. The choice is latched at contact-down: button changes
/// during contact post nothing, in either kind of stroke. A right-down still posted at a Down without the button held
/// (its RightUp failed) is released first, and the stroke is a left one.</item>
/// <item>Motion posts LeftDragged while the tip is down, else RightDragged while the side button is down, else
/// MouseMoved. A Drag after the button was released (pause mid-stroke) becomes a hover move until the next Down.</item>
/// <item>Each step's <see cref="TabletStep.Contact"/> is true while the tip is down in either kind of stroke, so pressure
/// and tilt go on the right-drag events too.</item>
/// <item>ToolChanged and ProximityLeave (and <see cref="Release"/>) post LeftUp, RightUp and ProximityLeave as needed;
/// ToolChanged then enters with the new tool, so apps switch pen/eraser.</item>
/// </list>
/// </remarks>
public static class TabletEventPlanner
{
    /// <summary>
    /// Upper bound of steps for one event (a double click on a tool mismatch: LeftUp, RightUp, MiddleUp, Leave, Enter,
    /// then LeftDown, LeftUp, LeftDown, LeftUp).
    /// </summary>
    public const int MaxSteps = 10;

    /// <summary>Plans the posts for one pen event and updates <paramref name="state"/>. Returns the step count.</summary>
    /// <param name="state">What has been posted.</param>
    /// <param name="kind">The event.</param>
    /// <param name="button">For ButtonDown/ButtonUp: the single button that changed.</param>
    /// <param name="tool">The event's tool.</param>
    /// <param name="steps">Destination, at least <see cref="MaxSteps"/> long.</param>
    /// <param name="held">The buttons held in the event's sample (<see cref="PenSample.Buttons"/>); decides a right-drag at Down.</param>
    /// <param name="action">The side-button action (<see cref="PenEvent.SideButton"/>).</param>
    public static int Plan(
        ref TabletPostedState state,
        PenEventKind kind,
        PenButtons button,
        PenTool tool,
        Span<TabletStep> steps,
        PenButtons held = PenButtons.None,
        SideButtonAction action = SideButtonAction.RightClick)
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
                steps[n++] = new TabletStep(MotionAction(state), state.Tool, state.Contact, state.Side);
                break;

            case PenEventKind.Down:
                EnsureProximity(ref state, tool, steps, ref n);
                if (state.Contact)
                {
                    steps[n++] = new TabletStep(MotionAction(state), state.Tool, true, state.Side);
                }
                else if ((held & PenButtons.Primary) != 0 && action == SideButtonAction.MiddleClick && !state.Right)
                {
                    // Side button held at contact-down with MiddleClick: a middle-drag stroke, latched until Up.
                    state.MiddleStroke = true;
                    if (state.Middle)
                    {
                        steps[n++] = new TabletStep(TabletAction.MiddleDragged, state.Tool, true, true);
                    }
                    else
                    {
                        state.Middle = true;
                        state.MiddleUsed = true;
                        steps[n++] = new TabletStep(TabletAction.MiddleDown, state.Tool, true, true);
                    }
                }
                else if ((held & PenButtons.Primary) != 0 && action == SideButtonAction.RightClick && !state.Middle)
                {
                    // Side button held at contact-down: a right-drag stroke, latched until Up.
                    state.RightStroke = true;
                    if (state.Right)
                    {
                        steps[n++] = new TabletStep(TabletAction.RightDragged, state.Tool, true, true);
                    }
                    else
                    {
                        state.Right = true;
                        steps[n++] = new TabletStep(TabletAction.RightDown, state.Tool, true, true);
                    }
                }
                else
                {
                    // A side down still posted (its up failed and was reverted, or the action changed while held):
                    // release it now; the stroke is a left one.
                    ReleaseSide(ref state, steps, ref n);

                    state.Left = true;
                    steps[n++] = new TabletStep(TabletAction.LeftDown, state.Tool, true, false);
                }

                break;

            case PenEventKind.Up:
                if (state.Left)
                {
                    state.Left = false;
                    steps[n++] = new TabletStep(TabletAction.LeftUp, state.Tool, false, state.Right);
                }
                else if (state.RightStroke)
                {
                    state.RightStroke = false;
                    if (state.Right)
                    {
                        state.Right = false;
                        steps[n++] = new TabletStep(TabletAction.RightUp, state.Tool, false, state.Middle);
                    }
                }
                else if (state.MiddleStroke)
                {
                    state.MiddleStroke = false;
                    if (state.Middle)
                    {
                        state.Middle = false;
                        steps[n++] = new TabletStep(TabletAction.MiddleUp, state.Tool, false, state.Right);
                    }
                }

                break;

            case PenEventKind.ButtonDown when button == PenButtons.Primary:
                if (state.Contact || action is SideButtonAction.None or SideButtonAction.Eraser)
                {
                    break; // latched at contact-down; None and Eraser post no button
                }

                EnsureProximity(ref state, tool, steps, ref n);
                if (state.Side)
                {
                    break;
                }

                switch (action)
                {
                    case SideButtonAction.MiddleClick:
                        state.Middle = true;
                        state.MiddleUsed = true;
                        steps[n++] = new TabletStep(TabletAction.MiddleDown, state.Tool, false, true);
                        break;
                    case SideButtonAction.DoubleClick:
                        steps[n++] = new TabletStep(TabletAction.LeftDown, state.Tool, false, false, ClickState: 1);
                        steps[n++] = new TabletStep(TabletAction.LeftUp, state.Tool, false, false, ClickState: 1);
                        steps[n++] = new TabletStep(TabletAction.LeftDown, state.Tool, false, false, ClickState: 2);
                        steps[n++] = new TabletStep(TabletAction.LeftUp, state.Tool, false, false, ClickState: 2);
                        break;
                    default:
                        state.Right = true;
                        steps[n++] = new TabletStep(TabletAction.RightDown, state.Tool, false, true);
                        break;
                }

                break;

            case PenEventKind.ButtonUp when button == PenButtons.Primary:
                if (!state.Contact)
                {
                    ReleaseSide(ref state, steps, ref n);
                }

                break;

            default:
                // Secondary button (reserved until Phase 5) and unknown kinds: nothing to post.
                break;
        }

        return n;
    }

    /// <summary>Plans LeftUp, RightUp and ProximityLeave for whatever is still posted. Returns the step count.</summary>
    public static int Release(ref TabletPostedState state, Span<TabletStep> steps)
    {
        int n = 0;
        ReleaseInto(ref state, steps, ref n);
        return n;
    }

    /// <summary>
    /// Plans an unconditional LeftUp, RightUp (and MiddleUp once the middle button was used) and ProximityLeave (with the last tool) and resets
    /// <paramref name="state"/>. Use it when the host's real button state is unknown, e.g. after posts were dropped
    /// because a permission was missing: a press posted before the drop may still be held by the host even though the
    /// planner has since seen its release. An unmatched up or leave is harmless; a stuck button is not.
    /// </summary>
    public static int Repair(ref TabletPostedState state, Span<TabletStep> steps)
    {
        PenTool tool = state.Tool;
        state.Left = false;
        state.Right = false;
        state.RightStroke = false;
        state.Middle = false;
        state.MiddleStroke = false;
        state.InProximity = false;
        int n = 0;
        steps[n++] = new TabletStep(TabletAction.LeftUp, tool, false, false);
        steps[n++] = new TabletStep(TabletAction.RightUp, tool, false, false);
        if (state.MiddleUsed)
        {
            steps[n++] = new TabletStep(TabletAction.MiddleUp, tool, false, false);
        }

        steps[n++] = new TabletStep(TabletAction.ProximityLeave, tool, false, false);
        return n;
    }

    /// <summary>
    /// Undoes the state change of one planned step that could not be posted, so <paramref name="state"/> keeps
    /// matching what the host actually received (a failed LeftUp leaves the left button down, to be released later).
    /// </summary>
    public static void Revert(ref TabletPostedState state, in TabletStep step)
    {
        switch (step.Action)
        {
            case TabletAction.LeftDown:
                state.Left = false;
                break;
            case TabletAction.LeftUp:
                state.Left = true;
                break;
            case TabletAction.RightDown:
                state.Right = false;
                state.RightStroke = false; // like a failed LeftDown: the stroke continues as a hover until the next Down
                break;
            case TabletAction.RightUp:
                state.Right = true;
                break;
            case TabletAction.MiddleDown:
                state.Middle = false;
                state.MiddleStroke = false;
                break;
            case TabletAction.MiddleUp:
                state.Middle = true;
                break;
            case TabletAction.ProximityEnter:
                state.InProximity = false;
                break;
            case TabletAction.ProximityLeave:
                state.InProximity = true;
                state.Tool = step.Tool;
                break;
            default:
                // Motion changes no state.
                break;
        }
    }

    /// <summary>The mouse event for motion given the posted button state.</summary>
    public static TabletAction MotionAction(bool left, bool right) =>
        left ? TabletAction.LeftDragged : right ? TabletAction.RightDragged : TabletAction.MouseMoved;

    /// <summary>The mouse event for motion given the posted state (left, then right, then middle).</summary>
    public static TabletAction MotionAction(in TabletPostedState state) =>
        state.Left ? TabletAction.LeftDragged
        : state.Right ? TabletAction.RightDragged
        : state.Middle ? TabletAction.MiddleDragged
        : TabletAction.MouseMoved;

    private static void ReleaseSide(ref TabletPostedState state, Span<TabletStep> steps, ref int n)
    {
        if (state.Right)
        {
            state.Right = false;
            steps[n++] = new TabletStep(TabletAction.RightUp, state.Tool, false, state.Middle);
        }

        if (state.Middle)
        {
            state.Middle = false;
            steps[n++] = new TabletStep(TabletAction.MiddleUp, state.Tool, false, false);
        }
    }

    private static void EnsureProximity(ref TabletPostedState state, PenTool tool, Span<TabletStep> steps, ref int n)
    {
        if (state.InProximity && state.Tool == tool)
        {
            return;
        }

        ReleaseInto(ref state, steps, ref n);
        state.InProximity = true;
        state.Tool = tool;
        steps[n++] = new TabletStep(TabletAction.ProximityEnter, tool, false, state.Side);
    }

    private static void ReleaseInto(ref TabletPostedState state, Span<TabletStep> steps, ref int n)
    {
        state.RightStroke = false;
        state.MiddleStroke = false;
        if (state.Left)
        {
            state.Left = false;
            steps[n++] = new TabletStep(TabletAction.LeftUp, state.Tool, false, state.Side);
        }

        ReleaseSide(ref state, steps, ref n);
        if (state.InProximity)
        {
            state.InProximity = false;
            steps[n++] = new TabletStep(TabletAction.ProximityLeave, state.Tool, false, false);
        }
    }
}
