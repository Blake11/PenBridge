#!/usr/bin/env bash
# Build, sign and launch the PenBridge menu-bar app (src/PenBridge.Mac).
#
# Usage: scripts/mac-run.sh [--release] [--no-build] [--reset-tcc] [--reset-keychain] [--reset-dev-secrets] [--logs] [-- <dotnet build/publish args>]
#   --release         publish Release (NativeAOT, universal) instead of building Debug (JIT)
#   --no-build        launch the last build
#   --reset-tcc       tccutil reset Accessibility and ScreenCapture (screen mirroring) for com.blake11.penbridge
#                     (re-prompts for permission)
#   --reset-keychain  delete every Keychain item of service com.blake11.penbridge (server identity
#                     and paired devices of Release builds: phones must pair again); asks for confirmation
#   --reset-dev-secrets  delete the Debug build's file secret store
#                     (~/Library/Application Support/PenBridge-Dev/secrets); asks for confirmation
#   --logs            after launching, stream unified logs: log stream --predicate 'process == "PenBridge"'
#
# The build itself signs (src/PenBridge.Mac/PenBridge.Mac.csproj): with the "PenBridge Dev" identity
# if present (scripts/mac-dev-cert.sh), so the Accessibility and Screen Recording grants survive rebuilds; otherwise ad-hoc
# (grant lost on every rebuild). Debug builds store secrets in files, Release builds in the Keychain
# (expect one Keychain prompt per new Release build, ADR-0017). --release runs `dotnet publish`: the
# NativeAOT app (issue #42); `-- <args>` go to dotnet build or publish.
# If the bundle is not signed with an existing "PenBridge Dev" identity
# (an older build, --no-build, or -- -p:EnableCodeSigning=false), it is re-signed with scripts/mac-sign.sh.
# Requires the macos workload and Xcode (scripts/doctor.sh).
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

CONFIG=Debug; BUILD=1; RESET_TCC=0; RESET_KEYCHAIN=0; RESET_DEV=0; LOGS=0; EXTRA=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --release) CONFIG=Release ;;
    --no-build) BUILD=0 ;;
    --reset-tcc) RESET_TCC=1 ;;
    --reset-keychain) RESET_KEYCHAIN=1 ;;
    --reset-dev-secrets) RESET_DEV=1 ;;
    --logs) LOGS=1 ;;
    -h|--help) usage_from_header "$0"; exit 0 ;;
    --) shift; EXTRA=("$@"); break ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
  shift
done

PROJ_DIR="$(dirname "$MAC_PROJECT")"
[[ -d "$PROJ_DIR" ]] || die "project not found: $PROJ_DIR"

if [[ $RESET_TCC -eq 1 ]]; then
  step "Resetting Accessibility and Screen Recording permissions for $APP_ID"
  tccutil reset Accessibility "$APP_ID" && ok "Accessibility reset — macOS will ask again on next injection"
  tccutil reset ScreenCapture "$APP_ID" && ok "Screen Recording reset — macOS will ask again when mirroring starts"
fi

if pgrep -x PenBridge >/dev/null; then
  step "Stopping running PenBridge"; pkill -x PenBridge || true; sleep 1
fi

confirm() {
  [[ -r /dev/tty ]] || die "$1 needs an interactive terminal to confirm"
  local answer
  read -r -p "    Type 'yes' to delete: " answer </dev/tty || die "cancelled"
  [[ "$answer" == "yes" ]] || die "cancelled"
}

# Debug builds keep secrets here instead of the Keychain (src/PenBridge.Mac/Services/SecretStoreFactory.cs).
DEV_SECRETS="$HOME/Library/Application Support/PenBridge-Dev/secrets"
if [[ $RESET_DEV -eq 1 ]]; then
  step "Deleting the Debug secret store ${DEV_SECRETS/#$HOME/~}"
  if [[ -d "$DEV_SECRETS" ]]; then
    warn "this removes the Debug server identity and its paired devices; phones must pair again with Debug builds"
    confirm --reset-dev-secrets
    rm -rf -- "$DEV_SECRETS"
    ok "deleted — a Debug build creates a fresh identity on next start"
  else
    ok "nothing to delete"
  fi
fi

if [[ $RESET_KEYCHAIN -eq 1 ]]; then
  step "Deleting Keychain items of service $APP_ID"
  warn "this removes the Release server identity and every paired device; phones must pair again"
  confirm --reset-keychain
  n=0
  # One item per call; exit 44 = no more items. Bounded in case an item cannot be deleted.
  while [[ $n -lt 1000 ]] && security delete-generic-password -s "$APP_ID" >/dev/null 2>&1; do n=$((n+1)); done
  security find-generic-password -s "$APP_ID" >/dev/null 2>&1 && die "some items could not be deleted (Keychain Access → login → search $APP_ID)"
  ok "deleted $n item$([[ $n -eq 1 ]] || echo s) — the app creates a fresh identity on next start"
fi

if [[ $BUILD -eq 1 ]]; then
  require_dotnet
  workload_installed macos || die "macos workload missing — run scripts/doctor.sh"
  if [[ "$CONFIG" == Release ]]; then
    # PublishAot applies only to `dotnet publish` (PenBridge.Mac.csproj); no .pkg for local runs.
    step "dotnet publish src/PenBridge.Mac -c Release (NativeAOT)"
    (cd "$REPO_ROOT" && dotnet publish "$PROJ_DIR" -c Release -p:CreatePackage=false ${EXTRA[@]+"${EXTRA[@]}"})
  else
    step "dotnet build src/PenBridge.Mac -c $CONFIG"
    (cd "$REPO_ROOT" && dotnet build "$PROJ_DIR" -c "$CONFIG" ${EXTRA[@]+"${EXTRA[@]}"})
  fi
fi

# Release: the universal bundle (the per-RID ones next to it are intermediates). Otherwise the newest .app.
APP="$PROJ_DIR/bin/$CONFIG/net10.0-macos/PenBridge.app"
[[ "$CONFIG" == Release && -d "$APP" ]] || APP="$(find "$PROJ_DIR/bin/$CONFIG" -maxdepth 4 -type d -name '*.app' -prune -exec stat -f '%m %N' {} + 2>/dev/null \
        | sort -rn | head -1 | cut -d' ' -f2-)"
[[ -n "$APP" && -d "$APP" ]] || die "no .app under $PROJ_DIR/bin/$CONFIG — build first"

# Any MonoBundle dylib: libcoreclr.dylib in JIT bundles, only the System.*.Native libraries under NativeAOT.
DYLIB="$(find "$APP/Contents/MonoBundle" -maxdepth 1 -name '*.dylib' -print -quit 2>/dev/null || true)"
# Capture before grepping: under pipefail, `codesign | grep -q` fails with SIGPIPE (141) when grep exits on
# the first match while codesign is still writing, which looked like "not signed" and forced a re-sign.
SIGNATURE="$(codesign -dvv "$APP" 2>&1 || true)"
IDENTITIES="$(security find-identity -p codesigning 2>/dev/null || true)"
# NativeAOT bundles (no CoreCLR) are signed without entitlements: Entitlements.plist only grants allow-jit.
SIGN_ARGS=()
[[ -e "$APP/Contents/MonoBundle/libcoreclr.dylib" ]] || SIGN_ARGS=(--no-entitlements)
if grep -qF "\"$DEV_CERT_NAME\"" <<<"$IDENTITIES"; then
  if ! grep -qxF "Authority=$DEV_CERT_NAME" <<<"$SIGNATURE" \
     || { [[ -n "$DYLIB" ]] && ! codesign --verify --strict "$DYLIB" 2>/dev/null; }; then
    step "Signing ${APP#"$REPO_ROOT"/} with \"$DEV_CERT_NAME\" (the build did not)"
    # Signs each MonoBundle dylib, then the bundle (see scripts/mac-sign.sh --help for why).
    "$REPO_ROOT/scripts/mac-sign.sh" ${SIGN_ARGS[@]+"${SIGN_ARGS[@]}"} "$APP" "$DEV_CERT_NAME"
  fi
else
  warn "no \"$DEV_CERT_NAME\" identity — ad-hoc signed; Accessibility and Keychain access must be re-granted after each rebuild"
  hint "scripts/mac-dev-cert.sh"
fi
codesign --verify --deep --strict "$APP" || die "bundle signature does not verify: $APP"
info "$(codesign -dr - "$APP" 2>&1 | grep designated)"

step "Launching (menu-bar app: look for the icon, no Dock icon)"
open -n "$APP"
ok "started"

if [[ $LOGS -eq 1 ]]; then
  step "Streaming logs (Ctrl-C to stop)"
  exec log stream --style compact --level debug --predicate 'process == "PenBridge"'
fi
