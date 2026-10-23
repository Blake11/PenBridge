using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>The defaults reproduce the behaviour without settings (ADR-0031..0033): every gesture on, speeds 1.</summary>
public class InputSettingsTests
{
    [Fact]
    public void Defaults_keep_every_gesture_on_at_normal_speed()
    {
        InputSettings s = InputSettings.Default;
        Assert.Multiple(
            () => Assert.Equal(StylusMode.Pen, s.StylusMode),
            () => Assert.Equal(1, s.MouseSpeed),
            () => Assert.True(s.MouseAcceleration),
            () => Assert.Equal(SideButtonAction.RightClick, s.SideButton),
            () => Assert.Equal(0, s.TipFeel),
            () => Assert.Equal(0, s.ClickThreshold),
            () => Assert.Equal(1, s.FullPressureAt),
            () => Assert.Equal(SmartZoomMode.FollowSystem, s.SmartZoom),
            () => Assert.Equal(1, s.PointerSpeed),
            () => Assert.Equal(1, s.ScrollSpeed),
            () => Assert.True(s.ZoomEnabled),
            () => Assert.True(s.RotateEnabled),
            () => Assert.True(s.ThreeFingerDragEnabled),
            () => Assert.True(s.FourFingerGesturesEnabled));
        s.Validate();
    }

    [Fact]
    public void A_new_instance_equals_the_default()
    {
        Assert.Equal(InputSettings.Default, new InputSettings());
    }

    [Fact]
    public void Applying_the_defaults_leaves_four_finger_gestures_on()
    {
        var rec = new GestureRecognizer(new RecordingSink()) { FourFingerGesturesEnabled = false };
        rec.ApplySettings(new InputSettings());
        Assert.True(rec.FourFingerGesturesEnabled);
    }
}
