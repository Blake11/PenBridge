using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

/// <summary>
/// <see cref="GestureRecognizerOptions"/> validation: every option is checked on its own and accepts its limit (0 for
/// distances, ratios and durations; any positive value for gains). Distances, ratios, gains and accelerations name
/// themselves in the exception; durations report "options".
/// </summary>
public class GestureOptionsValidationTests
{
    private static readonly GestureAcceleration Broken = new() { SlowSpeed = 500, FastSpeed = 400 };

    private static GestureRecognizerOptions With(string name, double value) => name switch
    {
        nameof(GestureRecognizerOptions.ScrollThreshold) => new() { ScrollThreshold = value },
        nameof(GestureRecognizerOptions.PinchThreshold) => new() { PinchThreshold = value },
        nameof(GestureRecognizerOptions.RotationThreshold) => new() { RotationThreshold = value },
        nameof(GestureRecognizerOptions.DragThreshold) => new() { DragThreshold = value },
        nameof(GestureRecognizerOptions.SwipeThreshold) => new() { SwipeThreshold = value },
        nameof(GestureRecognizerOptions.SystemPinchThreshold) => new() { SystemPinchThreshold = value },
        nameof(GestureRecognizerOptions.TapSlop) => new() { TapSlop = value },
        nameof(GestureRecognizerOptions.DoubleTapSlop) => new() { DoubleTapSlop = value },
        nameof(GestureRecognizerOptions.PointerSlop) => new() { PointerSlop = value },
        nameof(GestureRecognizerOptions.ClickSlop) => new() { ClickSlop = value },
        nameof(GestureRecognizerOptions.ScrollAxisLockRatio) => new() { ScrollAxisLockRatio = value },
        nameof(GestureRecognizerOptions.ScrollGain) => new() { ScrollGain = value },
        nameof(GestureRecognizerOptions.DragGain) => new() { DragGain = value },
        _ => throw new ArgumentException(name),
    };

    private static GestureRecognizerOptions WithDuration(string name, TimeSpan value) => name switch
    {
        nameof(GestureRecognizerOptions.TapMaxDuration) => new() { TapMaxDuration = value },
        nameof(GestureRecognizerOptions.DoubleTapWindow) => new() { DoubleTapWindow = value },
        nameof(GestureRecognizerOptions.ClickWindow) => new() { ClickWindow = value },
        nameof(GestureRecognizerOptions.TapDeadlineJitterAllowance) => new() { TapDeadlineJitterAllowance = value },
        nameof(GestureRecognizerOptions.PointerSettleTime) => new() { PointerSettleTime = value },
        _ => throw new ArgumentException(name),
    };

    private static void Create(GestureRecognizerOptions options) => _ = new GestureRecognizer(new RecordingSink(), options);

    public static TheoryData<string> NonNegative =>
    [
        nameof(GestureRecognizerOptions.ScrollThreshold),
        nameof(GestureRecognizerOptions.PinchThreshold),
        nameof(GestureRecognizerOptions.RotationThreshold),
        nameof(GestureRecognizerOptions.DragThreshold),
        nameof(GestureRecognizerOptions.SwipeThreshold),
        nameof(GestureRecognizerOptions.SystemPinchThreshold),
        nameof(GestureRecognizerOptions.TapSlop),
        nameof(GestureRecognizerOptions.DoubleTapSlop),
        nameof(GestureRecognizerOptions.PointerSlop),
        nameof(GestureRecognizerOptions.ClickSlop),
        nameof(GestureRecognizerOptions.ScrollAxisLockRatio),
    ];

    public static TheoryData<string> Positive =>
    [
        nameof(GestureRecognizerOptions.ScrollGain),
        nameof(GestureRecognizerOptions.DragGain),
    ];

    public static TheoryData<string> Durations =>
    [
        nameof(GestureRecognizerOptions.TapMaxDuration),
        nameof(GestureRecognizerOptions.DoubleTapWindow),
        nameof(GestureRecognizerOptions.ClickWindow),
        nameof(GestureRecognizerOptions.TapDeadlineJitterAllowance),
        nameof(GestureRecognizerOptions.PointerSettleTime),
    ];

    [Fact]
    public void The_defaults_are_valid() => Create(new GestureRecognizerOptions());

    [Theory]
    [MemberData(nameof(NonNegative))]
    public void A_distance_or_ratio_must_be_finite_and_not_negative(string name)
    {
        foreach (double bad in new[] { -1e-9, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var e = Assert.Throws<ArgumentOutOfRangeException>(() => Create(With(name, bad)));
            Assert.Equal(name, e.ParamName);
        }

        Create(With(name, 0));
    }

    [Theory]
    [MemberData(nameof(Positive))]
    public void A_gain_must_be_positive_and_finite(string name)
    {
        foreach (double bad in new[] { 0, -1, double.NaN, double.PositiveInfinity })
        {
            var e = Assert.Throws<ArgumentOutOfRangeException>(() => Create(With(name, bad)));
            Assert.Equal(name, e.ParamName);
        }

        Create(With(name, 1e-9));
    }

    [Theory]
    [MemberData(nameof(Durations))]
    public void A_duration_must_not_be_negative(string name)
    {
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => Create(WithDuration(name, TimeSpan.FromTicks(-1))));
        Assert.Equal("options", e.ParamName);
        Create(WithDuration(name, TimeSpan.Zero));
    }

    [Fact]
    public void Each_acceleration_is_required_and_validated_under_its_own_name()
    {
        Assert.Equal(
            nameof(GestureRecognizerOptions.ScrollAcceleration),
            Assert.Throws<ArgumentNullException>(() => Create(new() { ScrollAcceleration = null! })).ParamName);
        Assert.Equal(
            nameof(GestureRecognizerOptions.DragAcceleration),
            Assert.Throws<ArgumentNullException>(() => Create(new() { DragAcceleration = null! })).ParamName);
        Assert.Equal(
            nameof(GestureRecognizerOptions.ScrollAcceleration),
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(new() { ScrollAcceleration = Broken })).ParamName);
        Assert.Equal(
            nameof(GestureRecognizerOptions.DragAcceleration),
            Assert.Throws<ArgumentOutOfRangeException>(() => Create(new() { DragAcceleration = Broken })).ParamName);
    }

    [Fact]
    public void A_null_sink_throws() =>
        Assert.Equal("sink", Assert.Throws<ArgumentNullException>(() => new GestureRecognizer(null!)).ParamName);
}
