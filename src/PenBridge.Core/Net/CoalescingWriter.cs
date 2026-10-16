namespace PenBridge.Core.Net;

/// <summary>
/// Coalesces "state X changed, send it" requests into one writer: <see cref="Request"/> only ORs dirty bits into a
/// pending mask (under a short lock) and starts the writer loop if none runs; the loop takes the whole mask and calls
/// the drain callback, which reads and writes the <em>latest</em> state for each bit, until no bits are left. However
/// often a state changes, at most one write per bit is queued behind the one in flight, there's at most one writer task
/// and no waiter list, so a flood of requests costs a flag write each (ADR-0030 control-path bound).
/// </summary>
/// <remarks>
/// Nothing runs on the requesting thread but the lock and, when idle, queuing the loop to the thread pool, so callers
/// may request from event handlers, timers or under other locks. Requests made before <see cref="Enable"/> are kept and
/// drained by it. A drain that returns false (a failed write) stops the writer for good.
/// </remarks>
internal sealed class CoalescingWriter
{
    private readonly Lock _lock = new();
    private readonly Func<int, Task<bool>> _drain;
    private int _pending; // guarded by _lock
    private bool _enabled; // guarded by _lock
    private bool _stopped; // guarded by _lock
    private Task? _loop; // the running loop, null when idle; guarded by _lock

    /// <param name="drain">
    /// Writes the latest state of every bit in its argument, in the order the caller needs; false stops the writer.
    /// Must not throw. Called on one loop at a time.
    /// </param>
    public CoalescingWriter(Func<int, Task<bool>> drain) => _drain = drain;

    /// <summary>Starts writing: drains everything requested so far and every later request.</summary>
    /// <returns>Completes when the bits pending now (and any requested meanwhile) are written, or the writer stopped.</returns>
    public Task Enable()
    {
        lock (_lock)
        {
            _enabled = true;
            return StartLocked();
        }
    }

    /// <summary>Marks <paramref name="bits"/> dirty.</summary>
    /// <returns>
    /// Completes when the latest state of <paramref name="bits"/> is written, or the writer stopped; completed at once
    /// before <see cref="Enable"/>. Callers may ignore it. Every request while a loop runs shares that loop's task.
    /// </returns>
    public Task Request(int bits)
    {
        lock (_lock)
        {
            if (_stopped)
            {
                return Task.CompletedTask;
            }

            _pending |= bits;
            return _enabled ? StartLocked() : Task.CompletedTask;
        }
    }

    /// <summary>Drops pending bits and refuses later ones (the connection is closing).</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _stopped = true;
            _pending = 0;
        }
    }

    /// <summary>Bits waiting for the writer (for tests).</summary>
    internal int Pending
    {
        get
        {
            lock (_lock)
            {
                return _pending;
            }
        }
    }

    private Task StartLocked()
    {
        if (_stopped || (_loop is null && _pending == 0))
        {
            return _loop ?? Task.CompletedTask;
        }

        // A running loop re-reads _pending after each drain, so it picks up these bits too.
        return _loop ??= Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            int bits;
            lock (_lock)
            {
                bits = _stopped ? 0 : _pending;
                _pending = 0;
                if (bits == 0)
                {
                    _loop = null;
                    return;
                }
            }

            bool ok;
            try
            {
                ok = await _drain(bits).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The drain must not throw; if it does, stop instead of losing the loop with _loop set.
            catch (Exception)
#pragma warning restore CA1031
            {
                ok = false;
            }

            if (!ok)
            {
                lock (_lock)
                {
                    _stopped = true;
                    _pending = 0;
                    _loop = null;
                }

                return;
            }
        }
    }
}
