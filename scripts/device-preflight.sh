#!/usr/bin/env bash
# Read-only checks before a device test or an input-injection run. Run it first; fix every ✗ it reports.
#
# Usage: scripts/device-preflight.sh [--mac] [--android <serial> | --android-auto] [--gh]
#   --mac              the Mac: screen unlocked (CGSSessionScreenIsLocked), display awake, PenBridge running and its
#                      server listening (unified log, subsystem com.blake11.penbridge, since the app started),
#                      Accessibility granted (system TCC database when readable, else the app's own log line;
#                      a warning when neither tells — never prompts)
#   --android <serial> that adb device is online (scripts/android-device.sh list)
#   --android-auto     exactly one phone (not an emulator) is online; prints its serial
#   --gh               gh is logged in to github.com with the repo and workflow scopes
#   With no option: --mac --android-auto --gh.
#
# Exit status: 0 when every selected check passes (warnings don't fail), 1 otherwise. Each failure prints a
# one-line fix hint. Nothing is changed, launched or prompted.
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"
source "$(dirname "$0")/lib/device.sh"

DO_MAC=0; DO_ANDROID=0; SERIAL=""; DO_GH=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --mac) DO_MAC=1 ;;
    --android) DO_ANDROID=1; SERIAL="${2:-}"; [[ -n "$SERIAL" && "$SERIAL" != -* ]] || die "--android needs a serial (or use --android-auto)"; shift ;;
    --android-auto) DO_ANDROID=1; SERIAL="" ;;
    --gh) DO_GH=1 ;;
    -h|--help) usage_from_header "$0"; exit 0 ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
  shift
done
if [[ $DO_MAC -eq 0 && $DO_ANDROID -eq 0 && $DO_GH -eq 0 ]]; then DO_MAC=1; DO_ANDROID=1; DO_GH=1; fi

FAILED=0
bad() { fail "$1"; hint "$2"; FAILED=$((FAILED+1)); }

if [[ $DO_MAC -eq 1 ]]; then
  step "Mac"
  if mac_screen_locked; then bad "screen is locked" "unlock the Mac (the user's step); injected input goes nowhere while locked"
  else ok "screen unlocked"; fi

  if mac_display_asleep; then bad "display is asleep" "wake it: caffeinate -u -t 2 (or press a key)"
  else ok "display awake"; fi

  pid="$(penbridge_pid)"
  if [[ -z "$pid" ]]; then
    bad "PenBridge is not running" "scripts/mac-run.sh --release (run it unsandboxed: dev-cert signing)"
  else
    ok "PenBridge running (pid $pid)"
    state="$(mac_server_state "$pid")"
    case "$state" in
      listening) ok "server listening" ;;
      stopped)   bad "server stopped (sleep, network change or quit)" "restart it: scripts/mac-run.sh --release --no-build" ;;
      failed)    bad "server failed to start" "see: log show --last 10m --predicate 'subsystem == \"$APP_ID\"'" ;;
      *)         bad "no \"Server listening\" line since the app started" "wait a few seconds after launch, or restart: scripts/mac-run.sh --release --no-build" ;;
    esac
  fi

  case "$(mac_accessibility_state "${pid:-}")" in
    granted) ok "Accessibility granted" ;;
    denied)  bad "Accessibility not granted to $APP_ID" "System Settings → Privacy & Security → Accessibility → enable PenBridge (or scripts/mac-run.sh --reset-tcc and accept the prompt)" ;;
    *)       warn "Accessibility: can't tell without prompting (TCC database not readable, no app log line yet)" ;;
  esac
fi

if [[ $DO_ANDROID -eq 1 ]]; then
  step "Android"
  if ! ADB="$(adb_bin)"; then
    bad "adb not found" "scripts/bootstrap.sh"
  elif [[ -n "$SERIAL" ]]; then
    if adb_serial_online "$SERIAL"; then
      ok "adb device $SERIAL online ($("$ADB" -s "$SERIAL" shell getprop ro.product.model 2>/dev/null | tr -d '\r'))"
    else
      bad "adb device $SERIAL not online" "scripts/android-device.sh connect --auto (Wireless debugging on, same Wi-Fi)"
    fi
  else
    if phone="$(pick_phone_serial 2>&1)"; then
      ok "phone: $phone ($("$ADB" -s "$phone" shell getprop ro.product.model 2>/dev/null | tr -d '\r'))"
    else
      bad "$phone" "scripts/android-device.sh connect --auto, or pass --android <serial> (scripts/android-device.sh list)"
    fi
  fi
fi

if [[ $DO_GH -eq 1 ]]; then
  step "GitHub CLI"
  if ! have gh; then
    bad "gh not installed" "brew install gh && gh auth login"
  elif ! scopes="$(gh_token_scopes)"; then
    bad "gh is not logged in to github.com" "gh auth login --scopes repo,workflow"
  elif [[ -z "$scopes" ]]; then
    warn "gh token scopes unknown (fine-grained token or GH_TOKEN?) — can't confirm repo + workflow"
  else
    missing=""
    for s in repo workflow; do
      case " $scopes " in *" $s "*) ;; *) missing="$missing $s" ;; esac
    done
    if [[ -n "$missing" ]]; then bad "gh token lacks:$missing (has: $scopes)" "gh auth refresh -h github.com -s repo,workflow"
    else ok "gh scopes include repo, workflow"; fi
  fi
fi

if [[ $FAILED -gt 0 ]]; then
  printf '%s%d check(s) failed%s\n' "$C_RED" "$FAILED" "$C_RST" >&2
  exit 1
fi
ok "preflight passed"
