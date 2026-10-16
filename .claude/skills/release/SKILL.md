---
name: release
description: How PenBridge releases work — one release per roadmap phase (1.0.0 = Phase 3, minor per phase, patch for fixes), Conventional Commits → release-please release PR, Release-As footers, build-release (dry runs; producing the unsigned/ad-hoc macOS .dmg/.zip and signed Android APK on GitHub Releases); secrets, manual rebuilds, and unsigned-Mac-app caveats. Use when preparing or debugging a release, changing versioning, touching release workflows/secrets, or answering how users install the app.
---

# Releases

Details: `docs/releasing.md`, `.github/workflows/release-please.yml`, `.github/workflows/build-release.yml`, `release-please-config.json`. Update this skill if they change.

## Versioning (ADR-0021)
- **Every phase ends with a release**, after the phase is verified on devices. No separate release phase.
- **1.0.0 = end of Phase 3.** Forced once with a `Release-As: 1.0.0` footer (the 0.x release PR is never merged as 0.x).
- Later phases → next **minor** (1.1.0, 1.2.0 …): automatic from their `feat` commits. A phase with only fixes still needs `Release-As: x.(y+1).0`.
- Fixes between phases → next **patch** (1.1.1 …): automatic if only `fix`/`perf` are unreleased. If `feat` commits already landed: `Release-As: x.y.z` (the feats ship in it) or release the fix before merging more feats. No hotfix branch exists; ask before building one.
- **Major** (`!`/`BREAKING CHANGE`) only with an explicit user decision. If one lands by mistake: `BEGIN_COMMIT_OVERRIDE`…`END_COMMIT_OVERRIDE` in the merged PR's body, or `Release-As`.
- **Footers and squash merges:** the footer must be in the squash commit body: `gh pr merge <N> --squash --body "Release-As: X.Y.Z"` (user runs it). It applies to the next release only.

## Flow
1. PRs squash-merge into `main` with Conventional titles. From 1.0: `feat:` → minor, `fix:`/`perf:` → patch, `!` → major. `docs/ci/chore/test/refactor/build/style` don't bump.
2. `release-please.yml` (on push to main) maintains a release PR (version bump + CHANGELOG). The user holds it until the phase/fix is verified.
3. Without `RELEASE_PLEASE_TOKEN` (#23), the bot's release PR starts no CI. Trigger it with a user-token event: `gh pr close <N> && gh pr reopen <N>` (worked for 1.0.0), or set up the #23 PAT. Required checks must pass before merging.
4. Merging the release PR (user) creates tag `vX.Y.Z` + GitHub Release and calls `build-release.yml` (`dry_run: false`).
5. `build-release.yml` builds the macOS `.zip` + `.dmg` (ad-hoc) and the release-signed APK (checked with `apksigner`: debug-key APKs fail), writes `SHA256SUMS`, uploads to the release.

## Verify the published assets
```bash
gh release download vX.Y.Z -R Blake11/PenBridge -D /tmp/rel && cd /tmp/rel
shasum -a 256 -c SHA256SUMS
JAVA_HOME=/opt/homebrew/opt/openjdk@17 $ANDROID_HOME/build-tools/36.0.0/apksigner verify --print-certs <apk>   # compare with the release keystore's cert (keytool -list -v)
codesign -dv PenBridge.app           # from the .zip/.dmg: ad-hoc, identifier com.blake11.penbridge
lipo -archs PenBridge.app/Contents/MacOS/PenBridge   # x86_64 arm64 (universal)
```
- **Idle budget figures** (release checklist): `scripts/latency-report.sh --disable`, restart the Mac app, wait for "Server listening" + 30 s, then record idle CPU (`ps -o %cpu= -p <pid>`, expect 0.0), footprint (`footprint <pid>`, Activity Monitor's Memory) and RSS — always with the capture off (skill `mac-build-run`, "How to measure memory").
- A locally built Release APK is signed with the same release keystore, so the published APK installs over it in place (`adb install -r`) and keeps the pairing.

## Dry run and rebuilds (ask the user before running workflows)
- **Dry run** (the `workflow_dispatch` default): builds the assets as workflow artifacts only, no release touched. `version` stamps a future version:
  `gh workflow run build-release.yml -R Blake11/PenBridge --ref main -f dry_run=true -f version=1.0.0`, then `gh run download <id> -R Blake11/PenBridge`.
- **Rebuild an existing release:** `gh workflow run build-release.yml -R Blake11/PenBridge -f tag=vX.Y.Z -f dry_run=false` (re-uploads with `--clobber`). Non-dry runs need a tag, and `version` is rejected outside dry runs.
- Locally the same steps pass: `dotnet publish` (Mac, `-p:EnableCodeSigning=false`) → `scripts/mac-sign.sh --no-entitlements <app> -` → `ditto`/`hdiutil`; Android `dotnet publish -o <dir> -p:AndroidKeyStore=true …` emits `<id>.apk` (unsigned) and `<id>-Signed.apk`. `apksigner` needs a JDK on `PATH`.

## Secrets / vars (GitHub repo settings — never commit, never echo)
- `RELEASE_PLEASE_TOKEN` (optional PAT so release PRs trigger CI; falls back to `GITHUB_TOKEN`).
- Android: `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`. Missing keystore → unsigned/debug-signed APK path. Losing the keystore means users must uninstall to update — keep a backup.
- macOS (disabled until a paid Apple account): `MACOS_CERT_P12`, `MACOS_CERT_PASSWORD`, `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD`, toggled by repo variable `MACOS_NOTARIZE=true`.

## CI path filters (`ci.yml`, issue #52)
A `Detect changes` job (`dorny/paths-filter@v4`) gates the jobs with job-level `if:`. Never use workflow-level `paths:`: a required check that never reports blocks the PR, while a job skipped by `if:` reports "skipped" (passing). PRs and pushes to `main` use the same filters; the nightly `schedule` (iOS only on Sundays), `workflow_dispatch` and a failed `Detect changes` run everything. Runs on `main` are never cancelled; PR runs cancel older ones. Full table: CONTRIBUTING.md "CI jobs".

| Changed paths | Jobs |
|---|---|
| `.github/workflows/**`, `.github/actions/**`, `scripts/**`, `.githooks/**` | all |
| `src/PenBridge.Core/**`, `tests/**`, `tools/**`, `build/**`, `**/Directory.Build.*`, `**/Directory.Packages.props`, `global.json`, `PenBridge.slnx`, `.editorconfig` | all |
| `src/PenBridge.Mac/**` | macOS build |
| `src/PenBridge.Android/**` | Android build |
| `src/PenBridge.iOS/**`, `.github/workflows/**`, `.github/actions/**`, `global.json`, `scripts/check-bundle.sh` | iOS build (placeholder), only these (not Core/tests) |
| docs, `.claude/**`, `LICENSE`, issue/PR templates | none (only Conventional Commit title) |

Keep required check names (`name:`) unchanged when editing jobs, and add new projects or build-affecting root files to the filters.

## Unsigned macOS caveats (ADR-0007)
- The app is ad-hoc signed, not notarized → Gatekeeper blocks first launch. Users: right-click ▸ Open, or System Settings ▸ Privacy & Security ▸ Open Anyway, or `xattr -dr com.apple.quarantine /Applications/PenBridge.app`.
- Ad-hoc signature changes every release → **Accessibility grant is lost on each update**; users must remove and re-add PenBridge in Accessibility. Mention in release notes.
- Never claim the app is notarized; don't enable `MACOS_NOTARIZE` without the user.

## Rules
- Never create tags/releases manually or edit CHANGELOG by hand — release-please owns them.
- Merging the release PR, running workflows, and setting secrets need user approval.
- **Held release PR:** the release PR is held until the phase (or fix) is verified on devices (e.g. #1 until Phase 3 / 1.0.0, #88). Record the hold and its blocker in progress.md "Next steps"/the epic, and when the blocker clears re-raise it with the user (`gh pr list -R Blake11/PenBridge --label "autorelease: pending"`) — it never merges itself.
