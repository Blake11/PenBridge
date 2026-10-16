namespace PenBridge.Core.Input;

/// <summary>
/// The pen pressure curve (ADR-0032): output = clamp((p − threshold) / (full − threshold), 0, 1)^γ with
/// γ = 2^tipFeel (soft −1 → γ 0.5, linear 0 → 1, firm +1 → 2). A contact pressure below the threshold is no contact:
/// the pen hovers. Applies to the pen and eraser only, never to the finger fallback's synthetic pressure.
/// Immutable and allocation-free; the default curve (<c>new PressureCurve(0, 0, 1)</c>) returns the input unchanged.
/// </summary>
public readonly struct PressureCurve
{
    private readonly double _threshold;
    private readonly double _span;
    private readonly double _gamma;

    /// <param name="tipFeel">−1 (soft) … +1 (firm); 0 is linear.</param>
    /// <param name="clickThreshold">0..1: pressure below it hovers. 0 lets the phone's contact decide.</param>
    /// <param name="fullPressureAt">0..1, above <paramref name="clickThreshold"/>: pressure at or above it is full.</param>
    public PressureCurve(double tipFeel, double clickThreshold, double fullPressureAt)
    {
        if (!double.IsFinite(tipFeel) || !(clickThreshold >= 0) || !(fullPressureAt <= 1) || !(fullPressureAt > clickThreshold))
        {
            throw new ArgumentOutOfRangeException(nameof(tipFeel), "Needs a finite tip feel and 0 <= threshold < full <= 1.");
        }

        _threshold = clickThreshold;
        _span = fullPressureAt - clickThreshold;
        _gamma = Math.Pow(2, tipFeel);
        IsIdentity = tipFeel == 0 && clickThreshold == 0 && fullPressureAt == 1;
    }

    /// <summary>The linear curve with no threshold (the default settings).</summary>
    public static PressureCurve Linear => new(0, 0, 1);

    /// <summary>True when <see cref="TryApply"/> returns its input unchanged (and never hovers).</summary>
    public bool IsIdentity { get; }

    /// <summary>The click threshold (0..1): contact pressure below it hovers. <c>default</c> behaves as the identity.</summary>
    public double Threshold => _threshold;

    /// <summary>
    /// Curves a contact's wire pressure (0..65535). Returns false when it is below the click threshold: the pen
    /// hovers instead (<paramref name="output"/> is 0).
    /// </summary>
    public bool TryApply(ushort pressure, out ushort output)
    {
        if (IsIdentity || _span == 0)
        {
            output = pressure;
            return true;
        }

        double p = pressure / TabletEventMath.MaxPressure;
        if (p < _threshold)
        {
            output = 0;
            return false;
        }

        double u = (p - _threshold) / _span;
        u = u < 1 ? u : 1;
        if (_gamma != 1 && u > 0)
        {
            u = Math.Pow(u, _gamma);
        }

        output = (ushort)Math.Round(u * TabletEventMath.MaxPressure);
        return true;
    }

    /// <summary>The curve on a 0..1 pressure (for UI previews such as the test pad); below the threshold returns 0.</summary>
    public double Apply(double pressure)
    {
        if (IsIdentity || _span == 0)
        {
            return pressure < 0 ? 0 : pressure > 1 ? 1 : pressure;
        }

        if (!(pressure >= _threshold))
        {
            return 0;
        }

        double u = (pressure - _threshold) / _span;
        u = u < 1 ? u : 1;
        return _gamma == 1 || u == 0 ? u : Math.Pow(u, _gamma);
    }
}
