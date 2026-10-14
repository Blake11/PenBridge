# Releasing PenBridge

All apps share one version. It lives in `Directory.Build.props`:

```xml
<Version>0.1.0</Version><!-- x-release-please-version -->
```

[release-please](https://github.com/googleapis/release-please) owns that line, `CHANGELOG.md` and `.release-please-manifest.json`. Don't edit them by hand.

## When we release (ADR-0021)

There is no release phase: **every roadmap phase ends with a release**, cut after the phase is verified on the Mac and the phone.

| Release | Version | How release-please gets there |
|---|---|---|
| End of Phase 3 (Android client), the first release | **1.0.0** | `Release-As: 1.0.0` footer, once ([below](#cutting-100)) |
| End of each later phase | next **minor**: 1.1.0, 1.2.0 … | automatic: the phase's `feat` commits bump the minor |
| Bug fixes between phases | next **patch**: 1.1.1, 1.1.2 … | automatic if only `fix`/`perf` are unreleased; otherwise `Release-As` ([below](#patch-release-between-phases)) |
| Breaking change (`!`, `BREAKING CHANGE:`) | next **major** | **only with an explicit user decision** ([below](#major-versions)) |

## Flow

1. PRs are squash-merged into `main` with Conventional Commit titles.
2. On every push to `main`, `.github/workflows/release-please.yml` opens or updates a **release PR** (`chore: release X.Y.Z`). That PR bumps the version and the changelog.
   - From 1.0.0 on: `feat` → minor, `fix`/`perf` → patch, `!`/`BREAKING CHANGE` → major.
   - Before 1.0 (`bump-minor-pre-major`), a breaking change bumps the minor version instead. That setting has no effect from 1.0.0 on.
   - `docs`, `ci`, `chore`, `test`, `build`, `refactor` and `style` alone don't bump anything; they ride along with the next release.
   - Only `feat`, `fix`, `perf`, `revert`, `refactor` and `docs` appear in the changelog.
3. The release PR is held until the phase (or the fix) is verified. The user merges it; it never merges itself.
4. Merging the release PR creates the tag `vX.Y.Z` and a GitHub release. The same workflow then calls `.github/workflows/build-release.yml`, which:
   - builds `PenBridge.app` (Release, NativeAOT), checks the version in its `Info.plist`, signs it ad-hoc with `scripts/mac-sign.sh <app> -` (each `Contents/MonoBundle` native library, then the bundle), and packages `PenBridge-X.Y.Z-macos.zip` and `PenBridge-X.Y.Z-macos.dmg` (`hdiutil`, with an Applications link)
   - builds a release-signed `PenBridge-X.Y.Z-android.apk` with `ApplicationDisplayVersion=X.Y.Z` and `ApplicationVersion` (versionCode) `= major*1000000 + minor*1000 + patch`, and checks with `apksigner` that the release key (not the debug key) signed it
   - writes `SHA256SUMS` and uploads everything to the release.

   The release is public as soon as it's created; the assets follow about 15–30 minutes later.

The iOS app is a placeholder and is not released.

### Release-As footers

release-please reads a `Release-As: X.Y.Z` footer from any commit since the last release and uses that version for the next release PR. PRs are squash-merged, so the footer goes in the **squash commit body**:

```bash
gh pr merge <N> -R Blake11/PenBridge --squash --body "Release-As: 1.0.0"
```

`--body` replaces GitHub's default squash body. A footer only affects the release it lands before; afterwards normal bumping resumes.

### Cutting 1.0.0

1. Dry-run the build with the target version and test the artifacts on the Mac and the phone ([Dry run](#dry-run)).
2. Merge the phase's last PR (normally the README update) with `--body "Release-As: 1.0.0"`.
3. release-please retitles the open release PR to `chore: release 1.0.0`. Check its changelog.
4. Let CI run on the release PR. Without `RELEASE_PLEASE_TOKEN` its runs wait for approval: open the PR's checks (or Actions) and click **Approve and run**.
5. Merge the release PR. `v1.0.0`, the GitHub release and its assets follow.
6. Smoke-test the published `.dmg` and APK, then close the phase epic.

### Patch release between phases

- Only `fix`/`perf` commits since the last release: release-please already proposes the patch. Merge the release PR.
- `main` already has unreleased `feat` commits (the next phase has started): either
  - land the fix with `--body "Release-As: 1.1.1"`. The release then also ships those `feat` commits, listed under Features, or
  - release the fix alone: merge the fix and its release PR **before** merging further `feat` PRs. There is no hotfix branch set up; releasing from one needs a release-please run with `target-branch`, so ask first.

### Major versions

A `!` or `BREAKING CHANGE:` footer makes release-please propose the next major. That needs an explicit user decision; check PR titles for `!` before merging. If one slipped onto `main` without a decision, `main` can't be rewritten, so either:
- edit the merged PR's description and add a block release-please reads instead of the commit message:
  ```
  BEGIN_COMMIT_OVERRIDE
  feat(scope): the same subject, without the bang
  END_COMMIT_OVERRIDE
  ```
- or pin the version with a `Release-As` footer on the next merge.

Record the call in the release PR either way.

### Dry run

`build-release.yml` defaults to a dry run when started by hand: it builds every asset from the selected ref and keeps them as **workflow artifacts** (`release-mac`, `release-android`). No release or tag is touched. `version` stamps a future version, so the artifacts match what the release will ship:

```bash
gh workflow run build-release.yml -R Blake11/PenBridge --ref main -f dry_run=true -f version=1.0.0
gh run watch -R Blake11/PenBridge "$(gh run list -R Blake11/PenBridge --workflow build-release.yml -L 1 --json databaseId --jq '.[0].databaseId')"
gh run download -R Blake11/PenBridge <run-id> -D ~/Downloads/penbridge-dry-run
```

The run summary lists the SHA-256 of each asset and the APK signer. With the keystore secrets set, the APK is release-signed; without them it's unsigned (`-unsigned.apk`) and the run warns.

### Rebuilding assets by hand

To rebuild and re-upload the assets of an existing release (with `--clobber`):

```bash
gh workflow run build-release.yml -R Blake11/PenBridge -f tag=v1.0.0 -f dry_run=false
```

The tag must match the version in `Directory.Build.props` at that ref. `version` is rejected outside dry runs, and a non-dry run without `tag` fails, so nothing can be uploaded by accident.

## Secrets and variables

Set these in **Settings → Secrets and variables → Actions**.

| Name | Kind | Required | Purpose |
|---|---|---|---|
| `ANDROID_KEYSTORE_BASE64` | secret | yes, for releases | base64 of the release keystore (`.jks`) |
| `ANDROID_KEYSTORE_PASSWORD` | secret | yes | keystore (store) password |
| `ANDROID_KEY_ALIAS` | secret | yes | key alias inside the keystore |
| `ANDROID_KEY_PASSWORD` | secret | yes | key password |
| `RELEASE_PLEASE_TOKEN` | secret | optional | fine-grained PAT (contents + pull requests: read/write). Without it, the release PR is opened with `GITHUB_TOKEN`, and CI does **not** run on that PR. |
| `MACOS_NOTARIZE` | variable | optional | `true` switches on Developer ID signing and notarization |
| `MACOS_CERT_P12` | secret | when notarizing | base64 of the "Developer ID Application" certificate + private key (`.p12`) |
| `MACOS_CERT_PASSWORD` | secret | when notarizing | password of that `.p12` |
| `APPLE_ID` | secret | when notarizing | Apple ID email of the developer account |
| `APPLE_TEAM_ID` | secret | when notarizing | 10-character team id |
| `APPLE_APP_PASSWORD` | secret | when notarizing | app-specific password from appleid.apple.com |

Also enable **Settings → Actions → General → Workflow permissions → "Allow GitHub Actions to create and approve pull requests"**, so release-please can open its PR.

## Android release keystore

Create the keystore once and **back it up somewhere safe**. Losing it means future updates can't be installed over existing installs.

```bash
keytool -genkeypair -v \
  -keystore penbridge-release.jks \
  -storetype JKS \
  -alias penbridge \
  -keyalg RSA -keysize 4096 \
  -validity 10000 \
  -dname "CN=PenBridge, O=blake11"
```

Encode it and store the secrets with `gh`:

```bash
base64 -i penbridge-release.jks | tr -d '\n' > penbridge-release.jks.b64   # macOS
# Linux: base64 -w0 penbridge-release.jks > penbridge-release.jks.b64

gh secret set ANDROID_KEYSTORE_BASE64 < penbridge-release.jks.b64
gh secret set ANDROID_KEYSTORE_PASSWORD     # prompts
gh secret set ANDROID_KEY_ALIAS --body penbridge
gh secret set ANDROID_KEY_PASSWORD          # prompts
rm penbridge-release.jks.b64
```

Never commit the `.jks` or the base64 file.

## Installing the Android APK (sideload)

PenBridge isn't on Google Play yet ([#22](https://github.com/Blake11/PenBridge/issues/22)), so the APK is sideloaded:

1. On the phone, open the release page and download `PenBridge-X.Y.Z-android.apk`.
2. Open the downloaded file. Android asks to allow installs from that app (the browser or Files): **Settings → Allow from this source**, then go back and tap **Install**.
3. Google Play Protect may warn about an unknown developer: choose **More details → Install anyway**.

Or from a computer with adb (USB or wireless debugging, `scripts/android-device.sh`):

```bash
adb install -r PenBridge-X.Y.Z-android.apk
```

Updates install over the previous version because every release is signed with the same key. A development build (debug-signed, from `scripts/android-run.sh`) has a different signature: uninstall it first (`adb uninstall com.blake11.penbridge`), which also deletes its pairings.

## macOS signing

### Today: ad-hoc, not notarized

There is no paid Apple Developer account yet (ADR-0007), so the app is ad-hoc signed and Gatekeeper warns on first launch. To open it:

1. Open the `.dmg` and drag **PenBridge** to **Applications**.
2. In Finder, **right-click (or Control-click) PenBridge → Open → Open**. On macOS 15+, if no Open button appears, try to launch it once, then go to **System Settings → Privacy & Security** and click **Open Anyway**.

Or remove the quarantine flag in Terminal:

```bash
xattr -dr com.apple.quarantine /Applications/PenBridge.app
```

Then grant **Accessibility** when PenBridge asks (System Settings → Privacy & Security → Accessibility). Every new ad-hoc build has a different signature, so after an update you may need to remove PenBridge from the Accessibility list and add it again.

Users can check a download against `SHA256SUMS`:

```bash
shasum -a 256 -c SHA256SUMS --ignore-missing
```

### Later: Developer ID + notarization

The steps already exist in `build-release.yml` but are skipped. To turn them on:

1. Join the Apple Developer Program and create a **Developer ID Application** certificate. Export it with its private key from Keychain Access as `.p12`.
2. `base64 -i cert.p12 | gh secret set MACOS_CERT_P12`, then `gh secret set MACOS_CERT_PASSWORD`.
3. Create an app-specific password at appleid.apple.com, then set `APPLE_ID`, `APPLE_TEAM_ID` and `APPLE_APP_PASSWORD`.
4. `gh variable set MACOS_NOTARIZE --body true`.

The workflow then imports the certificate into a temporary keychain. It signs the app with `scripts/mac-sign.sh --hardened` (hardened runtime and secure timestamp on every MonoBundle library and the bundle, `src/PenBridge.Mac/Entitlements.plist` on the main executable), and signs the dmg. Under the hardened runtime CoreCLR needs `com.apple.security.cs.allow-jit` in `Entitlements.plist`; without it the app aborts with "Failed to initialize the VM". It submits the dmg with `notarytool --wait` and staples both the dmg and the app. Update the "open the un-notarized app" instructions above once this is live.
