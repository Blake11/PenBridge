#!/usr/bin/env bash
# Build, sign and launch the PenBridge menu-bar app (src/PenBridge.Mac).
#
# Usage: scripts/mac-run.sh [--release] [--no-build] [--reset-tcc] [--logs] [-- <dotnet build args>]
#   --release     build Release instead of Debug
#   --no-build    launch the last build
#   --reset-tcc   tccutil reset Accessibility com.blake11.penbridge (re-prompts for permission)
#   --logs        after launching, stream unified logs: log stream --predicate 'process == "PenBridge"'
#
# Signs with the "PenBridge Dev" identity if present (scripts/mac-dev-cert.sh) so the
# Accessibility grant survives rebuilds; otherwise falls back to ad-hoc signing (grant is lost
# on every rebuild). Requires the macos workload and Xcode (scripts/doctor.sh).
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

CONFIG=Debug; BUILD=1; RESET_TCC=0; LOGS=0; EXTRA=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --release) CONFIG=Release ;;
    --no-build) BUILD=0 ;;
    --reset-tcc) RESET_TCC=1 ;;
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

if [[ $BUILD -eq 1 ]]; then
  require_dotnet
  workload_installed macos || die "macos workload missing — run scripts/doctor.sh"
  step "dotnet build src/PenBridge.Mac -c $CONFIG"
  (cd "$REPO_ROOT" && dotnet build "$PROJ_DIR" -c "$CONFIG" -p:EnableCodeSigning=false ${EXTRA[@]+"${EXTRA[@]}"})
fi

APP="$(find "$PROJ_DIR/bin/$CONFIG" -maxdepth 4 -type d -name '*.app' -prune -exec stat -f '%m %N' {} + 2>/dev/null \
        | sort -rn | head -1 | cut -d' ' -f2-)"
[[ -n "$APP" && -d "$APP" ]] || die "no .app under $PROJ_DIR/bin/$CONFIG — build first"

step "Signing ${APP#"$REPO_ROOT"/}"
if security find-identity -p codesigning 2>/dev/null | grep -q "\"$DEV_CERT_NAME\""; then
  IDENTITY="$DEV_CERT_NAME"
else
  IDENTITY="-"
  warn "no \"$DEV_CERT_NAME\" identity — ad-hoc signing; Accessibility must be re-granted after each rebuild"
  hint "scripts/mac-dev-cert.sh"
fi
# Signs each MonoBundle dylib, then the bundle (see scripts/mac-sign.sh --help for why).
"$REPO_ROOT/scripts/mac-sign.sh" "$APP" "$IDENTITY"
info "$(codesign -dr - "$APP" 2>&1 | grep designated)"

step "Launching (menu-bar app: look for the icon, no Dock icon)"
open -n "$APP"
ok "started"

if [[ $LOGS -eq 1 ]]; then
  step "Streaming logs (Ctrl-C to stop)"
  exec log stream --style compact --level debug --predicate 'process == "PenBridge"'
fi
