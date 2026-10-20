using System.Diagnostics;

namespace PenBridge.Core.Settings;

/// <summary>
/// How a Float setting is shown as a slider (#212): the range, the default, the step in value units and the track
/// scale. Shared by the phone and the Mac so both snap, step and clamp the same way.
/// </summary>
/// <param name="Min">Lowest value (the track's 0).</param>
/// <param name="Max">Highest value (the track's 1).</param>
/// <param name="Default">The value the track snaps to near its position.</param>
/// <param name="Step">Granularity in value units, also for a log track.</param>
/// <param name="LogScale">Position maps to value in log2 space (speeds); needs <c>Min &gt; 0</c>.</param>
public readonly record struct SliderSpec(double Min, double Max, double Default, double Step, bool LogScale)
{
    /// <summary>Fraction of the track around the default's position that snaps to the default.</summary>
    public const double SnapWidth = 0.03;

    /// <summary>The track position 0..1 of a value (clamped to the range; log2 when <see cref="LogScale"/>).</summary>
    public double ToPosition(double value)
    {
        Debug.Assert(!LogScale || Min > 0, "A log track needs Min > 0.");
        if (Max <= Min)
        {
            return 0;
        }

        if (double.IsNaN(value))
        {
            value = Default;
        }

        value = Math.Clamp(value, Min, Max);
        double position = LogScale
            ? (Math.Log2(value) - Math.Log2(Min)) / (Math.Log2(Max) - Math.Log2(Min))
            : (value - Min) / (Max - Min);
        return Math.Clamp(position, 0, 1);
    }

    /// <summary>
    /// The value of a track position 0..1 (clamped): <see cref="Default"/> when within <see cref="SnapWidth"/> of the
    /// default's position, else the value rounded to <see cref="Step"/> and clamped to the range.
    /// </summary>
    public double FromPosition(double position)
    {
        position = double.IsNaN(position) ? 0 : Math.Clamp(position, 0, 1);
        if (Math.Abs(position - ToPosition(Default)) <= SnapWidth)
        {
            return Default;
        }

        double value = LogScale
            ? Math.Pow(2, Math.Log2(Min) + (position * (Math.Log2(Max) - Math.Log2(Min))))
            : Min + (position * (Max - Min));
        return Coerce(value);
    }

    /// <summary>A typed value clamped to the range and rounded to <see cref="Step"/> (no default snap).</summary>
    public double Coerce(double value)
    {
        if (double.IsNaN(value))
        {
            return Default;
        }

        value = Math.Clamp(value, Min, Max);
        if (Step > 0)
        {
            // The 10-digit round drops the multiplication's float noise (0.05 * 21 is 1.05, not 1.0500000000000003).
            value = Math.Round(Math.Round(value / Step, MidpointRounding.AwayFromZero) * Step, 10);
        }

        return Math.Clamp(value, Min, Max);
    }

    /// <summary>The value one <see cref="Step"/> above <see cref="Coerce"/>(value), clamped.</summary>
    public double StepUp(double value) => Coerce(Coerce(value) + Step);

    /// <summary>The value one <see cref="Step"/> below <see cref="Coerce"/>(value), clamped.</summary>
    public double StepDown(double value) => Coerce(Coerce(value) - Step);
}
