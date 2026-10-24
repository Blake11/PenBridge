namespace PenBridge.Android.Mirror;

/// <summary>
/// Rolling window of latency samples (µs) with p50/p95 on demand. Recording allocates nothing; thread-safe (one short
/// lock per sample, off the input path).
/// </summary>
internal sealed class MirrorLatencyStats
{
    private const int Window = 512;
    private readonly long[] _samples = new long[Window];
    private readonly long[] _scratch = new long[Window];
    private readonly Lock _lock = new();
    private int _next;
    private int _count;

    public void Record(long us)
    {
        lock (_lock)
        {
            _samples[_next] = us;
            _next = (_next + 1) % Window;
            _count = Math.Min(_count + 1, Window);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>p50/p95 of the last samples (up to 512); false with no samples.</summary>
    public bool TryGet(out long p50, out long p95, out int count)
    {
        lock (_lock)
        {
            count = _count;
            if (count == 0)
            {
                p50 = p95 = 0;
                return false;
            }

            Array.Copy(_samples, _scratch, count);
            Array.Sort(_scratch, 0, count);
            p50 = _scratch[(count - 1) / 2];
            p95 = _scratch[Math.Min(count - 1, (int)Math.Ceiling(count * 0.95) - 1)];
            return true;
        }
    }
}
