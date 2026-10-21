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

You don't need them for builds. On macOS, `Directory.Build.targets` fills in `JavaSdkDirectory` and `AndroidSdkDirectory` for the Android head from the locations bootstrap installs to, so a plain `dotnet build PenBridge.slnx` and Rider builds work with no profile changes:

| Property | Auto-detected value (first that exists) | Used only when |
|---|---|---|
| `JavaSdkDirectory` | `/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home`, then the `/usr/local` equivalent | `JavaSdkDirectory` and `JAVA_HOME` are both unset |
| `AndroidSdkDirectory` | `$HOME/Library/Android/sdk` | `AndroidSdkDirectory`, `ANDROID_HOME` and `ANDROID_SDK_ROOT` are all unset |

An explicit `-p:` property (or one Rider passes) and the environment variables always win, and CI (Linux, `setup-java`) never reaches the defaults. Check what a build will use with `scripts/doctor.sh` (section "Android build resolution") or `dotnet msbuild src/PenBridge.Android -getProperty:JavaSdkDirectory -getProperty:AndroidSdkDirectory` (empty = taken from the env var). The `scripts/android-*` helpers still set the variables per process and pass both properties explicitly. Export them in your profile only if you want `adb`/`emulator` on your `PATH`.

**Rider** evaluates projects with the same MSBuild, so it picks up the defaults; nothing needs setting. Leave Settings → Build, Execution, Deployment → Android → Android SDK/JDK location empty (or set them to the paths above). A path set there is passed to MSBuild and overrides the defaults.

### Manual steps (sudo / GUI)

The bootstrap script never runs sudo. It prints these commands for you instead.

1. **.NET workloads.** The SDK in `/usr/local/share/dotnet` is root-owned:
   ```bash
   cd <repo> && sudo dotnet workload install macos android ios
   ```
   Run it from the repo so `global.json` selects the right SDK band. CI pins the workload set in `.github/toolchain.env`; install that set locally with `--version` if you want to match it, and avoid `dotnet workload update` unless you also bump the pin (see the Xcode note below).
2. **Xcode.** Install it from the App Store, launch it once, then:
   ```bash
   sudo xcode-select -s /Applications/Xcode.app/Contents/Developer
   sudo xcodebuild -license accept
   sudo xcodebuild -runFirstLaunch
   ```
   The `macos` workload is built against one specific Xcode. The current workload set ships the macOS 27.0 SDK pack, which targets Xcode 27. If the build reports an Xcode version mismatch, install the Xcode it names. As a temporary escape hatch for a minor mismatch, use `-p:ValidateXcodeVersion=false`. CI and release builds pin the workload set and Xcode together in `.github/toolchain.env`; a local `dotnet workload update` can move past it, so if a local build wants a newer Xcode than CI uses, install that Xcode or return to the pinned set with `sudo dotnet workload update --version <set>`.
3. **Mac dev signing identity** (once, no sudo): run `scripts/mac-dev-cert.sh` (see below).

## Daily workflows

### Core tests (any OS, no workloads)

```bash
scripts/test.sh                              # = dotnet test --project tests/PenBridge.Core.Tests
scripts/test.sh -c Release
scripts/test.sh --filter-class "*Protocol*"  # xUnit v3 on Microsoft.Testing.Platform filter syntax
```

`global.json` opts into Microsoft.Testing.Platform, so the `--project` form is required. The old `dotnet test <path>` form is rejected.

**Test reports and coverage.** CI writes the xUnit markdown report and a PenBridge.Core coverage summary (ReportGenerator) to the job summary, and uploads `core-test-results` (JUnit, markdown, cobertura) and `core-coverage-report` (HTML). Locally, coverage is opt-in: `scripts/test.sh --coverage --coverage-output-format cobertura` (output under `tests/PenBridge.Core.Tests/TestResults`). CI also reports tests running over 60 s (`--long-running 60`) and aborts the run after 10 minutes (`--timeout 10m`); `--hangdump --hangdump-timeout 8m` writes a Mini dump first, uploaded as `core-hang-dump` (3 days) only when the job fails. Coverage is informational; there is no gate.

### Trimmed builds keep what Core needs

```bash
scripts/smoke-trimmed-pairing.sh                 # SimClient trimmed (partial + full) pairs and draws in-process
scripts/smoke-trimmed-pairing.sh --aot           # also Native AOT
scripts/check-bundle.sh --mac Release --android Release --ios Release   # after building the heads
```

`check-bundle.sh` checks that every type a bundled assembly references still exists in the bundle, and that the J-PAKE types are present whenever `PairingSession` is. It reads the Mac `.app`'s `MonoBundle`, the iOS `.app`, and the ILLink output that goes into the APK (`obj/<config>/net10.0-android/<rid>/linked`), because the APK packs assemblies into a compressed store. CI runs both scripts (issue #46).

The Mac Release app is NativeAOT (issue #42), so its bundle holds no assemblies. For it, `check-bundle.sh` reads the native symbols of each architecture (from `PenBridge.app.dSYM`, since the universal executable is stripped) and requires the J-PAKE code to be compiled in wherever `PairingSession` is. With `--mac Release` it also checks the reference closure of the ILLink output that ILC compiled (`obj/Release/net10.0-macos/<rid>/linked`). `--require-pairing` (used in CI) fails if the pairing code is missing altogether.

### Mac server

```bash
scripts/mac-dev-cert.sh            # one-time: "PenBridge Dev" code-signing identity
scripts/mac-run.sh                 # Debug build → sign → open the .app (menu-bar icon, no Dock icon)
scripts/mac-run.sh --release       # dotnet publish -c Release: the universal NativeAOT app
scripts/mac-run.sh --logs          # + log stream --predicate 'process == "PenBridge"'
scripts/mac-run.sh --no-build      # relaunch the last build
scripts/mac-run.sh --reset-tcc     # tccutil reset Accessibility com.blake11.penbridge, then run
scripts/mac-run.sh --reset-keychain   # delete the app's Keychain items (Release; asks; phones must pair again)
scripts/mac-run.sh --reset-dev-secrets   # delete the Debug file secret store (asks; phones must pair again)
```

**Every build signs** — `dotnet build`, `dotnet run`, Rider and `mac-run.sh` alike (issue #55). `PenBridge.Mac.csproj` runs `security find-identity -p codesigning` before the SDK's signing step: if the `PenBridge Dev` identity is in the keychain it sets `CodesignKey="PenBridge Dev"`, otherwise the SDK signs ad-hoc and the build prints a hint. The SDK's own signing (`EnableCodeSigning`, on by default) signs every native library in `Contents/MonoBundle`, then the bundle, with `Entitlements.plist`. Check with `codesign -dvv <app>` (`Authority=PenBridge Dev`).
- Opt out with `-p:PenBridgeDevSigning=false`; pick another identity with `-p:PenBridgeDevCodesignKey="<name>"`; an explicit `-p:CodesignKey=…` wins.
- `build-release.yml` publishes with `-p:EnableCodeSigning=false` (the target is skipped) and signs with `scripts/mac-sign.sh --no-entitlements <app> <identity>`: each MonoBundle library first, then the bundle with `codesign --force --deep --identifier com.blake11.penbridge`. The build rewrites the Microsoft-signed runtime dylibs there and `--deep` treats MonoBundle as resources, so a build with signing off that only re-signs the bundle is killed at launch with "Code Signature Invalid". `--hardened` adds `--options runtime --timestamp` (notarization). See `scripts/mac-sign.sh --help`.

**Release is NativeAOT** (issue #42, ADR-0016). `PenBridge.Mac.csproj` sets `PublishAot` for Release, and the macOS SDK applies it only to `dotnet publish`, which then trims every assembly fully and compiles the app into one universal executable (`bin/Release/net10.0-macos/PenBridge.app`; the per-RID bundles next to it are intermediates). `dotnet build -c Release` still produces an untrimmed JIT bundle; Debug stays JIT for fast iteration. The NativeAOT bundle's `MonoBundle` holds only the five `libSystem.*.Native` dylibs, and its signature carries no entitlements: `Entitlements.plist` only grants `allow-jit`, which CoreCLR needs under the hardened runtime and NativeAOT doesn't. A publish takes about as long as a JIT build (about 30 s clean, both architectures). Idle memory, launch time and how to measure them: skill `mac-build-run` ("Memory and startup").

`mac-run.sh` kills a running instance, builds (publishes for `--release`), re-signs with `mac-sign.sh` only if the bundle isn't already signed with an existing `PenBridge Dev` identity (an old build, `--no-build`, or `-- -p:EnableCodeSigning=false`), and launches.

#### Why the dev certificate (Accessibility / TCC)

macOS stores the Accessibility grant against the app's designated requirement. With ad-hoc signing, that requirement is the cdhash, which changes on every build. The grant then silently stops applying: `AXIsProcessTrusted()` returns false, and the toggle in System Settings still looks on.

When the app is signed with a fixed certificate, the requirement becomes `identifier "com.blake11.penbridge" and certificate root = H"…"` (for a self-signed certificate root and leaf are the same certificate), which stays stable across rebuilds.

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

#### Where secrets live: Debug files, Release Keychain

The server identity and the paired-device registry go through `Services/SecretStoreFactory.cs`:
- **Debug builds** use a file store, `~/Library/Application Support/PenBridge-Dev/secrets` (Core's `FileSecretStore`: directory 0700, files 0600, not encrypted at rest). No Keychain prompts, however often you recompile. The log says `Debug build: using development file secret store (not Keychain)` at startup.
- **Release builds always use the login Keychain** (service `com.blake11.penbridge`).
- The two never share secrets. A Debug build has its own server identity (fingerprint) and its own paired devices, so a phone paired with a Debug build must pair again with a Release build, and the other way round.
- Start Debug clean with `scripts/mac-run.sh --reset-dev-secrets`.

#### Keychain prompts (Release builds)

macOS checks two things on each Keychain read:
- **The item ACL** trusts the app by its designated requirement. Ad-hoc builds (cdhash) are a new app on every rebuild; `PenBridge Dev` builds share one requirement.
- **The partition list**. For code without an Apple Team ID, which includes the self-signed dev certificate, macOS records the build's **cdhash** there. Any build that recompiled something has a new cdhash and is outside the partition, so macOS asks again ("PenBridge wants to use your confidential information…"); **Always Allow** adds that build. An incremental build that rebuilt nothing keeps its cdhash and doesn't ask. The app can't widen the partition list itself (that needs the keychain password), and an explicit access object at creation gets the same cdhash partition. A certificate with a Team ID (Apple Development from a free Apple ID personal team, or Developer ID) should get a stable `teamid:` partition instead; that is untested here.

This is why Debug builds use the file store. A locally built Release prompts once per keychain item after each recompile. Items created by older builds (including Debug builds from before the file store) carry stale cdhash ACL entries. To start clean, which removes the Release server identity and every pairing so phones must pair again:

```bash
scripts/mac-run.sh --reset-keychain    # asks for "yes"; or Keychain Access → login → search com.blake11.penbridge → delete
```

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
- **No Java runtime / wrong JDK.** The Homebrew `openjdk@17` is keg-only and is not registered in `/usr/libexec/java_home`, because registering it needs a sudo symlink. The scripts find it under `$(brew --prefix)/opt/openjdk@17`. The JDK home is `…/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home`; the keg root `…/opt/openjdk@17` has a working `bin/java` but MSBuild rejects it with `XA5300 … Could not find required file 'jvm'`, so `doctor.sh` warns when `JAVA_HOME` lacks `release` or `lib/server/libjvm.dylib`. If `JAVA_HOME` points at a JDK newer than 21, Android builds fail, and `doctor.sh` warns about it.
- **Workload install "permission denied" / "access to the path is denied".** The dotnet root is root-owned, so use the `sudo` command above. Don't `chown` `/usr/local/share/dotnet`, because the official installer would fight it.
- **Android build can't find the JDK/SDK (`XA5300`) in Rider or with raw `dotnet build`.** Run `scripts/doctor.sh` and read "Android build resolution". The usual causes are a stale `JAVA_HOME`/`ANDROID_HOME` (they beat the auto-detection), a wrong JDK/SDK path in Rider's Android settings, or a JDK/SDK outside the default locations. Fix or unset the variable, or pass `-p:AndroidSdkDirectory=… -p:JavaSdkDirectory=…`. The workload's `warning … reading the output of '/usr/libexec/java_home -X' … Root element is missing` means no JDK is registered with `java_home` (true for the keg-only Homebrew JDK). It doesn't appear once a JDK is resolved, and it would not fail the build anyway because `TreatWarningsAsErrors` only covers compiler warnings.
- **`Unable to obtain lock file access on '/tmp/NuGetScratch/…'`.** A `sudo dotnet workload install` left a root-owned `/tmp/NuGetScratch`, and NuGet falls back to it when `TMPDIR` is unset (e.g. under `env -i`). Keep `TMPDIR` set, or run `sudo rm -rf /tmp/NuGetScratch`.
- **`Could not find BouncyCastle.Cryptography` at runtime / pairing closes the connection (Mac).** The bundle lacks an assembly Core needs. Run `scripts/check-bundle.sh --mac Debug`. See the toolchain skill ("BouncyCastle trimming") for the cause. Incremental builds don't remove a stale dll from an existing bundle, so delete `src/PenBridge.Mac/bin` and `obj` before you trust a check after changing trim settings.
- **Emulator won't boot.** Check `$TMPDIR/penbridge-emulator.log`. Try `--cold-boot`, then `--wipe`. `--recreate` rebuilds the AVD after you change `AVD_DEVICE` or `ANDROID_API_LEVEL`.
- **`adb pair` fails.** The code and the pairing port expire when the dialog closes; reopen it. Use the pairing port for `pair` and the connect port for `connect`. VPNs and guest Wi-Fi with client isolation block wireless adb.
- **Accessibility granted, but nothing happens.** The signature changed. Check `codesign -dr - <app>`: it should show `certificate root`/`certificate leaf`, not `cdhash`. Then run `--reset-tcc`.
- **Keychain prompt after a rebuild.** Debug builds never touch the Keychain; if one prompts, check the startup log for `development file secret store`. In a local Release build, a prompt is expected after a recompile (see "Keychain prompts"); choose Always Allow. A prompt after *every* launch or a `cdhash` designated requirement means the build signed ad-hoc: check the build output for `PenBridge: no "PenBridge Dev" code-signing identity` and run `scripts/mac-dev-cert.sh`.
- **Phone paired, but the Mac says it doesn't know it.** It was paired with the other build configuration. Debug and Release have separate identities and paired devices, so pair again.
