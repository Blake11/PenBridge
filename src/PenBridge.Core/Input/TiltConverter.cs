using PenBridge.Core.Protocol;

namespace PenBridge.Core.Input;

/// <summary>
/// Converts Android stylus axes (<c>AXIS_TILT</c> + <c>AXIS_ORIENTATION</c>) to the <see cref="PenSample"/> tilt
/// convention. Pure and allocation-free.
/// </summary>
/// <remarks>
/// <para><b>Android input.</b> <c>AXIS_TILT</c> is the angle from the surface normal in radians (0 = perpendicular,
/// π/2 = flat). <c>AXIS_ORIENTATION</c> is the direction the stylus <i>points</i> (its tip, seen from above) relative
/// to the screen's vertical axis in the current display rotation, in −π..π: 0 = up, π/2 = right, −π/2 = left,
/// ±π = down (clockwise positive).</para>
/// <para>Android's input reader derives both from the digitizer's per-axis HID tilt angles
/// (<c>tiltX</c> &gt; 0 = top of the pen toward +x, <c>tiltY</c> &gt; 0 = toward +y / the user):
/// <c>orientation = atan2(−sin tiltX, sin tiltY)</c>, <c>tilt = acos(cos tiltX · cos tiltY)</c>. So a pen whose top
/// leans right points its tip left (orientation −π/2), and a pen whose top leans toward the user points up
/// (orientation 0).</para>
/// <para><b>Output.</b> <see cref="ToTilt"/> inverts that formula exactly, recovering the per-axis angles in the Core
/// convention (+x = top leans right, +y = top leans down/toward the user, centi-degrees, clamped to ±9000).</para>
/// <para><b>Rotation.</b> <c>AXIS_ORIENTATION</c> is the tilt <i>azimuth</i>, already encoded in tiltX/tiltY; it is
/// not barrel rotation (twist about the pen's own axis), which the S Pen doesn't report. Samplers send
/// <see cref="PenSample.Rotation"/> = 0 and use the orientation only for this conversion.</para>
/// </remarks>
public static class TiltConverter
{
    private const double CentiDegreesPerRadian = 18000.0 / Math.PI;

    /// <summary>Converts <c>AXIS_TILT</c> and <c>AXIS_ORIENTATION</c> (radians) to tilt X/Y in centi-degrees.</summary>
    /// <param name="tiltRadians">Angle from vertical, 0..π/2 (values outside are clamped; NaN counts as 0).</param>
    /// <param name="orientationRadians">Direction the tip points, 0 = up, positive clockwise (NaN counts as 0).</param>
    /// <param name="tiltX">Centi-degrees, &gt; 0 when the pen top leans toward +x (right).</param>
    /// <param name="tiltY">Centi-degrees, &gt; 0 when the pen top leans toward +y (down).</param>
    public static void ToTilt(float tiltRadians, float orientationRadians, out short tiltX, out short tiltY)
    {
        double tilt = double.IsNaN(tiltRadians) ? 0 : Math.Clamp((double)tiltRadians, 0, Math.PI / 2);
        double orientation = double.IsFinite(orientationRadians) ? orientationRadians : 0;
        if (tilt == 0)
        {
            tiltX = 0;
            tiltY = 0;
            return;
        }

        // Inverse of tilt = acos(cos x · cos y), orientation = atan2(-sin x, sin y). With sin x = -r·sinφ and
        // sin y = r·cosφ: (1 - r²sin²φ)(1 - r²cos²φ) = cos²T  ⇒  k·r⁴ - r² + sin²T = 0, k = sin²φ·cos²φ.
        // The rationalized root below is stable for k → 0 (pen leaning along one axis).
        (double s, double c) = Math.SinCos(orientation);
        double sinT = Math.Sin(tilt);
        double sin2T = sinT * sinT;
        double k = s * s * c * c;
        double r2 = 2 * sin2T / (1 + Math.Sqrt(Math.Max(0, 1 - (4 * k * sin2T))));
        double r = Math.Sqrt(r2);

        tiltX = ToCentiDegrees(Math.Asin(Math.Clamp(-r * s, -1, 1)));
        tiltY = ToCentiDegrees(Math.Asin(Math.Clamp(r * c, -1, 1)));
    }

    private static short ToCentiDegrees(double radians) =>
        (short)Math.Clamp(Math.Round(radians * CentiDegreesPerRadian), -InputSampleCodec.MaxTilt, InputSampleCodec.MaxTilt);
}
