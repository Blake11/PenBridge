---
name: net-engine
description: Internals and gotchas of the PenBridge.Core network engine (src/PenBridge.Core/Net) — PenBridgeServer/PenBridgeClient threading, TimeProvider timers and the one-shot pen watchdog, IInputSink fault isolation, TLS control channel deadlines, UDP endpoint pinning, IPv4-mapped/IPv6 peers, pre-auth limits, forged-datagram rate limiting, TLS 1.2 on macOS. Use when changing or debugging Net code, the SimClient, wiring the engine into the Mac/Android heads, or adding a timer, socket loop or per-peer limit.
---

# Net engine (`src/PenBridge.Core/Net`)

Design and timings: `docs/specs/phase-1-core.md` (connection flow), ADR-0012/0013. Read the `<remarks>` on `SessionInput`, `PenBridgeServer`, `ControlChannel` before changing them. Tests: `tests/PenBridge.Core.Tests/Net/` (skill `core-testing`, "Fake time with real sockets" and "Soak"); a timing change isn't done until it survives a soak under full CPU load.

## Threads and timers
- Server: accept loop, UDP receive loop, one async task per TCP connection. Client: one read loop + one background fallback writer. No polling anywhere (idle ≈ 0% CPU).
- All timers come from the injected `TimeProvider` (`CreateTimer`), never `Task.Delay`/`System.Threading.Timer` directly, so `FakeTime` drives them in tests. Use `static` callbacks with a state object (no closure per arm).
- **Pen watchdog pattern:** `PenStateTracker` owns no timer. After every frame (and after each watchdog fire) re-arm a one-shot `ITimer` with `tracker.WatchdogDueIn` (`null` → `Timeout.InfiniteTimeSpan`, `≤ 0` → 1 ms), then call `CheckWatchdog()` when it fires. See `SessionInput.RearmLocked`/`OnWatchdog`.
- **Serialize every tracker/recognizer/sink call** (UDP datagrams, TLS `InputFrame`s, watchdog, SurfaceInfo, shutdown) under one server-wide lock, so `IInputSink` is never called concurrently — including while an old session releases on takeover.

## Fault isolation (`IInputSink` is foreign code)
- Timer callbacks and fire-and-forget loops (UDP loop, accept loop, writers) need a catch-all around anything that calls the sink, tracker or user callbacks (`#pragma warning disable CA1031` with a reason). An exception that escapes onto a timer thread crashes the process; one that escapes the UDP/accept loop silently kills it for every session.
- A sink fault closes only that session (reported once via the fault callback). The accept loop must survive `ConnectionReset` and any `Admit` failure.

## Control channel (TLS)
- Reads: per-read deadline + 10 s pre-auth handshake deadline; any failure is terminal (dispose, close). `ControlChannel` arms deadlines as `TimeProvider` timers (`_readDeadline`/`_writeDeadline`), the testable equivalent of `CancelAfter`.
- Writes: `Socket.SendTimeout` does **not** apply to async writes. Give each write its own deadline (2 s), and on expiry **close the socket** (`AbortHard`) — cancellation alone doesn't unstick a write blocked on a full send buffer.
- **Closing with Bye (takeover, unpair, shutdown):** write the Bye, half-close (`Shutdown(Send)`), stop the ping timer, then linger up to `ControlChannel.LingerTimeout` (1 s) draining until the peer closes, and only then abort. Aborting right after the Bye sends an RST that can discard it unread, so the client never learns it was taken over (#80).
- Client fallback sends go through a bounded latest-wins queue + single writer, so `SendState` never blocks the caller.
- TLS: macOS `SslStream` servers negotiate only TLS 1.2 (forcing 1.3 fails; ADR-0013). Accept 1.2+, never assert 1.3; tests assert the negotiated protocol is `Tls12 or Tls13`.

## Addresses and limits
- Dual-mode sockets report IPv4 peers as `::ffff:a.b.c.d`. Map with `IsIPv4MappedToIPv6 ? MapToIPv4() : addr` **before** comparing with UDP sources, sending UDP, or keying per-IP limits — otherwise the TCP and UDP views of one phone don't match.
- Per-peer limits group IPv6 by **/64** (one host usually owns the whole prefix); IPv4 by address. Pre-auth: 2 pending per key, 8 global, 10 s; pairing has its own window/lockout (skill `protocol-change`).

## UDP input
- Pin to the TCP peer IP until the first authenticated datagram, then to its exact endpoint; re-pin only on a datagram that passes AEAD **and** has a higher sequence (roaming works, replays/forgeries can't move it).
- Drop stale sequences before decrypting. Forged datagrams cost a decrypt each: a token bucket (16/s, burst 32) caps the AEAD attempts per session, overflow counts as `RateLimited`. A LAN flood can still delay real input (accepted, documented DoS limit) but never inject.
- UdpAck ≤ 1/s **plus a trailing one-shot ack** for the last sequence, else a stroke that ends right after an ack looks unacked and the client falls back to TLS. Client falls back after 2 s unacked, re-probes UDP every 10 s.
- Frames before SurfaceInfo are dropped (UDP can overtake TCP); the next full-state frame recovers.

## SimClient
`tools/PenBridge.SimClient` (`penbridge-sim pair|draw|serve|scripts`, `--in-process-server`) exercises every path without a phone; keep it working when the protocol or engine changes. Identity in `~/.penbridge/simclient/`.
