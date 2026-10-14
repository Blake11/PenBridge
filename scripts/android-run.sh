#!/usr/bin/env bash
# Build, deploy and launch the Android client (com.blake11.penbridge by default).
#
# Usage: scripts/android-run.sh [--emulator | --device <serial>] [--release] [--apk] [--app-id <id>]
#                               [--logs | --logs-only] [-- <dotnet build args>]
#   --emulator         target the emulator running AVD $AVD_NAME (default PenBridge_Tablet); if none
#                      matches: with no AVD_NAME override, the only running emulator, else start the AVD
#   --device <serial>  target a specific device (see scripts/android-device.sh list), e.g. 192.168.1.23:41234
#                      default: the only connected device
#   --release          Release configuration
#   --apk              build a standalone APK and `adb install -r` it, instead of `dotnet build -t:Run`
#                      (fast deployment); use when -t:Run misbehaves or to test the real package
#   --app-id <id>      build with this ApplicationId (-p:ApplicationId=<id>) and install/launch/log that
#                      package, e.g. com.blake11.penbridge.agent2, so parallel agents don't overwrite
#                      each other's install; works with --apk and --logs-only. Default: com.blake11.penbridge
#   --logs             after launch, stream logcat for the app's process (+ crashes / .NET runtime)
#   --logs-only        don't build; just attach logcat to the running app
#
# Parallel agents: give each its own AVD and ApplicationId, e.g.
#   AVD_NAME=PenBridge_Tablet_2 scripts/android-run.sh --emulator --app-id com.blake11.penbridge.agent2
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

CONFIG=Debug; TARGET=auto; SERIAL=""; MODE=run; LOGS=0; BUILD=1; EXTRA=(); APP_ID_OVERRIDE=
while [[ $# -gt 0 ]]; do
  case "$1" in
    --emulator) TARGET=emulator ;;
    --device) TARGET=device; SERIAL="${2:-}"; [[ -n "$SERIAL" ]] || die "--device needs a serial"; shift ;;
    --release) CONFIG=Release ;;
    --apk) MODE=apk ;;
    --app-id) APP_ID_OVERRIDE="${2:-}"; [[ -n "$APP_ID_OVERRIDE" ]] || die "--app-id needs a package name"; shift ;;
    --logs) LOGS=1 ;;
    --logs-only) LOGS=1; BUILD=0 ;;
    -h|--help) usage_from_header "$0"; exit 0 ;;
    --) shift; EXTRA=("$@"); break ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
  shift
done

# Android package names: two or more dot-separated segments, each starting with a letter.
if [[ -n "$APP_ID_OVERRIDE" ]]; then
  [[ "$APP_ID_OVERRIDE" =~ ^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$ ]] \
    || die "invalid --app-id '$APP_ID_OVERRIDE' (expected e.g. com.blake11.penbridge.agent2)"
  APP_ID="$APP_ID_OVERRIDE"
fi

export_android_env
require_adb
PROJ_DIR="$(dirname "$ANDROID_PROJECT")"

# ---- pick device --------------------------------------------------------------
devices() { "$ADB" devices | awk 'NR>1 && $2=="device" {print $1}'; }
emulator_running_avd() {  # $1 = AVD name; prints the serial of the online emulator running it
  local s
  for s in $(devices | grep '^emulator-' || true); do
    if [[ "$("$ADB" -s "$s" emu avd name 2>/dev/null | head -1 | tr -d '\r')" == "$1" ]]; then
      echo "$s"; return 0
    fi
  done
}
case "$TARGET" in
  emulator)
    SERIAL="$(emulator_running_avd "$AVD_NAME")"
    if [[ -z "$SERIAL" && "$AVD_NAME" == "$AVD_NAME_DEFAULT" ]]; then
      SERIAL="$(devices | grep '^emulator-' | head -1 || true)"
    fi
    if [[ -z "$SERIAL" ]]; then
      "$(dirname "$0")/android-emulator.sh"
      SERIAL="$(emulator_running_avd "$AVD_NAME")"
      [[ -n "$SERIAL" ]] || die "AVD $AVD_NAME did not come up (scripts/android-emulator.sh --help)"
    fi ;;
  device)
    devices | grep -qxF "$SERIAL" || die "device $SERIAL not connected (scripts/android-device.sh connect <ip:port>)" ;;
  auto)
    n="$(devices | wc -l | tr -d ' ')"
    [[ "$n" -eq 1 ]] || die "$n devices connected — pass --emulator or --device <serial> (scripts/android-device.sh list)"
    SERIAL="$(devices)" ;;
esac
ok "target: $SERIAL ($("$ADB" -s "$SERIAL" shell getprop ro.product.model 2>/dev/null | tr -d '\r'))"

MSBUILD_ENV=(-c "$CONFIG" -p:AndroidSdkDirectory="$ANDROID_HOME" -p:JavaSdkDirectory="$JAVA_HOME"
             "-p:AdbTarget=-s $SERIAL")
if [[ -n "$APP_ID_OVERRIDE" ]]; then MSBUILD_ENV+=("-p:ApplicationId=$APP_ID"); fi

# ---- build + deploy -----------------------------------------------------------
if [[ $BUILD -eq 1 ]]; then
  require_dotnet
  workload_installed android || die "android workload missing — run scripts/doctor.sh"
  [[ -d "$PROJ_DIR" ]] || die "project not found: $PROJ_DIR"
  cd "$REPO_ROOT"
  if [[ $MODE == run ]]; then
    step "dotnet build -t:Run ($CONFIG, $APP_ID → $SERIAL)"
    dotnet build "$PROJ_DIR" -t:Run "${MSBUILD_ENV[@]}" ${EXTRA[@]+"${EXTRA[@]}"}
  else
    step "dotnet build ($CONFIG, $APP_ID, standalone APK)"
    dotnet build "$PROJ_DIR" "${MSBUILD_ENV[@]}" -p:EmbedAssembliesIntoApk=true ${EXTRA[@]+"${EXTRA[@]}"}
    # Newest match; -exec … + runs nothing when there is no match (xargs would `ls` the cwd).
    APK="$(find "$PROJ_DIR/bin/$CONFIG" -name "$APP_ID-Signed.apk" -exec ls -t {} + 2>/dev/null | head -1 || true)"
    [[ -n "$APK" ]] || die "no $APP_ID-Signed.apk under $PROJ_DIR/bin/$CONFIG"
    step "adb install -r ${APK#"$REPO_ROOT"/}"
    "$ADB" -s "$SERIAL" install -r "$APK" >/dev/null || die "adb install failed"
    ok "installed"
    step "Launching $APP_ID"
    if "$ADB" -s "$SERIAL" shell monkey -p "$APP_ID" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1; then
      ok "launched"
    else
      fail "launch failed"
    fi
  fi
fi

# ---- logs ---------------------------------------------------------------------
if [[ $LOGS -eq 1 ]]; then
  step "Waiting for $APP_ID process…"
  pid=""
  for _ in $(seq 1 30); do
    pid="$("$ADB" -s "$SERIAL" shell pidof "$APP_ID" 2>/dev/null | tr -d '\r')"
    [[ -n "$pid" ]] && break; sleep 1
  done
  if [[ -n "$pid" ]]; then
    step "logcat --pid=$pid (Ctrl-C to stop)"
    exec "$ADB" -s "$SERIAL" logcat -v color,time --pid="$pid"
  else
    warn "app not running — showing crash/runtime tags instead"
    exec "$ADB" -s "$SERIAL" logcat -v color,time -s AndroidRuntime:E DOTNET:V monodroid:V mono-rt:V PenBridge:V
  fi
fi
