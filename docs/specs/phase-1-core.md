# Phase 1 — Core spec

Scope: everything in `PenBridge.Core` that later phases build on, plus `tools/PenBridge.SimClient`. ADRs 0003–0005 and 0008–0012 apply. This spec fixes the **contracts**; implementers choose the internals, but any deviation in a contract below must be raised before coding.

## Namespaces and folders (one workstream each)

| Folder (`src/PenBridge.Core/…`) | Namespace | Workstream |
|---|---|---|
| `Protocol/` | `PenBridge.Core.Protocol` | P1-A |
| `Security/` | `PenBridge.Core.Security` | P1-B |
| `Input/` | `PenBridge.Core.Input` | P1-C |
| `Net/` | `PenBridge.Core.Net` | P1-D (after A+B merged) |
| `tools/PenBridge.SimClient/` | `PenBridge.SimClient` | P1-D |

Tests mirror the folders: `tests/PenBridge.Core.Tests/<Folder>/`. The only external dependencies allowed are `BouncyCastle.Cryptography` (P1-B) and `Microsoft.Extensions.Logging.Abstractions`.

## Versioning

- The existing `ProtocolInfo.Version = 1` becomes `ProtocolVersion { Major = 1, Minor = 0 }`.
- A different major is rejected with `Error(IncompatibleVersion)`. A different minor is accepted: features are gated by capability flags.
- The Bonjour TXT record carries `v=1.0`, `fp=<base64url SHA-256 cert fingerprint>` and `name=<Mac name>`.

## Control channel (TCP + TLS 1.2+, ADR-0013)

- **Frame:** `u32 BE length` (type byte + payload, max 65 536, otherwise disconnect), then `u8 type`, then the payload. All integers are big-endian.
- **Strings:** `u16` length followed by UTF-8 bytes, max 256 bytes.
- **Unknown types:** a reserved range `0xF0–0xFF` is ignored. Any other unknown type closes the connection with an error.

Messages (C = phone → Mac, S = Mac → phone):

| Type | Dir | Name | Payload |
|---|---|---|---|
| 0x01 | C | Hello | version, deviceId (16 B), deviceName, capabilities (u32 flags) |
| 0x02 | S | HelloAck | version, serverName, capabilities, `challenge` (32 B) |
| 0x10 | C | PairRound1 | J-PAKE round-1 payload + `secretKind` (Pin=1, QrToken=2) |
| 0x11 | S | PairRound1 | J-PAKE round-1 |
| 0x12 | C | PairRound2 | J-PAKE round-2 |
| 0x13 | S | PairRound2 | J-PAKE round-2 |
| 0x14 | C | PairConfirm | MAC over (transcript, Mac cert fingerprint as the phone sees it) |
| 0x15 | S | PairConfirm | MAC, same construction, other role |
| 0x16 | S | PairResult | ok \| failed(attemptsLeft) \| expired |
| 0x20 | C | Auth | deviceId, `HMAC-SHA256(K_dev, "pb-auth-v1" ‖ challenge ‖ clientNonce(32) ‖ certFingerprint)`, clientNonce |
| 0x21 | S | SessionStart | udpPort, sessionId (u32), serverNonce (32 B) |
| 0x22 | C | SurfaceInfo | widthPx, heightPx, xdpi, ydpi (f32), orientation |
| 0x23 | S | UdpAck | highest sequence received so far (sent after the first valid datagram, then at most once per second while datagrams arrive) |
| 0x24 | C | InputFrame | same payload as a decrypted datagram. Used only in TLS fallback mode. |
| 0x30 | both | Ping / 0x31 Pong | u64 timestamp µs |
| 0x7E | S | Busy | — |
| 0x7F | both | Error | u16 code, message |
| 0x70 | both | Bye | reason (Normal, TakenOver, Unpaired, Shutdown) |

**Connection flow:**
1. Hello / HelloAck.
2. Unpaired: pairing rounds, then `PairResult(ok)`. The phone stores `K_dev` and the fingerprint. The flow continues on the same connection with Auth.
3. Paired: Auth. A bad HMAC gets `Error(AuthFailed)` and the connection closes.
4. SessionStart, then SurfaceInfo, then UDP input.
5. Phone: after sending UDP for 2 s without any `UdpAck`, switch to `InputFrame` over TLS. While in fallback, send one UDP probe datagram every 10 s and switch back to UDP when an ack covers it.
   - The Mac pins the session's UDP source endpoint on the first authenticated datagram and re-pins it only to a new source whose datagram authenticates and carries a sequence above the highest seen (roaming).
6. Heartbeat: Ping every 2 s. A peer silent for 6 s is dead.
7. Pre-auth limits: at most 8 unauthenticated connections in total and 2 per remote IP; a connection must reach SessionStart within 10 s of accept; pairing runs under its own timeout, then Auth gets a fresh 10 s.

**TLS identity:**
- The server identity is a self-signed ECDSA P-256 certificate (CN = PenBridge-<random>), valid for 10 years, created with `CertificateRequest`. It's persisted as PFX bytes through `ISecretStore`.
- The client presents no certificate. It validates the server by fingerprint pin when paired, and accepts any certificate while pairing (J-PAKE confirmation binds it instead).
- Known risk: `SslStream` server certificates on macOS can't use ephemeral key sets. Load them in a way that works on macOS, and test it there (`scripts/test.sh` runs on macOS locally).

## Key schedule

- `K_dev` (32 B) = HKDF-SHA256 over the J-PAKE shared key, salt = transcript hash, info `pb-device-key-v1`.
- `K_in` (32 B, per session) = HKDF-SHA256(`K_dev`, salt = clientNonce ‖ serverNonce, info `pb-input-v1` ‖ sessionId).
- Datagram nonce (96 bit) = `u32 sessionId` ‖ `u64 sequence`.
- `sequence` starts at 1 and strictly increases. The receiver drops anything ≤ its highest seen sequence (latest wins).
- A new session always means new nonces, so a nonce is never reused with the same key.

## Input datagram (UDP)

- **Header (16 B, also the AEAD associated data):** `u8 version(1)` ‖ `u8 flags` ‖ `u16 reserved=0` ‖ `u32 sessionId` ‖ `u64 sequence`.
- **Body:** AES-256-GCM ciphertext of the payload, followed by the 16 B tag.
- **Payload:** `u32 timeUs` (µs since session start; wraps) ‖ `u8 penCount(0|1)` ‖ `u8 touchCount(0..10)` ‖ pen sample? ‖ touch samples…, then `u8 historyCount` ‖ up to 8 earlier pen samples (oldest first, so batched historical samples arrive together).
- **Max datagram size:** 512 B.

**PenSample (16 B):**
- Positions: `u16 x`, `u16 y` (normalized 0..65535 over the surface).
- `u16 pressure` (0..65535).
- Tilt: `i16 tiltX`, `i16 tiltY` (centi-degrees, −9000..9000). tiltX > 0 means the pen top leans toward +x (right on the surface) and tiltY > 0 toward +y (down, toward the user), the CGEvent tablet convention; the Android sampler converts `AXIS_TILT`/`AXIS_ORIENTATION` to it.
- `u16 rotation` (centi-degrees 0..35999).
- `u8 tool`: Pen = 1, Eraser = 2.
- `u8 state` flags: InRange = 1, Contact = 2.
- `u8 buttons` flags: Primary = 1, Secondary = 2.
- `u8 reserved`.

**TouchSample (6 B):** `u8 pointerId` ‖ `u8 state` (Contact = 2) ‖ `u16 x` ‖ `u16 y`.

**Rules:**
- Every datagram carries the **complete current state**: the pen if it's in range, plus all active touches.
- While the pen is in contact or touches are active, the phone sends at least every 50 ms, even when nothing moves.
- While the pen is only hovering (in range, no contact), the phone sends a keepalive at least every 250 ms.
- **Receiver watchdog:** with contact or active touches, after 150 ms with no valid datagram, synthesize the full release in this order: pen-up (and button-ups), touches-up (active gestures end as `Cancelled`), then pen-out-of-range. While only hovering, synthesize pen-out-of-range after 1 s with no valid datagram.

## Input processing (Core, used by the heads)

- **`InputFilter`** (phone side): pen-first. Touches are suppressed while the pen is in range or in contact and for 300 ms afterwards. Canceled pointers are dropped. With no pen, a single finger is reported as a pen-like sample (tool = finger fallback). This mode is a SurfaceInfo capability, so the Mac knows pressure is synthetic.
  - **Palm by size:** the head passes each contact's size in `RawTouch.Size`, normalized 0..1 (Android: `getTouchMajor` over the device's touch-major axis range, falling back to `getSize`; 0 = unknown, never a palm). The size is local to the phone and never sent on the wire. A contact at or above `PalmSizeThreshold` (default 0.2, configurable; above 1 disables it) suppresses every touch until all fingers lift, then for `PalmSuppression` (default 200 ms). A finger landing in that window stays suppressed until it lifts and restarts the window. The 0.2 default is conservative (about 35 mm on a 6.8" screen when touch major is measured against the screen diagonal) and still needs tuning from an S23 touch-major capture.
  - **Finger-fallback arming delay:** a lone finger is held back for `FingerFallbackArmDelay` (default 100 ms; 0 arms at once). If a second finger lands first, it's a gesture: the held samples are dropped and the fingers go out as raw touches until all lift, so a gesture's first finger never becomes a stroke or a click. Otherwise the stroke starts and the held samples are replayed, oldest first, before the live sample (`InputFilterResult.ReplayCount`, `InputFilter.ReplaySamples`; the head sends them as history). A finger that lifts within the delay is replayed as a whole tap that ends with a `PenState.None` sample, also when a new lone finger replaces it in the same snapshot (whose hold then starts). The head passes each event's batched finger positions (`Process(..., history)`); the lone finger's are held too, and while it draws they come back through `ReplaySamples`, so nothing is lost. The head sends nothing while a finger is held. The hold buffer is fixed (128 samples, allocation-free); on overflow the newest sample replaces the last slot. A finger that doesn't move sends no events, so the head calls `Process` again at `InputFilter.PendingDeadline`.
  - **Finger mode (ADR-0022, #91):** `FingerMode` is `Cursor` by default or `Draw`. In draw mode the armed finger sends contact samples (a stroke), as above. In cursor mode the finger moves the cursor without pressing:
    - its samples are hover samples (in range, no contact), so the client's 250 ms hover keepalive keeps the cursor alive and the Mac's hover watchdog applies;
    - a **tap** is replayed as a click where it landed: a hover sample, a contact sample, then `PenState.None`. A tap lifts sooner than `TapMaxDuration` (default 200 ms) after landing and moves at most `TapSlop` (default 0.02, a fraction of the surface in normalized units). Any other lift ends with `PenState.None`, so a slow touch is never a click;
    - **double tap:** a touch landing within `DoubleTapWindow` (default 300 ms) after a tap and within `DoubleTapSlop` (default 0.05) of it is held until decided (`PendingDeadline` = landing + max(arming delay, tap duration)). If it lifts as a tap, it clicks again at exactly the first tap's point, so the Mac counts a double-click. If it moves beyond the tap slop or stays down for the tap duration (never before the arming delay), it starts a contact stroke from where it landed (moved one unit off the tap's point if it is exactly there), a left drag until lift. The Mac repeats the click count for finger-fallback presses only at exactly the same point (`TabletEventMath.ClickSlopFor`), so the drag starts with click state 1. A tap counts only for the very next touch;
    - in draw mode a held tap within the double-tap window and slop of the previous tap is replayed at exactly that tap's point (finger double-clicks);
    - the wire format is unchanged; on the Mac only the click state of finger presses changed. Pen suppression, palm rejection, the arming delay and two-finger touches work as in draw mode.
- **`CoordinateMapper`** (Mac side): maps a normalized (x, y) to a target rect in global display points, preserving the surface aspect ratio (letterbox/pillarbox, centered). It's configured with the surface size from SurfaceInfo and the target rect, and handles both orientations.
- **`PenStateTracker`** (Mac side): turns a stream of full-state datagrams into edge events: ProximityEnter/Leave, Down, Move, Drag, Up, ButtonDown/Up, ToolChanged. It also runs the watchdog. These events are what `IInputInjector` (Phase 2) consumes.
- **`GestureRecognizer`** (Mac side): from touch state it emits `Scroll(dx, dy, phase)` and `Pinch(scaleDelta, centerX, centerY, phase)`, with phases Began/Changed/Ended. A threshold distinguishes scroll from pinch, and the gesture locks once chosen. What these map to on the Mac is decided in Phase 4.
- **Allocation rules:** everything per sample is allocation-free; mapper, tracker and recognizer are structs or reuse buffers. Tests assert zero allocations on the hot path (`GC.GetAllocatedBytesForCurrentThread`).

## Abstractions for platform heads

- `ISecretStore`: `TryGet(string key, out byte[])`, `Set`, `Delete`. Core ships `InMemorySecretStore` and `FileSecretStore` (tests and SimClient only).
- `IPairedDeviceRegistry`: list, add, remove, touch(lastSeen), backed by `ISecretStore`.
- `IInputSink`: receives the tracker and recognizer events on the Mac side. Phase 2's injector implements it; SimClient and tests use a recording sink.
- `PenBridgeServer`: binds to 47820 with fallback, accepts sessions (one active, optional takeover), exposes `CurrentPin`, `CreateQrPayload()` and events.
- `PenBridgeClient`: connect, pair (PIN or QR payload), auth, `SendState(in PenSample?, ReadOnlySpan<TouchSample>)`.
- **QR payload:** `penbridge://pair?h=<host>&p=<port>&fp=<b64url>&t=<b64url token 32B>&v=1.0`.

## SimClient (`tools/PenBridge.SimClient`, net10.0 console)

- `pair --host <h> [--port] --pin <pin>` | `pair --qr "<payload>"`, then `draw <script>` with built-in scripts: line, circle, pressure-ramp, tilt-sweep, hover, button-click, two-finger-scroll, pinch, packet-loss (drops 20% of datagrams).
- It stores its identity in `~/.penbridge/simclient/`.
- An `--in-process-server` mode starts a `PenBridgeServer` with a logging `IInputSink`, for demos without the Mac app.

## Acceptance (phase)

- `scripts/test.sh` is green, and CI is green.
- Golden-byte tests exist for every control message and the datagram layout.
- J-PAKE tests cover: success; wrong PIN; expiry; 3-strike lockout; relay detection (a mismatched fingerprint fails confirmation).
- Loopback end-to-end: pair, auth, stream a stroke, receive the expected edge events; tamper / replay / old-sequence drops; watchdog pen-up; TLS fallback when UDP is blocked; Busy / takeover.
- Zero-allocation assertions on serialize → encrypt → decrypt → track.
- No trim/AOT warnings (`IsAotCompatible`).
