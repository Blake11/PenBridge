namespace PenBridge.Core.Settings;

/// <summary>What the caller does after <see cref="UpdateThrottle.Change"/>.</summary>
public enum ThrottleStep
{
    /// <summary>Nothing now: a timer is already armed and will send the latest state.</summary>
    None,

    /// <summary>Send the current state now.</summary>
    SendNow,

    /// <summary>Arm a one-shot timer for the returned delay, then call <see cref="UpdateThrottle.Fire"/>.</summary>
    Arm,
}

/// <summary>
/// Coalesces a burst of changes into at most one update per <see cref="Interval"/>, plus a final one when the burst
/// settles (ADR-0036: <c>ActiveArea</c> while a window target moves or resizes). The first change of a quiet period is
/// sent at once; later ones within the interval wait for one timer, which then sends the latest state.
/// </summary>
/// <remarks>
/// Platform-free and clock-free: the caller passes a monotonic "now" and owns the timer, so it arms one only while
/// changes arrive (idle costs nothing). Not thread-safe; use it from one thread (the Mac's main thread).
/// </remarks>
public sealed class UpdateThrottle
{
    /// <summary>The ActiveArea interval for a moving or resizing window target (ADR-0036).</summary>
    public static readonly TimeSpan ActiveAreaInterval = TimeSpan.FromMilliseconds(50);

    private TimeSpan _lastSent;
    private bool _sentOnce;
    private bool _armed;

    /// <exception cref="ArgumentOutOfRangeException">The interval is not positive.</exception>
    public UpdateThrottle(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        Interval = interval;
    }

    /// <summary>The shortest time between two updates.</summary>
    public TimeSpan Interval { get; }

    /// <summary>True while a timer is armed: a change is waiting to be sent.</summary>
    public bool IsArmed => _armed;

    /// <summary>A change happened at <paramref name="now"/>.</summary>
    /// <param name="now">A monotonic time; never earlier than a previous call's.</param>
    /// <param name="delay">With <see cref="ThrottleStep.Arm"/>, how long until <see cref="Fire"/>; else zero.</param>
    public ThrottleStep Change(TimeSpan now, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (_armed)
        {
            return ThrottleStep.None;
        }

        TimeSpan since = now - _lastSent;
        if (!_sentOnce || since >= Interval)
        {
            _sentOnce = true;
            _lastSent = now;
            return ThrottleStep.SendNow;
        }

        _armed = true;
        delay = Interval - since;
        return ThrottleStep.Arm;
    }

    /// <summary>The armed timer fired at <paramref name="now"/>. True: send the current state now.</summary>
    /// <remarks>False when no timer is armed (e.g. after <see cref="Reset"/>), so a stale timer sends nothing.</remarks>
    public bool Fire(TimeSpan now)
    {
        if (!_armed)
        {
            return false;
        }

        _armed = false;
        _sentOnce = true;
        _lastSent = now;
        return true;
    }

    /// <summary>Forgets a pending change and the last send (the target was replaced or cleared).</summary>
    public void Reset()
    {
        _armed = false;
        _sentOnce = false;
        _lastSent = TimeSpan.Zero;
    }
}
