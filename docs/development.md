# Development

How to set up the toolchain and build, run and debug PenBridge on each platform. All helpers live in `scripts/`; each one supports `--help`.

## Prerequisites

| Tool | Needed for | Installed by |
|---|---|---|
| macOS 14+ on Apple Silicon or Intel | everything | — |
| Homebrew | JDK, Android command-line tools | [brew.sh](https://brew.sh) |
| .NET SDK 10.0.1xx (pinned in `global.json`) | everything | [dot.net](https://dot.net) |
| .NET workloads `macos`, `android`, `ios` | the platform heads (Core and its tests don't need them) | `sudo dotnet workload install …` (printed by bootstrap) |
| Xcode (full app, not only the Command Line Tools) | `net10.0-macos` / `net10.0-ios` builds | App Store (manual) |
| JDK 17 (`openjdk@17`, keg-only) | Android builds | bootstrap |
| Android SDK in `~/Library/Android/sdk` | Android builds, adb, emulator | bootstrap |

Versions follow the .NET 10 Android workload defaults:
- `platforms;android-36` and `build-tools;36.0.0`.
- JDK 17.0 to 21.x is accepted. We use 17, which Microsoft recommends.
- The emulator image is `system-images;android-36;google_apis;arm64-v8a`, a Google APIs image without the Play Store, so it can be rooted and debugged.

To use different versions, override `ANDROID_API_LEVEL` / `ANDROID_BUILD_TOOLS` in the environment.

## Bootstrap

```bash
scripts/bootstrap.sh      # idempotent; re-run any time
scripts/doctor.sh         # read-only ✓/✗ check with fix hints; exit 1 if something required is missing
```

`bootstrap.sh` does the following:
- Installs `openjdk@17` and the `android-commandlinetools` cask.
- Installs the Android SDK packages into `~/Library/Android/sdk` and accepts the licenses. It also installs `cmdline-tools;latest`, so the SDK stays self-contained and the cask is only the seed.
- Checks the .NET workloads and Xcode.
- Sets `git config core.hooksPath .githooks`.

It writes nothing to your shell profile. Instead it prints the environment variables:

```bash
export JAVA_HOME="/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home"
export ANDROID_HOME="$HOME/Library/Android/sdk"
export PATH="$ANDROID_HOME/platform-tools:$ANDROID_HOME/emulator:$ANDROID_HOME/cmdline-tools/latest/bin:$PATH"
```

The `scripts/android-*` helpers set these per process and pass `-p:AndroidSdkDirectory` / `-p:JavaSdkDirectory` to MSBuild. You only need the variables in your profile for raw `dotnet build` / `adb` use or for an IDE (Rider: Settings → Build → Android).

### Manual steps (sudo / GUI)

The bootstrap script never runs sudo. It prints these commands for you instead.

1. **.NET workloads.** The SDK in `/usr/local/share/dotnet` is root-owned:
   ```bash
   cd <repo> && sudo dotnet workload install macos android ios
   ```
   Run it from the repo so `global.json` selects the right SDK band. Afterwards, run `dotnet workload update` (also with sudo) to move to newer workload sets.
2. **Xcode.** Install it from the App Store, launch it once, then:
   ```bash
   sudo xcode-select -s /Applications/Xcode.app/Contents/Developer
   sudo xcodebuild -license accept
   sudo xcodebuild -runFirstLaunch
   ```
   The `macos` workload is built against one specific Xcode. The current workload set ships the macOS 27.0 SDK pack, which targets Xcode 27. If the build reports an Xcode version mismatch, install the Xcode it names. As a temporary escape hatch for a minor mismatch, use `-p:ValidateXcodeVersion=false`.
3. **Mac dev signing identity** (once, no sudo): run `scripts/mac-dev-cert.sh` (see below).

## Daily workflows

### Core tests (any OS, no workloads)

```bash
scripts/test.sh                              # = dotnet test --project tests/PenBridge.Core.Tests
scripts/test.sh -c Release
scripts/test.sh --filter-class "*Protocol*"  # xUnit v3 on Microsoft.Testing.Platform filter syntax
```

`global.json` opts into Microsoft.Testing.Platform, so the `--project` form is required. The old `dotnet test <path>` form is rejected.

### Mac server

```bash
scripts/mac-dev-cert.sh            # one-time: "PenBridge Dev" code-signing identity
scripts/mac-run.sh                 # Debug build → sign → open the .app (menu-bar icon, no Dock icon)
scripts/mac-run.sh --release
scripts/mac-run.sh --logs          # + log stream --predicate 'process == "PenBridge"'
scripts/mac-run.sh --no-build      # relaunch the last build
scripts/mac-run.sh --reset-tcc     # tccutil reset Accessibility com.blake11.penbridge, then run
```

`mac-run.sh` builds with `EnableCodeSigning=false` and then re-signs the bundle with `codesign --force --deep --identifier com.blake11.penbridge`:
- With the `PenBridge Dev` identity when it exists, otherwise ad-hoc.
- With `src/PenBridge.Mac/Entitlements.plist` when that file is present.

It kills a running instance before it launches the new one.

#### Why the dev certificate (Accessibility / TCC)

macOS stores the Accessibility grant against the app's designated requirement. With ad-hoc signing, that requirement is the cdhash, which changes on every build. The grant then silently stops applying: `AXIsProcessTrusted()` returns false, and the toggle in System Settings still looks on.

When the app is signed with a fixed certificate, the requirement becomes `identifier "com.blake11.penbridge" and certificate leaf = H"…"`, which stays stable across rebuilds.

`mac-dev-cert.sh` makes the certificate as follows:
- It generates an RSA-2048 self-signed certificate with the codeSigning EKU, valid for 10 years.
- It imports the certificate into the login keychain and allows `/usr/bin/codesign` to use the key.
- It adds a user-domain code-signing trust setting for that one certificate. macOS asks for your password; cancelling is fine.

Trade-offs:
- The certificate is local-only. Gatekeeper still treats the app as unidentified, and the certificate is never used for releases (ADR-0007: those are ad-hoc).
- `codesign` works even while the certificate is untrusted (`find-identity` shows `CSSMERR_TP_NOT_TRUSTED`). This was verified in a throwaway keychain, so the trust step is only cosmetic.
- The first signing may show "codesign wants to use key…". Choose **Always Allow**.
- If you delete or recreate the certificate (`--delete`), the grant breaks once. Fix it with `scripts/mac-run.sh --reset-tcc` and then grant again.
- `--check` reports whether the identity is present and trusted.

#### Resetting Accessibility

If injection does nothing after a rebuild, or System Settings shows a stale entry:
1. Run `scripts/mac-run.sh --reset-tcc`.
2. Trigger injection.
3. Grant access in System Settings → Privacy & Security → Accessibility.

If the app was ever run unsigned, remove the old entry with the `–` button first.

### Android: emulator

```bash
scripts/android-emulator.sh               # create AVD "PenBridge_Tablet" (pixel_tablet, 2560x1600) if missing, boot, wait
scripts/android-emulator.sh --headless    # no window (CI-style)
scripts/android-emulator.sh --cold-boot | --wipe | --recreate | --stop
scripts/android-run.sh --emulator         # dotnet build -t:Run → install + launch com.blake11.penbridge
scripts/android-run.sh --emulator --logs  # + logcat filtered to the app's pid
```

**The emulator cannot test the stylus.** Mouse input arrives as `ToolType.Finger` with constant pressure 1.0. It has no tilt, no hover and no stylus buttons. Use the emulator for UI, flow, pairing and networking, and use the S23 Ultra for everything S Pen.

From the emulator, the Mac is reachable at `10.0.2.2`. Bonjour/mDNS does not cross the emulator's NAT, so pair with manual host:port or QR.

### Android: S23 Ultra over Wi-Fi

One-time setup on the phone:
1. Enable Developer options: Settings → About phone → Software information → tap **Build number** 7×.
2. Turn on Developer options → **Wireless debugging**. The phone must be on the same Wi-Fi as the Mac.
3. Tap **Pair device with pairing code**, then run:

```bash
scripts/android-device.sh pair 192.168.1.23:37123 123456   # IP:PORT + code from the pairing dialog
scripts/android-device.sh connect 192.168.1.23:41234       # "IP address & Port" on the main Wireless debugging screen
scripts/android-device.sh list
scripts/android-run.sh --device 192.168.1.23:41234 --logs
```

- The pairing port is different from the connect port.
- Pairing persists. The connect port changes after a reboot or after toggling Wireless debugging, so you only run `connect` again.
- If exactly one device is connected, `scripts/android-run.sh` with no target picks it.
- `--apk` builds a standalone APK (`EmbedAssembliesIntoApk=true`) and runs `adb install -r` instead of fast deployment.

## Troubleshooting

- **`sdkmanager` "deprecated, Android CLI will be used instead".** Since cmdline-tools 23, `sdkmanager` delegates to the new `android` CLI and prints a different `--list_installed` format: paths like `platforms/android-36` instead of `platforms;android-36`. The scripts detect installed packages from `<sdk>/<path>/package.xml` rather than parsing that output. On first use, the `android` binary downloads and unpacks itself and shows a metrics notice; `--no-metrics` disables the metrics.
- **No Java runtime / wrong JDK.** The Homebrew `openjdk@17` is keg-only and is not registered in `/usr/libexec/java_home`, because registering it needs a sudo symlink. The scripts find it under `$(brew --prefix)/opt/openjdk@17`. If `JAVA_HOME` points at a JDK newer than 21, Android builds fail, and `doctor.sh` warns about it.
- **Workload install "permission denied" / "access to the path is denied".** The dotnet root is root-owned, so use the `sudo` command above. Don't `chown` `/usr/local/share/dotnet`, because the official installer would fight it.
- **Android build can't find the SDK in Rider or with raw `dotnet build`.** Export `ANDROID_HOME` and `JAVA_HOME`, or pass `-p:AndroidSdkDirectory=… -p:JavaSdkDirectory=…`.
- **Emulator won't boot.** Check `$TMPDIR/penbridge-emulator.log`. Try `--cold-boot`, then `--wipe`. `--recreate` rebuilds the AVD after you change `AVD_DEVICE` or `ANDROID_API_LEVEL`.
- **`adb pair` fails.** The code and the pairing port expire when the dialog closes; reopen it. Use the pairing port for `pair` and the connect port for `connect`. VPNs and guest Wi-Fi with client isolation block wireless adb.
- **Accessibility granted, but nothing happens.** The signature changed. Check `codesign -dr - <app>`: it should show `certificate leaf`, not `cdhash`. Then run `--reset-tcc`.
