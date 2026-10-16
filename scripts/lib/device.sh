#!/usr/bin/env bash
# Device-test helpers (Mac session state, the PenBridge app's unified log, adb serials, gh scopes) shared by
# scripts/device-preflight.sh and scripts/device-test-setup.sh. Source it after lib/common.sh; do not execute it.
# Every function here is read-only.

if [[ -n "${PENBRIDGE_DEVICE_SOURCED:-}" ]]; then return 0; fi
PENBRIDGE_DEVICE_SOURCED=1

MAC_PROCESS="PenBridge"
MAC_LOG_PREDICATE="subsystem == \"$APP_ID\""
# The Android head logs under this tag (Android.Util.Log); the .NET runtime and crashes under the others.
# Quote these when typing them in zsh: it globs `TAG:*`.
LOGCAT_TAGS=(AndroidRuntime:E DOTNET:V monodroid:V mono-rt:V PenBridge:V ActivityTaskManager:I '*:S')

# ---- Mac session -------------------------------------------------------------
# 0 when the login session's screen is locked (or the console is locked / at the login window).
mac_screen_locked() {
  local plist
  plist="$(ioreg -n Root -d1 -a 2>/dev/null)" || return 1
  # CGSSessionScreenIsLocked appears in the session dictionary only while the screen is locked.
  grep -A1 -E '<key>(CGSSessionScreenIsLocked|IOConsoleLocked)</key>' <<<"$plist" | grep '<true/>' >/dev/null  # not -q: SIGPIPE under pipefail
}

# 0 when the display is asleep: the newest "Display is turned on/off" power-management event says off.
mac_display_asleep() {
  local last
  last="$(pmset -g log 2>/dev/null | grep -E 'Display is turned (on|off)' | tail -n 1)" || true
  [[ "$last" == *"Display is turned off"* ]]
}

# PID of the running Mac app, or nothing.
penbridge_pid() { pgrep -x "$MAC_PROCESS" 2>/dev/null | head -n 1 || true; }

# The app's unified-log lines since its process started (compact style), filtered by an ERE.
# $1 = pid, $2 = grep -E pattern. Prints nothing when the start time can't be read.
penbridge_log_since_start() {
  local pid="$1" pattern="$2" raw start
  raw="$(LC_ALL=C ps -o lstart= -p "$pid" 2>/dev/null | sed 's/  */ /g; s/^ //; s/ $//')" || true
  [[ -n "$raw" ]] || return 0
  start="$(LC_ALL=C date -j -f '%a %b %d %T %Y' "$raw" '+%Y-%m-%d %H:%M:%S' 2>/dev/null)" || return 0
  /usr/bin/log show --start "$start" --info --style compact --predicate "$MAC_LOG_PREDICATE" 2>/dev/null \
    | grep -E "$pattern" || true
}

# listening | stopped | failed | unknown — from the newest server lifecycle line since the app started.
mac_server_state() {  # $1 = pid
  local last
  last="$(penbridge_log_since_start "$1" 'Server (listening on port|stopped|failed to start)' | tail -n 1)"
  case "$last" in
    *"Server listening on port"*) echo listening ;;
    *"Server stopped"*) echo stopped ;;
    *"Server failed to start"*) echo failed ;;
    *) echo unknown ;;
  esac
}

# granted | denied | unknown — never prompts. Reads the system TCC database when this process may (Full Disk
# Access or an unsandboxed admin shell); otherwise falls back to the app's own log line since it started ($1 = pid,
# optional).
mac_accessibility_state() {
  local db="/Library/Application Support/com.apple.TCC/TCC.db" v last
  if [[ -r "$db" ]]; then
    v="$(/usr/bin/sqlite3 -readonly "$db" \
      "SELECT auth_value FROM access WHERE service='kTCCServiceAccessibility' AND client='$APP_ID' LIMIT 1;" 2>/dev/null)" \
      && { if [[ "$v" == 2 ]]; then echo granted; else echo denied; fi; return 0; }
  fi
  if [[ -n "${1:-}" ]]; then
    last="$(penbridge_log_since_start "$1" 'Accessibility permission (granted|missing)' | tail -n 1)"
    case "$last" in
      *"permission granted"*) echo granted; return 0 ;;
      *"permission missing"*) echo denied; return 0 ;;
    esac
  fi
  echo unknown
}

# ---- Android -----------------------------------------------------------------
# Online adb serials (state "device"). Needs $ADB (require_adb).
adb_online_serials() { "$ADB" devices 2>/dev/null | tr -d '\r' | awk 'NR>1 && $2=="device" {print $1}'; }

# The one connected phone (not an emulator). A wireless phone can be listed twice (ip:port and its
# adb-…._adb-tls-connect._tcp mDNS alias): serials with the same ro.serialno count once, preferring ip:port.
# Prints the serial; returns 1 (with a reason on stderr) when there is no phone or more than one.
pick_phone_serial() {
  local s hw seen="" picked="" n=0
  for s in $(adb_online_serials | grep -v '^emulator-' || true); do
    hw="$("$ADB" -s "$s" shell getprop ro.serialno 2>/dev/null | tr -d '\r')"
    [[ -n "$hw" ]] || hw="$s"
    case " $seen " in
      *" $hw "*)
        # Same phone again: keep the ip:port spelling.
        [[ "$s" =~ ^[0-9.]+:[0-9]+$ ]] && picked="$s"
        continue ;;
    esac
    seen="$seen $hw"; n=$((n+1)); picked="$s"
  done
  if [[ $n -eq 0 ]]; then echo "no phone connected over adb" >&2; return 1; fi
  if [[ $n -gt 1 ]]; then echo "$n phones connected — pass the serial explicitly" >&2; return 1; fi
  echo "$picked"
}

adb_serial_online() { adb_online_serials | grep -xF "$1" >/dev/null; }

# ---- GitHub CLI ---------------------------------------------------------------
# Prints the active github.com token's scopes, space-separated (empty when gh can't tell, e.g. GH_TOKEN with a
# fine-grained token). Returns 1 when gh is not logged in.
gh_token_scopes() {
  local out
  out="$(gh auth status --active --hostname github.com 2>&1)" || return 1
  sed -n "s/.*Token scopes: //p" <<<"$out" | tr -d "',"
}
