#!/usr/bin/env bash
# Sign a built PenBridge.app: every Mach-O in Contents/MonoBundle first, then the bundle.
#
# Usage: scripts/mac-sign.sh [--hardened] [--entitlements <plist>|--no-entitlements] <app> <identity>
#   <app>               path to PenBridge.app
#   <identity>          codesign identity: "-" (ad-hoc), "PenBridge Dev", "Developer ID Application: …"
#   --hardened          hardened runtime + secure timestamp (--options runtime --timestamp) on
#                       every signature; required for notarization
#   --entitlements F    entitlements for the main executable
#                       (default: src/PenBridge.Mac/Entitlements.plist if present)
#   --no-entitlements   sign without entitlements
#
# Why per-library signing: the .NET build rewrites the Microsoft-signed runtime dylibs in
# Contents/MonoBundle (invalidating their signature). MonoBundle is not a standard nested-code
# location, so `codesign --deep` treats those dylibs as resources and leaves them broken;
# dyld then kills the app at launch ("Code Signature Invalid"). Used by scripts/mac-run.sh and
# .github/workflows/build-release.yml. Idempotent (--force re-signs).
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

HARDENED=0; ENT="$REPO_ROOT/src/PenBridge.Mac/Entitlements.plist"; ENT_SET=0; POS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --hardened) HARDENED=1 ;;
    --entitlements) [[ $# -ge 2 ]] || die "--entitlements needs a file"; ENT="$2"; ENT_SET=1; shift ;;
    --no-entitlements) ENT="" ;;
    -h|--help) usage_from_header "$0"; exit 0 ;;
    --*) die "unknown option: $1 (see --help)" ;;
    *) POS+=("$1") ;;
  esac
  shift
done
[[ ${#POS[@]} -eq 2 ]] || die "expected <app> <identity> (see --help)"
APP="${POS[0]%/}"; IDENTITY="${POS[1]}"

[[ -d "$APP/Contents" ]] || die "not an app bundle: $APP"
have codesign || die "codesign not found (macOS only)"
if [[ -n "$ENT" && ! -f "$ENT" ]]; then
  [[ $ENT_SET -eq 1 ]] && die "entitlements file not found: $ENT"
  ENT=""
fi
if [[ "$IDENTITY" != "-" ]] && ! security find-identity -v -p codesigning 2>/dev/null | grep -qF "\"$IDENTITY\""; then
  # find-identity -v hides untrusted identities (e.g. a self-signed dev cert) — check without -v.
  security find-identity -p codesigning 2>/dev/null | grep -qF "\"$IDENTITY\"" \
    || die "code-signing identity not found in the keychain search list: $IDENTITY"
fi

COMMON=(--force --sign "$IDENTITY")
[[ $HARDENED -eq 1 ]] && COMMON+=(--options runtime --timestamp)

# codesign prints "replacing existing signature" for every --force re-sign; drop only that line.
quiet() { "$@" 2> >(grep -v 'replacing existing signature' >&2); }

n=0
while IFS= read -r -d '' f; do
  file -b "$f" | grep -q 'Mach-O' || continue
  quiet codesign "${COMMON[@]}" "$f"
  n=$((n+1))
done < <(find "$APP/Contents/MonoBundle" -type f \( -name '*.dylib' -o -name '*.so' -o -perm -u+x \) -print0 2>/dev/null)
info "signed $n MonoBundle native librar$([[ $n -eq 1 ]] && echo y || echo ies)"

BUNDLE_ARGS=("${COMMON[@]}" --deep --identifier "$APP_ID")
[[ -n "$ENT" ]] && BUNDLE_ARGS+=(--entitlements "$ENT")
quiet codesign "${BUNDLE_ARGS[@]}" "$APP"

# --deep --verify does not look at MonoBundle, so verify each library explicitly.
while IFS= read -r -d '' f; do
  file -b "$f" | grep -q 'Mach-O' || continue
  codesign --verify --strict "$f" || die "invalid signature after signing: $f"
done < <(find "$APP/Contents/MonoBundle" -type f \( -name '*.dylib' -o -name '*.so' -o -perm -u+x \) -print0 2>/dev/null)
codesign --verify --deep --strict "$APP" || die "bundle signature does not verify: $APP"
ok "signed $(basename "$APP") ($IDENTITY$([[ $HARDENED -eq 1 ]] && echo ', hardened runtime')${ENT:+, entitlements $(basename "$ENT")})"
