using PenBridge.Core.Input;
using static PenBridge.Core.Input.PenEventKind;

namespace PenBridge.Core.Tests.Input;

/// <summary>ADR-0032 side-button actions: hover presses, and the contact-down latch of ADR-0022.</summary>
public class SideButtonActionTests
{
    private readonly TabletStep[] _buffer = new TabletStep[TabletEventPlanner.MaxSteps];
    private TabletPostedState _state;

    private TabletStep[] Steps(PenEventKind kind, SideButtonAction action, PenButtons button = PenButtons.None, PenButtons held = PenButtons.None)
    {
        int n = TabletEventPlanner.Plan(ref _state, kind, button, PenTool.Pen, _buffer, held, action);
        return _buffer[..n];
    }

    private TabletAction[] Plan(PenEventKind kind, SideButtonAction action, PenButtons button = PenButtons.None, PenButtons held = PenButtons.None) =>
        Steps(kind, action, button, held).Select(s => s.Action).ToArray();

    [Fact]
    public void Middle_click_hover_press_posts_middle_down_and_up()
    {
        const SideButtonAction A = SideButtonAction.MiddleClick;
        Plan(ProximityEnter, A);
        Assert.Equal([TabletAction.MiddleDown], Plan(ButtonDown, A, PenButtons.Primary));
        Assert.Equal([TabletAction.MiddleDragged], Plan(Move, A, held: PenButtons.Primary));
        Assert.Equal([TabletAction.MiddleUp], Plan(ButtonUp, A, PenButtons.Primary));
        Assert.Equal([TabletAction.MouseMoved], Plan(Move, A));
        Assert.False(_state.Side);
    }

    [Fact]
    public void Middle_click_held_at_contact_down_is_a_middle_drag_latched_until_up()
    {
        const SideButtonAction A = SideButtonAction.MiddleClick;
        Plan(ProximityEnter, A);
        TabletStep down = Steps(Down, A, held: PenButtons.Primary)[0];
        Assert.Equal(new TabletStep(TabletAction.MiddleDown, PenTool.Pen, Contact: true, Side: true), down);
        Assert.Empty(Plan(ButtonUp, A, PenButtons.Primary)); // released during contact: nothing
        Assert.Equal([TabletAction.MiddleDragged], Plan(Drag, A));
        Assert.True(Steps(Drag, A)[0].Contact);
        Assert.Equal([TabletAction.MiddleUp], Plan(Up, A));
        Assert.Equal([TabletAction.ProximityLeave], Plan(ProximityLeave, A));
    }

    [Fact]
    public void Double_click_hover_press_posts_two_left_clicks_with_click_states_1_and_2()
    {
        const SideButtonAction A = SideButtonAction.DoubleClick;
        Plan(ProximityEnter, A);
        TabletStep[] steps = Steps(ButtonDown, A, PenButtons.Primary);
        Assert.Equal(
            [TabletAction.LeftDown, TabletAction.LeftUp, TabletAction.LeftDown, TabletAction.LeftUp],
            steps.Select(s => s.Action));
        Assert.Equal([1, 1, 2, 2], steps.Select(s => s.ClickState));
        Assert.Empty(Plan(ButtonUp, A, PenButtons.Primary));
        Assert.False(_state.Left);
    }

    [Theory]
    [InlineData(SideButtonAction.DoubleClick)]
    [InlineData(SideButtonAction.None)]
    [InlineData(SideButtonAction.Eraser)]
    public void Other_actions_held_at_contact_down_leave_a_left_stroke(SideButtonAction action)
    {
        Plan(ProximityEnter, action);
        Assert.Equal([TabletAction.LeftDown], Plan(Down, action, held: PenButtons.Primary));
        Assert.Equal([TabletAction.LeftDragged], Plan(Drag, action, held: PenButtons.Primary));
        Assert.Equal([TabletAction.LeftUp], Plan(Up, action, held: PenButtons.Primary));
    }

    [Theory]
    [InlineData(SideButtonAction.None)]
    [InlineData(SideButtonAction.Eraser)]
    public void None_and_eraser_hover_press_posts_no_button(SideButtonAction action)
    {
        Plan(ProximityEnter, action);
        Assert.Empty(Plan(ButtonDown, action, PenButtons.Primary));
        Assert.Equal([TabletAction.MouseMoved], Plan(Move, action, held: PenButtons.Primary));
        Assert.Empty(Plan(ButtonUp, action, PenButtons.Primary));
    }

    [Fact]
    public void Button_up_releases_the_posted_side_button_even_after_the_action_changed()
    {
        Plan(ProximityEnter, SideButtonAction.RightClick);
        Assert.Equal([TabletAction.RightDown], Plan(ButtonDown, SideButtonAction.RightClick, PenButtons.Primary));
        Assert.Equal([TabletAction.RightUp], Plan(ButtonUp, SideButtonAction.MiddleClick, PenButtons.Primary));
        Assert.False(_state.Side);
    }

    [Fact]
    public void Leave_releases_a_middle_button_and_repair_includes_it_once_used()
    {
        Plan(ProximityEnter, SideButtonAction.MiddleClick);
        Plan(ButtonDown, SideButtonAction.MiddleClick, PenButtons.Primary);
        Assert.Equal([TabletAction.MiddleUp, TabletAction.ProximityLeave], Plan(ProximityLeave, SideButtonAction.MiddleClick));
        int n = TabletEventPlanner.Repair(ref _state, _buffer);
        Assert.Contains(TabletAction.MiddleUp, _buffer[..n].Select(s => s.Action));
    }

    [Fact]
    public void Revert_of_a_failed_middle_down_ends_the_middle_stroke()
    {
        Plan(ProximityEnter, SideButtonAction.MiddleClick);
        TabletStep down = Steps(Down, SideButtonAction.MiddleClick, held: PenButtons.Primary)[0];
        TabletEventPlanner.Revert(ref _state, down);
        Assert.False(_state.Middle);
        Assert.False(_state.Contact);
    }

    // Eraser: the tracker turns the held side button into the eraser tool (proximity switch), latched at contact-down.

    private readonly RecordingSink _sink = new();
    private readonly ManualTimeProvider _time = new();
    private uint _timeUs = 10_000;

    private PenStateTracker EraserTracker()
    {
        var tracker = new PenStateTracker(_sink, _time) { Mapper = new CoordinateMapper(1, 1, new DisplayRect(0, 0, 65535, 65535)) };
        tracker.ApplySettings(new InputSettings { SideButton = SideButtonAction.Eraser });
        return tracker;
    }

    private void Frame(PenStateTracker t, PenSample s)
    {
        t.ProcessFrame(_timeUs, s, [], []);
        _timeUs += 5_000;
    }

    [Fact]
    public void Eraser_while_held_switches_the_proximity_to_the_eraser_and_back()
    {
        PenStateTracker t = EraserTracker();
        Frame(t, Samples.Hover(10, 10));
        Frame(t, Samples.Hover(10, 10, buttons: PenButtons.Primary));
        Frame(t, Samples.Hover(12, 10, buttons: PenButtons.Primary));
        Frame(t, Samples.Hover(12, 10));
        Assert.Equal([ProximityEnter, ToolChanged, Move, ToolChanged], _sink.PenKinds());
        Assert.Equal(PenTool.Eraser, _sink.Pen[1].Sample.Tool);
        Assert.Equal(PenTool.Pen, _sink.Pen[3].Sample.Tool);
        Assert.All(_sink.Pen, e => Assert.Equal(PenButtons.None, e.Sample.Buttons));
        Assert.All(_sink.Pen, e => Assert.Equal(SideButtonAction.Eraser, e.SideButton));
    }

    [Fact]
    public void Eraser_held_at_contact_down_is_an_eraser_stroke_until_lift()
    {
        PenStateTracker t = EraserTracker();
        Frame(t, Samples.Hover(10, 10, buttons: PenButtons.Primary));
        Frame(t, Samples.Touch(10, 10, buttons: PenButtons.Primary));
        Frame(t, Samples.Touch(14, 10)); // released during contact: still erasing
        Frame(t, Samples.Hover(14, 10)); // lifted, not held: back to the pen
        Assert.Equal([ProximityEnter, Down, Drag, Up, ToolChanged], _sink.PenKinds());
        Assert.All(_sink.Pen.Take(4), e => Assert.Equal(PenTool.Eraser, e.Sample.Tool));
        Assert.Equal(PenTool.Pen, _sink.Pen[4].Sample.Tool);
    }

    [Fact]
    public void Eraser_pressed_during_contact_changes_nothing_until_lift()
    {
        PenStateTracker t = EraserTracker();
        Frame(t, Samples.Hover(10, 10));
        Frame(t, Samples.Touch(10, 10));
        Frame(t, Samples.Touch(14, 10, buttons: PenButtons.Primary));
        Assert.Equal([ProximityEnter, Down, Drag], _sink.PenKinds());
        Assert.All(_sink.Pen, e => Assert.Equal(PenTool.Pen, e.Sample.Tool));
        Frame(t, Samples.Hover(14, 10, buttons: PenButtons.Primary)); // lifted while held: now the eraser
        Assert.Equal([ProximityEnter, Down, Drag, Up, ToolChanged], _sink.PenKinds());
        Assert.Equal(PenTool.Eraser, _sink.Pen[4].Sample.Tool);
    }

    [Fact]
    public void Right_click_default_keeps_the_button_on_the_events()
    {
        var t = new PenStateTracker(_sink, _time) { Mapper = new CoordinateMapper(1, 1, new DisplayRect(0, 0, 1, 1)) };
        Frame(t, Samples.Hover(10, 10));
        Frame(t, Samples.Hover(10, 10, buttons: PenButtons.Primary));
        Assert.Equal([ProximityEnter, ButtonDown], _sink.PenKinds());
        Assert.Equal(SideButtonAction.RightClick, _sink.Pen[1].SideButton);
    }
}
