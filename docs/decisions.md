# Architecture Decision Records

Append new ADRs at the bottom. Never rewrite an accepted ADR; supersede it with a new one.

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
**Status:** accepted (2026-10-13)
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
**Status:** accepted (2026-10-13)
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
