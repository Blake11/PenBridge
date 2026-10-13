#!/usr/bin/env bash
# Wireless adb helper for the real Galaxy S23 Ultra (or any Android 11+ phone).
#
# Usage:
#   scripts/android-device.sh pair <ip:pair-port> <6-digit-code>   one-time pairing
#   scripts/android-device.sh connect <ip:port>                    connect (after pairing / reboot)
#   scripts/android-device.sh list                                 adb devices -l
#   scripts/android-device.sh disconnect [ip:port]
#
# Enable Wireless debugging on the phone (same Wi-Fi as the Mac):
#   1. Settings → About phone → Software information → tap "Build number" 7× (Developer options).
#   2. Settings → Developer options → Wireless debugging → On (allow on this network).
#   3. Tap "Pair device with pairing code": it shows IP:PORT and a 6-digit code →
#        scripts/android-device.sh pair 192.168.1.23:37123 123456
#      (the pairing port differs from the connect port and changes every time the dialog opens)
#   4. The main Wireless debugging screen shows "IP address & Port" (connect port) →
#        scripts/android-device.sh connect 192.168.1.23:41234
# The connect port changes when Wireless debugging is toggled or the phone reboots; pairing persists.
# Optional: Developer options → "Disable adb authorization timeout" to stay paired longer.
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

cmd="${1:-}"; [[ $# -gt 0 ]] && shift
case "$cmd" in -h|--help|"") usage_from_header "$0"; exit 0 ;; esac
require_adb

case "$cmd" in
  pair)
    [[ $# -eq 2 ]] || die "usage: pair <ip:pair-port> <code>"
    step "Pairing with $1"
    out="$("$ADB" pair "$1" "$2" 2>&1)" || true
    if grep -qi 'successfully paired' <<<"$out"; then ok "$out"; hint "now: scripts/android-device.sh connect <ip:connect-port> (port from the Wireless debugging screen)"
    else fail "$out"; hint "re-open 'Pair device with pairing code' — code and port expire"; exit 1; fi ;;
  connect)
    [[ $# -eq 1 ]] || die "usage: connect <ip:port>"
    step "Connecting to $1"
    out="$("$ADB" connect "$1" 2>&1)" || true
    if grep -qE '^(connected|already connected)' <<<"$out"; then
      ok "$out"
      model="$("$ADB" -s "$1" shell getprop ro.product.model 2>/dev/null | tr -d '\r')"
      [[ -n "$model" ]] && ok "device: $model (serial $1)"
      hint "next: scripts/android-run.sh --device $1"
    else
      fail "$out"; hint "pair first, check same Wi-Fi, and use the connect port (not the pairing port)"; exit 1
    fi ;;
  list)
    step "adb devices"
    "$ADB" devices -l | tail -n +2 | sed '/^$/d; s/^/    /'
    hint "wireless devices show as ip:port or adb-<serial>._adb-tls-connect._tcp" ;;
  disconnect)
    "$ADB" disconnect "$@" ;;
  *) die "unknown command: $cmd (see --help)" ;;
esac
