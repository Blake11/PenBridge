---
name: android-build-run
description: Build, install, launch, and debug the PenBridge Android client (net10.0-android) on the PenBridge_Tablet emulator or the real Samsung S23 Ultra via wireless adb — pair/connect, run, logcat, and S Pen testing notes. Use when running the Android app, connecting the phone, reading device logs, or verifying stylus pressure/tilt/hover/button behaviour.
---

# Android: build, run, verify

Source of truth: `scripts/android-*.sh` (`--help`), `docs/development.md`. If they differ from this skill, fix the skill. SDK/JDK/workload problems → skill `toolchain`.

App/package id `com.blake11.penbridge`. Scripts set `JAVA_HOME`/`ANDROID_HOME` per process and pass `-p:AndroidSdkDirectory`/`-p:JavaSdkDirectory`.

## Emulator vs device
| Need | Use |
|---|---|
| UI layout, pairing flow, networking, finger input, crashes | Emulator |
| S Pen pressure, tilt, hover, side button, eraser, palm rejection, 240 Hz sampling, latency | **Real S23 Ultra only** |

- The emulator has **no stylus**: mouse = `ToolType.Finger`, pressure fixed 1.0, no tilt/hover/buttons. Never mark stylus behaviour verified from it.
- **Bonjour/mDNS discovery does not reach the emulator** (NAT). The Mac is `10.0.2.2` from inside it. Emulator connection tests use the **Advanced ▸ manual host:port + PIN** entry (a user-facing fallback, ADR-0008); real discovery must be verified on the S23.

## Emulator
AVD `PenBridge_Tablet` = `pixel_tablet` (2560x1600), `system-images;android-36;google_apis;arm64-v8a` (env `AVD_DEVICE`, `ANDROID_API_LEVEL` override).
```bash
scripts/android-emulator.sh                 # create if missing, boot, wait
scripts/android-emulator.sh --headless | --no-wait | --cold-boot | --wipe | --recreate | --stop
```
Won't boot → `$TMPDIR/penbridge-emulator.log`, then `--cold-boot`, then `--wipe`.

## Wireless adb (S23)
Phone: Developer options ▸ Wireless debugging ▸ on (same Wi-Fi as the Mac, no client isolation/VPN).
```bash
scripts/android-device.sh pair <ip:pair-port> <6-digit-code>   # from "Pair device with pairing code" (expires when dialog closes)
scripts/android-device.sh connect <ip:port>                    # "IP address & Port" on the main screen — different from pair port
scripts/android-device.sh list
scripts/android-device.sh disconnect [ip:port]
```
Pairing persists; the connect port changes after reboot or toggling Wireless debugging → just `connect` again. Pairing needs the user to read the code off the phone.

## Run
```bash
scripts/android-run.sh --emulator [--logs]           # starts PenBridge_Tablet if none running
scripts/android-run.sh --device <ip:port> --logs
scripts/android-run.sh                               # the only connected device
scripts/android-run.sh --release | --apk             # --apk: standalone APK + adb install -r (when -t:Run misbehaves)
scripts/android-run.sh --device <serial> --logs-only # attach logcat, no build
scripts/android-run.sh ... -- <dotnet build args>
```
Default deploy is `dotnet build -t:Run` (fast deployment). `--logs` = `adb logcat -v color,time --pid=<app pid>`; if the app isn't running it falls back to `AndroidRuntime:E DOTNET monodroid mono-rt PenBridge` tags.

## S Pen testing notes
- `GetToolType(i)` = `Stylus`/`Eraser`; side button via `ButtonState` (`StylusPrimary`) on touch and hover events.
- Hover arrives via `OnHoverEvent` (`HoverEnter/Move/Exit`), not `OnTouchEvent`. If Samsung Air view/Air command swallows hover, disable them.
- Consume **historical samples** (`HistorySize`, `GetHistoricalX/Y/AxisValue`) or you drop well below 240 Hz; `RequestUnbufferedDispatch` for latency.
- `Axis.Tilt` + `Axis.Orientation` → tilt X/Y conversion lives in Core (tested there).
- Ignore fingers/palm while the pen is in proximity; drop `FLAG_CANCELED` pointers.
- Raw values: Developer options ▸ Pointer location. No per-sample logging in release builds.
- Acceptance: draw in Krita on the Mac — light vs hard pressure, tilt, hover cursor without clicking, side-button action.
