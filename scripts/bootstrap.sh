#!/usr/bin/env bash
# Install / verify the PenBridge developer toolchain (idempotent; safe to re-run).
#
# Usage: scripts/bootstrap.sh [--skip-android] [--skip-workloads] [--help]
#
# Does:
#   - Homebrew: openjdk@17 (keg-only) and the android-commandlinetools cask
#   - Android SDK in $ANDROID_HOME (default ~/Library/Android/sdk): cmdline-tools,
#     platform-tools, emulator, platform + build-tools, arm64 Google APIs system image;
#     accepts SDK licenses
#   - .NET workloads: macos, android, ios (needs sudo when dotnet lives in /usr/local/share/dotnet —
#     the exact command is printed instead of being run)
#   - Checks Xcode (cannot be installed by script — prints instructions)
#   - git config core.hooksPath .githooks
#   - Prints the env vars to add to your shell profile (writes nothing global)
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

SKIP_ANDROID=0; SKIP_WORKLOADS=0
for arg in "$@"; do
  case "$arg" in
    --skip-android) SKIP_ANDROID=1 ;;
    --skip-workloads) SKIP_WORKLOADS=1 ;;
    -h|--help) usage_from_header "$0"; exit 0 ;;
    *) die "unknown argument: $arg (see --help)" ;;
  esac
done

MANUAL=()   # commands the user must run themselves

# ---- Homebrew ---------------------------------------------------------------
step "Homebrew"
if ! have brew; then
  [[ -x /opt/homebrew/bin/brew ]] && eval "$(/opt/homebrew/bin/brew shellenv)"
fi
have brew || die "Homebrew not found — install from https://brew.sh, then re-run"
ok "brew $(brew --version | head -1 | awk '{print $2}')"

export HOMEBREW_NO_ENV_HINTS=1
brew_quiet() {  # run brew, show output only on failure
  local log; log="$(mktemp)"
  if brew "$@" >"$log" 2>&1; then rm -f "$log"; else cat "$log" >&2; rm -f "$log"; return 1; fi
}
brew_formula() {
  if brew list --formula "$1" >/dev/null 2>&1; then ok "$1 (already installed)"
  else info "installing $1…"; brew_quiet install "$1" && ok "$1 installed"; fi
}
brew_cask() {
  if brew list --cask "$1" >/dev/null 2>&1; then ok "$1 (already installed)"
  else info "installing cask $1…"; brew_quiet install --cask "$1" && ok "$1 installed"; fi
}

if [[ $SKIP_ANDROID -eq 0 ]]; then
  brew_formula "$JDK_FORMULA"
  brew_cask android-commandlinetools
fi

# ---- Java -------------------------------------------------------------------
if [[ $SKIP_ANDROID -eq 0 ]]; then
  step "Java (JDK ${JDK_MIN_MAJOR}–${JDK_MAX_MAJOR})"
  JAVA_HOME="$(resolve_java_home)" || die "no supported JDK found even after installing $JDK_FORMULA"
  export JAVA_HOME
  ok "JDK $(jdk_major "$JAVA_HOME") at $JAVA_HOME"
fi

# ---- Android SDK ------------------------------------------------------------
if [[ $SKIP_ANDROID -eq 0 ]]; then
  step "Android SDK"
  export ANDROID_HOME; ANDROID_HOME="$(resolve_android_home)"
  mkdir -p "$ANDROID_HOME"
  SDKM="$(sdkmanager_bin)" || die "sdkmanager not found (android-commandlinetools cask missing?)"
  info "SDK root: $ANDROID_HOME"

  info "accepting licenses…"
  yes | "$SDKM" --sdk_root="$ANDROID_HOME" --licenses >/dev/null 2>&1 || true

  PKGS=("cmdline-tools;latest" "platform-tools" "emulator"
        "platforms;android-${ANDROID_API_LEVEL}" "build-tools;${ANDROID_BUILD_TOOLS}"
        "$ANDROID_SYSTEM_IMAGE")
  # Check the filesystem (package.xml) rather than parsing `--list_installed`: newer cmdline-tools
  # delegate sdkmanager to the `android` CLI, which prints a different list format.
  pkg_installed() { [[ -f "$ANDROID_HOME/${1//;//}/package.xml" ]]; }
  missing=()
  for p in "${PKGS[@]}"; do
    if pkg_installed "$p"; then ok "$p"; else missing+=("$p"); fi
  done
  if [[ ${#missing[@]} -gt 0 ]]; then
    info "installing: ${missing[*]} (large downloads, a few minutes)…"
    yes | "$SDKM" --sdk_root="$ANDROID_HOME" "${missing[@]}" 2>&1 \
      | grep -vE '^\[=*|^$|Loading|deprecated|Android CLI|android-cli' || true
    for p in "${missing[@]}"; do
      if pkg_installed "$p"; then ok "$p installed"; else fail "$p failed to install"; fi
    done
  fi
  yes | "$SDKM" --sdk_root="$ANDROID_HOME" --licenses >/dev/null 2>&1 || true
  [[ -f "$ANDROID_HOME/licenses/android-sdk-license" ]] && ok "SDK licenses accepted" || fail "SDK licenses not accepted"
fi

# ---- .NET workloads ---------------------------------------------------------
if [[ $SKIP_WORKLOADS -eq 0 ]]; then
  step ".NET workloads (${DOTNET_WORKLOADS[*]})"
  require_dotnet
  ok "dotnet SDK $(cd "$REPO_ROOT" && dotnet --version)"
  need=()
  for w in "${DOTNET_WORKLOADS[@]}"; do
    if workload_installed "$w"; then ok "workload $w"; else need+=("$w"); fi
  done
  if [[ ${#need[@]} -gt 0 ]]; then
    root="$(dotnet_root)"
    if [[ -w "$root" && -w "$root/sdk-manifests" ]]; then
      info "installing workloads: ${need[*]}…"
      (cd "$REPO_ROOT" && dotnet workload install "${need[@]}")
      ok "workloads installed"
    else
      warn "dotnet root $root is not writable — workloads need sudo"
      MANUAL+=("cd \"$REPO_ROOT\" && sudo dotnet workload install ${need[*]}")
    fi
  fi
fi

# ---- Xcode ------------------------------------------------------------------
step "Xcode (needed for the macos/ios workloads)"
if xcode_ok; then
  ok "$(xcodebuild -version | head -1) at $(xcode-select -p)"
  if ! xcodebuild -checkFirstLaunchStatus >/dev/null 2>&1; then
    MANUAL+=("sudo xcodebuild -runFirstLaunch")
  fi
else
  fail "full Xcode not selected (xcode-select -p: $(xcode-select -p 2>/dev/null || echo none))"
  hint "Install Xcode from the App Store (match the version the macos workload expects), launch it once, then:"
  MANUAL+=("sudo xcode-select -s /Applications/Xcode.app/Contents/Developer"
           "sudo xcodebuild -license accept"
           "sudo xcodebuild -runFirstLaunch")
fi

# ---- git hooks --------------------------------------------------------------
step "git hooks"
if git -C "$REPO_ROOT" rev-parse --git-dir >/dev/null 2>&1; then
  git -C "$REPO_ROOT" config core.hooksPath .githooks
  ok "core.hooksPath = .githooks"
  [[ -d "$REPO_ROOT/.githooks" ]] || warn ".githooks/ does not exist yet"
else
  warn "not a git repo — skipped"
fi

# ---- summary ----------------------------------------------------------------
step "Environment (add to ~/.zshrc yourself; nothing was written)"
if [[ $SKIP_ANDROID -eq 0 ]]; then
  cat <<ENV
    export JAVA_HOME="$JAVA_HOME"
    export ANDROID_HOME="$ANDROID_HOME"
    export PATH="\$ANDROID_HOME/platform-tools:\$ANDROID_HOME/emulator:\$ANDROID_HOME/cmdline-tools/latest/bin:\$PATH"
ENV
  info "${C_DIM}(the scripts/ helpers set these themselves; you only need them for raw dotnet/adb use)${C_RST}"
fi

if [[ ${#MANUAL[@]} -gt 0 ]]; then
  step "${C_YEL}Run these yourself (need sudo / GUI):${C_RST}"
  for c in "${MANUAL[@]}"; do printf '    %s\n' "$c"; done
  info "then re-run scripts/bootstrap.sh and scripts/doctor.sh"
else
  step "${C_GRN}Bootstrap complete${C_RST} — run scripts/doctor.sh"
fi
