namespace PenBridge.Core.Net;

/// <summary>
/// A token bucket on a <see cref="TimeProvider"/>: <c>perSecond</c> tokens refill per second up to <c>capacity</c>,
/// and each <see cref="TryTake"/> costs one. Not thread-safe: one caller (e.g. a connection's read loop) owns it.
/// </summary>
internal sealed class TokenBucket
{
    private readonly TimeProvider _time;
    private readonly double _capacity;
    private readonly double _perSecond;
    private double _tokens;
    private long _timestamp;

    public TokenBucket(TimeProvider time, double perSecond, double capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _time = time;
        _perSecond = perSecond;
        _capacity = capacity;
        _tokens = capacity;
        _timestamp = time.GetTimestamp();
    }

    /// <summary>Takes one token; false (nothing taken) when the bucket is empty.</summary>
    public bool TryTake()
    {
        long now = _time.GetTimestamp();
        double elapsed = _time.GetElapsedTime(_timestamp, now).TotalSeconds;
        _timestamp = now;
        if (elapsed > 0)
        {
            _tokens = Math.Min(_capacity, _tokens + (elapsed * _perSecond));
        }

        if (_tokens < 1)
        {
            return false;
        }

        _tokens -= 1;
        return true;
    }
}
