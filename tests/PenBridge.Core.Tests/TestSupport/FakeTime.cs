namespace PenBridge.Core.Tests;

/// <summary>
/// The one test clock. Deterministic, with timers: nothing fires until <see cref="Advance"/>, which runs due callbacks
/// in order on the calling thread. Wall clock, timestamps and timers all derive from the same virtual time.
/// Lives in the root test namespace so every test folder sees it without a using.
/// </summary>
internal sealed class FakeTime : TimeProvider
{
    /// <summary>Default wall-clock start (UTC).</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly DateTimeOffset _start;
    private readonly Lock _lock = new();
    private readonly List<FakeTimer> _timers = [];
    private long _ticks;

    public FakeTime()
        : this(DefaultStart)
    {
    }

    /// <summary>Starts the wall clock (<see cref="GetUtcNow"/>) at <paramref name="start"/>.</summary>
    public FakeTime(DateTimeOffset start) => _start = start;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _start + TimeSpan.FromTicks(Volatile.Read(ref _ticks));

    public override long GetTimestamp() => 1_000_000_000 + Volatile.Read(ref _ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time forward, firing every timer that comes due (including re-armed ones) in due order.</summary>
    public void Advance(TimeSpan by)
    {
        long target;
        lock (_lock)
        {
            target = _ticks + by.Ticks;
        }

        while (true)
        {
            FakeTimer? next = null;
            lock (_lock)
            {
                foreach (FakeTimer t in _timers)
                {
                    if (t.Due <= target && (next is null || t.Due < next.Due))
                    {
                        next = t;
                    }
                }

                if (next is null)
                {
                    Volatile.Write(ref _ticks, target);
                    return;
                }

                Volatile.Write(ref _ticks, Math.Max(_ticks, next.Due));
                next.Due = next.Period > 0 ? next.Due + next.Period : long.MaxValue;
            }

            next.Callback(next.State);
        }
    }

    /// <summary>
    /// Counts the armed one-shot timers (deadlines) for which <paramref name="match"/>(state, armed for, remaining)
    /// holds; periodic timers (pings) are skipped. Lets a test wait for real I/O to finish or re-arm a deadline
    /// instead of guessing a real delay. Not called under any product lock: <paramref name="match"/> must be pure.
    /// </summary>
    public int CountDeadlines(Func<object?, TimeSpan, TimeSpan, bool> match)
    {
        lock (_lock)
        {
            int n = 0;
            foreach (FakeTimer t in _timers)
            {
                if (t.Period == 0 && t.Due != long.MaxValue
                    && match(t.State, TimeSpan.FromTicks(t.ArmedFor), TimeSpan.FromTicks(t.Due - _ticks)))
                {
                    n++;
                }
            }

            return n;
        }
    }

    private sealed class FakeTimer(FakeTime owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public long Due { get; set; } = long.MaxValue;

        public long Period { get; set; }

        /// <summary>The due time of the last <see cref="Change"/>, in ticks (-1 when disarmed).</summary>
        public long ArmedFor { get; private set; } = -1;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                ArmedFor = dueTime == Timeout.InfiniteTimeSpan ? -1 : dueTime.Ticks;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + Math.Max(0, dueTime.Ticks);
                Period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                owner._timers.Remove(this);
                Due = long.MaxValue;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
