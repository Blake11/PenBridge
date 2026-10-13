#!/usr/bin/env bash
# Shared helpers for PenBridge developer scripts. Source it; do not execute it.
# shellcheck disable=SC2034

if [[ -n "${PENBRIDGE_COMMON_SOURCED:-}" ]]; then return 0; fi
PENBRIDGE_COMMON_SOURCED=1

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# ---- constants -------------------------------------------------------------
APP_ID="com.blake11.penbridge"
MAC_PROJECT="$REPO_ROOT/src/PenBridge.Mac/PenBridge.Mac.csproj"
ANDROID_PROJECT="$REPO_ROOT/src/PenBridge.Android/PenBridge.Android.csproj"
CORE_TESTS_PROJECT="$REPO_ROOT/tests/PenBridge.Core.Tests"
DEV_CERT_NAME="PenBridge Dev"

# Android SDK packages. Platform/build-tools follow the defaults of the .NET 10
# Android workload (Microsoft.Android.Sdk: AndroidSdkPlatformVersion / AndroidSdkBuildToolsVersion).
ANDROID_API_LEVEL="${ANDROID_API_LEVEL:-36}"
ANDROID_BUILD_TOOLS="${ANDROID_BUILD_TOOLS:-36.0.0}"
ANDROID_SYSTEM_IMAGE="system-images;android-${ANDROID_API_LEVEL};google_apis;arm64-v8a"
AVD_NAME="PenBridge_Tablet"
AVD_DEVICE="${AVD_DEVICE:-pixel_tablet}"

# The .NET 10 Android workload accepts JDK 17.0 .. 21.x (MinimumSupportedJavaVersion /
# LatestSupportedJavaVersion). We standardise on 17 (Microsoft's recommended JDK).
JDK_FORMULA="openjdk@17"
JDK_MIN_MAJOR=17
JDK_MAX_MAJOR=21

DOTNET_WORKLOADS=(macos android ios)

# ---- output ----------------------------------------------------------------
if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then
  C_RED=$'\033[31m'; C_GRN=$'\033[32m'; C_YEL=$'\033[33m'; C_BLU=$'\033[34m'
  C_DIM=$'\033[2m'; C_BLD=$'\033[1m'; C_RST=$'\033[0m'
else
  C_RED=; C_GRN=; C_YEL=; C_BLU=; C_DIM=; C_BLD=; C_RST=
fi

step()  { printf '%s==>%s %s%s%s\n' "$C_BLU" "$C_RST" "$C_BLD" "$*" "$C_RST"; }
info()  { printf '    %s\n' "$*"; }
ok()    { printf '  %s✓%s %s\n' "$C_GRN" "$C_RST" "$*"; }
warn()  { printf '  %s!%s %s\n' "$C_YEL" "$C_RST" "$*" >&2; }
fail()  { printf '  %s✗%s %s\n' "$C_RED" "$C_RST" "$*"; }
hint()  { printf '      %s↳ %s%s\n' "$C_DIM" "$*" "$C_RST"; }
die()   { printf '%serror:%s %s\n' "$C_RED" "$C_RST" "$*" >&2; exit 1; }

have()  { command -v "$1" >/dev/null 2>&1; }

# Print the leading comment block of the calling script (lines starting with "# ")
# as --help text.
usage_from_header() {
  local file="${1:-$0}"
  awk 'NR==1 {next} /^#/ {sub(/^# ?/, ""); print; next} {exit}' "$file"
}

# ---- Homebrew --------------------------------------------------------------
brew_prefix() {
  if have brew; then brew --prefix; elif [[ -x /opt/homebrew/bin/brew ]]; then echo /opt/homebrew; else echo /usr/local; fi
}

# ---- Java ------------------------------------------------------------------
jdk_major() {  # $1 = JAVA_HOME; prints major version or nothing
  local java="$1/bin/java" v
  [[ -x "$java" ]] || return 1
  v="$("$java" -version 2>&1 | awk -F'"' '/version/ {print $2; exit}')"
  v="${v#1.}"; echo "${v%%.*}"
}

jdk_ok() {
  local m; m="$(jdk_major "$1" 2>/dev/null)" || return 1
  [[ -n "$m" && "$m" -ge $JDK_MIN_MAJOR && "$m" -le $JDK_MAX_MAJOR ]]
}

# Resolve a supported JDK: $JAVA_HOME if supported, else Homebrew openjdk@17, else java_home -v 17/21.
resolve_java_home() {
  local c
  for c in "${JAVA_HOME:-}" \
           "$(brew_prefix)/opt/$JDK_FORMULA/libexec/openjdk.jdk/Contents/Home" \
           "$(/usr/libexec/java_home -v 17 2>/dev/null || true)" \
           "$(/usr/libexec/java_home -v 21 2>/dev/null || true)"; do
    if [[ -n "$c" ]] && jdk_ok "$c"; then echo "$c"; return 0; fi
  done
  return 1
}

# ---- Android SDK -----------------------------------------------------------
# Default matches the Android Studio / .NET Android default lookup location, so builds
# work without env vars. Override with ANDROID_HOME.
resolve_android_home() {
  echo "${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}}"
}

# sdkmanager: prefer the copy inside the SDK, fall back to the Homebrew cask (seed).
sdkmanager_bin() {
  local sdk; sdk="$(resolve_android_home)"
  local c
  for c in "$sdk/cmdline-tools/latest/bin/sdkmanager" \
           "$(brew_prefix)/share/android-commandlinetools/cmdline-tools/latest/bin/sdkmanager"; do
    [[ -x "$c" ]] && { echo "$c"; return 0; }
  done
  return 1
}

avdmanager_bin() {
  local sdk; sdk="$(resolve_android_home)"
  local c
  for c in "$sdk/cmdline-tools/latest/bin/avdmanager" \
           "$(brew_prefix)/share/android-commandlinetools/cmdline-tools/latest/bin/avdmanager"; do
    [[ -x "$c" ]] && { echo "$c"; return 0; }
  done
  return 1
}

adb_bin() {
  local a; a="$(resolve_android_home)/platform-tools/adb"
  if [[ -x "$a" ]]; then echo "$a"; elif have adb; then command -v adb; else return 1; fi
}

emulator_bin() {
  local e; e="$(resolve_android_home)/emulator/emulator"
  [[ -x "$e" ]] && echo "$e"
}

require_adb() {
  ADB="$(adb_bin)" || die "adb not found — run scripts/bootstrap.sh"
}

# Export env for Android/Java tooling in this process only (never written globally).
export_android_env() {
  local jh
  jh="$(resolve_java_home)" || die "no JDK ${JDK_MIN_MAJOR}–${JDK_MAX_MAJOR} found — run scripts/bootstrap.sh"
  export JAVA_HOME="$jh"
  export ANDROID_HOME; ANDROID_HOME="$(resolve_android_home)"
  export ANDROID_SDK_ROOT="$ANDROID_HOME"
  export PATH="$JAVA_HOME/bin:$ANDROID_HOME/platform-tools:$ANDROID_HOME/emulator:$ANDROID_HOME/cmdline-tools/latest/bin:$PATH"
}

# ---- .NET ------------------------------------------------------------------
require_dotnet() { have dotnet || die "dotnet not found — install the .NET 10 SDK (https://dot.net)"; }

workload_installed() {  # $1 = workload id
  dotnet workload list 2>/dev/null | awk 'NR>2 {print $1}' | grep -qx "$1"
}

dotnet_root() { dirname "$(readlink -f "$(command -v dotnet)")"; }

# ---- Xcode -----------------------------------------------------------------
xcode_ok() {
  local dev; dev="$(xcode-select -p 2>/dev/null || true)"
  [[ "$dev" == *.app/Contents/Developer ]] && xcodebuild -version >/dev/null 2>&1
}
