# Phase 2 — Mac server spec

Epic #11. Scope: the `src/PenBridge.Mac` head (net10.0-macos, AppKit), a thin layer on top of `PenBridge.Core.Net.PenBridgeServer`.
- **Applies:** ADR-0003 (CGEvent injection), ADR-0005 (absolute mapping), ADR-0008–0013.
- **In scope:** pen input. Two-finger gestures are **Phase 4** (#13): the Phase 2 sink accepts and ignores `GestureEvent`s.

## Folders (one workstream each)

| Folder (`src/PenBridge.Mac/…`) | Workstream | Wave |
|---|---|---|
| `Input/` | P2-A Injector + display mapping | 1 |
| `Services/` | P2-B Platform services + ServerHost | 1 |
| `Shell/` (+ `AppDelegate.cs`, `Main.cs`) | P2-C Menu-bar shell | 2 |
| `Pairing/` | P2-D Pairing window | 2 |
| `PenBridge.Mac.csproj`, scripts | P2-E NativeAOT + memory | 3 |

Wave 2 consumes the wave-1 public types. Wave 3 runs after everything else has merged.

## P2-A — `CGEventTabletInjector : IInputSink`

- **Proximity:** a `ProximityEnter` or `ToolChanged` event posts `kCGEventTabletProximity`.
  - Fields: enter/leave, pointer type (pen = 1, eraser = 3), and a stable device and pointer id. The capability mask declares pressure, tilt and rotation.
  - `ProximityLeave` posts the matching leave.
- **Motion:** each pen event posts a mouse event at the mapped point, `kCGMouseEventSubtype = kCGEventMouseSubtypeTabletPoint`.
  - Fields: `kCGTabletEventPointPressure` (0..1), tilt X/Y (−1..1: ±60° maps to ±1, clamped, which is the Wacom/Qt convention and the S Pen's physical range; the Y sign is the `TiltYSign` constant, verified in Krita), rotation, and `kCGMouseEventPressure`.
  - Event types: `Move` → `MouseMoved`, `Down` → `LeftMouseDown`, `Drag` → `LeftMouseDragged`, `Up` → `LeftMouseUp`.
  - The event timestamp comes from `PenEvent.TimestampUs`, mapped to nanoseconds on `CLOCK_UPTIME_RAW` (the same clock as `Stopwatch`; no mach timebase conversion).
- **Side button (Wacom default):** while hovering, `ButtonDown(Primary)` posts `RightMouseDown` at the current point and `ButtonUp(Primary)` posts `RightMouseUp`. Secondary is reserved and ignored. Button mapping becomes configurable in Phase 5.
  - **Right-drag (ADR-0022, #90):** if the side button is held at contact-down (a right-down is posted, or the Down sample's buttons include Primary), the stroke is a right-button stroke: `Down` posts `RightMouseDown` if needed (never `LeftMouseDown`), `Drag` posts `RightMouseDragged` and `Up` posts `RightMouseUp`. Pressure, tilt and the tip bit are on these events too.
  - The choice is latched at contact-down: pressing or releasing the side button during contact posts nothing, in either kind of stroke. This is decided in Core's `TabletEventPlanner` (`Plan(..., held)`, `TabletPostedState.RightStroke`).
- **Safety:** releasing on `ProximityLeave`, disconnect or pause posts any outstanding up/right-up, so nothing stays stuck.
- **Mapping:** `DisplayTargetResolver` lists displays (`CGGetActiveDisplayList`) with their UUID (`CGDisplayCreateUUIDFromDisplayID`).
  - The default is the main display. The user's choice is remembered by UUID and falls back to main when that display is missing.
  - `CGDisplayBounds` (global points) feeds Core's `CoordinateMapper` together with SurfaceInfo.
  - It reacts to display reconfiguration (`CGDisplayRegisterReconfigurationCallback`) with no polling.
- **Accessibility:** check `AXIsProcessTrusted()` before posting. When it's false, drop the events, raise an `AccessibilityMissing` state and log once.
- **Hot path:** no allocations per event; reuse CGEvent sources. `CGEventCreate*` calls are unavoidable, so release every event promptly.

## P2-B — Services

- `KeychainSecretStore : ISecretStore`: generic-password items, service `com.blake11.penbridge`, account = key. `kSecAttrAccessible` = after first unlock, device-only. Note that this attribute only takes effect in the data-protection keychain; in the file-based login keychain used by unsandboxed apps it's ignored. The items are never synchronizable. It holds the server PFX, device keys and the registry.
- `BonjourAdvertiser`: `NSNetService` `_penbridge._tcp` on the bound port, with the TXT record from Core (`v`, `fp`, `name` = the Mac's computer name). It re-publishes on name/port change and stops on quit.
- `OsLogLoggerProvider`: `ILogger` → `os_log`, subsystem `com.blake11.penbridge`, one category per type. Sample-path logging is forbidden.
- `SettingsStore` (`NSUserDefaults`): target display UUID, allow takeover, paused, run-at-startup mirror.
- `ServerHost`: owns the `PenBridgeServer` lifecycle (start on launch, stop on quit), holds the injector as its sink, and does this platform wiring:
  - **sleep/wake:** stop on `NSWorkspace.WillSleep`, restart on `DidWake`;
  - **network changes:** watch with `NWPathMonitor` and re-advertise;
  - **pause:** stop delivering input to the injector (releasing first) while keeping the session connected.

  It exposes an observable state for the shell: listening port, IPs, connection (device name) / idle, paused, AccessibilityMissing, stats, paired devices, and Forget.
- `LoginItem`: `SMAppService.MainApp` register/unregister/status.

## P2-C — Menu-bar shell

- **Icon states:** SF Symbols, template images. Idle `pencil.tip`, connected `pencil.tip.crop.circle.fill`, paused `pause.circle`, warning `exclamationmark.triangle`.
- **Menu** (rebuilt on `menuWillOpen`, no timers):
  1. Status line ("Listening on 192.168.1.20:47820", or "Connected: Galaxy S23 Ultra").
  2. Stats, shown when connected (RTT ms, packets/s, UDP/TLS).
  3. ⚠️ "Grant Accessibility…" when missing. It opens `x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility`.
  4. "Pause input", a toggle showing ⌃⌥⌘P.
  5. "Pair new device…".
  6. "Paired devices ▸": one entry per device (name, last seen), each with "Forget".
  7. "Display ▸": the active displays, checkmark on the target.
  8. "Allow takeover", a toggle.
  9. "Run at startup", a toggle.
  10. "Open logs": opens Console.app (or runs `log stream` in Terminal) filtered to the subsystem.
  11. "Quit PenBridge".
- **Global hotkey:** ⌃⌥⌘P via Carbon `RegisterEventHotKey`, which needs no Accessibility or Input Monitoring permission. It toggles pause and briefly flashes the icon.

## P2-D — Pairing window

- A small, non-resizable floating `NSPanel` (level floating, joins all spaces), centered.
- Contents: the QR code generated by `CIFilter qrCodeGenerator` from `server.CreateQrPayload()` and scaled with nearest-neighbor; the PIN in large monospaced digits; host IP(s):port; a countdown ring or label for the 2 min window; device-name instructions.
- Opening the panel calls `OpenPairingWindow()`, closing it calls `ClosePairingWindow()`. It closes itself on successful pairing ("Paired with <name>" for 1.5 s) and regenerates on expiry or lockout (it shows the backoff wait).

## P2-E — NativeAOT and memory

- Enable NativeAOT for the Mac head (Release), with full trimming. Fix or justify every warning.
- Measure idle RSS (`footprint` / `vmmap --summary`) at launch, connected-idle and after 10 min of streaming via SimClient.
- **Target:** ≤ 60 MB idle. If NativeAOT is blocked, use trimmed JIT and record the numbers.
- Document the results in the mac-build-run skill.

## Verification

- **Without a phone:** `SimClient pair --host 127.0.0.1 --pin <PIN shown in the pairing window>`, then `draw circle`, `pressure-ramp`, `tilt-sweep`, `hover`, `button-click`.
- **User checks** (device or app):
  - strokes with pressure and tilt in Krita;
  - right-click from the side button;
  - eraser switching;
  - the menu states;
  - run at startup across a reboot;
  - the hotkey;
  - sleep/wake reconnect.
- Each workstream PR lists what it verified and what needs a user check.
