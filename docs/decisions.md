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
