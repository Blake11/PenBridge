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
- Builds with `-p:EnableCodeSigning=false`, then `codesign --force --deep --identifier com.blake11.penbridge` with "PenBridge Dev" (else ad-hoc + warning), using `src/PenBridge.Mac/Entitlements.plist` if present.
- Check the signature: `codesign -dr - <app>` must show `certificate leaf`, not `cdhash`.
- Process name `PenBridge`; bundle id `com.blake11.penbridge`.

## LSUIElement / menu bar
- `LSUIElement=true` + `ActivationPolicy.Accessory`: no Dock icon, no app menu, no Cmd-Tab; only the `NSStatusItem`. Nothing visible → check `--logs` for a crash, or run the binary in `<app>/Contents/MacOS/` from a terminal for stdout.
- Windows (pairing PIN/QR) must activate the app to come to front.
- On notched displays a crowded menu bar can hide the icon.

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
