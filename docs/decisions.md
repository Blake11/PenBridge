# Architecture Decision Records

Add new ADRs at the bottom and a line to the index below (skill `adr`). Until the release that contains an ADR ships, it may be amended in place with a `**Amended YYYY-MM-DD (#n):** …` line under its status. Once released it is append-only: never rewrite it, supersede it with a new ADR.

Cite ADRs by number (and section) rather than reading this whole file.

## Index

| ADR | Title | Status |
|---|---|---|
| 0001 | .NET 10 LTS, native platform heads + shared Core | accepted |
| 0002 | Phone shows a blank pad; mirroring later | accepted |
| 0003 | Mac input via CGEvent tablet events behind IInputInjector | accepted |
| 0004 | Bonjour discovery, PIN/QR pairing, TLS control + encrypted UDP input | accepted; TLS version refined by 0013 |
| 0005 | Absolute mapping to one configurable display | accepted; refined by 0031 |
| 0006 | Input feature scope for MVP | accepted; finger fallback amended by 0022 |
| 0007 | Distribution and repo conventions | accepted; versioning refined by 0021 |
| 0008 | Manual host:port + PIN connection as a user-facing fallback | accepted |
| 0009 | PIN/QR pairing via J-PAKE (BouncyCastle), bound to the Mac's TLS certificate | accepted |
| 0010 | Many paired devices, one active session | accepted |
| 0011 | Fixed default port 47820 with fallback | accepted |
| 0012 | Phase 1 Core includes the cross-platform network engine | accepted |
| 0013 | Control channel accepts TLS 1.2+ | accepted; supersedes part of 0004 |
| 0014 | Issue-driven planning and bug tracking | accepted |
| 0015 | Windows host app as a later phase | accepted (scope only) |
| 0016 | Mac server UX defaults for Phase 2 | accepted; amended by 0018, 0022, 0023, 0031, 0032 |
| 0017 | Debug builds store secrets in a local file, not the Keychain | accepted |
| 0018 | NativeAOT Mac Release with a guarded SDK trimming substitution | accepted |
| 0019 | Android client UX defaults for Phase 3 | accepted |
| 0020 | No third-party telemetry: strip ML Kit datatransport | accepted |
| 0021 | Release at the end of every phase; 1.0.0 = Phase 3 | accepted |
| 0022 | Finger move/tap-click and side-button right-drag | accepted; one-finger cursor superseded by 0026 (fallback) |
| 0023 | Trackpad-parity gestures on the Mac | accepted; refined by 0024, 0029 |
| 0024 | A suppressed finger stays on the wire as "not in contact" until it lifts | accepted |
| 0025 | Recover a lost finger tap by resending its frame (protocol 1.1, `SampleDedupe`) | accepted |
| 0026 | One finger moves the cursor relatively, like a trackpad (`FingerTouches`, protocol 1.1) | accepted; refined by 0027, 0028 |
| 0027 | One-finger pointer slop and settle time | accepted; refined by 0028 |
| 0028 | Separate tap windows for one and two fingers | accepted; refined by 0029 |
| 0029 | The two-finger tap follows the macOS Smart Zoom setting | accepted; override added by 0033 |
| 0030 | Settings live where they apply; the phone edits the Mac's over the control channel (protocol 1.2) | accepted |
| 0031 | Target display or all displays, a dragged region, and the active area follows the target's aspect ratio | accepted |
| 0032 | Pen settings: Pen/Mouse mode, side-button action and pressure curve, applied on the Mac | accepted |
| 0033 | Gesture settings: Smart Zoom override, pointer and scroll speed, per-gesture toggles | accepted |

## ADR-0001 — .NET 10 LTS, native platform heads + shared Core
**Status:** accepted (2026-10-13)
- **Decision:** use C# on .NET 10 LTS. The Mac app is `net10.0-macos` (AppKit), Android is `net10.0-android`, and iOS is `net10.0-ios` (placeholder). Each is an independent app sharing `PenBridge.Core` (`net10.0`).
- **Why not MAUI:** Mac Catalyst is a poor fit for a menu-bar-only app. On mobile, MAUI adds abstraction on the hot input path, and stylus handling is native anyway.
- **Why not Avalonia:** a heavier runtime on the Mac, and S Pen specifics would still need native code.

## ADR-0002 — Phone shows a blank pad; mirroring later
**Status:** accepted (2026-10-13)
- **Decision:** the MVP phone UI is a blank input surface, like a Wacom Intuos.
- **Follow-on:** the protocol keeps room (message types, capability flags) for a future Mac→phone video stream.

## ADR-0003 — Mac input via CGEvent tablet events behind IInputInjector
**Status:** accepted (2026-10-13)
- **Decision:** the MVP posts CGEvent tablet proximity events and tablet-point mouse events (pressure, tilt, rotation). It needs the Accessibility permission but no driver, entitlement or paid account.
- **Abstraction:** the injector sits behind `IInputInjector`.
- **Deferred:** a DriverKit virtual HID digitizer (C++ dext, Apple-granted HID entitlements, paid account, notarization, system-extension approval). It is revisited only if real apps fail with CGEvent.
- **Rejected:** plain mouse emulation (no pressure).
- **Dev note:** unsigned rebuilds lose the Accessibility grant, so local builds are signed with a stable self-signed/dev certificate.

## ADR-0004 — Bonjour discovery, PIN/QR pairing, TLS control + encrypted UDP input
**Status:** accepted (2026-10-13)
- **Decision:** see the architecture "Networking & protocol" section. Only paired devices may inject input, and all traffic is encrypted.
- **Why UDP for samples:** it avoids TCP head-of-line stalls on Wi-Fi. Every packet carries the full pen state, plus a pen-up watchdog.
- **Why not QUIC:** `System.Net.Quic` needs libmsquic, which isn't shipped on macOS.

## ADR-0005 — Absolute mapping to one configurable display
**Status:** accepted (2026-10-13); refined by ADR-0031 (2026-10-16)
- **Decision:** the phone surface maps to one chosen display, or to a sub-region of it, preserving aspect ratio.
- **Deferred:** relative/trackpad mode.

## ADR-0006 — Input feature scope for MVP
**Status:** accepted (2026-10-13)
- **In:** pen-first input with finger fallback; S Pen pressure, tilt, hover, side-button mapping; two-finger gestures (scroll, pinch).
- **Deferred:** Air Actions (Samsung S Pen Remote SDK).

## ADR-0007 — Distribution and repo conventions
**Status:** accepted (2026-10-13)
- **Distribution:** GitHub Releases, MIT license. There is no paid Apple account yet, so the macOS app ships ad-hoc signed and un-notarized. Notarization steps are prepared but disabled. Android ships as a release-signed APK; the keystore lives in GitHub secrets.
- **Git flow:** `main` with PRs via `gh` and squash-merge. Branches start from fresh `origin/main` and use dashes only. Commits and PR titles follow Conventional Commits (validated in CI). release-please manages versions and the changelog.
- **Naming:** product `PenBridge`, bundle/app id prefix `com.blake11.penbridge`.

## ADR-0008 — Manual host:port + PIN connection as a user-facing fallback
**Status:** accepted (2026-10-13)
- **Context:** Bonjour/mDNS doesn't reach the Android emulator, which sits behind NAT and sees the Mac as `10.0.2.2`. Some Wi-Fi networks (guest, corporate, client isolation) also block multicast.
- **Decision:** the phone app offers **Advanced ▸ Connect manually**: the user enters host/IP, port and the PIN shown on the Mac.
- **Security:** the same pairing as ADR-0004 applies. The PIN authorizes the pairing, the Mac's cert fingerprint is pinned on first pairing, and nothing is weakened.
- **Display:** the Mac's "Pair new device…" window shows its IP address and port next to the PIN and QR code.

## ADR-0009 — PIN/QR pairing via J-PAKE (BouncyCastle), bound to the Mac's TLS certificate
**Status:** accepted (2026-10-13)
- **Context:** the first pairing happens before the phone has pinned the Mac's certificate. With plain PIN-over-TLS, a relay on the LAN could pass the PIN through.
- **Decision:** pairing runs J-PAKE (RFC 8236) over the TLS control channel, using `BouncyCastle.Cryptography` (`Org.BouncyCastle.Crypto.Agreement.JPake`).
  - The shared secret is the 6-digit PIN (manual or discovered path) or the QR one-time token (QR path). It's one flow with two secret sources.
  - The J-PAKE key-confirmation step MACs the Mac's certificate SHA-256 fingerprint as each side sees it. A relay presenting its own certificate therefore fails confirmation.
  - Success yields a 32-byte device key `K_dev`, stored on both sides (platform secret store). The phone pins the fingerprint.
- **PIN policy:** a PIN is valid for 2 min. After 3 failed attempts it's discarded and a new one is shown. Only one pairing attempt runs at a time.
- **Rejected:** SPAKE2 (no maintained .NET implementation; hand-written curve math is too risky) and SRP-6a (an asymmetric client/server design).
- **Consequence:** adds a BouncyCastle dependency. It must stay trim/AOT-clean, and we use only the JPake and digest classes directly.

## ADR-0010 — Many paired devices, one active session
**Status:** accepted (2026-10-13)
- **Decision:** the Mac keeps a registry of paired devices (id, name, `K_dev`, paired-at, last-seen), and the menu can forget one.
- **One active session:** only one authenticated session may inject input at a time. Another device gets `Busy`, unless the user has enabled "allow takeover", in which case the old session is closed with `Bye(TakenOver)`.

## ADR-0011 — Fixed default port 47820 with fallback
**Status:** accepted (2026-10-13)
- **Decision:** TCP (control) and UDP (input) both use 47820. If it's taken, the server tries 47821–47829.
- **Visibility:** the bound port is advertised over Bonjour and shown in the pairing window (ADR-0008).

## ADR-0012 — Phase 1 Core includes the cross-platform network engine
**Status:** accepted (2026-10-13)
- **Decision:** `PenBridge.Core` contains the TLS control server/client, the encrypted UDP input channel, session orchestration and the pairing state machines, all on `System.Net`. They are tested end to end over loopback.
- **Platform heads add only:** discovery (Bonjour/NSD), secret-store adapters (Keychain/Keystore), UI and input injection/sampling.
- **Gesture recognition runs on the receiver (Mac) side**, from raw touch-pointer state carried in every input datagram. Lost packets then can't drop scroll deltas, and the phone stays a thin sampler.

## ADR-0013 — Control channel accepts TLS 1.2+ (supersedes the "TLS 1.3" detail of ADR-0004)
**Status:** accepted (2026-10-13)
- **Context:** on macOS, the .NET `SslStream` server negotiates only TLS 1.2; forcing 1.3 makes the handshake fail. Cipher suites can't be restricted (`CipherSuitesPolicy` is unsupported there).
- **Decision:** the control channel allows TLS 1.2 and 1.3 and negotiates the highest the platform supports.
- **Why this is acceptable:** security doesn't rest on TLS alone.
  - Input datagrams use AES-256-GCM under `K_in` derived from `K_dev`.
  - Reconnect auth is an HMAC under `K_dev` bound to the certificate fingerprint.
  - J-PAKE pairing stays secure even over a compromised channel.
  - TLS adds confidentiality for control messages and server authentication via the pinned fingerprint.
- **Revisit:** when .NET's macOS TLS server supports 1.3; a test asserts the negotiated version so the change is noticed. The alternative, BouncyCastle TLS 1.3, remains available if the threat model changes.

## ADR-0014 — Issue-driven planning and bug tracking
**Status:** accepted (2026-10-13)
- **Context:** phase and workstream state lived only in local, untracked `progress.md` files, and bugs found by agents or reviewers were scattered across reports. Nothing was visible on GitHub or linked from PRs.
- **Decision:** GitHub issues are the plan of record, and implementation is driven from them.
  - Each roadmap phase is an epic issue (`type: epic`) with a milestone of the same name. The epic's task list references its workstream issues.
  - Each workstream gets an issue (goal, owned files, acceptance, tests, dependencies) **before** it's delegated. Subagent prompts carry the issue number.
  - Bugs found by agents or reviewers that aren't fixed in the same PR become `type: bug` issues. Post-MVP ideas are issues with the `later` label and the "Later" milestone.
  - Every PR references its issue: `Closes #N` (or `Refs #N` when it doesn't finish the issue).
  - Labels: `type: *`, `area: *`, `status: in progress`, `status: blocked`, `later` (skill `issues`).
- **Progress files:** local progress files stay as session state and link the issues: the architect owns `progress.md`, and each workstream agent writes only its own `ws-<id>.md`, because concurrent agents editing one shared file lost each other's sections (skill `progress-tracking`).
- **Unchanged:** Conventional Commit messages and PR titles (ADR-0007).

## ADR-0015 — Windows host app as a later phase
**Status:** accepted (2026-10-13), scope only; the design is finalised when the phase starts.
- **Context:** the user wants the phone to work as a tablet for Windows PCs too. The phone client and protocol are host-agnostic.
- **Decision:** add a later phase, "Windows host": a `net10.0-windows` tray app, `src/PenBridge.Windows`, that reuses `PenBridge.Core` unchanged (server engine, pairing, PenStateTracker, GestureRecognizer, CoordinateMapper). Only platform glue is new:
  - **Shell:** a notification-area (tray) icon with the same menu as the Mac server, and no taskbar window. UI tech is decided at phase start: WinForms `NotifyIcon` (lightest) or WinUI 3.
  - **Input injection:** `InjectSyntheticPointerInput` with `PT_PEN` (pressure, tilt, rotation, eraser, barrel button; Windows 10 1809+). It needs no driver and works with Windows Ink-aware apps.
    - Touch gestures are injected as `PT_TOUCH` or mapped to wheel/zoom.
    - This sits behind the same `IInputSink` / injector abstraction as the Mac (ADR-0003).
  - **Discovery:** native mDNS via `DnsServiceRegister` (Windows 10+), advertising `_penbridge._tcp`.
  - **Secrets:** a DPAPI (`ProtectedData`, current user) adapter for `ISecretStore`.
  - **Run at startup:** HKCU `Run` key (unpackaged) or `StartupTask` (MSIX).
  - **TLS:** SChannel supports TLS 1.3, so ADR-0013's concession is macOS-only.
  - **Release:** an MSIX or zip on GitHub Releases, plus a `windows-latest` CI build job. Code signing is deferred, like macOS notarization.
- **Consequence:** platform heads must keep Core free of macOS assumptions: no Apple-specific coordinate conventions in Core, and the display model stays generic.

## ADR-0016 — Mac server UX defaults for Phase 2
**Status:** accepted (2026-10-13); the side-button-while-touching rule is amended by ADR-0022 (2026-10-14); the gestures phase boundary is lifted by ADR-0023 (2026-10-14); the target display is refined by ADR-0031 and the side-button mapping made configurable by ADR-0032 (2026-10-16)
- **S Pen side button:** right-click, following the Wacom side-switch default. Pressing it posts right-down at the current point and releasing posts right-up, both while hovering and while touching. The mapping becomes configurable in Phase 5.
- **Eraser:** the eraser tool is reported as the eraser pointer type in tablet proximity events, so drawing apps switch tools themselves.
- **Pairing UI:** a floating panel. Opening it opens the pairing window (ADR-0009), and it closes on success.
- **Menu:** includes pause input with the global hotkey ⌃⌥⌘P (Carbon `RegisterEventHotKey`, which needs no extra permission), connection stats and Open logs.
- **Target display:** the main display by default. The user's choice is remembered by display UUID and falls back to main.
- **Memory:** NativeAOT plus full trimming; accept ≤ 60 MB idle RSS.
- **Phase boundary:** gesture events are ignored until Phase 4.

## ADR-0017 — Debug builds store secrets in a local file, not the Keychain
**Status:** accepted (2026-10-13)
- **Context:** for code with no Apple Team ID (we sign with a self-signed "PenBridge Dev" certificate), macOS records the binary's cdhash in each Keychain item's partition list. Every recompile therefore triggers an access prompt, even with a stable designated requirement. Custom access objects, partition-list edits and the data-protection keychain don't help without a Team ID (#55).
- **Decision:** Debug builds use Core's `FileSecretStore` under `~/Library/Application Support/PenBridge-Dev/secrets` (0700 directory, 0600 files). Release and distributed builds always use the Keychain.
- **Consequences:**
  - Debug and Release keep separate identities and pairings, so a phone paired with a Debug build has to pair again with Release.
  - Debug-build secrets rely on file permissions only, which is acceptable for local development.
- **Revisit:** if a paid or free Apple Development certificate with a Team ID is adopted, the Keychain can be used for Debug too.

## ADR-0018 — NativeAOT Mac Release with a guarded SDK trimming substitution
**Status:** accepted (2026-10-13). Refines the memory line of ADR-0016.
- **Decision:** the Mac **Release** app is published with NativeAOT and full trimming. Debug and `dotnet build` stay JIT.
- **Substitution:** `src/PenBridge.Mac/Trimming.Substitutions.xml` stubs `ObjCRuntime.Dlfcn::dlopen(string,int32)`.
  - Its only callers are in `NSObject..cctor`, which eagerly dlopens about 45 frameworks into fields that are never read. That costs about 28 MB of RSS.
  - Every framework the app actually uses is linked as an `LC_LOAD_DYLIB` dependency, and the Carbon and dns_sd P/Invokes don't go through `Dlfcn`.
- **Guard:** a CI check (via `scripts/check-bundle.sh`) fails the build if a workload update adds a `Dlfcn.dlopen` caller outside `NSObject..cctor`, or if any of the stored fields becomes read. The substitution then has to be re-validated.
- **Result:** idle RSS is 50–62 MB (target ≤ 60 MB), footprint about 20 MB, and launch time 168 ms (JIT: 205 MB / 164 MB / 771 ms).
- **Signing:** the AOT signature carries no `allow-jit` entitlement.
- **Revisit:** drop the substitution if the SDK stops eager-loading, or if the guard fires and re-validation isn't worth the effort.

## ADR-0019 — Android client UX defaults for Phase 3
**Status:** accepted (2026-10-13)
- **Orientation:** a user toggle on the pad (lock landscape / lock portrait / auto), landscape-locked by default. Changes apply only between strokes and send a new SurfaceInfo.
- **Ink echo:** a faint local trail of the last ~1 s that fades. It is not the real canvas; Mac screen mirroring is a later phase.
- **Startup:** auto-connect to the most recently used paired Mac once it's discovered. Otherwise show the Mac list (discovered + paired + Pair new + Connect manually).
- **Pad chrome:** a tiny dim status dot (connected / UDP or TLS / reconnecting). Tap it or swipe in from the edge for a small menu; system Back leaves the pad.
- **QR scanning:** CameraX with Google ML Kit barcode scanning, **bundled** model (no Play Services dependency). CAMERA is requested only on "Scan QR".
- **Secrets:** an AndroidKeyStore AES-GCM key encrypting a small file store (`ISecretStore`).
- **Tilt conversion:** `Axis.Tilt` + `Axis.Orientation` → the Core convention (+x right, +y down, centi-degrees), as a pure function in Core with tests.

## ADR-0020 — No third-party telemetry: strip ML Kit datatransport
**Status:** accepted (2026-10-14)
- **Context:** ML Kit barcode scanning (ADR-0019) pulls in Google's `datatransport` runtime. It batches SDK usage events and uploads them to Google through its CCT backend, independent of Play Services and of anything the user agreed to.
- **Decision:** PenBridge sends no third-party telemetry. The Android manifest removes the datatransport components with `tools:node="remove"`: `TransportBackendDiscovery` (so no backend is registered), `JobInfoSchedulerService` and `AlarmManagerSchedulerBroadcastReceiver`. ML Kit only logs "Transport backend 'cct' is not registered"; the bundled model still loads and decodes on-device.
- **Why not an SDK opt-out:** ML Kit has no public switch to disable its usage logging.
- **Why not another scanner:** ZXing-style decoders are less robust on camera frames. The bundled ML Kit model works with the uploads cut at the manifest.
- **Consequences:** a dependency update that adds a new uploader must be caught. Review the merged manifest (`obj/<config>/net10.0-android/android/AndroidManifest.xml`) for `datatransport`, `firebase` or analytics components when bumping ML Kit or CameraX. Applies to every future head and dependency.

## ADR-0021 — Release at the end of every phase; 1.0.0 = Phase 3
**Status:** accepted (2026-10-14). Refines the versioning part of ADR-0007; replaces roadmap Phase 6 ("Release pipeline live"). ADR-0007 never covered a release phase, so it isn't superseded.
- **Context:** the roadmap held every release back for a separate Phase 6 after the MVP (phases 1–5). The pipeline (release-please + `build-release.yml`) already works, and Phase 3 is the first point where a phone draws on a Mac end to end.
- **Decision:**
  - There is no release phase. **Every phase ends with a release**, cut after the phase is verified on devices.
  - **1.0.0** is the release at the end of Phase 3 (Android client). The first release is forced with a `Release-As: 1.0.0` commit footer.
  - Each later phase release bumps the **minor** version (Phase 4 → 1.1.0, Phase 5 → 1.2.0 …).
  - Bug fixes released between phases bump the **patch** version (1.1.1, 1.1.2 …). If `main` already has unreleased `feat` commits, a `Release-As: x.y.z` footer keeps it at patch.
  - A **major** bump (`!` / `BREAKING CHANGE`) needs an explicit user decision; otherwise reword the commit or pin the version with `Release-As`.
- **Why not keep Phase 6:** users would wait for phases 4 and 5 with a working app already on `main`, and the release pipeline would first run under pressure at the end.
- **Consequences:** each phase's Ship step includes the README update and merging the release PR (skill `feature-workflow`). The Phase 6 epic (#15) closes as superseded by #88; its scope moves into #88. The 0.x line is skipped: no 0.2.0 is published.

## ADR-0022 — Finger move/tap-click and side-button right-drag
**Status:** accepted (2026-10-14). Its absolute one-finger cursor is superseded by ADR-0026 (2026-10-15) when both sides support `FingerTouches`, and stays the fallback. Amends the finger-fallback behaviour of ADR-0006 and the side-button-while-touching rule of ADR-0016.
- **Context:** in the S23 test (P3-D), a finger touch pressed the mouse button, so moving the cursor alone was impossible and dragging selected text (#91). The side button right-clicked while hovering but did nothing useful while the tip touched (#90).
- **Decision (finger, #91):** with finger fallback, one finger moves the cursor without pressing (`FingerFallbackMode.Cursor`, the default). Core's phone-side `InputFilter` does it, so the wire format is unchanged and the only Mac change is the click state of finger presses (below):
  - while the finger moves it emits hover samples (in range, no contact), so the client's hover keepalive keeps the cursor alive;
  - a tap (shorter than `TapMaxDuration`, 200 ms, and moving at most `TapSlop`, 2% of the surface) is replayed as a click where it landed: hover, contact, out of range;
  - a touch within `DoubleTapWindow` (300 ms) of a tap and within `DoubleTapSlop` (5%) of it is held until decided. Lifting as a tap clicks again at the first tap's point, so the Mac counts a double-click. Moving beyond the tap slop or staying down for the tap duration starts a contact stroke from where it landed, a left drag until lift;
  - the #68 arming delay, palm rejection, pen suppression and two-finger touches are unchanged. A second finger during any hold makes it a gesture. `FingerFallbackMode.Draw` keeps the old drawing behaviour.
- **Decision (side button, #90):** if the side button is held at contact-down, the stroke is a right-button press/drag/release (RightMouseDown/Dragged/Up with the tablet subtype, pressure and tilt included), like Wacom. The button is **latched at contact-down**: pressing or releasing it during contact posts nothing, in either kind of stroke. Hover right-click is unchanged. This lives in Core's `TabletEventPlanner`, which decides from the held buttons of the Down sample.
- **Why not on the Mac:** cursor mode would need a new tool or a capability, which is a protocol change. Taps on the phone also have the exact touch timing.
- **Click state:** a double-tap-hold drag must reach apps as a plain single-click drag (`kCGMouseEventClickState` 1): with click state 2 a text view selects by word and Finder may open the item instead of dragging it. The Mac injector repeats the click count for finger-fallback presses only at exactly the same point (`TabletEventMath.ClickSlopFor`: slop 0, pens keep 6 pt). The filter snaps a double tap's second click to the first tap's point, and nudges a drag's first sample one unit off that point. So a double tap is click state 1 then 2, and a double-tap drag is 1 then 1. In draw mode, a held tap within `DoubleTapWindow` and `DoubleTapSlop` of the previous tap is replayed at exactly that tap's point, so finger double-clicks a few pixels apart still reach click state 2.
- **Consequences / deferred:** Settings ▸ One finger: "Cursor (tap to click…)" (default) or "Draw", persisted in `SettingsStore`. Button mapping still becomes configurable in Phase 5.

## ADR-0023 — Trackpad-parity gestures on the Mac
**Status:** accepted (2026-10-14). Resolves the open pinch item in `docs/architecture.md` and lifts ADR-0016's "gestures ignored until Phase 4".
- **Context:** Core's `GestureRecognizer` (ADR-0012, Mac side) emits gestures from the raw touches in every datagram, and the Mac injector ignores them. The user wants the phone's fingers to behave **like a macOS trackpad**. macOS has no public API for posting magnify, rotate or Dock gestures.
- **Principle:** match the trackpad's default behaviour with three-finger drag on. The mapping is fixed in Phase 4 and configurable in Phase 5. Only the natural-scrolling direction is read from the system (at each gesture start, never polled). There is no wire change, and the Android pen/one-finger behaviour is unchanged.
- **Cursor:** gestures act at the **current cursor position**, which stays put like on a trackpad. Every posted scroll/gesture event carries that position. Only three-finger drag moves it, relatively.
- **Recognition (Core, platform-free, tested):**
  - **2 fingers:** scroll is exclusive. Pinch and rotate combine into one transform, which starts when the spread change or the rotation arc crosses its threshold and is ≥ the centroid travel. A **tap** is a secondary click, held back by the double-tap window so a **double tap** can be a smart zoom instead.
  - **Scroll acceleration:** a trackpad-like curve. Slow movement stays ~1:1 in mapped points and fast movement scales up. **Momentum** after a flick is computed by a model that doesn't depend on the tick rate.
  - **3 fingers: drag.** A left-button drag that moves the cursor by the centroid's relative motion, released when the fingers lift.
  - **4 fingers:** swipe up = Mission Control, down = App Exposé, left/right = switch Space/full-screen app. Pinch in = Launchpad, spread = show desktop. Swipes report direction and progress.
  - **Interrupts:** pen in range, the watchdog, pause or disconnect cancel the current gesture and its momentum. A finger landing stops a coast.
- **Injection (Mac):**
  - **Scroll:** continuous whole-pixel scroll-wheel events (the remainder is carried), with scroll phases and then momentum phases on a one-shot timer that runs only while coasting. **Swipe between pages** comes from posting these exactly like a trackpad, with phases, so AppKit's scroll-swipe tracking sees them.
  - **Pinch/rotate/smart zoom:** trackpad gesture events posted through undocumented CGEvent gesture fields. Like a trackpad's, they reach only the frontmost app.
  - **4-finger gestures:** interactive Dock gesture events that follow the fingers, if they work on this macOS. Otherwise the gesture fires once past a threshold through the default shortcuts (⌃↑, ⌃↓, ⌃←/→; Launchpad and show desktop through their system actions). The spike decides which.
- **Why not ⌘+scroll for pinch:** browsers and Preview would scroll instead of zoom. **Why not touchscreen semantics (cursor at the fingers, fixed direction):** the user wants trackpad behaviour.
- **Consequences / deferred:**
  - The undocumented event fields can change with a macOS update. The Mac smoke test covers each gesture, and every injector logs once and degrades to the discrete fallback.
  - Phase 5 Settings: per-gesture on/off, finger mapping, scroll gain/acceleration, invert.
  - Mirroring the trackpad's own preference domain was rejected for now: its keys are undocumented and present on Macs without a trackpad.

## ADR-0024 — A suppressed finger stays on the wire as "not in contact" until it lifts
**Status:** accepted (2026-10-15). Refines ADR-0022's palm/cancel handling for gestures (ADR-0023); no protocol version change.
- **Context:** when a palm or a platform cancel (`FLAG_CANCELED`) suppressed touches, the phone-side `InputFilter` dropped them from the next datagram. The Mac's `GestureRecognizer` read that as a lift, so a palm within ~200 ms of two fingers landing produced a stray two-finger Tap (a right-click), and a palm mid-scroll Ended the scroll with momentum instead of cancelling it (#114).
- **Decision:**
  - The phone keeps reporting each already-sent touch that gets suppressed as present with `Contact=false` (touch state 0), every frame, until that pointer lifts. The value is already valid on the wire; no phone sent it before.
  - The Mac treats a present non-contact touch as a cancel: an active or held gesture is **Cancelled** (no Tap, no momentum), and no new gesture starts until all touches are gone.
- **Why not a new "canceled" bit:** the touch decoder (1.0.0 included) rejects datagrams with unknown state bits, so a new bit would need a capability negotiation. **Why not freezing the contact as still down:** it breaks ADR-0022's palm rule (a quick palm-and-lift would still tap) and would hold a three-finger drag's button.
- **Compatibility:** a 1.0.0 phone with a 1.1.0 Mac behaves as before. A 1.1.0 phone with a 1.0.0 Mac: the datagrams are valid, and 1.0.0 ignores gestures.
- **Consequences:** `docs/specs/phase-1-core.md` documents the `Contact=false` semantics. The end-to-end pipeline tests cover it.

## ADR-0025 — Recover a lost finger tap by resending its frame (protocol 1.1, `SampleDedupe`)
**Status:** accepted (2026-10-15). A minor protocol change: protocol 1.1 adds a capability bit.
- **Context:** in one-finger cursor mode (ADR-0022) a tap travels as hover → contact → none inside **one** datagram. If that datagram is lost, the click is lost silently: the release repeats resend only the final empty state (#92). The input wire carries no per-sample timestamps, and the Mac re-applies every history sample, so a naive resend would double-click.
- **Decision:**
  - The Mac skips a frame's history samples unless its `timeUs` is later than the last applied frame's. The current sample is always applied (it's full state, so re-applying is harmless). The watermark resets per session.
  - Capability bit `SampleDedupe` (protocol **1.1**): the Mac advertises it. Only then do the phone's release repeats resend a click frame (contact in history, ending empty; up to 8 samples) **unchanged**. Each new frame gets a strictly later `timeUs`, and any new frame cancels the resend.
- **Compatibility:** a 1.0.0 Mac doesn't advertise the bit, so a 1.1 phone behaves as before (no double-click). A 1.0.0 phone with a 1.1 Mac: the dedupe is practically harmless: 1.0.0 truncates frame times to µs without bumping, so only two frames in the same µs could share a time, and the second would lose its history samples (not its state). Minor-version mismatches are accepted (major only is refused).
- **Why not sending clicks over the TLS channel:** it needs new message types and a second input path, and the head would have to order them against UDP samples.

## ADR-0026 — One finger moves the cursor relatively, like a trackpad (`FingerTouches`, protocol 1.1)
**Status:** accepted (2026-10-15); its pointer slop is refined by ADR-0027 (2026-10-15). Supersedes ADR-0022's absolute one-finger cursor when both sides support it; ADR-0022 stays the fallback. Adds a capability bit to protocol 1.1 (still unreleased, so no further minor bump).
- **Context:** in the P4-C device test (#110), one finger put the cursor where it touched, because cursor mode sends absolute hover samples (ADR-0022). On a trackpad, one finger moves the cursor from where it already is, and the user wants trackpad parity (ADR-0023). The phone can't do this alone, because it doesn't know where the Mac's cursor is.
- **Decision:**
  - New capability bit `FingerTouches` (2). When both sides advertise it and the finger mode is `Cursor`, the phone's `InputFilter` sends a lone finger as raw touches, the same touch stream gestures use, instead of hover samples. The phone no longer holds or arms a lone finger in this mode, and no longer decides its taps. Palm rejection, pen suppression and the withdrawn-touch rule (ADR-0024) stay phone-side.
  - Core's `GestureRecognizer` on the Mac handles one finger, with the cursor staying put until it moves:
    - **Move:** the finger's movement moves the cursor by the same amount, through the gain and acceleration curve of three-finger drag (no jump to the touch point). Movement within `TapSlop` of the landing point is held back, so a second finger landing doesn't nudge the cursor first.
    - **Tap = left click** at the current cursor, posted on lift with no delay (one-finger taps aren't held). Two taps within the double-tap window and slop give a double click (click state 2).
    - **Tap, then touch and move** within the double-tap window ("tap and a half") is a left drag from the current cursor, released on lift.
    - **A second finger** ends the one-finger action and starts the two-finger decision as today. A one-finger drag in progress is released first.
  - The Mac moves the cursor by posting mouse-moved events, or dragged events during a drag, at the current location plus the delta, clamped to the displays. Clicks and drags use mouse events at the cursor. Pause, the watchdog, pen proximity and disconnect cancel these like other gestures, and a drag is always released.
  - The pen stays absolute: it puts the cursor where it touches, as with a tablet.
  - Draw mode (`FingerFallbackMode.Draw`) is unchanged: absolute strokes.
- **Compatibility:** if either side lacks `FingerTouches` (a 1.0.0 peer, or a 1.1 build from before this ADR), the phone keeps ADR-0022's absolute cursor mode. Old Macs never receive lone-finger touches.
- **Why the recognizer:** it already sees every finger and owns tap/slop timing, the first-finger hold and gesture switching, so one finger becomes another gesture there. The phone-side alternative (sending deltas as samples) would need a new sample kind and still couldn't share the trackpad's tap and gesture decisions.
- **Consequences / deferred:** in this mode a tap is decided from touch frames, so losing one frame must neither lose nor double a click (tests cover it); ADR-0025 still covers the fallback mode. Pointer speed becomes configurable in Phase 5.

## ADR-0027 — One-finger pointer slop and settle time
**Status:** accepted (2026-10-15). Refines ADR-0026's "movement within `TapSlop` is held back"; its click window is set by ADR-0028 (2026-10-15).
- **Context:** holding a lone finger until `TapSlop` (3.5%, about 2.5 mm) would make every cursor move start late and feel sticky. A smaller slop alone lets the first finger of a staggered two-finger tap drift into a cursor move, which loses the right-click or the smart zoom (review of #124).
- **Decision:**
  - **Pointer slop:** a lone finger starts moving the cursor beyond `PointerSlop` (1.5%, about 1 mm).
  - **Settle time:** during the first `PointerSettleTime` (100 ms) after it lands, while a second finger can still land, it starts only beyond `TapSlop`. Either way, the first move catches up all the movement since landing.
  - **Click slop:** a one-finger double click or tap-and-a-half must land within `ClickSlop` (5%, as in ADR-0022) of the previous click, not within the wider smart-zoom `DoubleTapSlop`.
  - **Finger fallback gate:** the one-finger cursor needs the phone's finger fallback to be on (SurfaceInfo `FingerFallback`), so a lone finger does nothing when fallback is off.
- **Why:** a gesture that can turn into another must not decide before that other gesture's slop does.

## ADR-0028 — Separate tap windows for one and two fingers
**Status:** accepted (2026-10-15). Refines ADR-0026 and ADR-0027, which used "the double-tap window" for one-finger double clicks as well.
- **Context:** a held two-finger tap fires its right-click only when the double-tap window ends, plus `TapDeadlineJitterAllowance` (50 ms). At 400 ms (#128) the user found the right-click too slow in the S23 test (#110, round 3). One-finger clicks are posted at once, so their window adds no delay.
- **Decision:**
  - `DoubleTapWindow` is **300 ms** and applies to two fingers only (held tap, smart zoom), so the right-click comes about 350 ms after lift.
  - The new `ClickWindow`, **400 ms**, applies to one-finger double clicks and tap-and-a-half.
  - The relaxed two-finger slops and tap duration from #128 stay as they are.
- **Why:** the window is the only part of the smart-zoom fix that costs latency; the slops gave most of the reliability. Phase 5 may make both windows configurable.

## ADR-0029 — The two-finger tap follows the macOS Smart Zoom setting
**Status:** accepted (2026-10-15). Refines ADR-0023 (double tap = smart zoom) and ADR-0028 (the 300 ms `DoubleTapWindow`).
- **Context:** in the S23 test (#110), the two-finger right-click still felt slow at about 350 ms after lift. The hold exists only to tell a single tap from a smart-zoom double tap. A Mac trackpad has the same trade-off: with Smart Zoom off, its two-finger tap right-clicks at once.
- **Decision:**
  - The Mac reads the macOS Smart Zoom setting (`TrackpadTwoFingerDoubleTapGesture` in the trackpad preference domains), as it does for natural scrolling (ADR-0023). It re-reads the setting when a two-finger tap is decided, so a change applies without a restart.
  - **Smart Zoom off:** a two-finger tap is a secondary click on lift, with no hold, and a double tap makes two secondary clicks.
  - **Smart Zoom on:** the tap is held for `DoubleTapWindow`, shortened to **250 ms** (about 300 ms after lift with the jitter allowance). The relaxed slops from #128 stay as they are.
  - A missing or unreadable setting counts as on, the macOS default.
  - Phase 5 adds a PenBridge setting that can override the macOS one.
- **Why:** it matches what the same Mac does with a real trackpad, and it lets the user choose speed over smart zoom in the place they already know.
- Amended 2026-10-15 (#142): DoubleTapWindow 200 ms, jitter allowance 20 ms, ~225 ms after lift; measured S23 double-tap gaps 78–135 ms, MacBook trackpad right-click ~300 ms after lift.

## ADR-0030 — Settings live where they apply; the phone edits the Mac's over the control channel (protocol 1.2)
**Status:** accepted (2026-10-16)
- **Context:** Phase 5 adds settings on both sides (epic #14). Most of them apply on the Mac (mapping, pen, gestures), a few on the phone (orientation, finger and palm options, the surface look). The user wants to change Mac settings from the phone too.
- **Decision:**
  - **One owner per setting:** each setting is stored and applied on the side that uses it. The Mac's are **global**, not per paired phone (one session at a time, ADR-0010). The Mac stores them in `NSUserDefaults`, the phone in SharedPreferences. Phone-only settings never go over the wire.
  - **Remote edit:** with the new capability `RemoteSettings` (4) on both sides, the phone can view and edit the Mac's settings from a "Mac" section in its Settings screen, except drawing the region, which needs the Mac screen ("Reset region to full display" is allowed).
  - **Wire (protocol 1.2, all after Auth/SessionStart):**
    - `0x40` S `SettingsSnapshot`: every Mac setting as a list of entries. Sent once after SessionStart, after each change from either side, and in reply to every `SettingsUpdate` (the reply is the acknowledgement).
    - `0x41` C `SettingsUpdate`: one or more entries to change. The Mac validates each one (known key, matching type, in range); it applies and persists the valid ones and ignores the rest. The snapshot it sends back is the truth, so the phone just redraws from it.
    - `0x42` S `DisplayList`: the displays the user can pick (id, name, is-main, size in points), plus the pseudo-target "All displays". Sent with the first snapshot and on display reconfiguration.
    - Entry: `key u16, type u8, len u16, value`. Types: Bool (u8), Int (i32), Float (f32), String (≤ 256 B UTF-8), Rect (4 × f32, normalized). Unknown keys or a type mismatch are skipped (forward compatibility), and an entry count above 64 is a protocol error. The key table lives in Core (`MacSettingKey`), with each key's type, range and default; it is the one source for the Mac store, the validation and the phone UI.
  - The Mac's Settings window edits the same settings locally and pushes a snapshot to the connected phone.
- **Why not both sides store everything:** two copies need conflict rules, and a phone that has been away would overwrite newer Mac choices. With one owner, the snapshot is always right.
- **Why not Mac-only editing:** the user wants to tune from the phone while drawing, and the snapshot costs one message type more.
- **Consequences / deferred:** a 1.0/1.1 peer simply has no Mac section on the phone. Per-device profiles stay out of scope. Phone settings can't be edited from the Mac.

## ADR-0031 — Target display or all displays, a dragged region, and the phone's active area follows the target's aspect ratio
**Status:** accepted (2026-10-16). Refines ADR-0005 (fit direction, all displays) and ADR-0016 (target display).
- **Context:** `CoordinateMapper` fits the phone surface inside the display. The S23 in landscape is wider than the Mac display, so the pen can't reach the display's top and bottom strips (#127). ADR-0005 promised a region; the user also wants an "All displays" target.
- **Decision:**
  - **Target:** one display (remembered by UUID, falling back to main, as in ADR-0016), or **All displays**: the bounding box of every display, as one desktop. Points that fall in a gap between displays are clamped onto the nearest display.
  - **Region:** "Set region…" in the Mac Settings window dims the target display and lets the user drag a rectangle (Esc cancels; "Full display" resets). The region is stored per display UUID as a normalized rect. An **aspect-lock** checkbox (default off) keeps the dragged rectangle at the phone surface's aspect ratio. Regions don't apply to "All displays".
  - **Active area (#127):** the inverse of today's fit. The active part of the phone is the largest rectangle with the target's (display or region) aspect ratio, centred on the phone surface, and it maps onto the whole target. A pen outside it is pinned to the nearest edge of the target, so the margins make the edges easy to reach. Strokes keep their shape.
  - **Wire:** with the capability `ActiveArea` (8) on both sides, the Mac sends `0x43` S `ActiveArea` (normalized rect in the current surface orientation, 4 × u16 on the 0..65535 scale) after each SurfaceInfo and each target, region or display change. In Mouse mode (ADR-0032) it sends the full surface.
  - **Phone surface (#130):** L-shaped corner ticks at the active-area corners, the margins outside it dimmed, and an optional centre cross. Phone-only settings: background (black, dark grey, light), marker opacity, centre marker on/off. The markers stay visible while drawing (they're faint). Without `ActiveArea` the phone marks the whole surface.
- **Why not a full outline:** the corner ticks show the edges without a frame running through the strokes; the dimmed margin already shows the boundary.
- **Consequences / deferred:** numeric region fields and per-app mappings stay out of scope.

## ADR-0032 — Pen settings: Pen/Mouse mode, side-button action and pressure curve, applied on the Mac
**Status:** accepted (2026-10-16). Makes ADR-0016's side-button mapping configurable; keeps ADR-0022's contact-down latch.
**Amended 2026-10-16 (#155):** the click threshold has a 0.02 release hysteresis; a stylus-mode change applies after the current stroke; Mouse mode moves 8 pt per phone mm at speed 1.
- **Context:** Wacom drivers offer a relative Mouse mode (#129), a side-switch action and a tip-feel curve. ADR-0006 and ADR-0016 deferred the button mapping to Phase 5, and the pressure curve was deferred from Phase 1.
- **Decision:** all three live in Core on the Mac side and are applied before injection, so the input wire format doesn't change.
  - **Stylus mode** (`Pen` default, `Mouse`):
    - Pen: absolute, as today, with ADR-0031's active area.
    - Mouse: the hovering pen moves the cursor from where it is, by its movement in phone millimetres (from SurfaceInfo DPI) times its own **Mouse speed** (separate from the finger pointer speed) through the ADR-0026 acceleration curve, which an **Acceleration** toggle can switch off. Entering proximity anchors the pen, so re-entering never jumps the cursor. The tip clicks and drags at the cursor with pressure and tilt (tablet subtype), and the side button keeps its action. The cursor is clamped to the displays (the target display, or all of them with "All displays").
    - A quick toggle is in the Mac menu, in the Mac Settings window and on the phone (remote edit).
  - **Side-button action:** Right click (default), Middle click, Eraser while held, Double click, None.
    - Hovering press: Right and Middle post that button's down on press and up on release, at the pen's point. Double click posts two left clicks (click states 1, 2) on press. None posts nothing. Eraser switches the proximity to the eraser pointer type while held, so drawing apps change tool, and back on release.
    - Held at contact-down (the ADR-0022 latch stays): Right and Middle make the stroke a drag with that button, Eraser makes it an eraser stroke, and Double click and None leave it a normal left stroke. Pressing or releasing the button during contact posts nothing.
  - **Pressure curve** (pen and eraser only; the finger fallback's synthetic pressure is not curved):
    - **Tip feel**, −1 (soft) … +1 (firm), default 0 = linear: output = input^γ with γ = 2^tipFeel (soft γ = 0.5, firm γ = 2).
    - Advanced: **Click threshold** 0 … 0.5 (default 0: the phone's contact decides): below it the pen hovers; a stroke presses at the threshold and releases only below threshold − 0.02 (at least 0), so pressure near it doesn't flicker. **Full pressure at** 0.5 … 1 (default 1).
    - Output = curve((p − threshold) / (full − threshold)), clamped to 0..1, allocation-free per sample.
    - The Mac Settings window has a live test pad that draws with the curved pressure.
- **Why on the Mac:** Wacom keeps these in the driver too. The Mac sees every sample before injection, and one place means the phone stays a pure sampler and older phones get the settings too.
- **Consequences / deferred:** keystroke, pan/scroll and mode-toggle button actions and a curve editor stay out of scope.

## ADR-0033 — Gesture settings: Smart Zoom override, pointer and scroll speed, per-gesture toggles
**Status:** accepted (2026-10-16). Delivers the Phase 5 items deferred by ADR-0026 (pointer speed) and ADR-0029 (Smart Zoom override).
**Amended 2026-10-16 (#155):** gesture gain is independent of the pen's active area and region (the Phase 4 basis); the speed settings scale it; with All displays, gestures measure on the main display (the one at 0,0, else the first); Zoom/Rotate toggle changes apply from the next pinch.
- **Decision (Mac settings, editable from the phone per ADR-0030):**
  - **Smart Zoom:** Follow macOS (default, ADR-0029) / On / Off.
  - **Pointer speed** scales the one-finger cursor and three-finger drag gain; **Scroll speed** scales two-finger scrolling and its momentum. Both 0.25 … 4, default 1.
  - **Toggles**, all on by default: Zoom (pinch), Rotate, Three-finger drag, Four-finger gestures. Off drops that output in Core's `GestureRecognizer`: zoom off and rotate off each remove their part of the combined transform, and with both off two fingers only scroll, tap and smart-zoom; three or four fingers with their toggle off do nothing.
  - The tap windows (ADR-0028) stay fixed.
- **Why:** these are the trackpad pane's settings, and they're the ones the device tests asked for.
