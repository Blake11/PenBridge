namespace PenBridge.Core.Input;

/// <summary>
/// Side-effect free conversions from the wire units of <see cref="PenSample"/> to the units tablet injectors post
/// (the CGEvent tablet fields on macOS), plus the timestamp and click-count math. Allocation-free.
/// </summary>
public static class TabletEventMath
{
    /// <summary>The largest wire pressure.</summary>
    public const double MaxPressure = 65535.0;

    /// <summary>Wire tilt magnitude that means 90°.</summary>
    public const double TiltFullScale = 9000.0;

    /// <summary>Wire pressure 0..65535 to 0..1 (0 = none, 65535 = full).</summary>
    public static double Pressure(ushort pressure) => pressure / MaxPressure;

    /// <summary>
    /// Wire tilt in centi-degrees to −1..1 (±90° = ±1), clamped. tiltX &gt; 0 leans the pen top toward +x (right
    /// on the surface), tiltY &gt; 0 toward +y (down, toward the user): the CGEvent tablet convention, so no sign flip.
    /// </summary>
    public static double Tilt(short centiDegrees)
    {
        double t = centiDegrees / TiltFullScale;
        return t > 1 ? 1 : t < -1 ? -1 : t;
    }

    /// <summary>Barrel rotation in centi-degrees (0..35999) to degrees.</summary>
    public static double RotationDegrees(ushort centiDegrees) => centiDegrees / 100.0;

    /// <summary>Converts stopwatch-style ticks at <paramref name="frequency"/> Hz to nanoseconds.</summary>
    /// <remarks>
    /// Integer math (128-bit intermediate, rounded down), so no floating-point error at any uptime; the identity when
    /// the frequency is 1 GHz, the macOS <c>Stopwatch</c> frequency.
    /// </remarks>
    public static long TicksToNanoseconds(long ticks, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        return frequency == 1_000_000_000 ? ticks : (long)((Int128)ticks * 1_000_000_000 / frequency);
    }

    /// <summary>
    /// Maps a session timestamp (µs on the phone's clock, already unwrapped to 64 bits by <see cref="PenStateTracker"/>)
    /// onto the host's event clock (ns) and updates <paramref name="anchor"/>.
    /// </summary>
    /// <remarks>
    /// The phone and host clocks are unrelated, so the mapping is an offset learned from arrivals:
    /// <list type="bullet">
    /// <item>The first sample, or one that would land in the future (it arrived faster than any before), re-anchors so
    /// it maps to <paramref name="nowNs"/>. The offset converges on the lowest observed latency and spacing within a
    /// batch (history samples) is preserved.</item>
    /// <item>A sample more than <paramref name="maxLagNs"/> in the past re-anchors too: a stall, a new session (its
    /// clock restarts near 0) or clock drift must not leave events far behind.</item>
    /// <item>The result never goes backwards.</item>
    /// </list>
    /// </remarks>
    public static long MapTimestamp(ref HostClockAnchor anchor, long sampleUs, long nowNs, long maxLagNs)
    {
        long sampleNs = sampleUs * 1000;
        long mapped = sampleNs + anchor.OffsetNs;
        if (!anchor.IsSet || mapped > nowNs || mapped < nowNs - maxLagNs)
        {
            anchor.OffsetNs = nowNs - sampleNs;
            anchor.IsSet = true;
            mapped = nowNs;
        }

        if (mapped < anchor.LastNs)
        {
            mapped = anchor.LastNs;
        }

        anchor.LastNs = mapped;
        return mapped;
    }

    /// <summary>
    /// Timestamp for a host-initiated post (release on pause/quit): <paramref name="nowNs"/>, but never before the last
    /// mapped timestamp. Updates <paramref name="anchor"/>.
    /// </summary>
    public static long Now(ref HostClockAnchor anchor, long nowNs)
    {
        long t = nowNs > anchor.LastNs ? nowNs : anchor.LastNs;
        anchor.LastNs = t;
        return t;
    }

    /// <summary>
    /// The click count for a new button press: one more than the previous press when it follows within
    /// <paramref name="intervalNs"/> (the system double-click interval) and <paramref name="slop"/> points, else 1.
    /// Without it a pen double-tap never registers as a double-click.
    /// </summary>
    public static long NextClickCount(ref ClickState state, long nowNs, DisplayPoint at, long intervalNs, double slop)
    {
        double dx = at.X - state.Position.X;
        double dy = at.Y - state.Position.Y;
        bool repeat = state.Count > 0 && nowNs - state.TimeNs <= intervalNs && (dx * dx) + (dy * dy) <= slop * slop;
        state.Count = repeat ? state.Count + 1 : 1;
        state.TimeNs = nowNs;
        state.Position = at;
        return state.Count;
    }
}

/// <summary>State of <see cref="TabletEventMath.MapTimestamp"/>; start from <c>default</c>.</summary>
public struct HostClockAnchor
{
    /// <summary>False until the first sample anchors the clocks.</summary>
    public bool IsSet { readonly get; internal set; }

    /// <summary>Host ns minus sample ns.</summary>
    public long OffsetNs { readonly get; internal set; }

    /// <summary>The last timestamp returned (results never go below it).</summary>
    public long LastNs { readonly get; internal set; }
}

/// <summary>State of <see cref="TabletEventMath.NextClickCount"/> for one button; start from <c>default</c>.</summary>
public struct ClickState
{
    /// <summary>The click count of the last press.</summary>
    public long Count { readonly get; internal set; }

    /// <summary>Host ns of the last press.</summary>
    public long TimeNs { readonly get; internal set; }

    /// <summary>Where the last press happened.</summary>
    public DisplayPoint Position { readonly get; internal set; }
}
