# Screen mirroring — spec (protocol 1.3)

ADR-0038. Epic #16. This spec fixes the **contracts** that Core, the Mac head, the Android head and the SimClient build against in parallel. Implementers choose the internals; any deviation in a contract below goes back to the architect first. All integers are big-endian (as in `docs/specs/phase-1-core.md`).

## Versioning and capability

- `ProtocolVersion.Current` becomes **1.3**; the Bonjour TXT `v=1.3`.
- New capability bit `ScreenMirror = 16`. Mirroring is used only when **both** sides advertise it. The Core client always advertises it; the server only with `PenBridgeServerOptions.ScreenMirror` (off by default; the Mac head turns it on).
- Receiving any message below without `ScreenMirror` negotiated, or before SessionStart, is a protocol error. A 1.2 peer never advertises the bit, so it never gets these messages; mixed versions fall back to the blank pad.

## Mac settings (RemoteSettings, ADR-0030)

| Key | Name | Type | Range / values | Default |
|---|---|---|---|---|
| 30 | MirrorEnabled | Bool | | false |
| 31 | MirrorQuality | Int | 0 Low, 1 Balanced, 2 High | 1 |
| 32 | MirrorFps | Int | 0 Auto, or a limit: 30, 60, 120 | 0 |

Same validation and snapshot rules as the 1.2 keys (`MacSettings`, `SettingsWriteRules`). A 1.2 phone ignores the unknown keys.

## Control messages (TLS)

| Type | Dir | Name | Payload |
|---|---|---|---|
| 0x50 | C | MirrorStart | `u8 codecs` (bit 0 H.264, bit 1 HEVC: hardware decoders the phone has), `u16 maxWidthPx`, `u16 maxHeightPx` (decoder limit), `u8 maxFps` (the phone display's highest refresh rate, rounded, ≥ 1), `u16 videoUdpPort` (the phone's video socket) |
| 0x51 | C | MirrorStop | — |
| 0x52 | S | MirrorConfig | `u8 epoch`, `u8 codec` (1 H.264, 2 HEVC), `u16 widthPx`, `u16 heightPx`, `u8 fps`, `u32 bitrateKbps`, `u16 videoUdpPort` (the Mac's video socket) |
| 0x53 | S | MirrorState | `u8 state`, `u8 reason` (below) |
| 0x54 | C | KeyframeRequest | `u8 epoch`, `u32 lastCompleteFrame` |
| 0x55 | C | MirrorReport | `u8 epoch`, `u32 framesComplete`, `u32 framesDropped`, `u32 fragmentsLost` (counters since MirrorConfig, wrap), `u32 lastRenderedFrame`, `u64 lastRenderedAtUs` (phone clock, the Ping clock) |

**MirrorState:** `state` 0 Off, 1 Starting, 2 Streaming, 3 Paused, 4 Throttled (input guard paused video), 5 Unavailable. `reason` (for Off/Paused/Unavailable, else 0): 0 None, 1 SettingOff, 2 PenBridgePaused, 3 PermissionDenied (Screen Recording), 4 TlsFallback, 5 NoCommonCodec, 6 CaptureFailed, 7 PhoneStopped. Unknown `state`/`reason` values are shown as Unavailable, not an error (forward compatible).

**Rules:**
- The phone sends **MirrorStart** when it wants video: `MirrorEnabled` is true in the snapshot, the app is in the foreground, the screen is on, and the session is not in TLS fallback. It sends **MirrorStop** when any of these ends. Starting again is another MirrorStart (its fields may differ).
- The Mac answers every MirrorStart, and every change that matters, with **MirrorState**. While it can stream it sends **MirrorConfig**, then `Streaming`. A new MirrorConfig (new `epoch`, mod 256) is sent whenever the codec, size, fps or bitrate changes: target, region, window resize, active area, quality, fps, or an input-guard step. The phone reconfigures its decoder on a new epoch and drops datagrams of other epochs.
- **Codec choice (Mac):** the best codec in `phone codecs ∩ Mac hardware encoders`, HEVC first, then H.264. Empty → `Unavailable/NoCommonCodec`. If the HEVC session fails to start, fall back to H.264 (new epoch).
- **Size:** the active area in phone pixels (SurfaceInfo size × ActiveArea), capped at the source's pixel size, the phone's `maxWidthPx/maxHeightPx` and 3840×2160, keeping the aspect ratio. Width and height are rounded down to even numbers. `fps = min(source refresh, maxFps)` for Auto, also `≤ MirrorFps` when a limit is set, and never above the encoder's capability at that size; lowered further by the input guard. Source refresh = the target display's current refresh rate (All displays: the highest among them; a variable-rate display: its maximum).
- **KeyframeRequest:** the Mac encodes the next frame as a keyframe. It honours at most one request per 250 ms (later ones within the window are folded into it) and drops requests for an old epoch.
- **MirrorReport:** every 500 ms while streaming; the Mac uses it for the input guard and the latency log. The Mac drops reports beyond 4 per second.
- **Bye**, takeover, session end: the video socket stops and the per-session keys are discarded.

## Key schedule (additions)

- `K_vs` (Mac → phone video) = HKDF-SHA256(`K_dev`, salt = clientNonce ‖ serverNonce, info `pb-video-s2c-v1` ‖ sessionId).
- `K_vc` (phone → Mac probes) = the same with info `pb-video-c2s-v1`.
- Nonce = `u32 sessionId` ‖ `u64 sequence`, per key. Each direction has one sequence for the whole session, starting at 1, strictly increasing, never reset across epochs. `K_in` is unchanged.

## Video datagram (UDP, its own socket on both sides)

- **Header (24 B, also the AEAD associated data):** `u8 version(1)` ‖ `u8 kind` ‖ `u8 epoch` ‖ `u8 flags` ‖ `u32 sessionId` ‖ `u64 sequence` ‖ `u32 frameNumber` ‖ `u16 fragmentIndex` ‖ `u16 fragmentCount`.
- **Body:** AES-256-GCM ciphertext + 16 B tag.
- **Max datagram size:** 1200 B (so the payload is ≤ 1160 B).
- **kind 1 — VideoFragment (Mac → phone, `K_vs`):** `flags` bit 0 Keyframe (on every fragment of a keyframe). The first fragment's payload starts with `u32 captureTimeUs` (Mac session clock, the server's Ping clock) ‖ `u32 frameLength`, then the bytes; later fragments are bytes only. A frame is one Annex-B access unit; keyframes start with VPS/SPS/PPS (HEVC) or SPS/PPS (H.264). `fragmentCount` 1..1024, `fragmentIndex < fragmentCount`, `frameLength` ≤ 1 MiB and must equal the reassembled size; `frameNumber` strictly increases within an epoch.
- **kind 2 — Probe (phone → Mac, `K_vc`):** `frameNumber`, `fragmentIndex`, `fragmentCount` = 0; payload empty. Sent from the phone's video socket to the Mac's `videoUdpPort` right after each MirrorConfig and every 1 s while streaming.
- Unknown `version` or `kind`, an inconsistent header, an epoch other than the current one, or a failed tag → drop the datagram silently (counted).

**Endpoint rules (security):**
- The Mac sends video only to the source endpoint of the latest authenticated Probe of this session, and only while the phone asked for video. Before the first Probe nothing is sent. Re-pinning follows the input rule (a new source whose Probe authenticates and carries a higher sequence). Forged datagrams count against the existing forged-datagram budget (skill `net-engine`).
- The phone accepts video only from the Mac's address and `videoUdpPort` in the MirrorConfig.

**Reassembly (phone):** at most 4 frames in flight, a 2 MiB pool in total, no per-datagram allocation. A frame is dropped when a newer frame completes before it, or 100 ms after its first fragment. After a dropped or out-of-order frame the phone feeds the decoder nothing until the next keyframe, and sends a KeyframeRequest (repeated every 250 ms until a keyframe arrives).

## QoS (ADR-0038 "input always wins")

- **DSCP:** the phone's input socket TOS `0xB8` (EF). Video sockets on both sides `0x88` (AF41); on the Mac also `SO_NET_SERVICE_TYPE = NET_SERVICE_TYPE_VI`, and the Mac input socket `NET_SERVICE_TYPE_VO`. Core exposes a per-socket hook (`Action<Socket>` on the server and client options) so heads set platform options; the cross-platform TOS is set in Core. A failure to set them is logged once and ignored.
- **Threads:** the Mac input receive → inject path runs at the highest priority (user-interactive QoS). Capture callbacks, encode and the video send loop run below it (user-initiated or lower). On Android, `PadSendQueue` stays above the video receive and decode threads.
- **Pacing (Mac):** a frame's fragments go out spread over at most half the frame interval, through a token bucket at 1.5 × the target bitrate. A keyframe may take up to one full interval.
- **Input guard (Core `MirrorRateController`):**
  - Inputs: the server's Ping RTT samples; gaps between input datagrams while the pen or a touch is in contact (the phone's 50 ms keepalive rule makes > 30 ms a gap); video loss from MirrorReport.
  - Baseline: the minimum RTT over the last 10 s.
  - Degraded: RTT > baseline + 8 ms over 2 consecutive samples, an input gap > 30 ms during contact, or fragment loss > 2 % over a report.
  - Ladder, one step per 500 ms while degraded: bitrate × 0.7 down to the preset floor, then fps halves (e.g. 120 → 60 → 30, never below 30), then pause video (`Throttled`).
  - Recovery: after 2 s healthy, one step back up every 1 s (fps first back up to the planned rate, then bitrate × 1.15 up to the preset).
  - Pure logic with an injected `TimeProvider`; each step yields a new MirrorConfig.
- **Quality presets** (`MirrorQuality`, at 60 fps; scaled by fps: × (fps / 60)^0.6, so 30 fps ≈ × 0.66 and 120 fps ≈ × 1.5; scaled by pixel count relative to 2.5 MP, clamped 2–40 Mbps; H.264 × 1.4):

| Preset | Target | Floor |
|---|---|---|
| Low | 6 Mbps | 2 Mbps |
| Balanced | 12 Mbps | 3 Mbps |
| High | 24 Mbps | 6 Mbps |

## Core API (contract between Core and the heads)

New folder `src/PenBridge.Core/Mirror/` (namespace `PenBridge.Core.Mirror`), plus protocol and Net additions:

- **Protocol/Security:** the six messages above in `Protocol/Messages/MirrorMessages.cs`, the setting keys 30–32, `VideoDatagram` (header encode/decode), `VideoCipher` (like `InputCipher`, one per key and direction), and the `K_vs`/`K_vc` derivation in `KeySchedule`.
- **Mirror (pure logic):** `MirrorStreamPlanner` (codec choice, size, fps and bitrate from the inputs above → a `MirrorStreamConfig` record), `MirrorRateController` (input guard), `VideoFragmenter` (access unit → datagrams into a caller buffer), `VideoPacer` (send times), `FrameReassembler` (datagrams → complete access units from a pool; drop and keyframe-needed signals).
- **Server (Mac):**
  - `PenBridgeServerOptions.ScreenMirror`.
  - `PenBridgeServer.MirrorRequested(MirrorRequest)` / `MirrorStopped` events: the head starts or stops capture.
  - `PenBridgeServer.MirrorConfigChanged(MirrorStreamConfig)` event: the head reconfigures its encoder.
  - `PenBridgeServer.KeyframeRequested` event.
  - `PenBridgeServer.SetMirrorState(MirrorState, MirrorReason)`.
  - `PenBridgeServer.SendVideoFrame(ReadOnlySpan<byte> accessUnit, bool keyframe, long captureTimeUs)`: thread-safe and non-blocking. It fragments, encrypts and paces on a Core-owned send thread below input priority, and drops the frame when the previous one hasn't finished sending.
  - Core runs the input guard and the planner and raises MirrorConfigChanged itself; the head only reports capability: `SetMirrorEncoders(codecs)` and `SetMirrorSource(pixelSize, refreshHz)` for the current target.
- **Client (phone):**
  - `PenBridgeClient.StartMirror(MirrorRequest)` / `StopMirror()`.
  - Events: `MirrorConfigReceived(MirrorStreamConfig)`, `MirrorStateChanged(MirrorState, MirrorReason)`, `VideoFrameReady(VideoFrame)`. A `VideoFrame` is pooled; the head must `Dispose` it after queueing it to the decoder. The client sends Probes, KeyframeRequests and MirrorReports itself; the head reports renders via `ReportFrameRendered(frameNumber, renderedAtUs)`.
  - `PenBridgeClient.NowUs`: the client's Ping clock (µs), the clock `renderedAtUs` uses.
- **Thread hooks (both sides):** every thread Core starts for mirroring (video receive/send) calls an optional `Action<MirrorThreadRole>` from the server/client options on that thread when it starts, so a head can set its priority with platform APIs (Android `Process.SetThreadPriority`, the Mac's QoS class). `Thread.Priority` is not enough: it has no effect on Android. Core also sets `Thread.Priority` below normal on these threads as a portable default.

## Heads

- **Mac:**
  - Capture: ScreenCaptureKit `SCStream`. A display/region/window target is one stream with `sourceRect` = the mapped rect in display points and output size = the stream size. Window = its clipped screen rect, so overlapping windows show as on screen. All displays = one stream per display, composited on the GPU into the bounding box. `showsCursor = true`. The content filter excludes PenBridge's own app. Pixel format NV12, queue depth 3. `minimumFrameInterval = 1/fps` (fps from MirrorConfig, up to the display's refresh). The head reports each target display's refresh rate to Core (`SetMirrorSource(pixelSize, refreshHz)`) and on change.
  - Encode: VideoToolbox `VTCompressionSession`, hardware only, real-time, `AllowFrameReordering = false`, `MaxKeyFrameInterval` very large (keyframes on request), average bitrate from MirrorConfig, Annex-B output with parameter sets on keyframes.
  - Permission: Screen Recording (`CGPreflightScreenCaptureAccess` / `CGRequestScreenCaptureAccess`), requested the first time mirroring is enabled, with the same guidance pattern as Accessibility. Denied → `Unavailable/PermissionDenied`.
  - UI: Settings → a Mirroring tab (switch, quality, fps, permission status) and a menu-bar "Mirror screen" item.
- **Android:**
  - Decode: `MediaCodec` (async, hardware, low-latency where supported: `KEY_LOW_LATENCY` / vendor keys) into a `SurfaceView` placed under the existing `DrawSurfaceView`, positioned on the active area rect. Supported codecs come from `MediaCodecList` (hardware only). Render each frame as soon as it's decoded (no presentation-time scheduling). `maxFps` = the display's highest supported refresh rate (`Display.SupportedModes`); while mirroring at > 60 fps the activity asks for that rate (`preferredDisplayModeId` / `Surface.setFrameRate`).
  - Lifecycle: MirrorStart/Stop with foreground and screen state. State overlay for Paused and Unavailable reasons.
  - Settings: a quick toggle on the pad (writes `MirrorEnabled`) plus the Mac section's quality and fps.
- **SimClient:** `mirror [--seconds N] [--out file.h264|.hevc] [--codecs h264,hevc]` requests a stream, reassembles it, writes the Annex-B bytes and prints frames, drops, keyframe requests and latency. Combined with `draw` it can check the input path while video streams.

## Tests

- Core: golden bytes and round trips for every new message and the video header; truncated, oversized and garbage input; capability gating (a message without `ScreenMirror` → protocol error); key derivation vectors; nonce monotonicity; endpoint pinning and Probe auth; planner size/codec cases; rate-controller ladder and recovery with a fake clock; fragmenter ↔ reassembler with loss, reorder, duplicates and epoch change; zero allocations on fragment, seal, open and reassemble (`Alloc.AssertZero`); loopback end-to-end: SimClient-style client gets frames, and a loss triggers a KeyframeRequest.
- Devices: S23 + Mac. Pen latency p95 with mirroring on vs off (`scripts/latency-report.sh`), video latency from MirrorReport, alignment at the active-area edges, every target type including All displays with two displays, quality and fps changes, permission denied, pause, background/resume.
