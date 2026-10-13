---
name: toolchain
description: PenBridge developer toolchain setup and troubleshooting — scripts/bootstrap.sh and scripts/doctor.sh, .NET 10 SDK and workloads (sudo), Xcode version for the macos workload, JDK 17 for Android, Android SDK/sdkmanager quirks. Use when setting up a machine, when doctor.sh reports ✗, or when a build fails for SDK/JDK/Xcode/workload reasons.
---

# Toolchain

Source of truth: `scripts/bootstrap.sh`, `scripts/doctor.sh`, `scripts/lib/common.sh`, `docs/development.md` (Troubleshooting). Fix this skill when they change.

```bash
scripts/doctor.sh                       # read-only ✓/✗ + fix hints; exit 1 if something required is missing
scripts/bootstrap.sh [--skip-android] [--skip-workloads]   # idempotent; never runs sudo, prints sudo commands
```
bootstrap: Homebrew `openjdk@17` + `android-commandlinetools` cask (seed), Android SDK in `~/Library/Android/sdk` (platform-tools, emulator, `platforms;android-36`, `build-tools;36.0.0`, arm64 Google APIs image, licenses), `git config core.hooksPath .githooks`; prints env vars, writes no profile.

## Steps only the user can do (agents: ask, don't attempt)
- **Workloads** — `/usr/local/share/dotnet` is root-owned, so install needs sudo:
  `cd <repo> && sudo dotnet workload install macos android ios` (from the repo so `global.json` picks the SDK band); later `sudo dotnet workload update`. Never `chown` the dotnet root.
- **Xcode** — the current macos workload pack (macOS 27.0 SDK) expects **Xcode 27**. After installing from the App Store and launching once:
  ```bash
  sudo xcode-select -s /Applications/Xcode.app/Contents/Developer
  sudo xcodebuild -license accept
  sudo xcodebuild -runFirstLaunch
  ```
  Version-mismatch error → install the Xcode it names; temporary escape hatch for a minor mismatch: `-p:ValidateXcodeVersion=false`.
- **`scripts/mac-dev-cert.sh`** — prompts for the login password (skill `mac-build-run`).
- **Wireless adb pairing** — needs the code shown on the phone (skill `android-build-run`).

## Gotchas
- **JDK**: .NET 10 Android workload accepts JDK 17.0–21.x; we use Homebrew `openjdk@17` (keg-only, not in `/usr/libexec/java_home`). Scripts find it under `$(brew --prefix)/opt/openjdk@17`. The JDK home is **`$(brew --prefix)/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home`** — the keg root (`…/opt/openjdk@17`) has a working `bin/java` but MSBuild rejects it (`XA5300 … Could not find required file 'jvm'`). `jdk_ok` requires a `release` file and `lib/server/libjvm.dylib`, so doctor rejects a keg-root `JAVA_HOME` and prints the correct `…/Contents/Home` path. A `JAVA_HOME` > 21 breaks Android builds (doctor warns).
- **sdkmanager** (cmdline-tools 23+) hands off to the new `android` CLI ("deprecated, Android CLI will be used instead") with a different `--list_installed` format (`platforms/android-36` paths). Scripts detect installed packages via `<sdk>/<path>/package.xml` — don't parse sdkmanager output. First run self-downloads and shows a metrics notice (`--no-metrics`).
- **Raw `dotnet build` / Rider** for Android: export `JAVA_HOME` and `ANDROID_HOME` (printed by bootstrap) or pass `-p:AndroidSdkDirectory=… -p:JavaSdkDirectory=…`.
- **CA1711** (`AnalysisLevel latest-recommended` + warnings-as-errors) rejects Apple's `AppDelegate`/`SceneDelegate` names; the Mac/iOS csproj files carry `NoWarn CA1711`.
- **Core tests** need no workloads: `scripts/test.sh` (= `dotnet test --project tests/PenBridge.Core.Tests`; MTP via `global.json`, the `dotnet test <path>` form is rejected; filters like `--filter-class "*Protocol*"`).
