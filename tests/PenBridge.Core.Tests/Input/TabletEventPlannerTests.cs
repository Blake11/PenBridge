using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

public class TabletEventPlannerTests
{
    private readonly TabletStep[] _buffer = new TabletStep[TabletEventPlanner.MaxSteps];
    private TabletPostedState _state;

    [Fact]
    public void Hover_stroke_and_leave_post_a_balanced_sequence()
    {
        Assert.Equal([TabletAction.ProximityEnter], Plan(PenEventKind.ProximityEnter));
        Assert.Equal([TabletAction.MouseMoved], Plan(PenEventKind.Move));
        Assert.Equal([TabletAction.LeftDown], Plan(PenEventKind.Down));
        Assert.Equal([TabletAction.LeftDragged], Plan(PenEventKind.Drag));
        Assert.Equal([TabletAction.LeftUp], Plan(PenEventKind.Up));
        Assert.Equal([TabletAction.ProximityLeave], Plan(PenEventKind.ProximityLeave));
        AssertNothingPosted();
    }

    [Fact]
    public void Contact_flag_and_tip_state_follow_the_left_button()
    {
        Plan(PenEventKind.ProximityEnter);
        Assert.True(Steps(PenEventKind.Down)[0].Contact);
        Assert.True(Steps(PenEventKind.Drag)[0].Contact);
        Assert.False(Steps(PenEventKind.Up)[0].Contact);
        Assert.False(Steps(PenEventKind.Move)[0].Contact);
    }

    [Fact]
    public void Side_button_while_hovering_is_a_right_click_and_hover_moves_drag_right()
    {
        Plan(PenEventKind.ProximityEnter);
        TabletStep down = Steps(PenEventKind.ButtonDown, PenButtons.Primary)[0];
        Assert.Equal(new TabletStep(TabletAction.RightDown, PenTool.Pen, Contact: false, Side: true), down);
        Assert.Equal([TabletAction.RightDragged], Plan(PenEventKind.Move));
        TabletStep up = Steps(PenEventKind.ButtonUp, PenButtons.Primary)[0];
        Assert.Equal(new TabletStep(TabletAction.RightUp, PenTool.Pen, Contact: false, Side: false), up);
        Assert.Equal([TabletAction.MouseMoved], Plan(PenEventKind.Move));
    }

    [Fact]
    public void Side_button_during_contact_posts_right_down_and_motion_stays_a_left_drag()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.Down);
        TabletStep down = Steps(PenEventKind.ButtonDown, PenButtons.Primary)[0];
        Assert.Equal(new TabletStep(TabletAction.RightDown, PenTool.Pen, Contact: true, Side: true), down);
        TabletStep drag = Steps(PenEventKind.Drag)[0];
        Assert.Equal(new TabletStep(TabletAction.LeftDragged, PenTool.Pen, Contact: true, Side: true), drag);
        Assert.Equal([TabletAction.RightUp], Plan(PenEventKind.ButtonUp, PenButtons.Primary));
        Assert.Equal([TabletAction.LeftUp], Plan(PenEventKind.Up));
    }

    [Fact]
    public void Leave_while_pressed_releases_everything_first()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.Down);
        Plan(PenEventKind.ButtonDown, PenButtons.Primary);
        Assert.Equal(
            [TabletAction.LeftUp, TabletAction.RightUp, TabletAction.ProximityLeave],
            Plan(PenEventKind.ProximityLeave));
        AssertNothingPosted();
    }

    [Fact]
    public void Release_is_idempotent_and_matches_leave()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.ButtonDown, PenButtons.Primary);
        int n = TabletEventPlanner.Release(ref _state, _buffer);
        Assert.Equal([TabletAction.RightUp, TabletAction.ProximityLeave], Actions(n));
        Assert.Equal(0, TabletEventPlanner.Release(ref _state, _buffer));
    }

    [Fact]
    public void Unmatched_up_and_button_up_post_nothing()
    {
        Assert.Empty(Plan(PenEventKind.Up));
        Assert.Empty(Plan(PenEventKind.ButtonUp, PenButtons.Primary));
        Assert.Empty(Plan(PenEventKind.ProximityLeave));
    }

    [Fact]
    public void Secondary_button_is_ignored()
    {
        Plan(PenEventKind.ProximityEnter);
        Assert.Empty(Plan(PenEventKind.ButtonDown, PenButtons.Secondary));
        Assert.Empty(Plan(PenEventKind.ButtonUp, PenButtons.Secondary));
        Assert.False(_state.Right);
    }

    [Fact]
    public void Repeated_down_and_enter_do_not_double_post()
    {
        Plan(PenEventKind.ProximityEnter);
        Assert.Empty(Plan(PenEventKind.ProximityEnter));
        Plan(PenEventKind.Down);
        Assert.Equal([TabletAction.LeftDragged], Plan(PenEventKind.Down));
        Plan(PenEventKind.ButtonDown, PenButtons.Primary);
        Assert.Empty(Plan(PenEventKind.ButtonDown, PenButtons.Primary));
    }

    [Fact]
    public void Tool_change_leaves_with_the_old_tool_and_enters_with_the_new_one()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.Down);
        TabletStep[] steps = Steps(PenEventKind.ToolChanged, tool: PenTool.Eraser);
        Assert.Equal(
            [
                new TabletStep(TabletAction.LeftUp, PenTool.Pen, false, false),
                new TabletStep(TabletAction.ProximityLeave, PenTool.Pen, false, false),
                new TabletStep(TabletAction.ProximityEnter, PenTool.Eraser, false, false),
            ],
            steps);
        Assert.Equal(PenTool.Eraser, _state.Tool);
    }

    [Fact]
    public void Events_without_proximity_enter_first_and_a_mismatched_tool_re_enters()
    {
        // After a pause or a missing permission the injector missed the enter: the next event recovers it.
        Assert.Equal([TabletAction.ProximityEnter, TabletAction.LeftDown], Plan(PenEventKind.Down));

        // Worst case: the tool differs while both buttons are down (MaxSteps).
        Plan(PenEventKind.ButtonDown, PenButtons.Primary);
        Assert.Equal(
            [TabletAction.LeftUp, TabletAction.RightUp, TabletAction.ProximityLeave, TabletAction.ProximityEnter, TabletAction.LeftDown],
            Plan(PenEventKind.Down, tool: PenTool.Eraser));
    }

    [Fact]
    public void Drag_after_release_mid_stroke_is_a_hover_move_until_the_next_down()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.Down);
        TabletEventPlanner.Release(ref _state, _buffer); // pause mid-stroke

        Assert.Equal([TabletAction.ProximityEnter, TabletAction.MouseMoved], Plan(PenEventKind.Drag));
        Assert.Empty(Plan(PenEventKind.Up));
        Assert.Equal([TabletAction.LeftDown], Plan(PenEventKind.Down));
    }

    [Fact]
    public void Dropped_posts_then_repair_releases_everything_the_host_may_hold()
    {
        // Posted for real: enter + left down. Then the permission is revoked: the planner keeps running but its
        // posts are dropped, so the host still holds the left button although the planner saw the Up.
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.Down);
        Plan(PenEventKind.Up);       // dropped
        Plan(PenEventKind.Move);     // dropped
        Assert.False(_state.Left);

        int n = TabletEventPlanner.Repair(ref _state, _buffer);
        Assert.Equal([TabletAction.LeftUp, TabletAction.RightUp, TabletAction.ProximityLeave], Actions(n));
        Assert.Equal(PenTool.Pen, _buffer[2].Tool);
        AssertNothingPosted();

        // Input resumes cleanly: the next event re-enters proximity.
        Assert.Equal([TabletAction.ProximityEnter, TabletAction.MouseMoved], Plan(PenEventKind.Move));
    }

    [Fact]
    public void Revert_keeps_state_matching_what_was_actually_posted()
    {
        Plan(PenEventKind.ProximityEnter);
        Plan(PenEventKind.Down);
        Plan(PenEventKind.ButtonDown, PenButtons.Primary);

        // Leave plans LeftUp, RightUp, ProximityLeave; the LeftUp post fails, the others succeed.
        int n = TabletEventPlanner.Plan(ref _state, PenEventKind.ProximityLeave, PenButtons.None, PenTool.Pen, _buffer);
        Assert.Equal(3, n);
        TabletEventPlanner.Revert(ref _state, _buffer[0]);
        Assert.True(_state.Left);
        Assert.False(_state.Right);
        Assert.False(_state.InProximity);

        // A later release retries the outstanding LeftUp.
        Assert.Equal(1, TabletEventPlanner.Release(ref _state, _buffer));
        Assert.Equal(TabletAction.LeftUp, _buffer[0].Action);
    }

    [Theory]
    [InlineData(TabletAction.LeftDown)]
    [InlineData(TabletAction.RightDown)]
    [InlineData(TabletAction.ProximityEnter)]
    public void Reverting_a_failed_press_or_enter_forgets_it(TabletAction failed)
    {
        Assert.Equal(2, TabletEventPlanner.Plan(ref _state, failed == TabletAction.RightDown ? PenEventKind.ButtonDown : PenEventKind.Down,
            PenButtons.Primary, PenTool.Pen, _buffer));
        TabletStep step = failed == TabletAction.ProximityEnter ? _buffer[0] : _buffer[1];
        Assert.Equal(failed, step.Action);
        TabletEventPlanner.Revert(ref _state, step);
        Assert.Equal(failed != TabletAction.ProximityEnter, _state.InProximity);
        Assert.False(failed == TabletAction.LeftDown && _state.Left);
        Assert.False(failed == TabletAction.RightDown && _state.Right);
    }

    [Fact]
    public void Reverting_a_failed_leave_keeps_proximity_and_tool()
    {
        Plan(PenEventKind.ProximityEnter, tool: PenTool.Eraser);
        int n = TabletEventPlanner.Release(ref _state, _buffer);
        TabletEventPlanner.Revert(ref _state, _buffer[n - 1]);
        Assert.True(_state.InProximity);
        Assert.Equal(PenTool.Eraser, _state.Tool);
    }

    [Fact]
    public void Planning_is_allocation_free()
    {
        var buffer = new TabletStep[TabletEventPlanner.MaxSteps];
        var state = default(TabletPostedState);
        PenEventKind[] kinds =
        [
            PenEventKind.ProximityEnter, PenEventKind.Move, PenEventKind.ButtonDown, PenEventKind.Move,
            PenEventKind.Down, PenEventKind.Drag, PenEventKind.ButtonUp, PenEventKind.Up, PenEventKind.ToolChanged,
            PenEventKind.ProximityLeave,
        ];
        long posted = 0;
        Alloc.AssertZero(2_000, i =>
        {
            PenTool tool = (i / kinds.Length) % 2 == 0 ? PenTool.Pen : PenTool.Eraser;
            posted += TabletEventPlanner.Plan(ref state, kinds[i % kinds.Length], PenButtons.Primary, tool, buffer);
            if (i % 7 == 0)
            {
                posted += TabletEventPlanner.Release(ref state, buffer);
            }

            if (i % 11 == 0)
            {
                posted += TabletEventPlanner.Repair(ref state, buffer);
                TabletEventPlanner.Revert(ref state, buffer[i % 3]);
            }
        }, () => posted);
    }

    private void AssertNothingPosted()
    {
        Assert.False(_state.InProximity);
        Assert.False(_state.Left);
        Assert.False(_state.Right);
    }

    private TabletAction[] Plan(PenEventKind kind, PenButtons button = PenButtons.None, PenTool tool = PenTool.Pen) =>
        Actions(TabletEventPlanner.Plan(ref _state, kind, button, tool, _buffer));

    private TabletStep[] Steps(PenEventKind kind, PenButtons button = PenButtons.None, PenTool tool = PenTool.Pen) =>
        _buffer.AsSpan(0, TabletEventPlanner.Plan(ref _state, kind, button, tool, _buffer)).ToArray();

    private TabletAction[] Actions(int count)
    {
        var actions = new TabletAction[count];
        for (int i = 0; i < count; i++)
        {
            actions[i] = _buffer[i].Action;
        }

        return actions;
    }
}
