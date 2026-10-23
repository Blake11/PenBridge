using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// Whole-step checks for <see cref="TabletEventPlanner"/>: each planned <see cref="TabletStep"/> and the posted state
/// after it. The Mac injector turns <c>Contact</c> into pressure plus the tip button and <c>Side</c> into the lower
/// side button, so a wrong flag changes the CGEvent it sends. Expected flags: Contact = the tip is down after the step,
/// Side = a side-button mouse button (right or middle) is down after the step.
/// </summary>
public class TabletEventPlannerStepTests
{
    private const PenButtons Primary = PenButtons.Primary;

    private readonly TabletStep[] _buffer = new TabletStep[TabletEventPlanner.MaxSteps];
    private TabletPostedState _state;

    [Fact]
    public void Left_stroke_steps_and_state()
    {
        AssertPlan(Step(TabletAction.ProximityEnter, false, false), PenEventKind.ProximityEnter);
        AssertState(inProximity: true);
        AssertPlan(Step(TabletAction.MouseMoved, false, false), PenEventKind.Move);
        AssertPlan(Step(TabletAction.LeftDown, true, false), PenEventKind.Down);
        AssertState(inProximity: true, left: true);
        AssertPlan(Step(TabletAction.LeftDragged, true, false), PenEventKind.Drag);
        AssertPlan(Step(TabletAction.LeftDragged, true, false), PenEventKind.Down); // repeated down
        AssertPlan(Step(TabletAction.LeftUp, false, false), PenEventKind.Up);
        AssertState(inProximity: true);
        AssertPlan(Step(TabletAction.ProximityLeave, false, false), PenEventKind.ProximityLeave);
        AssertState();
    }

    [Fact]
    public void Down_without_proximity_enters_first_with_the_event_tool()
    {
        AssertPlan(
            [Step(TabletAction.ProximityEnter, false, false, PenTool.Eraser), Step(TabletAction.LeftDown, true, false, PenTool.Eraser)],
            PenEventKind.Down,
            tool: PenTool.Eraser);
        AssertState(inProximity: true, left: true, tool: PenTool.Eraser);
    }

    [Fact]
    public void Hover_right_click_posts_right_down_drags_right_and_releases()
    {
        Plan(PenEventKind.ProximityEnter);
        AssertPlan(Step(TabletAction.RightDown, false, true), PenEventKind.ButtonDown, Primary);
        AssertState(inProximity: true, right: true);
        AssertPlan(Step(TabletAction.RightDragged, false, true), PenEventKind.Move);
        AssertPlan(Step(TabletAction.RightDragged, false, true), PenEventKind.Drag);
        AssertPlan(Step(TabletAction.RightUp, false, false), PenEventKind.ButtonUp, Primary);
        AssertState(inProximity: true);
        AssertPlan(Step(TabletAction.MouseMoved, false, false), PenEventKind.Move);
    }

    [Fact]
    public void Hover_middle_click_posts_middle_down_drags_middle_and_releases()
    {
        Plan(PenEventKind.ProximityEnter);
        AssertPlan(Step(TabletAction.MiddleDown, false, true), PenEventKind.ButtonDown, Primary, action: SideButtonAction.MiddleClick);
        AssertState(inProximity: true, middle: true, middleUsed: true);
        AssertPlan(Step(TabletAction.MiddleDragged, false, true), PenEventKind.Move);
        AssertPlan(Step(TabletAction.MiddleDragged, false, true), PenEventKind.Drag);
        AssertPlan(Step(TabletAction.MiddleUp, false, false), PenEventKind.ButtonUp, Primary);
        AssertState(inProximity: true, middleUsed: true);
        AssertPlan(Step(TabletAction.MouseMoved, false, false), PenEventKind.Move);
    }

    [Fact]
    public void Right_drag_stroke_latches_at_contact_down_and_clears_at_up()
    {
        Plan(PenEventKind.ProximityEnter);
        AssertPlan(Step(TabletAction.RightDown, true, true), PenEventKind.Down, held: Primary);
        AssertState(inProximity: true, right: true, rightStroke: true);
        AssertPlan(Step(TabletAction.RightDragged, true, true), PenEventKind.Drag, held: Primary);
        AssertPlan(Step(TabletAction.RightDragged, true, true), PenEventKind.Down, held: Primary);
        AssertPlan(Step(TabletAction.RightDragged, true, true), PenEventKind.Drag); // button released mid-stroke
        AssertPlan(Step(TabletAction.RightUp, false, false), PenEventKind.Up);
        AssertState(inProximity: true);
    }

    [Fact]
    public void Right_drag_stroke_continues_a_hover_right_down()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.ButtonDown, Primary);
        AssertPlan(Step(TabletAction.RightDragged, true, true), PenEventKind.Down, held: Primary);
        AssertState(inProximity: true, right: true, rightStroke: true);
        AssertPlan(Step(TabletAction.RightUp, false, false), PenEventKind.Up);
        AssertState(inProximity: true);
    }

    [Fact]
    public void Middle_drag_stroke_latches_at_contact_down_and_clears_at_up()
    {
        Plan(PenEventKind.ProximityEnter);
        AssertPlan(Step(TabletAction.MiddleDown, true, true), PenEventKind.Down, held: Primary, action: SideButtonAction.MiddleClick);
        AssertState(inProximity: true, middle: true, middleStroke: true, middleUsed: true);
        AssertPlan(Step(TabletAction.MiddleDragged, true, true), PenEventKind.Drag, held: Primary);
        AssertPlan(Step(TabletAction.MiddleDragged, true, true), PenEventKind.Down, held: Primary, action: SideButtonAction.MiddleClick);
        AssertPlan(Step(TabletAction.MiddleDragged, true, true), PenEventKind.Drag);
        AssertPlan(Step(TabletAction.MiddleUp, false, false), PenEventKind.Up);
        AssertState(inProximity: true, middleUsed: true);
        AssertPlan(Step(TabletAction.MouseMoved, false, false), PenEventKind.Move);
    }

    [Fact]
    public void Middle_drag_stroke_continues_a_hover_middle_down()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.ButtonDown, Primary, action: SideButtonAction.MiddleClick);
        AssertPlan(Step(TabletAction.MiddleDragged, true, true), PenEventKind.Down, held: Primary, action: SideButtonAction.MiddleClick);
        AssertState(inProximity: true, middle: true, middleStroke: true, middleUsed: true);
        AssertPlan(Step(TabletAction.MiddleUp, false, false), PenEventKind.Up);
        AssertState(inProximity: true, middleUsed: true);
    }

    [Theory]
    [InlineData(SideButtonAction.RightClick, PenButtons.None)]
    [InlineData(SideButtonAction.RightClick, PenButtons.Secondary)]
    [InlineData(SideButtonAction.MiddleClick, PenButtons.None)]
    [InlineData(SideButtonAction.MiddleClick, PenButtons.Secondary)]
    [InlineData(SideButtonAction.DoubleClick, Primary)]
    [InlineData(SideButtonAction.None, Primary)]
    [InlineData(SideButtonAction.Eraser, Primary)]
    public void Down_without_the_side_button_held_for_this_action_is_a_left_stroke(SideButtonAction action, PenButtons held)
    {
        Plan(PenEventKind.ProximityEnter);
        AssertPlan(Step(TabletAction.LeftDown, true, false), PenEventKind.Down, held: held, action: action);
        AssertState(inProximity: true, left: true);
    }

    [Theory]
    [InlineData(SideButtonAction.RightClick, TabletAction.RightDown)]
    [InlineData(SideButtonAction.MiddleClick, TabletAction.MiddleDown)]
    public void Down_with_the_side_button_among_other_held_buttons_starts_the_side_stroke(SideButtonAction action, TabletAction expected)
    {
        Plan(PenEventKind.ProximityEnter);
        AssertPlan(Step(expected, true, true), PenEventKind.Down, held: Primary | PenButtons.Secondary, action: action);
    }

    [Fact]
    public void Down_with_middle_mapped_and_a_right_still_posted_releases_it_and_is_a_left_stroke()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.ButtonDown, Primary); // right posted while the action was RightClick
        AssertPlan(
            [Step(TabletAction.RightUp, false, false), Step(TabletAction.LeftDown, true, false)],
            PenEventKind.Down,
            held: Primary,
            action: SideButtonAction.MiddleClick);
        AssertState(inProximity: true, left: true);
    }

    [Fact]
    public void Down_with_right_mapped_and_a_middle_still_posted_releases_it_and_is_a_left_stroke()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.ButtonDown, Primary, action: SideButtonAction.MiddleClick);
        AssertPlan(
            [Step(TabletAction.MiddleUp, false, false), Step(TabletAction.LeftDown, true, false)],
            PenEventKind.Down,
            held: Primary);
        AssertState(inProximity: true, left: true, middleUsed: true);
        AssertPlan(Step(TabletAction.LeftUp, false, false), PenEventKind.Up);
    }

    [Fact]
    public void Failed_middle_up_never_makes_the_next_stroke_a_middle_drag()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.Down, held: Primary, action: SideButtonAction.MiddleClick);
        TabletStep up = Steps(PenEventKind.Up)[0];
        TabletEventPlanner.Revert(ref _state, up);
        AssertState(inProximity: true, middle: true, middleUsed: true);

        AssertPlan(
            [Step(TabletAction.MiddleUp, false, false), Step(TabletAction.LeftDown, true, false)],
            PenEventKind.Down,
            action: SideButtonAction.MiddleClick);
        AssertState(inProximity: true, left: true, middleUsed: true);
    }

    [Fact]
    public void Side_button_changes_during_either_side_stroke_post_nothing()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.Down, held: Primary);
        AssertPlan([], PenEventKind.ButtonDown, Primary);
        AssertPlan([], PenEventKind.ButtonUp, Primary);
        AssertState(inProximity: true, right: true, rightStroke: true);
        Plan(PenEventKind.Up);

        Plan(PenEventKind.Down, held: Primary, action: SideButtonAction.MiddleClick);
        AssertPlan([], PenEventKind.ButtonDown, Primary, action: SideButtonAction.MiddleClick);
        AssertPlan([], PenEventKind.ButtonUp, Primary, action: SideButtonAction.MiddleClick);
        AssertState(inProximity: true, middle: true, middleStroke: true, middleUsed: true);
    }

    [Fact]
    public void Side_button_down_while_a_side_button_is_posted_posts_nothing()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.ButtonDown, Primary);
        AssertPlan([], PenEventKind.ButtonDown, Primary, action: SideButtonAction.MiddleClick);
        AssertPlan([], PenEventKind.ButtonDown, Primary, action: SideButtonAction.DoubleClick);
        AssertState(inProximity: true, right: true);
    }

    [Fact]
    public void Button_up_releases_whichever_side_button_is_posted_whatever_the_action_now_is()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.ButtonDown, Primary, action: SideButtonAction.MiddleClick);
        AssertPlan(Step(TabletAction.MiddleUp, false, false), PenEventKind.ButtonUp, Primary, action: SideButtonAction.RightClick);
        Plan(PenEventKind.ButtonDown, Primary);
        AssertPlan(Step(TabletAction.RightUp, false, false), PenEventKind.ButtonUp, Primary, action: SideButtonAction.None);
        AssertState(inProximity: true, middleUsed: true);
    }

    [Fact]
    public void Right_and_middle_both_posted_release_right_while_the_middle_stays_down()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.ButtonDown, Primary);
        TabletEventPlanner.Revert(ref _state, Step(TabletAction.MiddleUp, false, false)); // a failed middle up
        AssertState(inProximity: true, right: true, middle: true);

        AssertPlan(
            [Step(TabletAction.RightUp, false, true), Step(TabletAction.MiddleUp, false, false)],
            PenEventKind.ButtonUp,
            Primary);
        AssertState(inProximity: true);
    }

    [Theory]
    [InlineData(SideButtonAction.None)]
    [InlineData(SideButtonAction.Eraser)]
    public void None_and_eraser_post_no_button_even_without_proximity(SideButtonAction action)
    {
        AssertPlan([], PenEventKind.ButtonDown, Primary, action: action);
        AssertState(tool: PenTool.None);
    }

    [Fact]
    public void Double_click_posts_two_left_clicks_with_click_states_1_and_2_and_no_flags()
    {
        Plan(PenEventKind.ProximityEnter);
        AssertPlan(
            [
                Step(TabletAction.LeftDown, false, false, clickState: 1),
                Step(TabletAction.LeftUp, false, false, clickState: 1),
                Step(TabletAction.LeftDown, false, false, clickState: 2),
                Step(TabletAction.LeftUp, false, false, clickState: 2),
            ],
            PenEventKind.ButtonDown,
            Primary,
            action: SideButtonAction.DoubleClick);
        AssertState(inProximity: true);
        AssertPlan([], PenEventKind.ButtonUp, Primary, action: SideButtonAction.DoubleClick);
    }

    [Fact]
    public void Double_click_on_a_tool_mismatch_is_the_longest_plan()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.ButtonDown, Primary);
        TabletEventPlanner.Revert(ref _state, Step(TabletAction.MiddleUp, false, false));
        TabletStep[] expected =
        [
            Step(TabletAction.RightUp, false, true),
            Step(TabletAction.MiddleUp, false, false),
            Step(TabletAction.ProximityLeave, false, false),
            Step(TabletAction.ProximityEnter, false, false, PenTool.Eraser),
            Step(TabletAction.LeftDown, false, false, PenTool.Eraser, clickState: 1),
            Step(TabletAction.LeftUp, false, false, PenTool.Eraser, clickState: 1),
            Step(TabletAction.LeftDown, false, false, PenTool.Eraser, clickState: 2),
            Step(TabletAction.LeftUp, false, false, PenTool.Eraser, clickState: 2),
        ];
        AssertPlan(expected, PenEventKind.ButtonDown, Primary, PenTool.Eraser, action: SideButtonAction.DoubleClick);
        AssertState(inProximity: true, tool: PenTool.Eraser, middleUsed: false);
        Assert.True(expected.Length <= TabletEventPlanner.MaxSteps);
    }

    [Fact]
    public void Tool_change_with_the_same_tool_still_leaves_and_enters()
    {
        Plan(PenEventKind.ProximityEnter);
        AssertPlan(
            [Step(TabletAction.ProximityLeave, false, false), Step(TabletAction.ProximityEnter, false, false)],
            PenEventKind.ToolChanged);
        AssertState(inProximity: true);
    }

    [Fact]
    public void Tool_change_releases_a_left_stroke_and_a_side_button_before_the_leave()
    {
        Plan(PenEventKind.Down);
        AssertPlan(
            [
                Step(TabletAction.LeftUp, false, false),
                Step(TabletAction.ProximityLeave, false, false),
                Step(TabletAction.ProximityEnter, false, false),
            ],
            PenEventKind.ToolChanged);
        AssertState(inProximity: true);

        Plan(PenEventKind.ButtonDown, Primary);
        AssertPlan(
            [
                Step(TabletAction.RightUp, false, false),
                Step(TabletAction.ProximityLeave, false, false),
                Step(TabletAction.ProximityEnter, false, false, PenTool.Eraser),
            ],
            PenEventKind.ToolChanged,
            tool: PenTool.Eraser);
        AssertState(inProximity: true, tool: PenTool.Eraser);
    }

    [Fact]
    public void Leave_releases_each_kind_of_stroke_with_whole_steps()
    {
        Plan(PenEventKind.Down, held: Primary);
        AssertPlan(
            [Step(TabletAction.RightUp, false, false), Step(TabletAction.ProximityLeave, false, false)],
            PenEventKind.ProximityLeave);
        AssertState();

        Plan(PenEventKind.Down, held: Primary, action: SideButtonAction.MiddleClick);
        AssertPlan(
            [Step(TabletAction.MiddleUp, false, false), Step(TabletAction.ProximityLeave, false, false)],
            PenEventKind.ProximityLeave);
        AssertState(middleUsed: true);

        Plan(PenEventKind.Down);
        AssertPlan(
            [Step(TabletAction.LeftUp, false, false), Step(TabletAction.ProximityLeave, false, false)],
            PenEventKind.ProximityLeave);
        AssertState(middleUsed: true);
    }

    [Fact]
    public void Release_with_a_left_stroke_and_a_stuck_side_button_keeps_the_side_flag_until_it_is_released()
    {
        Plan(PenEventKind.Down);
        TabletEventPlanner.Revert(ref _state, Step(TabletAction.RightUp, false, false)); // a failed right up
        AssertState(inProximity: true, left: true, right: true);

        int n = TabletEventPlanner.Release(ref _state, _buffer);
        Assert.Equal(
            [
                Step(TabletAction.LeftUp, false, true),
                Step(TabletAction.RightUp, false, false),
                Step(TabletAction.ProximityLeave, false, false),
            ],
            _buffer.AsSpan(0, n).ToArray());
        AssertState();
    }

    [Fact]
    public void Left_up_while_right_is_stuck_down_keeps_the_side_flag()
    {
        Plan(PenEventKind.Down);
        TabletEventPlanner.Revert(ref _state, Step(TabletAction.RightUp, false, false));
        AssertPlan(Step(TabletAction.LeftUp, false, true), PenEventKind.Up);
        AssertState(inProximity: true, right: true);
    }

    [Fact]
    public void Repair_posts_every_release_with_the_last_tool_and_resets_all_state()
    {
        Plan(PenEventKind.Down, tool: PenTool.Eraser);
        int n = TabletEventPlanner.Repair(ref _state, _buffer);
        Assert.Equal(
            [
                Step(TabletAction.LeftUp, false, false, PenTool.Eraser),
                Step(TabletAction.RightUp, false, false, PenTool.Eraser),
                Step(TabletAction.ProximityLeave, false, false, PenTool.Eraser),
            ],
            _buffer.AsSpan(0, n).ToArray());
        AssertState(tool: PenTool.Eraser);
    }

    [Fact]
    public void Repair_after_the_middle_button_was_used_also_releases_the_middle_button()
    {
        Plan(PenEventKind.Down, held: Primary, action: SideButtonAction.MiddleClick);
        Plan(PenEventKind.Up);
        Plan(PenEventKind.Down, held: Primary);
        Plan(PenEventKind.Down, held: Primary); // right stroke after a middle stroke: left/right/middle flags in play
        int n = TabletEventPlanner.Repair(ref _state, _buffer);
        Assert.Equal(
            [
                Step(TabletAction.LeftUp, false, false),
                Step(TabletAction.RightUp, false, false),
                Step(TabletAction.MiddleUp, false, false),
                Step(TabletAction.ProximityLeave, false, false),
            ],
            _buffer.AsSpan(0, n).ToArray());
        AssertState(middleUsed: true);
    }

    [Fact]
    public void Repair_clears_strokes_and_side_buttons_that_are_down()
    {
        Plan(PenEventKind.Down, held: Primary);
        TabletEventPlanner.Repair(ref _state, _buffer);
        AssertState();

        Plan(PenEventKind.Down, held: Primary, action: SideButtonAction.MiddleClick);
        TabletEventPlanner.Repair(ref _state, _buffer);
        AssertState(middleUsed: true);

        Plan(PenEventKind.Down);
        TabletEventPlanner.Repair(ref _state, _buffer);
        AssertState(middleUsed: true);
    }

    [Fact]
    public void Revert_undoes_exactly_the_state_of_the_failed_step()
    {
        // Right stroke: a failed RightDown forgets the right button and the stroke.
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.Down, held: Primary);
        TabletEventPlanner.Revert(ref _state, Step(TabletAction.RightDown, true, true));
        AssertState(inProximity: true);

        // Middle stroke: a failed MiddleDown forgets the middle button and the stroke (MiddleUsed is history).
        Plan(PenEventKind.Up);
        Plan(PenEventKind.Down, held: Primary, action: SideButtonAction.MiddleClick);
        TabletEventPlanner.Revert(ref _state, Step(TabletAction.MiddleDown, true, true));
        AssertState(inProximity: true, middleUsed: true);

        // A failed RightUp / MiddleUp / LeftUp leaves that button down.
        TabletEventPlanner.Revert(ref _state, Step(TabletAction.RightUp, false, false));
        AssertState(inProximity: true, right: true, middleUsed: true);
        TabletEventPlanner.Revert(ref _state, Step(TabletAction.MiddleUp, false, false));
        AssertState(inProximity: true, right: true, middle: true, middleUsed: true);
        TabletEventPlanner.Revert(ref _state, Step(TabletAction.LeftUp, false, false));
        AssertState(inProximity: true, left: true, right: true, middle: true, middleUsed: true);
        TabletEventPlanner.Revert(ref _state, Step(TabletAction.LeftDown, true, false));
        AssertState(inProximity: true, right: true, middle: true, middleUsed: true);
    }

    [Theory]
    [InlineData(TabletAction.MouseMoved)]
    [InlineData(TabletAction.LeftDragged)]
    [InlineData(TabletAction.RightDragged)]
    [InlineData(TabletAction.MiddleDragged)]
    public void Reverting_a_motion_step_changes_no_state(TabletAction action)
    {
        Plan(PenEventKind.Down, held: Primary);
        TabletEventPlanner.Revert(ref _state, Step(action, true, true));
        AssertState(inProximity: true, right: true, rightStroke: true);
    }

    [Fact]
    public void Reverting_a_failed_leave_restores_the_tool_of_that_step()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.ProximityLeave);
        TabletEventPlanner.Revert(ref _state, Step(TabletAction.ProximityLeave, false, false, PenTool.Eraser));
        AssertState(inProximity: true, tool: PenTool.Eraser);
        TabletEventPlanner.Revert(ref _state, Step(TabletAction.ProximityEnter, false, false, PenTool.Eraser));
        AssertState(tool: PenTool.Eraser);
    }

    [Theory]
    [InlineData(true, true, TabletAction.LeftDragged)]
    [InlineData(true, false, TabletAction.LeftDragged)]
    [InlineData(false, true, TabletAction.RightDragged)]
    [InlineData(false, false, TabletAction.MouseMoved)]
    public void Motion_action_for_left_and_right(bool left, bool right, TabletAction expected) =>
        Assert.Equal(expected, TabletEventPlanner.MotionAction(left, right));

    [Fact]
    public void Motion_action_for_the_posted_state_prefers_left_then_right_then_middle()
    {
        var state = default(TabletPostedState);
        Assert.Equal(TabletAction.MouseMoved, TabletEventPlanner.MotionAction(state));
        state.Middle = true;
        Assert.Equal(TabletAction.MiddleDragged, TabletEventPlanner.MotionAction(state));
        state.Right = true;
        Assert.Equal(TabletAction.RightDragged, TabletEventPlanner.MotionAction(state));
        state.Left = true;
        Assert.Equal(TabletAction.LeftDragged, TabletEventPlanner.MotionAction(state));
    }

    [Fact]
    public void Posted_state_contact_and_side_are_derived_from_the_buttons()
    {
        var state = default(TabletPostedState);
        Assert.False(state.Contact);
        Assert.False(state.Side);
        state.Left = true;
        Assert.True(state.Contact);
        Assert.False(state.Side);
        state = default;
        state.RightStroke = true;
        Assert.True(state.Contact);
        state = default;
        state.MiddleStroke = true;
        Assert.True(state.Contact);
        state = default;
        state.Right = true;
        Assert.False(state.Contact);
        Assert.True(state.Side);
        state = default;
        state.Middle = true;
        Assert.True(state.Side);
    }

    private static TabletStep Step(TabletAction action, bool contact, bool side, PenTool tool = PenTool.Pen, int clickState = 0) =>
        new(action, tool, contact, side, clickState);

    private TabletStep[] Steps(
        PenEventKind kind,
        PenButtons button = PenButtons.None,
        PenTool tool = PenTool.Pen,
        PenButtons held = PenButtons.None,
        SideButtonAction action = SideButtonAction.RightClick) =>
        _buffer.AsSpan(0, TabletEventPlanner.Plan(ref _state, kind, button, tool, _buffer, held, action)).ToArray();

    private void Plan(
        PenEventKind kind,
        PenButtons button = PenButtons.None,
        PenTool tool = PenTool.Pen,
        PenButtons held = PenButtons.None,
        SideButtonAction action = SideButtonAction.RightClick) =>
        Steps(kind, button, tool, held, action);

    private void AssertPlan(
        TabletStep expected,
        PenEventKind kind,
        PenButtons button = PenButtons.None,
        PenTool tool = PenTool.Pen,
        PenButtons held = PenButtons.None,
        SideButtonAction action = SideButtonAction.RightClick) =>
        AssertPlan([expected], kind, button, tool, held, action);

    private void AssertPlan(
        TabletStep[] expected,
        PenEventKind kind,
        PenButtons button = PenButtons.None,
        PenTool tool = PenTool.Pen,
        PenButtons held = PenButtons.None,
        SideButtonAction action = SideButtonAction.RightClick) =>
        Assert.Equal(expected, Steps(kind, button, tool, held, action));

    private void AssertState(
        bool inProximity = false,
        PenTool tool = PenTool.Pen,
        bool left = false,
        bool right = false,
        bool middle = false,
        bool rightStroke = false,
        bool middleStroke = false,
        bool middleUsed = false)
    {
        Assert.Equal(
            (inProximity, tool, left, right, middle, rightStroke, middleStroke, middleUsed),
            (_state.InProximity, _state.Tool, _state.Left, _state.Right, _state.Middle, _state.RightStroke, _state.MiddleStroke, _state.MiddleUsed));
    }
}
