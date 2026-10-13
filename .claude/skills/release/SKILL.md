---
name: release
description: How PenBridge releases work — Conventional Commits → release-please release PR → build-release workflow producing the unsigned/ad-hoc macOS .dmg/.zip and signed Android APK on GitHub Releases; secrets, manual rebuilds, and unsigned-Mac-app caveats. Use when preparing or debugging a release, changing versioning, touching release workflows/secrets, or answering how users install the app.
---

# Releases

Details: `docs/releasing.md`, `.github/workflows/release-please.yml`, `.github/workflows/build-release.yml`, `release-please-config.json`. Update this skill if they change.

## Flow
1. PRs squash-merge into `main` with Conventional titles. `feat:` → minor, `fix:`/`perf:` → patch, `feat!:`/`BREAKING CHANGE:` → major (pre-1.0 per release-please config). `docs/ci/chore/test/refactor` don't release.
2. `release-please.yml` (on push to main) maintains a release PR (version bump + CHANGELOG).
3. Merging the release PR (user approval) creates tag `vX.Y.Z` + GitHub Release and calls `build-release.yml`.
4. `build-release.yml` builds the macOS `.zip` + `.dmg` and the Android APK and uploads them to the release.
- Rebuild assets for an existing tag: `gh workflow run build-release.yml -f tag=vX.Y.Z` (ask first). Without `tag` it only produces workflow artifacts.

## Secrets / vars (GitHub repo settings — never commit, never echo)
- `RELEASE_PLEASE_TOKEN` (optional PAT so release PRs trigger CI; falls back to `GITHUB_TOKEN`).
- Android: `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`. Missing keystore → unsigned/debug-signed APK path. Losing the keystore means users must uninstall to update — keep a backup.
- macOS (disabled until a paid Apple account): `MACOS_CERT_P12`, `MACOS_CERT_PASSWORD`, `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD`, toggled by repo variable `MACOS_NOTARIZE=true`.

## Unsigned macOS caveats (ADR-0007)
- The app is ad-hoc signed, not notarized → Gatekeeper blocks first launch. Users: right-click ▸ Open, or System Settings ▸ Privacy & Security ▸ Open Anyway, or `xattr -dr com.apple.quarantine /Applications/PenBridge.app`.
- Ad-hoc signature changes every release → **Accessibility grant is lost on each update**; users must remove and re-add PenBridge in Accessibility. Mention in release notes.
- Never claim the app is notarized; don't enable `MACOS_NOTARIZE` without the user.

## Rules
- Never create tags/releases manually or edit CHANGELOG by hand — release-please owns them.
- Merging the release PR, running workflows, and setting secrets need user approval.
- **Held release PR:** the user may hold the release PR on purpose (e.g. #1, until a usable MVP at the end of Phase 3; it also needs `RELEASE_PLEASE_TOKEN`, #23). Record the hold and its blocker in progress.md "Next steps"/the epic, and when the blocker clears re-raise it with the user (`gh pr list -R Blake11/PenBridge --label "autorelease: pending"`) — it never merges itself.
