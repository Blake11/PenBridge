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
Keep that collection small: an allocation test over a live session goes in `Net/NetworkAllocationTests.cs`, not in its
feature's network class, or the whole class runs serially (and escapes the parallel soak).

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
- Inject time and randomness; no real sleeps. There is **one test clock**: `FakeTime` (`TestSupport/FakeTime.cs`,
  root namespace `PenBridge.Core.Tests`, so no using). Timestamps, wall clock (`new FakeTime(start)`) and timers all
  move only in `Advance`. Don't add another `TimeProvider` fake.
- Network tests: loopback or fakes with timeouts, never fixed delays.
- Asserts that show values: `Assert.InRange(actual, low, high)` (inclusive) instead of `Assert.True(a <op> b)`,
  `Assert.Equal(expected, actual)` on arrays/spans instead of `SequenceEqual`, and `Assert.Multiple(() => ..., () => ...)`
  for several bounds at once. Keep the semantics exact: an open bound is `Math.BitIncrement(x)`/`Math.BitDecrement(x)`
  (`double.Epsilon` for `> 0`), `n + 1` for integers, and `double.PositiveInfinity`/`long.MaxValue` for a missing
  side. Keep `Assert.True(cond, "why")` when the message explains what a bare value can't.
- `scripts/test.sh` lists the names of failing tests after the summary (from `TestResults/junit.xml`).

## Time-based filters (hold-back)
A filter that holds input back until a deadline (`InputFilter` holds a lone finger for `FingerFallbackArmDelay`) can't
rely on the next event: a still finger sends none. Give it a deadline API (`PendingDeadline`) and have the head arm a
timer that calls `Process` again then (Android: `DrawSurfaceView` arm runnable). Test both: the deadline value, and
that a `Process` call at the deadline with no new input releases the held samples.
Scripted waits depend on these deadlines too: SimClient `DrawScripts.TapSettle` must exceed the recognizer's
`DoubleTapWindow + TapDeadlineJitterAllowance`, or the script ends before a held two-finger tap fires. Raise it when
those defaults change.

## Wait on exactly what you assert
Await the same signal the assertion checks (the event, not a property set earlier or on another thread): #219 waited on a property, then asserted an event raised later on another thread. Use a `TaskCompletionSource` (always `TaskCreationOptions.RunContinuationsAsynchronously`, or the test resumes on a product thread), an `EventLog<T>` or `Wait.Until`, with a timeout.
- `Last*` properties are stored before their event is raised: a test that counts events waits on the event queue, not the property.
- A client `Disconnected`/`Bye` doesn't mean the server freed the session (it lingers first). With takeover off, wait for `!HasSession` before pairing again.
- A side effect after a proxy signal (a strike after `raw.IsClosed`) needs its own wait: `Wait.Until(() => h.Server.PairingAttemptsLeft, n => n == 2, ...)`.

## The settle API: the one way to wait in Net tests (`Net/NetTestSupport.cs`)
- **`await h.SettleAsync()`**: the ordering barrier for a running session. Reusable, independent of fake time. It
  completes once every control frame either side sent before it was handled by the other side, including the pushes
  those frames caused (it runs `ServerConnection.RoundTripAsync`, an internal test hook: pushes drained, Ping, Pong, twice).
  Use it before a negative assertion ("nothing else arrived") and instead of ad-hoc barriers (pings via fake time,
  display-list pairs).
  It orders **TLS control frames only**: UDP datagrams in flight and the UdpAcks they trigger aren't covered. Wait for
  those explicitly (`Counters.Accepted`, `client.AcksReceived`, ack deadlines), as the 30 s streaming test does.
- **`EventLog<T>`**: subscribe with `log.Add`, take `int mark = log.Mark()`, act, settle, assert on `log.Since(mark)`;
  `log.WaitFor(mark, n, because)` for a positive wait. Never count from a baseline taken before the session settled
  or after `Clear()`: a late event from setup then counts as new (#250).
- **`Wait.Until(probe, ok, because)`**: real-time polling (Stopwatch, yields then 1 ms) bounded by `Wait.Limit`; on
  timeout it prints the last value seen. `Wait.Until(condition, because)` for plain conditions.
- **Cancellation:** `Wait.Until`, `AdvanceUntil` and `Bounded()` stop on `TestContext.Current.CancellationToken` as
  well as `Wait.Limit`, so a run timeout or Ctrl+C ends a hung wait at once. Don't suppress xUnit1051: pass
  `TestContext.Current.CancellationToken` where the analyzer asks.
- **`Wait.Step`**: the only way to move fake time across I/O (also without a session); see below.
- **Negative assertions over fake time:** timers fire only inside `Advance`, so after advancing to 1 ms before a
  deadline assert it is still armed (`CountDeadlines(... remaining == 1 ms) == 1`) instead of sleeping and checking.
- **Real-time limits** (blocking, dispose): bound them against the product timeout the bug would wait for
  (`ControlChannel.WriteTimeout`, `LingerTimeout`), not a guessed number of milliseconds.
- **`h.ConnectPairedAsync()`**: an already-paired client (`ServerFixture.SeedPairing`: a fresh device key on both
  sides, then reconnect auth). Use it unless the test is about pairing; `h.PairAsync()` runs J-PAKE (about 0.2 s
  each). A takeover test may keep one `PairAsync` when the takeover *by a pairing device* is the subject.

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
pkill -x yes
```
Parallel runs share the build output, so pass `--no-build`; read failures from each log. Repeat a few rounds.
- Also soak with `DOTNET_PROCESSOR_COUNT=2` under the same `yes` load: the runtime then sizes the thread pool and test
  parallelism for 2 cores, which starves continuations the way a small CI runner does.
- Soak the classes you touched, e.g. Net + Security: the in-process runner takes `-namespace PenBridge.Core.Tests.Net`
  (repeatable).
- zsh `wait $pids` doesn't split the string: run soaks inside `bash -c` with an array. `timeout` isn't on macOS; stop the load with `pkill -x yes`.
- `dotnet test` sometimes stalls for minutes before running when launched from the Bash sandbox (observed; cause unknown, test durations then include the stall). The in-process runner (`tests/PenBridge.Core.Tests/bin/<cfg>/net10.0/PenBridge.Core.Tests -class <name>`, `-longRunning <s>`) has no stall and suits soaks.

## Fake time with real sockets (Net tests)
- `FakeTime`'s timers fire only inside `Advance`, in due order, on the calling thread; `Net/NetTestSupport.cs` adds `Wait.Until` / `Wait.AdvanceUntil` / `Wait.Step` (bounded real-time polling).
- Never jump past a deadline in one `Advance` while I/O must happen in between (pings, acks): every timer that comes
  due fires before any socket I/O runs. Step with `Wait.Step(time, total[, step, settled])` (≤ 1 s, below the 2 s
  write timeout): after each step it waits, bounded by `Wait.Limit`, until real I/O caught up (its private `Settled`:
  pings re-armed the peer timeouts, no control write in flight; built on `FakeTime.CountDeadlines`). A loop that
  advances by hand (e.g. 40 ms keepalives) settles each step with `h.SettleAsync()`.
- **Never a fixed real sleep after a fake-time step.** "Advance 2 s, sleep 10 ms" passed alone and failed under load
  (#83): the pong hadn't arrived, so the next step fired the 6 s peer timeout. Wait for a condition, with a bound.
- **Test the moment the stream stops, right after an ack.** Rate-limited or batched protocol messages (UdpAck at most
  1/s) leave the tail unanswered unless something flushes it (a trailing ack). Steady streaming hides that bug;
  "stream, stop right after an ack, step 6 s, assert still on UDP" catches it.
- A deadline counts from when its operation *started*, which may be later than the test thinks (a writer drains
  asynchronously). Wait for a real-time condition first (e.g. `ControlWriteInFlightFor > 300 ms`), then advance.
- Share one `ServerIdentity`: the assembly fixture `ServerFixture` (`TestSupport/ServerFixture.cs`,
  `[assembly: AssemblyFixture]`) owns it (`ServerFixture.Current.Identity`, also `NetHarness.SharedServerIdentity`)
  plus the relay's second identity, and disposes them after the run. Creating/loading a PKCS#12 costs about 1 s on
  macOS, so a fresh identity per test makes the suite slow and pushes timing tests past their real-time waits. The
  fixture starts the identity in the background so it overlaps the first tests; keep it that way (an eager create
  in `InitializeAsync` delays every test by that second). Only identity tests (`ServerIdentityTests`) make their own.
- TLS version: assert `Tls12 or Tls13`, never 1.3 only (macOS servers negotiate 1.2, ADR-0013).
- To stall a TCP writer quickly, shrink both buffers (`PausableProxy` listener receive buffer, client
  `TcpSendBufferSize`) and keep sending until a write is really in flight.
- Don't step fake time before the server has read a burst you are rate-testing: token buckets refill with fake time. Wait on a real-time condition first (`handled + dropped == sent`); `h.SettleAsync()` doesn't move fake time.
- "Answered even if unchanged" tests: settle without moving the state (`h.SettleAsync()`), then count from a mark.
- Wire-quantized f32 values sit on thresholds (0.3 round-trips to 0.29999): use 0.301 in threshold tests. Allocation tests over settings paths should vary every branch per iteration (hover/button/contact/below-threshold/finger) so warm-up covers them.
