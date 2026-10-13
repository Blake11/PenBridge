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
Default deploy is `dotnet build -t:Run` (fast deployment). It builds, installs and launches but prints nothing after `Build succeeded` — confirm the launch with `adb -s <serial> logcat -d | grep "Displayed com.blake11.penbridge"` (and `adb shell pidof com.blake11.penbridge`); screenshot with `adb -s <serial> exec-out screencap -p > /tmp/x.png`. The activity class name is a `crc64…` hash (`com.blake11.penbridge/crc64….MainActivity`) unless `[Activity(Name = …)]` is set — use `monkey -p <pkg> -c android.intent.category.LAUNCHER 1` to launch, not `am start -n`. `--logs` = `adb logcat -v color,time --pid=<app pid>`; if the app isn't running it falls back to `AndroidRuntime:E DOTNET monodroid mono-rt PenBridge` tags.

## Release APK (what CI does)
`dotnet publish src/PenBridge.Android/PenBridge.Android.csproj -c Release -p:AndroidPackageFormat=apk -p:AndroidKeyStore=false -o <dir>` (+ SDK/JDK dirs locally) → `<dir>/com.blake11.penbridge.apk` (**unsigned**, `apksigner` says "DOES NOT VERIFY") **and** `com.blake11.penbridge-Signed.apk` (signed with the **debug** key, installable). Release trims (ILLink "Optimizing assemblies for size" notice is informational, not a warning). Check version fields: `$ANDROID_HOME/build-tools/36.0.0/aapt2 dump badging <apk> | head -3`.

## S Pen testing notes
- `GetToolType(i)` = `Stylus`/`Eraser`; side button via `ButtonState` (`StylusPrimary`) on touch and hover events.
- Hover arrives via `OnHoverEvent` (`HoverEnter/Move/Exit`), not `OnTouchEvent`. If Samsung Air view/Air command swallows hover, disable them.
- Consume **historical samples** (`HistorySize`, `GetHistoricalX/Y/AxisValue`) or you drop well below 240 Hz; `RequestUnbufferedDispatch` for latency.
- `Axis.Tilt` + `Axis.Orientation` → tilt X/Y conversion lives in Core (`Input/TiltConverter`, tested there). Android's input reader builds both axes from the digitizer's per-axis HID tilt (tx > 0 = pen top toward +x/right, ty > 0 = toward +y/the user): `orientation = atan2(-sin tx, sin ty)`, `tilt = acos(cos tx · cos ty)`. So orientation is where the **tip** points (0 = up, π/2 = right): a top leaning right gives −π/2. Exact inverse (φ = orientation, T = tilt, k = sin²φ·cos²φ): `r² = 2·sin²T / (1 + √(1 − 4k·sin²T))`, `tx = asin(−r·sin φ)`, `ty = asin(r·cos φ)`. `AXIS_ORIENTATION` is the tilt azimuth, not barrel twist (S Pen has none): send `Rotation = 0`.
- Emulator: `adb shell input stylus swipe x1 y1 x2 y2 ms` injects `ToolType.Stylus` (pressure 1, no tilt/hover) — enough to exercise the stylus code path, not to verify it.
- API 36 on large screens (sw ≥ 600dp, e.g. the `PenBridge_Tablet` AVD) ignores `RequestedOrientation`; test orientation locks there with `adb shell wm set-ignore-orientation-request false` (restore with `true`). Phones (S23) honour it.
- Ignore fingers/palm while the pen is in proximity; drop `FLAG_CANCELED` pointers.
- Raw values: Developer options ▸ Pointer location. No per-sample logging in release builds.
- Acceptance: draw in Krita on the Mac — light vs hard pressure, tilt, hover cursor without clicking, side-button action.
