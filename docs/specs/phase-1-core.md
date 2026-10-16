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
- **1.1** adds the `SampleDedupe` and `FingerTouches` capabilities (below); 1.0 peers ignore them.
- **1.2** adds the `RemoteSettings` and `ActiveArea` capabilities and messages `0x40–0x43` (below, ADR-0030/0031); a 1.0/1.1 peer never advertises them, so it never gets these messages.
- **Capabilities** (`u32` flags in Hello/HelloAck; unknown bits are ignored): a feature is used only when the peer that must support it advertised it.
  - `SampleDedupe = 1` (since 1.1). Mac: it applies a frame's history samples only if the frame's `timeUs` advances past the previous applied frame's (see the input rules). Phone: it may resend a click frame at its original `timeUs`; it does so only when the Mac advertised this bit.
  - `FingerTouches = 2` (since 1.1, ADR-0026). Used only when **both** sides advertise it (`PenBridgeClient.NegotiatedCapabilities`, `SessionInfo.Capabilities`). Phone: in cursor finger mode it sends a lone finger as raw touches (no hover samples, no arming hold, no phone-side tap decision) and makes a touch lift survive one lost datagram (see the input rules). Mac: the session's `GestureRecognizer` turns one finger into a relative cursor (`OneFingerCursor`). The Core client always advertises it; the Mac server only with `PenBridgeServerOptions.FingerTouches` (off by default until the Mac head posts the one-finger gesture kinds). Without it on either side the phone keeps ADR-0022's absolute cursor.
  - `RemoteSettings = 4` (since 1.2, ADR-0030). Both sides must advertise it. The Mac sends `DisplayList` then `SettingsSnapshot` right after SessionStart, a snapshot after every change from either side and in reply to every `SettingsUpdate`, and the display list on display changes. The Core client always advertises it; the server only with `PenBridgeServerOptions.RemoteSettings`.
  - `ActiveArea = 8` (since 1.2, ADR-0031). Both sides must advertise it. The Mac sends `ActiveArea` after each SurfaceInfo and target, region, display or mode change. The Core client always advertises it; the server only with `PenBridgeServerOptions.ActiveArea`.
  - Receiving a settings or active-area message without its capability negotiated, or before SessionStart, is a protocol error.
- The Bonjour TXT record carries `v=1.2`, `fp=<base64url SHA-256 cert fingerprint>` and `name=<Mac name>`.

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
| 0x40 | S | SettingsSnapshot | `u8 count` (≤ 64) + setting entries: every Mac setting (1.2, `RemoteSettings`) |
| 0x41 | C | SettingsUpdate | `u8 count` (1..64) + setting entries to change; answered by a snapshot (1.2, `RemoteSettings`) |
| 0x42 | S | DisplayList | `u8 count` (≤ 16) + `{ id string, name string, isMain u8, widthPt f32, heightPt f32 }`; "All displays" is implied (1.2, `RemoteSettings`) |
| 0x43 | S | ActiveArea | `u16 x, y, w, h` on the 0..65535 scale, current surface orientation; `w, h > 0`, `x + w, y + h ≤ 65535` (1.2, `ActiveArea`) |
| 0x7E | S | Busy | — |
| 0x7F | both | Error | u16 code, message |
| 0x70 | both | Bye | reason (Normal, TakenOver, Unpaired, Shutdown) |

**Setting entries (1.2):** `u16 key ‖ u8 type ‖ u16 len ‖ value`. Types: 1 Bool (`u8` 0/1), 2 Int (`i32`), 3 Float (`f32`, finite), 4 String (≤ 256 B UTF-8, `len` = byte count), 5 Rect (4 × `f32` x, y, w, h normalized: inside 0..1, w, h > 0). A reader skips an entry of unknown type or whose length or value doesn't fit its type, and the receiver ignores unknown keys (forward compatible); a count over the cap, or a truncated entry, is a protocol error. Display ids are never empty or `"all"`; an invalid display entry is a protocol error.

**Mac setting keys** (`PenBridge.Core.Settings.MacSettingKey`; table, validation and defaults in `MacSettings`, values in `MacSettingValues`):

| Key | Name | Type | Range / values | Default |
|---|---|---|---|---|
| 1 | TargetDisplay | String | `""` main, `"all"` all displays, else a display id from DisplayList | `""` |
| 2 | Region | Rect | for the current target display; ignored for all displays | 0,0,1,1 |
| 3 | RegionAspectLock | Bool | | false |
| 10 | StylusMode | Int | 0 Pen, 1 Mouse | 0 |
| 11 | MouseSpeed | Float | 0.25..4 | 1 |
| 12 | MouseAcceleration | Bool | | true |
| 13 | SideButton | Int | 0 RightClick, 1 MiddleClick, 2 Eraser, 3 DoubleClick, 4 None | 0 |
| 14 | TipFeel | Float | −1..1 | 0 |
| 15 | ClickThreshold | Float | 0..0.5 | 0 |
| 16 | FullPressureAt | Float | 0.5..1 | 1 |
| 20 | SmartZoom | Int | 0 FollowSystem, 1 On, 2 Off | 0 |
| 21 | PointerSpeed | Float | 0.25..4 | 1 |
| 22 | ScrollSpeed | Float | 0.25..4 | 1 |
| 23 | Zoom | Bool | | true |
| 24 | Rotate | Bool | | true |
| 25 | ThreeFingerDrag | Bool | | true |
| 26 | FourFingerGestures | Bool | | true |

`FullPressureAt > ClickThreshold + 0.1` (in `f32`) must hold across the pair. The Mac validates each entry of an update on its own (`MacSettings.TryValidate`: known key, its type, in range, a display id from the list it sends; the display list is a required argument) and checks the pair on the result (`MacSettingValues.ApplyUpdate(entries, displays)`: if the result breaks it, both pressure entries of that update are ignored); it applies the valid ones, ignores the rest, and replies with the full snapshot, which is the truth. The phone reads a snapshot with `MacSettingValues.FromEntries`, which takes the Mac's TargetDisplay as sent.

**Connection flow:**
1. Hello / HelloAck.
2. Unpaired: pairing rounds, then `PairResult(ok)`. The phone stores `K_dev` and the fingerprint. The flow continues on the same connection with Auth.
3. Paired: Auth. A bad HMAC gets `Error(AuthFailed)` and the connection closes.
4. SessionStart, then SurfaceInfo, then UDP input. With `RemoteSettings` the Mac sends DisplayList and SettingsSnapshot right after SessionStart (before or after SurfaceInfo arrives).
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
- State 2 is a finger in contact. State 0 (`Contact=false`) is a **withdrawn** touch (ADR-0024): the phone already sent it as a contact, then suppressed it (palm, platform cancel, pen suppression), and keeps sending it this way in every datagram until that pointer lifts. The Mac cancels the gesture (Cancelled: no tap, no momentum), counts only contacts as fingers, and starts no new gesture until every touch present then is gone. Withdrawn touches count toward the 10-touch cap; they don't need the 50 ms contact keepalive. Any other state bit is invalid and the datagram is dropped. Phones before 1.1.0 never send state 0.
- **Lone fingers:** without `FingerTouches` a phone in finger-fallback mode never sends a lone finger as a touch (it goes out as a finger-fallback pen sample); touches carry gestures, and a finger left over after one. With `FingerTouches` negotiated and cursor mode, a touch sequence that starts with one contact is that finger as a relative cursor on the Mac. A phone with finger fallback turned off also sends lone-finger touches; the Mac ignores them unless the session negotiated `FingerTouches` **and** the latest SurfaceInfo carries `SurfaceCapabilities.FingerFallback` (the phone sends a new SurfaceInfo when the setting changes), so fallback off still means a lone finger does nothing.

**Rules:**
- Every datagram carries the **complete current state**: the pen if it's in range, plus all active touches.
- While the pen is in contact or touches are active, the phone sends at least every 50 ms, even when nothing moves.
- While the pen is only hovering (in range, no contact), the phone sends a keepalive at least every 250 ms.
- **Frame times:** each new frame's `timeUs` is strictly after the previous new frame's (the phone bumps it by 1 µs if its clock didn't move). Only a resend (below) reuses an earlier time.
- **Sample dedupe (Mac, `SampleDedupe`):** history samples were taken before their frame's `timeUs`. A frame whose `timeUs` doesn't advance past the last applied frame's (equal, or an earlier time that the stale check let through) skips its history and applies only its current pen sample, which is the full state and idempotent. The watermark is the tracker's unwrapped frame clock: per session, kept across SurfaceInfo changes (the phone clock runs on). A 1.0 phone never reuses a time, so this changes nothing for it.
- **Release repeats and click resend (#92):** after the state becomes empty (pen out of range, no touches) the phone repeats it twice at the contact interval. If the frame that emptied it carried a whole click (a contact sample in its history, then the empty state: a cursor-mode tap or double-tap replay, which travels in one datagram) and the Mac advertised `SampleDedupe`, each repeat resends **that frame as it was** (same `timeUs`, same history), and a click frame also starts the repeats when nothing active was sent before it (a tap released within the arming delay). A lost click datagram is recovered, and the Mac applies the click once. Any newer frame ends the resend. Without the capability the repeats carry only the empty state, as in 1.0.
- **Touch lifts (`FingerTouches`):** a one-finger tap is decided from touch frames on the Mac, so the phone makes its lift survive one lost datagram without doubling it. (1) Before a frame in which a touch that was in contact is gone or withdrawn, the phone sends the previous state once more (new `timeUs`) unless a keepalive already resent it, so a quick tap's only contact frame can't be the one lost (an identical full state changes nothing on the Mac). (2) After a lift that empties the state, the release repeats resend that frame with its own `timeUs`, so a repeat standing in for a lost lift gives the Mac the real lift time and a tap near `TapMaxDuration` stays a tap. (1) applies only over UDP (TLS fallback frames are reliable); both need the capability negotiated.
- **Receiver watchdog:** with contact or active touches, after 150 ms with no valid datagram, synthesize the full release in this order: pen-up (and button-ups), touches-up (active gestures end as `Cancelled`), then pen-out-of-range. While only hovering, synthesize pen-out-of-range after 1 s with no valid datagram.

## Input processing (Core, used by the heads)

- **`InputFilter`** (phone side): pen-first. Touches are suppressed while the pen is in range or in contact and for 300 ms afterwards. Canceled or palm-suppressed pointers that were already sent stay on the wire as present with `Contact=false` until they lift (ADR-0024); unsent ones are dropped. With no pen, a single finger is reported as a pen-like sample (tool = finger fallback). This mode is a SurfaceInfo capability, so the Mac knows pressure is synthetic.
  - **Palm by size:** the head passes each contact's size in `RawTouch.Size`, normalized 0..1 (Android: `getTouchMajor` over the device's touch-major axis range, falling back to `getSize`; 0 = unknown, never a palm). The size is local to the phone and never sent on the wire. A contact at or above `PalmSizeThreshold` (default 0.2, configurable; above 1 disables it) suppresses every touch until all fingers lift, then for `PalmSuppression` (default 200 ms). A finger landing in that window stays suppressed until it lifts and restarts the window. The 0.2 default is conservative (about 35 mm on a 6.8" screen when touch major is measured against the screen diagonal) and still needs tuning from an S23 touch-major capture.
  - **Finger-fallback arming delay:** a lone finger is held back for `FingerFallbackArmDelay` (default 100 ms; 0 arms at once). If a second finger lands first, it's a gesture: the held samples are dropped and the fingers go out as raw touches until all lift, so a gesture's first finger never becomes a stroke or a click. Otherwise the stroke starts and the held samples are replayed, oldest first, before the live sample (`InputFilterResult.ReplayCount`, `InputFilter.ReplaySamples`; the head sends them as history). A finger that lifts within the delay is replayed as a whole tap that ends with a `PenState.None` sample, also when a new lone finger replaces it in the same snapshot (whose hold then starts). The head passes each event's batched finger positions (`Process(..., history)`); the lone finger's are held too, and while it draws they come back through `ReplaySamples`, so nothing is lost. The head sends nothing while a finger is held. The hold buffer is fixed (128 samples, allocation-free); on overflow the newest sample replaces the last slot. A finger that doesn't move sends no events, so the head calls `Process` again at `InputFilter.PendingDeadline`.
  - **Relative cursor (ADR-0026):** with `InputFilterOptions.FingerTouches` (set by the head from the negotiated `FingerTouches`) and cursor mode, a lone finger is reported as a raw touch from its first event until all fingers lift, exactly like a gesture's fingers: nothing is held, armed or replayed (`PendingDeadline` stays null), and taps are decided by the Mac's recognizer. Palm rejection, pen suppression and withdrawn touches are unchanged. Draw mode ignores the option. The cursor-mode rules below are the fallback when the capability isn't negotiated.
  - **Finger mode (ADR-0022, #91):** `FingerMode` is `Cursor` by default or `Draw`. In draw mode the armed finger sends contact samples (a stroke), as above. In cursor mode without `FingerTouches` the finger moves the cursor without pressing:
    - its samples are hover samples (in range, no contact), so the client's 250 ms hover keepalive keeps the cursor alive and the Mac's hover watchdog applies;
    - a **tap** is replayed as a click where it landed: a hover sample, a contact sample, then `PenState.None`. A tap lifts sooner than `TapMaxDuration` (default 200 ms) after landing and moves at most `TapSlop` (default 0.02, a fraction of the surface in normalized units). Any other lift ends with `PenState.None`, so a slow touch is never a click;
    - **double tap:** a touch landing within `DoubleTapWindow` (default 300 ms) after a tap and within `DoubleTapSlop` (default 0.05) of it is held until decided (`PendingDeadline` = landing + max(arming delay, tap duration)). If it lifts as a tap, it clicks again at exactly the first tap's point, so the Mac counts a double-click. If it moves beyond the tap slop or stays down for the tap duration (never before the arming delay), it starts a contact stroke from where it landed (moved one unit off the tap's point if it is exactly there), a left drag until lift. The Mac repeats the click count for finger-fallback presses only at exactly the same point (`TabletEventMath.ClickSlopFor`), so the drag starts with click state 1. A tap counts only for the very next touch;
    - in draw mode a held tap within the double-tap window and slop of the previous tap is replayed at exactly that tap's point (finger double-clicks);
    - the wire format is unchanged; on the Mac only the click state of finger presses changed. Pen suppression, palm rejection, the arming delay and two-finger touches work as in draw mode.
- **`CoordinateMapper`** (Mac side): maps a normalized (x, y) to a target rect in global display points, preserving the surface aspect ratio (letterbox/pillarbox, centered). It's configured with the surface size from SurfaceInfo and the target rect, and handles both orientations.
- **`PenStateTracker`** (Mac side): turns a stream of full-state datagrams into edge events: ProximityEnter/Leave, Down, Move, Drag, Up, ButtonDown/Up, ToolChanged. It also runs the watchdog. These events are what `IInputInjector` (Phase 2) consumes.
- **`GestureRecognizer`** (Mac side): from touch state it emits `Scroll(dx, dy, phase)` and `Pinch(scaleDelta, centerX, centerY, phase)`, with phases Began/Changed/Ended. A threshold distinguishes scroll from pinch, and the gesture locks once chosen. What these map to on the Mac is decided in Phase 4 (ADR-0023; the contract is in the `GestureKind` XML docs). A scroll whose starting movement is within `ScrollAxisLockRatio` (default 0.5, about 27°) of an axis is locked to it until it ends: the other delta is 0 in every event, so momentum coasts along that axis only (#123). A two-finger tap follows the smart-zoom switch `GestureRecognizerOptions.SmartZoomEnabled` (ADR-0029; a `Func<bool>` the Mac backs with the macOS Smart Zoom setting, null = on), read once at the tap's lift on the input thread: off, the tap is a `Tap` (secondary click) on lift with no hold and no `PendingDeadline`, and a double tap is two Taps; on, it is held for `DoubleTapWindow` (default 200 ms; the timer deadline adds the 20 ms `TapDeadlineJitterAllowance`, so about 225 ms after the lift, timed from the arrival of the lift frame's first copy: a repeat with the same frame time, such as a release repeat, doesn't restart it, #142) and a second tap within it and `DoubleTapSlop` makes a `SmartZoom`. A tap held when the switch turns off still ends as a Tap, at its deadline or at the next tap's lift.
  - **One finger (ADR-0026, `OneFingerCursor`, on per session when `FingerTouches` was negotiated, and only while the latest SurfaceInfo has `FingerFallback`: `GestureRecognizer.PhoneFingerFallback`):** only a touch sequence whose first frame has one contact (never a finger left over from a multi-finger gesture). Movement within `PointerSlop` (default 0.015 of the short side: about 1 mm on the S23 Ultra, about 13 pt when its landscape surface fills a 1920×1080 pt display) moves nothing, so a second finger landing doesn't nudge the cursor; during the first `PointerSettleTime` (100 ms) after landing only movement beyond `TapSlop` counts, since a second finger can still land and a gesture's slop must be at least that of every gesture it can turn into (a staggered two-finger tap or smart zoom stays one); past it `PointerMove` Began carries the whole movement since the landing and Changed the per-frame movement, in display points through the three-finger-drag gain and acceleration, Ended on the lift. A tap that drifts farther than the slop is a move, not a click. A lift within `TapMaxDuration` that never left the slop is a `Click` at once (`ClickCount` 1, or one more when it landed within `ClickWindow` (default 400 ms; separate from the two-finger `DoubleTapWindow`, 200 ms, since a Click is never held) and `ClickSlop` (0.05) of the previous Click: 2 = double click). A touch landing that way that moves past the slop, or stays down past the tap duration, is a `PointerDrag` (left button, click state 1) released on the lift. A second finger ends the one-finger action (`Ended`; a drag releases first) and the fingers start the multi-finger decision. The pen, a withdrawn touch, the watchdog, session end and the Mac's pause cancel them (`Cancelled`; a drag's button is released); a touch interrupted that way is no Click. All decisions use frame times; nothing is held for a timer. A frame in which every earlier contact is gone and new ones are down (a coalesced snapshot, or a lost lift datagram) first ends the old touch sequence. Accepted limit: losing two or more consecutive datagrams of a multi-finger touch can leave the Mac seeing a lone finger, i.e. a pointer or a stray Click; the echo before each lift covers any single loss.
- **Allocation rules:** everything per sample is allocation-free; mapper, tracker and recognizer are structs or reuse buffers. Tests assert zero allocations on the hot path (`GC.GetAllocatedBytesForCurrentThread`).

## Abstractions for platform heads

- `ISecretStore`: `TryGet(string key, out byte[])`, `Set`, `Delete`. Core ships `InMemorySecretStore` and `FileSecretStore` (tests and SimClient only).
- `IPairedDeviceRegistry`: list, add, remove, touch(lastSeen), backed by `ISecretStore`.
- `IInputSink`: receives the tracker and recognizer events on the Mac side. Phase 2's injector implements it; SimClient and tests use a recording sink.
- `PenBridgeServer`: binds to 47820 with fallback, accepts sessions (one active, optional takeover), exposes `CurrentPin`, `CreateQrPayload()` and events.
- `PenBridgeClient`: connect, pair (PIN or QR payload), auth, `SendState(in PenSample?, ReadOnlySpan<TouchSample>)`.
- **QR payload:** `penbridge://pair?h=<host>&p=<port>&fp=<b64url>&t=<b64url token 32B>&v=1.2` (the current protocol version).

## SimClient (`tools/PenBridge.SimClient`, net10.0 console)

- `pair --host <h> [--port] --pin <pin>` | `pair --qr "<payload>"`, then `draw <script>` with built-in scripts: line, circle, pressure-ramp, tilt-sweep, hover, button-click, two-finger-scroll, pinch, packet-loss (drops 20% of datagrams).
- It stores its identity in `~/.penbridge/simclient/`.
- An `--in-process-server` mode starts a `PenBridgeServer` with a logging `IInputSink`, for demos without the Mac app.
- `settings [--set Key=Value]...` (1.2) prints the Mac's settings snapshot and display list and sends an update (e.g. `--set StylusMode=Mouse`); `draw --settings` prints what the Mac sends. The in-process server keeps validated settings in memory with two fake displays.

## Acceptance (phase)

- `scripts/test.sh` is green, and CI is green.
- Golden-byte tests exist for every control message and the datagram layout.
- J-PAKE tests cover: success; wrong PIN; expiry; 3-strike lockout; relay detection (a mismatched fingerprint fails confirmation).
- Loopback end-to-end: pair, auth, stream a stroke, receive the expected edge events; tamper / replay / old-sequence drops; watchdog pen-up; TLS fallback when UDP is blocked; Busy / takeover.
- Zero-allocation assertions on serialize → encrypt → decrypt → track.
- No trim/AOT warnings (`IsAotCompatible`).
