#!/usr/bin/env bash
# Prepare a device-test round: Mac Release app + phone app running and connected, latency capture on, filtered
# log captures recording in the background. Replaces the manual prep steps of a device-test round.
#
# Usage: scripts/device-test-setup.sh --round <dir> [--device <serial> | --auto] [--release] [--no-build]
#        scripts/device-test-setup.sh --stop <dir>
#   --round <dir>      capture folder, created if needed; by convention .claude/progress/<feature>/<round>/
#                      in the main checkout
#   --device <serial>  the phone's adb serial (scripts/android-device.sh list)
#   --auto             the only connected phone (default); emulators are ignored
#   --release          also build the Android app in Release (the Mac app is always the Release build)
#   --no-build         don't build: relaunch the last Mac Release build (scripts/mac-run.sh --release --no-build)
#                      and launch the app already installed on the phone
#   --stop <dir>       ask the Mac app for a latency dump (scripts/latency-report.sh --dump-now, saved to
#                      <dir>/latency.txt), then stop the captures listed in <dir>/pids
#
# Steps: device-preflight checks (screen unlocked, display awake, Accessibility, phone online); latency capture on
# (scripts/latency-report.sh --enable — the Mac app is restarted anyway); captures started; scripts/mac-run.sh
# --release (stops a running PenBridge first); wait for "Server listening"; scripts/android-run.sh --device <serial>
# (an in-place update keeps the phone's pairing); wait for "Session started" on the Mac.
#
# Files in <dir>:
#   mac.log           /usr/bin/log stream --level debug --style compact --predicate 'subsystem == "com.blake11.penbridge"'
#   phone.log         adb logcat --pid=<app pid> (stops being useful if the app restarts: use phone-tags.log)
#   phone-tags.log    adb logcat AndroidRuntime:E DOTNET:V monodroid:V mono-rt:V PenBridge:V ActivityTaskManager:I *:S
#   mac-run.txt, android-run.txt   build/launch output
#   pids              "<pid> <capture>" per line; read by --stop
#
# Exit status: 0 ready (session started), 3 ready but no session within 60 s (pairing needed: pair from the phone,
# then watch mac.log for "Session started"), 1 on any failure.
#
# Run it unsandboxed: the Mac build signs with the "PenBridge Dev" certificate, which a sandboxed shell can't use.
# After the round: scripts/latency-report.sh --disable turns the latency capture off again.
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"
source "$(dirname "$0")/lib/device.sh"

SCRIPTS="$REPO_ROOT/scripts"
ROUND=""; STOP=""; SERIAL=""; ANDROID_RELEASE=0; BUILD=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --round) ROUND="${2:-}"; [[ -n "$ROUND" && "$ROUND" != -* ]] || die "--round needs a directory"; shift ;;
    --stop) STOP="${2:-}"; [[ -n "$STOP" && "$STOP" != -* ]] || die "--stop needs the round directory"; shift ;;
    --device) SERIAL="${2:-}"; [[ -n "$SERIAL" && "$SERIAL" != -* ]] || die "--device needs a serial"; shift ;;
    --auto) SERIAL="" ;;
    --release) ANDROID_RELEASE=1 ;;
    --no-build) BUILD=0 ;;
    -h|--help) usage_from_header "$0"; exit 0 ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
  shift
done
[[ -n "$ROUND" || -n "$STOP" ]] || die "pass --round <dir> or --stop <dir> (see --help)"
[[ -z "$ROUND" || -z "$STOP" ]] || die "--round and --stop are exclusive"

# Live capture PIDs from <dir>/pids whose command still is a capture we started (guards against PID reuse).
live_captures() {  # $1 = pids file; prints "<pid> <name>"
  local pid name cmd
  [[ -f "$1" ]] || return 0
  while read -r pid name; do
    [[ "$pid" =~ ^[0-9]+$ ]] || continue
    cmd="$(ps -o command= -p "$pid" 2>/dev/null || true)"
    case "$cmd" in *"log stream"*|*logcat*) echo "$pid $name" ;; esac
  done <"$1"
}

# ---- --stop -------------------------------------------------------------------
if [[ -n "$STOP" ]]; then
  [[ -d "$STOP" ]] || die "no such directory: $STOP"
  DIR="$(cd "$STOP" && pwd)"
  step "Latency dump"
  mac_pid="$(penbridge_pid)"
  if [[ -z "$mac_pid" ]]; then
    warn "PenBridge is not running — no dump (a session that ended already wrote one: scripts/latency-report.sh)"
  elif [[ -z "$(penbridge_log_since_start "$mac_pid" 'Latency capture on' | tail -n 1)" ]]; then
    # The SIGUSR1 handler exists only while the capture is on; without it the signal would terminate the app.
    warn "the running PenBridge has latency capture off — no dump requested"
  elif "$SCRIPTS/latency-report.sh" --dump-now >"$DIR/latency.txt" 2>&1; then
    sed 's/^/    /' "$DIR/latency.txt"; ok "saved $DIR/latency.txt"
  else
    warn "no dump: $(tail -n 1 "$DIR/latency.txt")"
  fi
  step "Stopping captures"
  live="$(live_captures "$DIR/pids")"
  if [[ -z "$live" ]]; then
    ok "nothing running"
  else
    while read -r pid name; do
      kill "$pid" 2>/dev/null && ok "stopped $name (pid $pid)" || warn "could not stop $name (pid $pid)"
    done <<<"$live"
  fi
  rm -f "$DIR/pids"
  info "captures kept in $DIR; turn latency capture off when done: scripts/latency-report.sh --disable"
  exit 0
fi

# ---- setup --------------------------------------------------------------------
mkdir -p "$ROUND"
DIR="$(cd "$ROUND" && pwd)"
PIDS="$DIR/pids"
if [[ -n "$(live_captures "$PIDS")" ]]; then
  die "captures already running for $DIR — stop them first: scripts/device-test-setup.sh --stop $DIR"
fi
: >"$PIDS"

export_android_env
require_adb
if [[ -z "$SERIAL" ]]; then
  SERIAL="$(pick_phone_serial 2>&1)" || die "$SERIAL — scripts/android-device.sh connect --auto, or pass --device <serial>"
fi

step "Preflight"
FAILED=0
if mac_screen_locked; then fail "screen is locked"; hint "unlock the Mac (the user's step)"; FAILED=1; else ok "screen unlocked"; fi
if mac_display_asleep; then fail "display is asleep"; hint "caffeinate -u -t 2"; FAILED=1; else ok "display awake"; fi
case "$(mac_accessibility_state "$(penbridge_pid)")" in
  granted) ok "Accessibility granted" ;;
  denied) fail "Accessibility not granted"; hint "System Settings → Privacy & Security → Accessibility → PenBridge"; FAILED=1 ;;
  *) warn "Accessibility: can't tell without prompting — check mac.log for \"Accessibility permission missing\"" ;;
esac
if adb_serial_online "$SERIAL"; then ok "phone $SERIAL online"; else fail "phone $SERIAL not online"; hint "scripts/android-device.sh connect --auto"; FAILED=1; fi
[[ $FAILED -eq 0 ]] || die "preflight failed"

step "Latency capture"
if [[ "$(defaults read "$APP_ID" PenBridgeLatencyLog 2>/dev/null || true)" == 1 ]]; then
  ok "already on"
else
  "$SCRIPTS/latency-report.sh" --enable >/dev/null; ok "enabled (takes effect with the restart below)"
fi

# Start a capture detached into its own session, so it outlives this script and its caller's shell.
start_capture() {  # $1 = file name in $DIR, rest = command
  local name="$1"; shift
  nohup /usr/bin/perl -MPOSIX -e 'POSIX::setsid(); exec @ARGV or die "exec: $!\n"' "$@" \
    >"$DIR/$name" 2>&1 </dev/null &
  echo "$! $name" >>"$PIDS"
  ok "$name (pid $!)"
}

# Wait up to $3 s for an ERE in a file, looking only at lines after line $4 (default 0).
wait_for_line() {  # $1 = file, $2 = pattern, $3 = timeout s, $4 = skip lines
  local i
  for ((i = 0; i < $3; i++)); do
    tail -n +"$(( ${4:-0} + 1 ))" "$1" 2>/dev/null | grep -E "$2" >/dev/null && return 0  # not -q: SIGPIPE under pipefail
    sleep 1
  done
  return 1
}

step "Starting captures in $DIR"
start_capture mac.log /usr/bin/log stream --level debug --style compact --predicate "$MAC_LOG_PREDICATE"
# -T 1: start at the newest line instead of replaying the whole buffer.
start_capture phone-tags.log "$ADB" -s "$SERIAL" logcat -v threadtime -T 1 "${LOGCAT_TAGS[@]}"

step "Mac app (Release)"
mac_args=(--release); [[ $BUILD -eq 1 ]] || mac_args+=(--no-build)
info "scripts/mac-run.sh ${mac_args[*]} → mac-run.txt"
"$SCRIPTS/mac-run.sh" "${mac_args[@]}" >"$DIR/mac-run.txt" 2>&1 \
  || { tail -n 20 "$DIR/mac-run.txt" >&2; die "mac-run.sh failed (full output: $DIR/mac-run.txt); captures still running — --stop $DIR"; }
wait_for_line "$DIR/mac.log" 'Server listening on port' 120 \
  || die "no \"Server listening\" within 120 s (see $DIR/mac.log; Keychain prompt?) — --stop $DIR"
ok "$(grep -E 'Server listening on port' "$DIR/mac.log" | tail -n 1 | sed 's/.*\] //')"

step "Phone app on $SERIAL"
mark="$(wc -l <"$DIR/mac.log" | tr -d ' ')"
if [[ $BUILD -eq 1 ]]; then
  and_args=(--device "$SERIAL"); [[ $ANDROID_RELEASE -eq 1 ]] && and_args+=(--release)
  info "scripts/android-run.sh ${and_args[*]} → android-run.txt"
  "$SCRIPTS/android-run.sh" "${and_args[@]}" >"$DIR/android-run.txt" 2>&1 \
    || { tail -n 20 "$DIR/android-run.txt" >&2; die "android-run.sh failed (full output: $DIR/android-run.txt) — --stop $DIR"; }
else
  "$ADB" -s "$SERIAL" shell monkey -p "$APP_ID" -c android.intent.category.LAUNCHER 1 >"$DIR/android-run.txt" 2>&1 \
    || die "could not launch $APP_ID on $SERIAL (installed? drop --no-build) — --stop $DIR"
fi
app_pid=""
for _ in $(seq 1 30); do
  app_pid="$("$ADB" -s "$SERIAL" shell pidof "$APP_ID" 2>/dev/null | tr -d '\r')"
  [[ -n "$app_pid" ]] && break; sleep 1
done
if [[ -n "$app_pid" ]]; then
  ok "$APP_ID running (pid $app_pid)"
  start_capture phone.log "$ADB" -s "$SERIAL" logcat -v threadtime --pid="$app_pid"
else
  warn "$APP_ID is not running on the phone — see phone-tags.log (crash?)"
fi

rc=0
step "Waiting for the phone's session"
if wait_for_line "$DIR/mac.log" 'Session started: ' 60 "$mark"; then
  ok "$(tail -n +"$((mark + 1))" "$DIR/mac.log" | grep -E 'Session started: ' | tail -n 1 | sed 's/.*\] //') — pairing kept"
else
  warn "no session within 60 s — PAIRING NEEDED (the user's step): Mac menu → Pair new device…, then scan the QR or enter the PIN on the phone"
  hint "then watch: grep 'Session started' $DIR/mac.log"
  rc=3
fi

step "Captures running"
while read -r pid name; do info "$pid  $DIR/$name"; done <"$PIDS"
info "stop:  $SCRIPTS/device-test-setup.sh --stop $DIR"
info "(raw:  kill $(awk '{printf "%s ", $1}' "$PIDS")— then scripts/latency-report.sh --dump-now)"
exit $rc
