#!/usr/bin/env bash
# Read-only check of every PenBridge prerequisite (✓ / ✗ with fix hints). Changes nothing.
#
# Usage: scripts/doctor.sh [--help]
#
# Exit code: 0 if everything required is present, 1 otherwise. Warnings (!) don't fail.
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

case "${1:-}" in -h|--help) usage_from_header "$0"; exit 0 ;; "") ;; *) die "unknown argument: $1" ;; esac

ERRORS=0
bad()  { fail "$1"; [[ -n "${2:-}" ]] && hint "$2"; ERRORS=$((ERRORS+1)); }
soft() { warn "$1"; [[ -n "${2:-}" ]] && hint "$2"; }

step "System"
ok "macOS $(sw_vers -productVersion) ($(uname -m))"
if have brew || [[ -x /opt/homebrew/bin/brew ]]; then ok "Homebrew"; else bad "Homebrew missing" "https://brew.sh"; fi
if have git; then ok "git $(git --version | awk '{print $3}')"; else bad "git missing" "xcode-select --install"; fi
hp="$(git -C "$REPO_ROOT" config --get core.hooksPath || true)"
if [[ "$hp" == ".githooks" ]]; then ok "git core.hooksPath = .githooks"
else soft "git core.hooksPath not set" "git config core.hooksPath .githooks"; fi

step ".NET"
if have dotnet; then
  ok "dotnet SDK $(cd "$REPO_ROOT" && dotnet --version) (global.json: $(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$REPO_ROOT/global.json" 2>/dev/null || echo n/a))"
  missing_wl=()
  for w in "${DOTNET_WORKLOADS[@]}"; do
    if workload_installed "$w"; then ok "workload $w"; else missing_wl+=("$w"); fi
  done
  if [[ ${#missing_wl[@]} -gt 0 ]]; then
    bad "workload(s) missing: ${missing_wl[*]}" "cd $REPO_ROOT && sudo dotnet workload install ${missing_wl[*]}"
  fi
else
  bad "dotnet missing" "install the .NET 10 SDK from https://dot.net"
fi

step "Xcode (macos/ios builds)"
if xcode_ok; then
  ok "$(xcodebuild -version | tr '\n' ' ')at $(xcode-select -p)"
  if xcodebuild -checkFirstLaunchStatus >/dev/null 2>&1; then ok "first-launch components installed"
  else bad "Xcode first-launch not done" "sudo xcodebuild -runFirstLaunch"; fi
else
  bad "full Xcode not selected (xcode-select -p: $(xcode-select -p 2>/dev/null || echo none))" \
      "App Store → Xcode, then: sudo xcode-select -s /Applications/Xcode.app/Contents/Developer && sudo xcodebuild -runFirstLaunch"
fi

step "Mac signing / TCC"
if security find-identity -p codesigning 2>/dev/null | grep -q "\"$DEV_CERT_NAME\""; then ok "code-signing identity \"$DEV_CERT_NAME\""
else soft "no \"$DEV_CERT_NAME\" identity — rebuilds lose the Accessibility grant" "scripts/mac-dev-cert.sh"; fi

step "Java"
if jh="$(resolve_java_home)"; then ok "JDK $(jdk_major "$jh") at $jh"
else bad "no JDK ${JDK_MIN_MAJOR}–${JDK_MAX_MAJOR}" "brew install $JDK_FORMULA"; fi
if [[ -n "${JAVA_HOME:-}" ]] && ! jdk_ok "$JAVA_HOME"; then
  soft "JAVA_HOME=$JAVA_HOME is not a JDK ${JDK_MIN_MAJOR}–${JDK_MAX_MAJOR} home (Android builds fail, e.g. XA5300)" "$(jdk_fix_hint "$JAVA_HOME")"
fi

step "Android SDK"
sdk="$(resolve_android_home)"
if [[ -d "$sdk" ]]; then ok "ANDROID_HOME → $sdk${ANDROID_HOME:+ (from env)}"; else bad "SDK dir $sdk missing" "scripts/bootstrap.sh"; fi
chk() { if [[ -e "$sdk/$1" ]]; then ok "$2"; else bad "$2 missing" "scripts/bootstrap.sh"; fi; }
chk "cmdline-tools/latest/bin/sdkmanager" "cmdline-tools"
chk "platform-tools/adb" "platform-tools (adb)"
chk "emulator/emulator" "emulator"
chk "platforms/android-${ANDROID_API_LEVEL}" "platforms;android-${ANDROID_API_LEVEL}"
chk "build-tools/${ANDROID_BUILD_TOOLS}" "build-tools;${ANDROID_BUILD_TOOLS}"
chk "system-images/android-${ANDROID_API_LEVEL}/google_apis/arm64-v8a" "system image (API ${ANDROID_API_LEVEL} google_apis arm64)"
if [[ -f "$sdk/licenses/android-sdk-license" ]]; then ok "SDK licenses accepted"
else bad "SDK licenses not accepted" "yes | sdkmanager --licenses"; fi
avd_home="${ANDROID_AVD_HOME:-$HOME/.android/avd}"
if [[ -f "$avd_home/$AVD_NAME.ini" ]]; then ok "AVD $AVD_NAME"
else soft "AVD $AVD_NAME not created" "scripts/android-emulator.sh"; fi

step "Android devices"
if a="$(adb_bin 2>/dev/null)"; then
  devs="$("$a" devices 2>/dev/null | awk 'NR>1 && NF {print "      " $0}')"
  if [[ -n "$devs" ]]; then ok "adb devices:"; echo "$devs"
  else soft "no device/emulator connected" "scripts/android-emulator.sh or scripts/android-device.sh connect <ip:port>"; fi
else
  soft "adb not available — skipped"
fi

echo
if [[ $ERRORS -eq 0 ]]; then step "${C_GRN}All required prerequisites present${C_RST}"; exit 0
else step "${C_RED}$ERRORS problem(s)${C_RST} — fix the ✗ items above (most: scripts/bootstrap.sh)"; exit 1; fi
