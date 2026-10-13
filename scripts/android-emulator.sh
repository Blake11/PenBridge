#!/usr/bin/env bash
# Create (if missing) and start the "PenBridge_Tablet" Android emulator (large-screen profile).
#
# Usage: scripts/android-emulator.sh [--recreate] [--cold-boot] [--wipe] [--headless] [--no-wait] [--stop]
#   --recreate   delete and recreate the AVD (e.g. after changing ANDROID_API_LEVEL / AVD_DEVICE)
#   --cold-boot  ignore the quick-boot snapshot
#   --wipe       factory-reset the AVD's data
#   --headless   no emulator window (-no-window)
#   --no-wait    don't wait for boot to complete
#   --stop       shut down running emulators and exit
# Env: AVD_DEVICE (default pixel_tablet), ANDROID_API_LEVEL (default 36).
#
# Stylus limitations: the emulator only sends mouse-as-finger touches (ToolType.Finger, pressure
# fixed at 1.0, no tilt, no hover, no stylus buttons). Use it for UI/flow/pairing/networking; test
# S Pen pressure, tilt, hover and the side button on the real S23 Ultra (scripts/android-device.sh).
# From the emulator, the Mac host is reachable at 10.0.2.2, but mDNS/Bonjour discovery does not
# cross the emulator NAT — use manual host/port (or QR) pairing.
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

RECREATE=0; ARGS=(); WAIT=1; STOP=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --recreate) RECREATE=1 ;;
    --cold-boot) ARGS+=(-no-snapshot-load) ;;
    --wipe) ARGS+=(-wipe-data) ;;
    --headless) ARGS+=(-no-window -no-audio) ;;
    --no-wait) WAIT=0 ;;
    --stop) STOP=1 ;;
    -h|--help) usage_from_header "$0"; exit 0 ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
  shift
done

export_android_env
require_adb
EMU="$(emulator_bin)" || die "emulator not installed — run scripts/bootstrap.sh"

running_emulators() { "$ADB" devices | awk '$1 ~ /^emulator-/ {print $1}'; }

if [[ $STOP -eq 1 ]]; then
  step "Stopping emulators"
  for s in $(running_emulators); do "$ADB" -s "$s" emu kill >/dev/null 2>&1 && ok "$s stopped"; done
  exit 0
fi

AVDM="$(avdmanager_bin)" || die "avdmanager not found — run scripts/bootstrap.sh"
AVD_HOME="${ANDROID_AVD_HOME:-$HOME/.android/avd}"

step "AVD $AVD_NAME ($AVD_DEVICE, $ANDROID_SYSTEM_IMAGE)"
if [[ $RECREATE -eq 1 && -f "$AVD_HOME/$AVD_NAME.ini" ]]; then
  "$AVDM" delete avd -n "$AVD_NAME" >/dev/null && info "deleted old AVD"
fi
if [[ -f "$AVD_HOME/$AVD_NAME.ini" ]]; then
  ok "exists"
else
  [[ -d "$ANDROID_HOME/system-images/android-${ANDROID_API_LEVEL}/google_apis/arm64-v8a" ]] \
    || die "system image $ANDROID_SYSTEM_IMAGE missing — run scripts/bootstrap.sh"
  echo no | "$AVDM" create avd -n "$AVD_NAME" -k "$ANDROID_SYSTEM_IMAGE" -d "$AVD_DEVICE" >/dev/null
  cfg="$AVD_HOME/$AVD_NAME.avd/config.ini"
  # Hardware keyboard + GPU acceleration; keep the default (large) RAM of the tablet profile.
  for kv in "hw.keyboard=yes" "hw.gpu.enabled=yes" "hw.gpu.mode=host"; do
    k="${kv%%=*}"; grep -q "^$k" "$cfg" && sed -i '' "s|^$k.*|$kv|" "$cfg" || echo "$kv" >>"$cfg"
  done
  ok "created"
fi

if "$ADB" devices | grep -q '^emulator-'; then
  for s in $(running_emulators); do
    name="$("$ADB" -s "$s" emu avd name 2>/dev/null | head -1 | tr -d '\r')"
    if [[ "$name" == "$AVD_NAME" ]]; then ok "already running as $s"; exit 0; fi
  done
fi

step "Starting emulator"
LOG="${TMPDIR:-/tmp}"; LOG="${LOG%/}/penbridge-emulator.log"
nohup "$EMU" -avd "$AVD_NAME" -netdelay none -netspeed full ${ARGS[@]+"${ARGS[@]}"} >"$LOG" 2>&1 &
EMU_PID=$!
info "pid $EMU_PID — log: $LOG"

if [[ $WAIT -eq 1 ]]; then
  info "waiting for boot (first boot can take a minute or two)…"
  "$ADB" -e wait-for-device
  booted=0
  for _ in $(seq 1 300); do
    if [[ "$("$ADB" -e shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" == "1" ]]; then booted=1; break; fi
    kill -0 "$EMU_PID" 2>/dev/null || { fail "emulator exited — see $LOG"; tail -20 "$LOG"; exit 1; }
    sleep 1
  done
  [[ $booted -eq 1 ]] || die "boot not complete after 5 min — see $LOG (try --cold-boot or --wipe)"
  ok "booted: $("$ADB" devices | awk '$1 ~ /^emulator-/ {print $1}' | head -1)"
  hint "next: scripts/android-run.sh --emulator"
fi
