---
name: core-testing
description: How to write PenBridge.Core tests that stay deterministic, especially zero-allocation (hot-path) assertions. Use when adding or fixing tests in tests/PenBridge.Core.Tests, when asserting "does not allocate" with GC.GetAllocatedBytesForCurrentThread, or when an allocation test is flaky in Release/CI.
---

# Core testing

Run with `scripts/test.sh` (add `-c Release`; CI runs Release on x64, local is usually arm64).

## Zero-allocation tests: always use `Alloc.AssertZero`

Helper: `tests/PenBridge.Core.Tests/TestSupport/Alloc.cs` (namespace `PenBridge.Core.Tests.Input`, so add
`using PenBridge.Core.Tests.Input;`). Self-tests: `TestSupport/AllocTests.cs`.

```csharp
long ran = 0;
Alloc.AssertZero(2_000, i =>
{
    HotPath(buffer, (ulong)i + 1);   // vary branches with i % n
    ran++;
}, () => ran);
```

Never write a one-shot `before = GC.GetAllocatedBytesForCurrentThread(); loop; Assert.Equal(0, after - before)`.
It is flaky: the counter also sees one-off runtime allocations on the thread (tier-0 -> OSR/tier-1 compiles,
lazy static init, first call of a branch not run yet), and when they happen depends on JIT mode and arch.

What the helper does (keep these properties if you touch it):
1. Warm up with whole passes of the **same** workload, so every branch has run before measuring.
2. Runs the loop in an `AggressiveOptimization` method (no OSR patchpoints in the measuring loop).
3. Measures several passes and keeps the **minimum**. A real per-sample allocation repeats every pass and still
   fails; one-off runtime work does not.
4. Requires each progress counter to grow in every measured pass, so a skipped path can't pass by doing nothing.

Rules for the workload lambda:
- Spans/`stackalloc` can't be captured: use arrays allocated outside the lambda (they're set up once, not measured).
- Closures are fine (allocated once, before measuring). `Assert.True(cond)` on a passing path doesn't allocate;
  avoid assert messages built with interpolation per iteration.
- Choose the iteration count so a pass takes milliseconds (1k-10k), not seconds.

Shake out JIT timing before you report a new allocation test:
```bash
scripts/test.sh -c Release
DOTNET_TieredPGO=0 scripts/test.sh -c Release
DOTNET_TC_QuickJitForLoops=1 DOTNET_OSR_HitLimit=1 scripts/test.sh -c Release   # forces OSR inside loops
```

## General
- Inject time (`ManualTimeProvider` in `Input/TestSupport.cs`) and randomness; no real sleeps.
- Network tests: loopback or fakes with timeouts, never fixed delays.
