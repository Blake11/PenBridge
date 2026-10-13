#!/usr/bin/env bash
# Build, deploy and launch the Android client (com.blake11.penbridge).
#
# Usage: scripts/android-run.sh [--emulator | --device <serial>] [--release] [--apk] [--logs] [-- <dotnet build args>]
#   --emulator         target the running emulator (starts PenBridge_Tablet if none is running)
#   --device <serial>  target a specific device (see scripts/android-device.sh list), e.g. 192.168.1.23:41234
#                      default: the only connected device
#   --release          Release configuration
#   --apk              build a standalone APK and `adb install -r` it, instead of `dotnet build -t:Run`
#                      (fast deployment); use when -t:Run misbehaves or to test the real package
#   --logs             after launch, stream logcat for the app's process (+ crashes / .NET runtime)
#   --logs-only        don't build; just attach logcat to the running app
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

CONFIG=Debug; TARGET=auto; SERIAL=""; MODE=run; LOGS=0; BUILD=1; EXTRA=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --emulator) TARGET=emulator ;;
    --device) TARGET=device; SERIAL="${2:-}"; [[ -n "$SERIAL" ]] || die "--device needs a serial"; shift ;;
    --release) CONFIG=Release ;;
    --apk) MODE=apk ;;
    --logs) LOGS=1 ;;
    --logs-only) LOGS=1; BUILD=0 ;;
    -h|--help) usage_from_header "$0"; exit 0 ;;
    --) shift; EXTRA=("$@"); break ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
  shift
done

export_android_env
require_adb
PROJ_DIR="$(dirname "$ANDROID_PROJECT")"

# ---- pick device --------------------------------------------------------------
devices() { "$ADB" devices | awk 'NR>1 && $2=="device" {print $1}'; }
case "$TARGET" in
  emulator)
    SERIAL="$(devices | grep '^emulator-' | head -1 || true)"
    if [[ -z "$SERIAL" ]]; then
      "$(dirname "$0")/android-emulator.sh"
      SERIAL="$(devices | grep '^emulator-' | head -1)"
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

# ---- build + deploy -----------------------------------------------------------
if [[ $BUILD -eq 1 ]]; then
  require_dotnet
  workload_installed android || die "android workload missing — run scripts/doctor.sh"
  [[ -d "$PROJ_DIR" ]] || die "project not found: $PROJ_DIR"
  cd "$REPO_ROOT"
  if [[ $MODE == run ]]; then
    step "dotnet build -t:Run ($CONFIG → $SERIAL)"
    dotnet build "$PROJ_DIR" -t:Run "${MSBUILD_ENV[@]}" ${EXTRA[@]+"${EXTRA[@]}"}
  else
    step "dotnet build ($CONFIG, standalone APK)"
    dotnet build "$PROJ_DIR" "${MSBUILD_ENV[@]}" -p:EmbedAssembliesIntoApk=true ${EXTRA[@]+"${EXTRA[@]}"}
    APK="$(find "$PROJ_DIR/bin/$CONFIG" -name "$APP_ID-Signed.apk" -print0 2>/dev/null | xargs -0 ls -t 2>/dev/null | head -1)"
    [[ -n "$APK" ]] || die "no $APP_ID-Signed.apk under $PROJ_DIR/bin/$CONFIG"
    step "adb install -r ${APK#"$REPO_ROOT"/}"
    "$ADB" -s "$SERIAL" install -r "$APK" >/dev/null && ok "installed"
    step "Launching $APP_ID"
    "$ADB" -s "$SERIAL" shell monkey -p "$APP_ID" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1 \
      && ok "launched" || fail "launch failed"
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
