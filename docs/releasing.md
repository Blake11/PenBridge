# Releasing PenBridge

All apps share one version. It lives in `Directory.Build.props`:

```xml
<Version>0.1.0</Version><!-- x-release-please-version -->
```

[release-please](https://github.com/googleapis/release-please) owns that line, `CHANGELOG.md` and `.release-please-manifest.json`. Don't edit them by hand.

## Flow

1. PRs are squash-merged into `main` with Conventional Commit titles.
2. On every push to `main`, `.github/workflows/release-please.yml` opens or updates a **release PR** (`chore: release X.Y.Z`). That PR bumps the version and the changelog.
   - `feat` → minor, `fix`/`perf` → patch, `!`/`BREAKING CHANGE` → major.
   - Before 1.0 (`bump-minor-pre-major`), a breaking change bumps the minor version instead.
   - Only `feat`, `fix`, `perf`, `revert`, `refactor` and `docs` appear in the changelog.
3. Merging the release PR creates the tag `vX.Y.Z` and a GitHub release. The same workflow then calls `.github/workflows/build-release.yml`, which:
   - builds `PenBridge.app` (Release), signs it ad-hoc (`codesign -s -`), and packages `PenBridge-X.Y.Z-macos.zip` and `PenBridge-X.Y.Z-macos.dmg` (`hdiutil`, with an Applications link)
   - builds a release-signed `PenBridge-X.Y.Z-android.apk` with `ApplicationDisplayVersion=X.Y.Z` and `ApplicationVersion` (versionCode) `= major*1000000 + minor*1000 + patch`
   - writes `SHA256SUMS` and uploads everything to the release.

The iOS app is a placeholder and is not released.

### Rebuilding assets by hand

Go to Actions → **Build release** → *Run workflow*.
- With `tag` set to an existing release (e.g. `v0.2.0`), it rebuilds that tag and re-uploads the assets with `--clobber`. The tag must match the version in `Directory.Build.props` at that ref.
- With `tag` empty, it only produces workflow artifacts. Without the keystore secret, the APK is unsigned.

### Forcing a version

Add a commit to `main` with a `Release-As: 1.0.0` footer:

```bash
git commit --allow-empty -m "chore: release 1.0.0" -m "Release-As: 1.0.0"
```

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

The workflow then imports the certificate into a temporary keychain. It signs the app with the hardened runtime, using `src/PenBridge.Mac/Entitlements.plist` if present, and signs the dmg. It submits the dmg with `notarytool --wait` and staples both the dmg and the app. Update the "open the un-notarized app" instructions above once this is live.
