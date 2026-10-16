#!/usr/bin/env bash
# Print the offset-corrected pen-to-Mac latency (p50/p95/max, sample counts) from the Mac server's latency dump.
#
# Usage: scripts/latency-report.sh [--enable | --disable] [--dump-now] [--file <dump>] [--sim] [--dir <dir>]
#                                  [--logcat] [--device <serial>]
#   --enable / --disable  turn the Mac app's capture on/off (user default PenBridgeLatencyLog; restart the app).
#                         Or launch it with PENBRIDGE_LATENCY_LOG=1 in its environment.
#   --dump-now            ask the running PenBridge app for a dump now (SIGUSR1) instead of waiting for the
#                         session to end; waits up to 5 s for the new file. Refuses when the running app's
#                         capture is off (no "Latency capture on" log line since it started): the app handles
#                         SIGUSR1 only while capturing, and the default action would terminate it
#   --file <dump>         report this dump instead of the newest one
#   --sim                 newest SimClient dump (latency-sim-*.tsv) instead of the newest Mac app dump
#   --dir <dir>           dump folder (default ~/Library/Logs/PenBridge)
#   --logcat              also save the phone's PenBridge logcat (connect / transport fallback events) next to the
#                         dump, for context; the numbers come from the Mac dump alone
#   --device <serial>     adb serial for --logcat (default: the only connected device)
#
# How it works: the Mac records every input frame's timeUs (phone clock) with its arrival and inject times, plus the
# phone's Pings (one-way samples) and its own Ping round trips. PenBridge.Core's LatencyAnalyzer estimates the clock
# offset as min(one-way) - minRTT/2 (error within +-minRTT/2, reported per session) with a drift fit, and the dump's
# "#" lines hold the result. Measured span: phone SendState -> Mac injector returned. The phone's own input
# dispatch (MotionEvent time -> SendState) is not on the wire and not included.
#
# S23 run: scripts/latency-report.sh --enable, restart the Mac app, connect the phone, draw for 30 s or more (pings
# every 2 s feed the offset estimate), disconnect (or --dump-now), then run scripts/latency-report.sh.
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"
source "$(dirname "$0")/lib/device.sh"

DIR="$HOME/Library/Logs/PenBridge"; FILE=""; DUMP_NOW=0; SIM=0; LOGCAT=0; SERIAL=""; TOGGLE=""
DEFAULTS_KEY="PenBridgeLatencyLog"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --enable) TOGGLE=on ;;
    --disable) TOGGLE=off ;;
    --dump-now) DUMP_NOW=1 ;;
    --file) FILE="${2:-}"; [[ -n "$FILE" ]] || die "--file needs a path"; shift ;;
    --sim) SIM=1 ;;
    --dir) DIR="${2:-}"; [[ -n "$DIR" ]] || die "--dir needs a path"; shift ;;
    --logcat) LOGCAT=1 ;;
    --device) SERIAL="${2:-}"; [[ -n "$SERIAL" ]] || die "--device needs a serial"; shift ;;
    -h|--help) usage_from_header "$0"; exit 0 ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
  shift
done

if [[ -n "$TOGGLE" ]]; then
  if [[ "$TOGGLE" == on ]]; then
    defaults write "$APP_ID" "$DEFAULTS_KEY" -bool true
    ok "latency capture enabled; restart the Mac app (scripts/mac-run.sh --no-build)"
  else
    defaults delete "$APP_ID" "$DEFAULTS_KEY" 2>/dev/null || true
    ok "latency capture disabled; restart the Mac app"
  fi
  [[ "$DUMP_NOW" == 0 && -z "$FILE" ]] && exit 0
fi

pattern='latency-2*.tsv'
[[ "$SIM" == 1 ]] && pattern='latency-sim-*.tsv'

newest() {  # newest dump matching $pattern in $DIR, or nothing
  local f
  f="$(find "$DIR" -maxdepth 1 -name "$pattern" -type f 2>/dev/null | sort | tail -n 1)" || true
  printf '%s' "$f"
}

if [[ "$DUMP_NOW" == 1 ]]; then
  [[ "$SIM" == 0 ]] || die "--dump-now works with the Mac app only (SimClient dumps at session end)"
  pid="$(penbridge_pid)"
  [[ -n "$pid" ]] || die "PenBridge (the Mac app) is not running"
  # The handler exists only while the capture is on (LatencyLog); otherwise SIGUSR1 would terminate the app. The
  # user default may have changed since launch, so ask the app's own log, and send nothing when unsure.
  if [[ -z "$(penbridge_log_since_start "$pid" 'Latency capture on' | tail -n 1)" ]]; then
    die "the running PenBridge (pid $pid) has latency capture off, so no dump was requested (SIGUSR1 would quit it). Turn it on with --enable and restart the app."
  fi
  before="$(newest)"
  kill -USR1 "$pid" || die "could not signal PenBridge (pid $pid)"
  for _ in $(seq 1 50); do
    now="$(newest)"
    [[ -n "$now" && "$now" != "$before" ]] && break
    sleep 0.1
  done
  [[ -n "${now:-}" && "$now" != "$before" ]] || die "no new dump in $DIR (capture off, or nothing recorded yet?)"
fi

if [[ -z "$FILE" ]]; then
  FILE="$(newest)"
  [[ -n "$FILE" ]] || die "no $pattern in $DIR (enable capture with --enable, then end a session or use --dump-now)"
fi
[[ -f "$FILE" ]] || die "no such dump: $FILE"
head -n 1 "$FILE" | grep -q '^# PenBridge latency dump v1$' || die "$FILE is not a PenBridge latency dump"

step "Latency: $FILE"
# Summary lines (after the header and the records/frequency line) are "# <text>".
grep '^# ' "$FILE" | tail -n +3 | sed 's/^# /    /'
info "(ms; pen_down = first contact frame of each stroke; offset_error bounds every value per session)"

if [[ "$LOGCAT" == 1 ]]; then
  require_adb
  args=()
  [[ -n "$SERIAL" ]] && args=(-s "$SERIAL")
  out="${FILE%.tsv}-logcat.txt"
  "$ADB" ${args[@]+"${args[@]}"} logcat -d -v threadtime -s PenBridge >"$out" || die "adb logcat failed (device connected? see scripts/android-device.sh list)"
  ok "phone log saved: $out ($(wc -l <"$out" | tr -d ' ') lines)"
fi
