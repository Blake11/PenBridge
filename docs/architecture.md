# PenBridge — Architecture

PenBridge turns a phone (primary target: Samsung Galaxy S23 Ultra with S Pen) into a drawing tablet for macOS over the local network.

```
┌──────────────── Phone (Android, iOS later) ───────────────┐        ┌────────────── Mac (menu-bar server) ──────────────┐
│ DrawSurfaceView ─► InputSampler ─► PenBridge.Core client  │  LAN   │ PenBridge.Core server ─► InputRouter ─► IInputInjector │
│  (MotionEvent: stylus/finger/hover)   (protocol, crypto)  │◄──────►│  (TLS control + UDP input)        (CGEvent tablet) │
│ NsdManager discovery, QR/PIN pairing                       │        │ NSStatusItem menu, Bonjour advert, SMAppService    │
└────────────────────────────────────────────────────────────┘        └────────────────────────────────────────────────────┘
```

## Projects

| Project | TFM | Role |
|---|---|---|
| `src/PenBridge.Core` | `net10.0` | Platform-free: wire protocol, serialization, session crypto, pairing state machine, coordinate mapping, gesture recognition, discovery/transport abstractions. No platform APIs. |
| `src/PenBridge.Mac` | `net10.0-macos` | Menu-bar server (AppKit bindings). `LSUIElement=true` (no Dock icon). |
| `src/PenBridge.Android` | `net10.0-android` | Native .NET for Android client app. |
| `src/PenBridge.iOS` | `net10.0-ios` | **Placeholder only** — empty app that references Core. Not implemented. |
| `tests/PenBridge.Core.Tests` | `net10.0` | xUnit v3 tests for Core (runs on any OS, no workloads). |
| `tools/PenBridge.SimClient` | `net10.0` | *(Phase 1)* Console fake phone: pairs and streams scripted strokes, so the Mac server can be tested without a phone. |

Rule: platform apps contain only platform glue; anything testable without a device goes into Core.

## Platform minimums

- .NET 10 LTS (SDK pinned in `global.json`, roll forward latest feature). Upgrade to .NET 11 after GA.
- macOS 14+ (SMAppService, modern AppKit). Apple Silicon + Intel.
- Android: minSdk 29, targetSdk latest stable. Primary test device S23 Ultra.
- iOS 17+ (placeholder).

## macOS server

- **Shell**: `NSApplication` with `ActivationPolicy.Accessory`, `NSStatusItem` icon in the menu bar. Menu: status (listening / connected device), Pair new device… (shows PIN + QR), Display ▸ (target display, All displays), Pen / Mouse mode, Settings… (⌘,), Quit. The **Settings window** (ADR-0030–0033) has Display (target, region, aspect lock), Pen (mode, mouse speed and acceleration, side button, tip feel, click threshold, full pressure, live test pad), Gestures (Smart Zoom, pointer and scroll speed, toggles) and General (run at startup, allow takeover) tabs. Settings are global and stored in `NSUserDefaults`.
- **Run at startup**: `SMAppService.MainApp.Register()/Unregister()`.
- **Resource budget**: idle ≈ 0% CPU and a small RSS (goal < 40 MB). No polling; everything is socket/event-driven. Trim the app; evaluate NativeAOT for the macOS head.
- **Input injection** (`IInputInjector`, ADR-0003):
  - MVP: `CGEventTabletInjector` posts a `kCGEventTabletProximity` event (pen/eraser enter and leave), then mouse down/drag/up/move events with the `TabletPoint` subtype carrying pressure, tilt X/Y and rotation. It needs the **Accessibility** permission (check `AXIsProcessTrusted`, then guide the user to grant it).
  - Later (only if compatibility gaps appear): a DriverKit virtual HID digitizer (C++ dext, Apple entitlements, paid account).
- **Mapping** (ADR-0031): the target is one display (`CGDisplayBounds`) or all displays, optionally a dragged sub-region of a display. The phone's **active area** is the largest rectangle with the target's aspect ratio, centred on the phone surface; it maps onto the whole target, and a pen outside it is pinned to the nearest edge. The Mac sends the active area to the phone (`ActiveArea`). The math lives in Core.
- **Pen settings** (ADR-0032), applied in Core before injection: Pen (absolute) or Mouse (relative, own speed and acceleration) stylus mode, the side-button action (right, middle, eraser while held, double click, none; latched at contact-down) and the pressure curve (tip feel, click threshold, full pressure).
- **Gestures** (ADR-0023): trackpad parity, with three-finger drag on. Core recognizes the gestures and the Mac only posts them, at the current cursor position, which stays put. 2 fingers: scroll with acceleration and momentum (continuous phased scroll-wheel events, so swipe-between-pages works; direction follows the natural-scrolling setting). Pinch and rotate combine into one transform (magnify/rotate gesture events, undocumented CGEvent fields, frontmost app only). Tap = secondary click, double tap = smart zoom. 3 fingers: drag. 4 fingers: Mission Control / App Exposé / Spaces / Launchpad / show desktop, interactive through Dock gesture events if feasible, otherwise the system shortcuts. Pen in range, the watchdog, pause or disconnect cancel a gesture and its momentum.

## Android client

- `DrawSurfaceView` (custom `View`) is full-screen immersive and keeps the screen on. It calls `RequestUnbufferedDispatch` for low latency.
- It consumes `OnTouchEvent`, `OnHoverEvent` and `OnGenericMotionEvent`, including **historical samples** (`GetHistoricalX/Y/AxisValue`) to keep the full ~240 Hz S Pen rate.
- **Stylus** (`ToolType.Stylus`/`Eraser`): pressure, `Axis.Tilt` + `Axis.Orientation` → tilt X/Y. Hover → proximity + move. `ButtonStylusPrimary` → configurable action (default right-click while hovering; held at contact-down the stroke is a right-drag, ADR-0022; eraser and others are options).
- **Fingers**: pen-first. While the stylus is in proximity or touching, finger/palm input is ignored, and canceled pointers (`FLAG_CANCELED`) are dropped; a finger already sent stays reported as not in contact until it lifts, so the Mac cancels a gesture instead of reading a lift (ADR-0024). Without a stylus, one finger moves the cursor relatively, like a trackpad: a tap clicks at the cursor, and tap-and-a-half drags. The phone sends it as touches and Core's recognizer handles it (ADR-0026). Against a peer without `FingerTouches`, the cursor is absolute (hover): a quick tap clicks and double-tap-and-hold drags (ADR-0022). Settings can switch it to drawing (fixed pressure). Two to four fingers produce trackpad-style gestures, recognized in Core (ADR-0023).
- **Surface** (ADR-0031): corner ticks at the active area's corners, dimmed margins outside it, an optional centre cross; background and marker opacity are phone settings.
- **Settings**: phone-only settings (orientation, finger mode, palm rejection, surface look) in SharedPreferences; with `RemoteSettings`, a "Mac" section edits the Mac's settings (ADR-0030).
- **Discovery**: `NsdManager` browses `_penbridge._tcp`. **Pairing**: scan the QR code, or pick the discovered Mac and type its PIN. **Advanced ▸ Connect manually**: enter host:port + PIN, for networks without mDNS and for the emulator (ADR-0008).

## Networking & protocol (ADR-0004)

- **Discovery**: Bonjour/mDNS service `_penbridge._tcp`, advertised by the Mac. The TXT record has the protocol version, device name and cert fingerprint.
- **Control channel**: TCP + TLS 1.2+ (`SslStream`, highest negotiated; ADR-0013), `NoDelay`. The Mac generates a self-signed identity on first run and stores it in the Keychain.
  - Carries hello/version negotiation, pairing, settings, heartbeats, and the session key for the input channel.
  - **Settings sync** (ADR-0030, protocol 1.2, capability `RemoteSettings`): each setting lives on the side that applies it. The Mac sends a `SettingsSnapshot` and a `DisplayList` after SessionStart and on every change; the phone sends `SettingsUpdate`, and the Mac answers with a new snapshot. With `ActiveArea` the Mac also sends the active area after each SurfaceInfo or mapping change.
  - Messages are length-prefixed, with a type byte and a compact binary body. They are low-frequency.
- **Pairing**:
  - The Mac shows a 6-digit PIN and a QR code. The QR holds host, port, cert fingerprint and a one-time token.
  - The phone pins the Mac's cert fingerprint. The Mac stores the paired device id and its key.
  - Unpaired clients are rejected, because a connected device controls the mouse.
- **Input channel**: UDP datagrams encrypted with AES-256-GCM, using a per-session key from the control channel. Each datagram has a sequence number and a batch of samples. A sample is a fixed binary struct (normalized x/y, pressure, tilt, tool, contact/hover state, buttons, timestamp).
  - Every packet carries the full pen state, so a lost packet is corrected by the next one. Old sequence numbers are dropped (latest wins).
  - Safety: if the pen is down and no packet arrives for 150 ms, the server issues pen-up.
  - Fallback: if UDP is blocked, input goes over the TLS channel.
- **Versioning**: the protocol version is in the hello and the TXT record. A major mismatch is refused with a clear message on both sides; minor versions interoperate, with new features gated by capability bits (protocol 1.1 adds `SampleDedupe`: a lost one-datagram finger tap is recovered by resending its frame, ADR-0025, and `FingerTouches`, ADR-0026; protocol 1.2 adds `RemoteSettings` and `ActiveArea`, ADR-0030, ADR-0031).
- **Wire contract**: the exact message table, datagram layout and key schedule are in `docs/specs/phase-1-core.md`, with all integers big-endian.
- **Serialization**: hand-written `BinaryPrimitives`/`Span<T>`, with no reflection. It must stay trim/AOT-safe and allocation-free on the hot path.
- **Diagnostics (latency)**: off by default; enabled with `PENBRIDGE_LATENCY_LOG=1` or `scripts/latency-report.sh --enable` (the `PenBridgeLatencyLog` user default), then restart the Mac app.
  - The server (`PenBridge.Core.Diagnostics`) records each input frame's `timeUs`, arrival and inject time, the phone's Pings and its own Ping round trips into a preallocated ring buffer. Nothing is logged per frame.
  - Dumps go to `~/Library/Logs/PenBridge/latency-*.tsv` (0600) at session end, on SIGUSR1 and on quit. `scripts/latency-report.sh` prints p50/p95/max and the counts.
  - What's measured: phone `SendState` → the Mac injector returned. The phone's own dispatch (MotionEvent → `SendState`) isn't on the wire, so it isn't included.
  - The clock offset is min(one-way Ping) − minRTT/2, with a drift fit, so no protocol change is needed. Its error is within ±minRTT/2, reported per session.

## Roadmap

| Phase | Scope |
|---|---|
| 0 | Repo bootstrap: Claude setup, skills, conventions, CI/CD skeleton, toolchain *(this session)* |
| 1 | Core: protocol, serialization, crypto session, pairing state machine, mapping, gesture recognizer + tests; `SimClient` |
| 2 | Mac server: menu bar, run-at-startup, Bonjour, TLS/UDP server, pairing UI, Accessibility flow, CGEvent tablet injector |
| 3 | Android client: discovery, pairing (QR + PIN), drawing surface, S Pen pressure/tilt/hover/button, finger fallback *(done, released 1.0.0)* |
| 4 | Gestures with trackpad parity (ADR-0023): 2-finger scroll/momentum/pinch/rotate/tap/smart zoom/swipe pages, 3-finger drag, 4-finger system gestures |
| 5 | Settings (ADR-0030–0033): display/region/all-displays mapping with the aspect-fit active area, Pen/Mouse mode, side button, pressure curve, gesture settings, phone surface look; Mac settings editable from the phone (protocol 1.2) |
| Later | **Android UX improvements** ([#95](https://github.com/Blake11/PenBridge/issues/95)): scope to be defined |
| Later | **Windows host app** (tray app, `InjectSyntheticPointerInput` pen injection, ADR-0015), Mac screen mirroring onto the phone (video stream), DriverKit virtual digitizer, iOS client, S Pen Air Actions, Developer ID signing + notarization, Google Play |

MVP = phases 1–5. **Each phase ends with a release** (ADR-0021): an ad-hoc signed `.dmg`/`.zip` and a release-signed APK on GitHub Releases. Phase 3 is **1.0.0**; each later phase bumps the minor version (1.1.0, 1.2.0 …), and bug-fix releases between phases bump the patch.
