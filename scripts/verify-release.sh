#!/usr/bin/env bash
# Smoke-check the assets of a release or of a build-release dry run.
#
# Usage: scripts/verify-release.sh <tag> | --run <run-id> [--version X.Y.Z] [--keep] [--help]
#                   (a pre-release suffix like 1.3.0-rc.1 is accepted)
#   <tag>             release tag, e.g. v1.2.1 (downloads the release assets)
#   --run <run-id>    dry-run workflow run id (downloads its artifacts)
#   --version X.Y.Z   expected version; default: from the tag, else from the asset file names
#   --keep            keep the temp download dir (its path is printed)
#
# Checks: SHA256SUMS (releases only; dry runs have none); macOS .zip app (CFBundleShortVersionString, bundle id, ad-hoc signature,
# universal binary); .dmg mounts and holds the same version; APK package, versionName,
# versionCode (major*1000000 + minor*1000 + patch) and signer SHA-256 (the release cert).
# Needs gh (authenticated), a JDK for apksigner (resolved like scripts/doctor.sh) and the
# Android build-tools. Exit code: 0 if everything matches, 1 otherwise.
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

REPO="Blake11/PenBridge"
RELEASE_CERT_SHA256="4b5ba8ca1d4da24aafd4e6e98b0c481583b6939c47fecb889549f11259d99cde"

tag=""; run_id=""; version=""; keep=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help) usage_from_header "$0"; exit 0 ;;
    --run) [[ $# -ge 2 ]] || die "--run needs a run id"; run_id="$2"; shift 2 ;;
    --version) [[ $# -ge 2 ]] || die "--version needs X.Y.Z"; version="$2"; shift 2 ;;
    --keep) keep=1; shift ;;
    -*) die "unknown argument: $1" ;;
    *) [[ -z "$tag" ]] || die "only one tag allowed"; tag="$1"; shift ;;
  esac
done
[[ -n "$tag" || -n "$run_id" ]] || die "give a tag or --run <run-id> (see --help)"
[[ -z "$tag" || -z "$run_id" ]] || die "give either a tag or --run, not both"
have gh || die "gh is required"
[[ -z "$tag" || -n "$version" ]] || version="${tag#v}"

TMP="$(mktemp -d "${TMPDIR:-/tmp}/penbridge-verify.XXXXXX")"
MNT=""
cleanup() {
  [[ -n "$MNT" ]] && { hdiutil detach "$MNT" -quiet || hdiutil detach -force "$MNT" -quiet; } >/dev/null 2>&1 || true
  if [[ $keep -eq 1 ]]; then info "kept $TMP"; else rm -rf "$TMP"; fi
}
trap cleanup EXIT

ERRORS=0
bad() { fail "$1"; [[ -n "${2:-}" ]] && hint "$2"; ERRORS=$((ERRORS+1)); return 0; }
expect() {  # $1 label, $2 actual, $3 expected
  if [[ "$2" == "$3" ]]; then ok "$1: $2"; else bad "$1: got '$2', expected '$3'"; fi
}

step "Download"
if [[ -n "$tag" ]]; then
  gh release download "$tag" -R "$REPO" -D "$TMP/assets" || die "could not download release $tag"
else
  gh run download "$run_id" -R "$REPO" -D "$TMP/assets" || die "could not download run $run_id artifacts"
fi
find_one() { find "$TMP/assets" -type f -name "$1" | head -n1; }
ZIP="$(find_one 'PenBridge-*-macos.zip')"; DMG="$(find_one 'PenBridge-*-macos.dmg')"
APK="$(find_one 'PenBridge-*-android.apk')"; SUMS="$(find_one 'SHA256SUMS')"
# Dry-run artifacts carry no SHA256SUMS (it is generated only when publishing).
pairs=("macOS .zip:$ZIP" "macOS .dmg:$DMG" "Android .apk:$APK")
[[ -n "$tag" ]] && pairs+=("SHA256SUMS:$SUMS")
for pair in "${pairs[@]}"; do
  [[ -n "${pair#*:}" ]] && ok "found ${pair%%:*}: $(basename "${pair#*:}")" || bad "missing ${pair%%:*}"
done
[[ $ERRORS -eq 0 ]] || die "incomplete assets"
if [[ -z "$version" ]]; then
  version="$(basename "$APK" | sed -E 's/^PenBridge-(.*)-android\.apk$/\1/')"
  info "version from asset names: $version"
fi
[[ "$version" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)(-[0-9A-Za-z.-]+)?$ ]] || die "version '$version' is not X.Y.Z[-prerelease]"
# Core X.Y.Z only; a pre-release suffix does not change the code (as in build-release.yml).
CODE=$(( BASH_REMATCH[1]*1000000 + BASH_REMATCH[2]*1000 + BASH_REMATCH[3] ))

step "Checksums"
if [[ -z "$SUMS" ]]; then info "no SHA256SUMS in a dry run; skipped"; else
# Dry-run artifacts may sit in separate dirs; check each file next to its sum entry by name.
while read -r sum name; do
  [[ -n "$sum" ]] || continue
  name="${name#\*}"; f="$(find_one "$name")"
  if [[ -z "$f" ]]; then bad "$name listed in SHA256SUMS but not downloaded"; continue; fi
  actual="$(shasum -a 256 "$f" | awk '{print $1}')"
  if [[ "$actual" == "$sum" ]]; then ok "$name"; else bad "$name checksum mismatch"; fi
done < "$SUMS"
for f in "$ZIP" "$DMG" "$APK"; do
  awk -v n="$(basename "$f")" '{ s=$2; sub(/^\*/, "", s); if (s == n) found=1 } END { exit !found }' "$SUMS" || bad "$(basename "$f") not listed in SHA256SUMS"
done
fi

check_app() {  # $1 = .app path, $2 = label
  local app="$1" plist="$1/Contents/Info.plist"
  local v id exe archs
  v="$(plutil -extract CFBundleShortVersionString raw "$plist" 2>/dev/null)" || { v=""; bad "$2: cannot read CFBundleShortVersionString"; }
  expect "$2 version" "$v" "$version"
  id="$(plutil -extract CFBundleIdentifier raw "$plist" 2>/dev/null)" || { id=""; bad "$2: cannot read CFBundleIdentifier"; }
  expect "$2 bundle id" "$id" "$APP_ID"
  exe="$(plutil -extract CFBundleExecutable raw "$plist" 2>/dev/null)" || { exe=""; bad "$2: cannot read CFBundleExecutable"; }
  archs="$(lipo -archs "$app/Contents/MacOS/$exe" 2>/dev/null | tr ' ' '\n' | sort | tr '\n' ' ')" || archs=""
  expect "$2 archs" "${archs% }" "arm64 x86_64"
  local sig; sig="$(codesign -dv "$app" 2>&1 || true)"
  grep -q "^Identifier=$APP_ID\$" <<<"$sig" && ok "$2 signature identifier $APP_ID" || bad "$2 signature identifier is not $APP_ID"
  grep -q "^Signature=adhoc\$" <<<"$sig" && ok "$2 signature is ad-hoc" || bad "$2 signature is not ad-hoc"
  codesign --verify --deep --strict "$app" 2>/dev/null && ok "$2 signature verifies" || bad "$2 signature does not verify"
}

step "macOS .zip"
mkdir -p "$TMP/zip"
ditto -x -k "$ZIP" "$TMP/zip" || bad "could not extract $(basename "$ZIP")"
APP="$(find "$TMP/zip" -maxdepth 2 -name '*.app' -type d | head -n1)"
if [[ -n "$APP" ]]; then check_app "$APP" ".zip app"; else bad "no .app inside $(basename "$ZIP")"; fi

step "macOS .dmg"
MNT="$TMP/mnt"; mkdir -p "$MNT"
if hdiutil attach "$DMG" -nobrowse -readonly -noverify -mountpoint "$MNT" -quiet >/dev/null 2>&1; then
  ok "mounts"
  DAPP="$(find "$MNT" -maxdepth 2 -name '*.app' -type d | head -n1)"
  if [[ -n "$DAPP" ]]; then check_app "$DAPP" ".dmg app"; else bad "no .app inside the .dmg"; fi
else
  MNT=""; bad "the .dmg does not mount"
fi

step "Android APK"
sdk="$(resolve_android_home)"
bt="$(find "$sdk/build-tools" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | sort -V | tail -n1)" || true
[[ -n "$bt" ]] || die "no Android build-tools in $sdk (scripts/bootstrap.sh)"
badging="$("$bt/aapt2" dump badging "$APK")" || { badging=""; bad "aapt2 dump badging failed"; }
field() { sed -nE "s/^package: (.* )?$1='([^']*)'.*/\2/p" <<<"$badging" | head -n1; }
expect "package" "$(field name)" "$APP_ID"
expect "versionName" "$(field versionName)" "$version"
expect "versionCode" "$(field versionCode)" "$CODE"
if jh="$(resolve_java_home)"; then
  certs="$(JAVA_HOME="$jh" PATH="$jh/bin:$PATH" "$bt/apksigner" verify --print-certs "$APK" 2>&1)" \
    || bad "apksigner verify failed: $certs"
  signer="$(sed -nE 's/^Signer #1 certificate SHA-256 digest: *([0-9a-f]+).*/\1/p' <<<"$certs" | head -n1)"
  expect "signer SHA-256" "$signer" "$RELEASE_CERT_SHA256"
else
  bad "no JDK ${JDK_MIN_MAJOR}–${JDK_MAX_MAJOR} for apksigner" "brew install $JDK_FORMULA"
fi

echo
if [[ $ERRORS -eq 0 ]]; then ok "all checks passed for $version"; else die "$ERRORS check(s) failed for $version"; fi
