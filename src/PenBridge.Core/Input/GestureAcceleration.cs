namespace PenBridge.Core.Input;

/// <summary>
/// Trackpad-like acceleration curve for relative gesture deltas (scroll, three-finger drag): gain 1 at or below
/// <see cref="SlowSpeed"/>, rising smoothly (smoothstep) to <see cref="MaxGain"/> at <see cref="FastSpeed"/> and
/// above. Speeds are in mapped display points per second. Immutable; <see cref="GainAt"/> is allocation-free.
/// </summary>
public sealed record GestureAcceleration
{
    /// <summary>No acceleration: gain 1 at every speed.</summary>
    public static GestureAcceleration None { get; } = new() { MaxGain = 1 };

    /// <summary>At or below this speed movement stays 1:1.</summary>
    public double SlowSpeed { get; init; } = 400;

    /// <summary>At or above this speed the gain is <see cref="MaxGain"/>.</summary>
    public double FastSpeed { get; init; } = 4000;

    /// <summary>The largest gain (≥ 1).</summary>
    public double MaxGain { get; init; } = 3;

    /// <summary>The gain for a movement speed (non-finite or negative speeds count as 0).</summary>
    public double GainAt(double speed)
    {
        if (!(speed > SlowSpeed))
        {
            return 1;
        }

        if (speed >= FastSpeed)
        {
            return MaxGain;
        }

        double u = (speed - SlowSpeed) / (FastSpeed - SlowSpeed);
        return 1 + ((MaxGain - 1) * u * u * (3 - (2 * u)));
    }

    /// <summary>Throws when the curve is not 0 ≤ SlowSpeed &lt; FastSpeed (finite) with MaxGain ≥ 1 (finite).</summary>
    internal void Validate(string name)
    {
        if (!(SlowSpeed >= 0) || !(FastSpeed > SlowSpeed) || !double.IsFinite(FastSpeed)
            || !(MaxGain >= 1) || !double.IsFinite(MaxGain))
        {
            throw new ArgumentOutOfRangeException(name, "Needs 0 <= SlowSpeed < FastSpeed and MaxGain >= 1, all finite.");
        }
    }
}
