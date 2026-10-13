using PenBridge.Core.Input;

namespace PenBridge.Core.Tests.Input;

internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks = 1_000_000;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}

internal sealed class RecordingSink : IInputSink
{
    public List<PenEvent> Pen { get; } = new(4096);

    public List<GestureEvent> Gestures { get; } = new(4096);

    public void OnPenEvent(in PenEvent e) => Pen.Add(e);

    public void OnGestureEvent(in GestureEvent e) => Gestures.Add(e);

    public PenEventKind[] PenKinds() => Pen.Select(e => e.Kind).ToArray();

    public void Clear()
    {
        Pen.Clear();
        Gestures.Clear();
    }
}

internal static class Samples
{
    public static PenSample Hover(ushort x, ushort y, PenTool tool = PenTool.Pen, PenButtons buttons = PenButtons.None) =>
        new(x, y, 0, 0, 0, 0, tool, PenState.InRange, buttons);

    public static PenSample Touch(ushort x, ushort y, ushort pressure = 30000, PenTool tool = PenTool.Pen, PenButtons buttons = PenButtons.None) =>
        new(x, y, pressure, 0, 0, 0, tool, PenState.InRange | PenState.Contact, buttons);
}

/// <summary>
/// Zero-allocation assertions that are robust across JIT modes and architectures.
/// </summary>
/// <remarks>
/// <c>GC.GetAllocatedBytesForCurrentThread</c> also counts one-off runtime allocations made on the measuring thread:
/// tier-0 to OSR transitions and their synchronous compiles, lazy static/type initialization, and first calls of
/// branches not yet exercised. Their timing differs per JIT/arch (e.g. x64 Release in CI vs arm64 locally). So:
/// (1) run the identical workload for whole warm-up passes, so every branch runs before measuring; (2) keep the
/// measuring loop in an <c>AggressiveOptimization</c> method (fully optimized, no OSR patchpoints); (3) measure
/// several passes and take the minimum, so a real per-sample allocation (which recurs every pass) still fails while
/// one-off runtime work does not; (4) require a progress counter to advance during the measured pass, so the
/// hot path really ran (a suppressed or short-circuited path would trivially allocate nothing).
/// </remarks>
internal static class Alloc
{
    private const int WarmupPasses = 3;
    private const int MeasuredPasses = 5;

    /// <summary>
    /// Asserts that <paramref name="action"/> allocates nothing. The action receives an index that keeps increasing
    /// across passes (derive branch selection from it with <c>%</c>); every <paramref name="progress"/> counter
    /// must grow during each measured pass.
    /// </summary>
    public static void AssertZero(int iterationsPerPass, Action<int> action, params Func<long>[] progress)
    {
        int index = 0;
        for (int p = 0; p < WarmupPasses; p++)
        {
            index = RunPass(action, index, iterationsPerPass);
        }

        long[] progressBefore = new long[progress.Length];
        long best = long.MaxValue;
        for (int p = 0; p < MeasuredPasses && best != 0; p++)
        {
            for (int k = 0; k < progress.Length; k++)
            {
                progressBefore[k] = progress[k]();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            index = RunPass(action, index, iterationsPerPass);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            for (int k = 0; k < progress.Length; k++)
            {
                Assert.True(progress[k]() > progressBefore[k], $"Progress counter {k} did not advance: the measured pass skipped the hot path.");
            }

            best = Math.Min(best, bytes);
        }

        Assert.Equal(0, best);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static int RunPass(Action<int> action, int start, int count)
    {
        int end = start + count;
        for (int i = start; i < end; i++)
        {
            action(i);
        }

        return end;
    }
}
