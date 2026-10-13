#!/usr/bin/env bash
# Build, sign and launch the PenBridge menu-bar app (src/PenBridge.Mac).
#
# Usage: scripts/mac-run.sh [--release] [--no-build] [--reset-tcc] [--reset-keychain] [--logs] [-- <dotnet build args>]
#   --release         build Release instead of Debug
#   --no-build        launch the last build
#   --reset-tcc       tccutil reset Accessibility com.blake11.penbridge (re-prompts for permission)
#   --reset-keychain  delete every Keychain item of service com.blake11.penbridge (server identity
#                     and paired devices: all phones must pair again); asks for confirmation
#   --logs            after launching, stream unified logs: log stream --predicate 'process == "PenBridge"'
#
# The build itself signs (src/PenBridge.Mac/PenBridge.Mac.csproj): with the "PenBridge Dev" identity
# if present (scripts/mac-dev-cert.sh), so the Accessibility grant survives rebuilds; otherwise ad-hoc
# (grant lost on every rebuild). If the bundle is not signed with an existing "PenBridge Dev" identity
# (an older build, --no-build, or -- -p:EnableCodeSigning=false), it is re-signed with scripts/mac-sign.sh.
# Requires the macos workload and Xcode (scripts/doctor.sh).
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

CONFIG=Debug; BUILD=1; RESET_TCC=0; RESET_KEYCHAIN=0; LOGS=0; EXTRA=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --release) CONFIG=Release ;;
    --no-build) BUILD=0 ;;
    --reset-tcc) RESET_TCC=1 ;;
    --reset-keychain) RESET_KEYCHAIN=1 ;;
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
  step "Resetting Accessibility permission for $APP_ID"
  tccutil reset Accessibility "$APP_ID" && ok "reset — macOS will ask again on next injection"
fi

if pgrep -x PenBridge >/dev/null; then
  step "Stopping running PenBridge"; pkill -x PenBridge || true; sleep 1
fi

if [[ $RESET_KEYCHAIN -eq 1 ]]; then
  step "Deleting Keychain items of service $APP_ID"
  warn "this removes the server identity and every paired device; phones must pair again"
  [[ -r /dev/tty ]] || die "--reset-keychain needs an interactive terminal to confirm"
  read -r -p "    Type 'yes' to delete: " answer </dev/tty
  [[ "$answer" == "yes" ]] || die "cancelled"
  n=0
  # One item per call; exit 44 = no more items. Bounded in case an item cannot be deleted.
  while [[ $n -lt 1000 ]] && security delete-generic-password -s "$APP_ID" >/dev/null 2>&1; do n=$((n+1)); done
  security find-generic-password -s "$APP_ID" >/dev/null 2>&1 && die "some items could not be deleted (Keychain Access → login → search $APP_ID)"
  ok "deleted $n item$([[ $n -eq 1 ]] || echo s) — the app creates a fresh identity on next start"
fi

if [[ $BUILD -eq 1 ]]; then
  require_dotnet
  workload_installed macos || die "macos workload missing — run scripts/doctor.sh"
  step "dotnet build src/PenBridge.Mac -c $CONFIG"
  (cd "$REPO_ROOT" && dotnet build "$PROJ_DIR" -c "$CONFIG" ${EXTRA[@]+"${EXTRA[@]}"})
fi

APP="$(find "$PROJ_DIR/bin/$CONFIG" -maxdepth 4 -type d -name '*.app' -prune -exec stat -f '%m %N' {} + 2>/dev/null \
        | sort -rn | head -1 | cut -d' ' -f2-)"
[[ -n "$APP" && -d "$APP" ]] || die "no .app under $PROJ_DIR/bin/$CONFIG — build first"

if security find-identity -p codesigning 2>/dev/null | grep -qF "\"$DEV_CERT_NAME\""; then
  if ! codesign -dvv "$APP" 2>&1 | grep -qxF "Authority=$DEV_CERT_NAME" \
     || ! codesign --verify --strict "$APP/Contents/MonoBundle/libcoreclr.dylib" 2>/dev/null; then
    step "Signing ${APP#"$REPO_ROOT"/} with \"$DEV_CERT_NAME\" (the build did not)"
    # Signs each MonoBundle dylib, then the bundle (see scripts/mac-sign.sh --help for why).
    "$REPO_ROOT/scripts/mac-sign.sh" "$APP" "$DEV_CERT_NAME"
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
