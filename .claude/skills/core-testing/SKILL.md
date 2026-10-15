---
name: core-testing
description: How to write PenBridge.Core tests that stay deterministic, especially zero-allocation (hot-path) assertions. Use when adding or fixing tests in tests/PenBridge.Core.Tests, when asserting "does not allocate" with GC.GetAllocatedBytesForCurrentThread, or when an allocation test is flaky in Release/CI.
---

# Core testing

Run with `scripts/test.sh` (add `-c Release`; CI runs Release on x64, local is usually arm64).

## Zero-allocation tests: always use `Alloc.AssertZero`

Helper: `tests/PenBridge.Core.Tests/TestSupport/Alloc.cs` (namespace `PenBridge.Core.Tests.Input`, so add
`using PenBridge.Core.Tests.Input;`). Self-tests: `TestSupport/AllocTests.cs`.
Put every class that calls it in `[Collection(AllocationGroup.Name)]` (`TestSupport/AllocationGroup.cs`, not parallel).
Runtime work done for parallel tests can otherwise hit every measured pass. A failure lists each pass's bytes.

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
- `scripts/test.sh` lists the names of failing tests after the summary (from `TestResults/junit.xml`).

## Time-based filters (hold-back)
A filter that holds input back until a deadline (`InputFilter` holds a lone finger for `FingerFallbackArmDelay`) can't
rely on the next event: a still finger sends none. Give it a deadline API (`PendingDeadline`) and have the head arm a
timer that calls `Process` again then (Android: `DrawSurfaceView` arm runnable). Test both: the deadline value, and
that a `Process` call at the deadline with no new input releases the held samples.
Scripted waits depend on these deadlines too: SimClient `DrawScripts.TapSettle` must exceed the recognizer's
`DoubleTapWindow + TapDeadlineJitterAllowance`, or the script ends before a held two-finger tap fires. Raise it when
those defaults change.

## Gesture slops
A new gesture's slop must be at least the slop of every gesture it can still turn into. The one-finger pointer
(ADR-0026) once started at 1.5% while a staggered two-finger tap tolerates 3.5%, so a drifting first finger became a
move and killed the right-click. Use the wider slop until the other gesture can no longer start (`PointerSettleTime`),
and test the drift *between* the two slops.

## Soak (flaky timing tests)
Before calling a timing test fixed, run it under full CPU load, in parallel:
```bash
scripts/test.sh -c Release                                             # build once (and a baseline)
for i in $(seq "$(sysctl -n hw.ncpu)"); do yes > /dev/null & done     # load every core
for i in 1 2 3 4; do (dotnet test --project tests/PenBridge.Core.Tests -c Release --no-build \
  > /tmp/soak-$i.log 2>&1 || echo "run $i FAILED") & done; wait
pkill yes
```
Parallel runs share the build output, so pass `--no-build`; read failures from each log. Repeat a few rounds.

## Fake time with real sockets (Net tests)
- `Net/NetTestSupport.cs` has `FakeTime` (a TimeProvider whose timers fire only inside `Advance`, in due order, on the
  calling thread) plus `Wait.Until` / `Wait.AdvanceUntil` / `Wait.Step` (bounded real-time polling).
- Never jump past a deadline in one `Advance` while I/O must happen in between (pings, acks): every timer that comes
  due fires before any socket I/O runs. Step with `Wait.Step(time, total[, step, settled])` (≤ 1 s, below the 2 s
  write timeout): after each step it waits, bounded by `Wait.Limit`, until real I/O caught up (`Wait.Settled`: pings
  re-armed the peer timeouts, no control write in flight; built on `FakeTime.CountDeadlines`).
- **Never a fixed real sleep after a fake-time step.** "Advance 2 s, sleep 10 ms" passed alone and failed under load
  (#83): the pong hadn't arrived, so the next step fired the 6 s peer timeout. Wait for a condition, with a bound.
- **Test the moment the stream stops, right after an ack.** Rate-limited or batched protocol messages (UdpAck at most
  1/s) leave the tail unanswered unless something flushes it (a trailing ack). Steady streaming hides that bug;
  "stream, stop right after an ack, step 6 s, assert still on UDP" catches it.
- A deadline counts from when its operation *started*, which may be later than the test thinks (a writer drains
  asynchronously). Wait for a real-time condition first (e.g. `ControlWriteInFlightFor > 300 ms`), then advance.
- Share one `ServerIdentity`: use `NetTestSupport`'s `SharedServerIdentity` (lazy, process-wide). Creating/loading a
  PKCS#12 costs about 1 s on macOS, so a fresh identity per test makes the suite slow and pushes timing tests
  past their real-time waits.
- TLS version: assert `Tls12 or Tls13`, never 1.3 only (macOS servers negotiate 1.2, ADR-0013).
- To stall a TCP writer quickly, shrink both buffers (`PausableProxy` listener receive buffer, client
  `TcpSendBufferSize`) and keep sending until a write is really in flight.
