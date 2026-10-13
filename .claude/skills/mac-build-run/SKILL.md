---
name: mac-build-run
description: Build, sign, run, and debug the PenBridge macOS menu-bar server (net10.0-macos) — "PenBridge Dev" signing cert, Accessibility/TCC permission handling, logs, LSUIElement behaviour, and verifying CGEvent tablet input (pressure/tilt) in Krita. Use when building or running the Mac app, when injected input does nothing, when the Accessibility grant is lost, or when testing the CGEvent injector.
---

# Mac: build, run, verify

Source of truth: `scripts/mac-run.sh`, `scripts/mac-dev-cert.sh` (`--help`), `docs/development.md`. If they differ from this skill, fix the skill. Toolchain/Xcode problems → skill `toolchain`.

## One-time: dev signing identity (user runs it)
```bash
scripts/mac-dev-cert.sh            # creates self-signed "PenBridge Dev" in the login keychain
scripts/mac-dev-cert.sh --check    # present + trusted?
scripts/mac-dev-cert.sh --delete   # then mac-run.sh --reset-tcc once
```
- It asks for the **login password** (trust setting) and the first codesign may ask "codesign wants to use key" → Always Allow. Agents can't answer those prompts — ask the user to run it.
- Why: TCC stores the grant against the designated requirement. Ad-hoc = cdhash → changes every build → grant silently lost (toggle still looks on). Signed with the cert, the requirement is `identifier "com.blake11.penbridge" and certificate leaf = H"…"` → survives rebuilds.
- codesign works even while the cert is **untrusted** (`find-identity` shows `CSSMERR_TP_NOT_TRUSTED`); the leaf hash pins it, so trust is cosmetic.

## Run
```bash
scripts/mac-run.sh                 # Debug build → sign → open (kills a running instance first)
scripts/mac-run.sh --release
scripts/mac-run.sh --no-build      # relaunch last build
scripts/mac-run.sh --reset-tcc     # tccutil reset Accessibility com.blake11.penbridge, then run
scripts/mac-run.sh --logs          # + log stream --style compact --level debug --predicate 'process == "PenBridge"'
scripts/mac-run.sh -- <dotnet build args>   # e.g. -- -p:ValidateXcodeVersion=false
```
- Builds with `-p:EnableCodeSigning=false`, then `scripts/mac-sign.sh <app> <identity>` (shared with `build-release.yml`) with "PenBridge Dev" (else ad-hoc + warning): each MonoBundle Mach-O, then `codesign --force --deep --identifier com.blake11.penbridge`, using `src/PenBridge.Mac/Entitlements.plist` if present; `--hardened` = `--options runtime --timestamp`.
- Check the signature: `codesign -dr - <app>` must show a certificate hash (`certificate root = H"…"` for our self-signed cert — root == leaf), not `cdhash`. `codesign -dvv` (two v) shows `Authority=PenBridge Dev`.
- **MonoBundle dylibs**: the build rewrites the Microsoft-signed runtime dylibs in `Contents/MonoBundle` (signature becomes invalid) and with `EnableCodeSigning=false` nothing re-signs them; `codesign --deep` treats MonoBundle as resources, so `codesign --verify --deep` still passes. Result: instant `SIGKILL (Code Signature Invalid)`, "Invalid Page" in dyld, crash report in `~/Library/Logs/DiagnosticReports/PenBridge-*.ips`. `mac-sign.sh` signs (and verifies) each MonoBundle library before the bundle; check one with `codesign -v <app>/Contents/MonoBundle/libcoreclr.dylib`.
- **Hardened runtime** (`mac-sign.sh --hardened`): CoreCLR needs `com.apple.security.cs.allow-jit` (else "Failed to initialize the VM", exit 134). Testing it with the self-signed dev cert also needs `com.apple.security.cs.disable-library-validation` (no Team ID → dyld "different Team IDs"); a Developer ID cert doesn't.
- Process name `PenBridge` comes from `<AssemblyName>PenBridge</AssemblyName>` (default would be `PenBridge.Mac`); bundle id `com.blake11.penbridge`.
- Output: Debug → `bin/Debug/net10.0-macos/osx-arm64/PenBridge.app`; Release builds osx-arm64 + osx-x64 and a universal `bin/Release/net10.0-macos/PenBridge.app` (the newest, which the script picks).
- No GNU `timeout` on macOS: run the binary with `&`, `sleep`, then `kill`; exit 137 = killed by the system (signing), 143 = your SIGTERM.

## LSUIElement / menu bar
- `LSUIElement=true` + `ActivationPolicy.Accessory`: no Dock icon, no app menu, no Cmd-Tab; only the `NSStatusItem`. Nothing visible → check `--logs` for a crash, or run the binary in `<app>/Contents/MacOS/` from a terminal for stdout.
- Windows (pairing PIN/QR) must activate the app to come to front.
- On notched displays a crowded menu bar can hide the icon.
- Verify headless: `lsappinfo info "$(lsappinfo find bundleid=com.blake11.penbridge)"` shows `type="UIElement"` (no Dock icon). Agents can't confirm the status item: `screencapture` needs Screen Recording ("could not create image from display") and `osascript`/System Events needs Accessibility for the terminal — ask the user to look.
- Baseline idle (skeleton): 0.0% CPU, RSS ~110 MB Debug / ~185 MB Release universal.

## Accessibility (TCC)
- `AXIsProcessTrusted()` false → posted CGEvents are dropped silently. The app prompts and links to System Settings ▸ Privacy & Security ▸ Accessibility.
- Granted but nothing happens: signature changed → `--reset-tcc`, trigger injection, grant again. If it ever ran unsigned, remove the old entry with `–` first.
- The grant is per signed identity; running a build from another worktree path may need a re-grant — prefer running from the main tree.

## Verifying tablet input
- No phone: `tools/PenBridge.SimClient` streams scripted strokes to the server.
- **Krita** is the reference: pressure brush; Settings ▸ Configure Krita ▸ Tablet settings has a pressure tester. Expect width varying with pressure; tilt affects tilt-aware brushes.
- Proximity-enter (`kCGEventTabletProximity`, pen/eraser) before tablet-point mouse events, proximity-leave after — without it many apps treat input as a plain mouse.
- Pressure 0..1 in `kCGTabletEventPointPressure` (subtype `TabletPoint`) and `kCGMouseEventPressure`; tilt −1..1.
- While down use `LeftMouseDragged`, not `MouseMoved`; unmatched down/up leaves apps stuck (150 ms pen-up watchdog covers packet loss).
- Sanity-check in Preview/Notes (mouse behaviour) and on multiple displays/regions.

## Mac services notes (`src/PenBridge.Mac/Services`)
- **Bonjour** uses the DNS-SD C API (`DNSServiceRegister` + `DNSServiceSetDispatchQueue` on the main queue, static `[UnmanagedCallersOnly]` reply callback; NativeAOT-clean). `NSNetService`/`NSHost` are obsoleted (CA1422 = build error). Check with `dns-sd -B _penbridge._tcp` and `dns-sd -L "<name>" _penbridge._tcp local`.
- **Instance name ≤ 63 UTF-8 bytes** (one DNS label), cut on a code-point boundary (`Utf8Text.Truncate`); the TXT `name` may be up to 250 bytes. Non-ASCII computer names ("Paul’s MacBook Pro", CJK, emoji) hit the label limit fast.
- Reply errors (mDNSResponder restart) → deallocate + re-register with 1→30 s backoff (one-shot `DispatchAfter`, no polling). Computer-name changes come from an `SCDynamicStore` notification on the computer-name key.
- **Keychain**: login (file-based) keychain; `kSecAttrAccessible` is ignored there. The first access by a newly signed/copied bundle can block for 10–50 s (securityd re-validates the signature, maybe a hidden "wants to use confidential information" prompt) → keep Keychain I/O off the main thread.
- **os_log**: `OSLogLevel.Info` entries aren't persisted by default; map Information → Default. Use `/usr/bin/log` in scripts (zsh has a `log` builtin).
- Parallel agents: `mac-run.sh` does `pkill -x PenBridge`; to test alongside another worktree's build, copy the .app, rename `Contents/MacOS/PenBridge` + `CFBundleExecutable`, and re-sign with `scripts/mac-sign.sh`.
