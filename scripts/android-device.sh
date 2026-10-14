#!/usr/bin/env bash
# Wireless adb helper for the real Galaxy S23 Ultra (or any Android 11+ phone).
#
# Usage:
#   scripts/android-device.sh pair <ip:pair-port> <6-digit-code>   one-time pairing
#   scripts/android-device.sh connect <ip:port>                    connect (after pairing / reboot)
#   scripts/android-device.sh connect --auto                       find the connect endpoint via mDNS
#                                                                  (adb mdns services, _adb-tls-connect._tcp)
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
# The connect port changes when Wireless debugging is toggled, Wi-Fi drops or the phone reboots; pairing
# persists, so `connect --auto` re-discovers the current port. The pairing port (_adb-tls-pairing._tcp) is
# only valid while the pairing dialog is open; --auto never uses it.
# --auto needs Wireless debugging on and the Mac on the same Wi-Fi; if several phones advertise it, it
# lists them and asks for an explicit <ip:port>.
# Optional: Developer options → "Disable adb authorization timeout" to stay paired longer.
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

cmd="${1:-}"; [[ $# -gt 0 ]] && shift
case "$cmd" in -h|--help|"") usage_from_header "$0"; exit 0 ;; esac
require_adb

# Unique ip:port of every _adb-tls-connect._tcp service adb's mDNS browser currently sees.
# `adb mdns services` lines: <instance> <service type> <ip:port> (tab-separated).
discover_connect_endpoints() {
  "$ADB" mdns services 2>/dev/null | tr -d '\r' \
    | awk '$2 ~ /^_adb-tls-connect\._tcp\.?$/ && $3 ~ /:[0-9]+$/ {print $3}' | sort -u
}

connect_to() {  # $1 = ip:port
  local out model
  step "Connecting to $1"
  out="$("$ADB" connect "$1" 2>&1)" || true
  if grep -qE '^(connected|already connected)' <<<"$out"; then
    ok "$out"
    model="$("$ADB" -s "$1" shell getprop ro.product.model 2>/dev/null | tr -d '\r')" || true
    [[ -n "$model" ]] && ok "device: $model (serial $1)"
    hint "next: scripts/android-run.sh --device $1"
  else
    fail "$out"; hint "pair first, check same Wi-Fi, and use the connect port (not the pairing port)"; exit 1
  fi
}

case "$cmd" in
  pair)
    [[ $# -eq 2 ]] || die "usage: pair <ip:pair-port> <code>"
    step "Pairing with $1"
    out="$("$ADB" pair "$1" "$2" 2>&1)" || true
    if grep -qi 'successfully paired' <<<"$out"; then ok "$out"; hint "now: scripts/android-device.sh connect <ip:connect-port> (port from the Wireless debugging screen)"
    else fail "$out"; hint "re-open 'Pair device with pairing code' — code and port expire"; exit 1; fi ;;
  connect)
    [[ $# -eq 1 ]] || die "usage: connect <ip:port> | connect --auto"
    target="$1"
    if [[ "$target" == --auto ]]; then
      step "Discovering _adb-tls-connect._tcp via adb mdns services"
      "$ADB" start-server >/dev/null 2>&1 || die "adb server failed to start"
      endpoints=""
      for _ in 1 2 3 4 5; do   # a freshly started adb server needs a moment to collect mDNS answers
        endpoints="$(discover_connect_endpoints)"
        [[ -n "$endpoints" ]] && break
        sleep 1
      done
      n="$(printf '%s' "$endpoints" | grep -c . || true)"
      if [[ "$n" -eq 0 ]]; then
        fail "no _adb-tls-connect._tcp service found"
        hint "phone: Wireless debugging on, same Wi-Fi as the Mac (no client isolation/VPN), paired once"
        hint "check '$ADB mdns check', or pass the port from the Wireless debugging screen: connect <ip:port>"
        exit 1
      elif [[ "$n" -gt 1 ]]; then
        fail "$n devices advertise wireless debugging:"
        printf '%s\n' "$endpoints" | sed 's/^/    /'
        die "pick one: scripts/android-device.sh connect <ip:port>"
      fi
      target="$endpoints"
      ok "found $target"
    fi
    connect_to "$target" ;;
  list)
    step "adb devices"
    "$ADB" devices -l | tail -n +2 | sed '/^$/d; s/^/    /'
    hint "wireless devices show as ip:port or adb-<serial>._adb-tls-connect._tcp" ;;
  disconnect)
    "$ADB" disconnect "$@" ;;
  *) die "unknown command: $cmd (see --help)" ;;
esac
