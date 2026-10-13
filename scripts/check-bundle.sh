#!/usr/bin/env bash
# Check that a built head still contains the code PenBridge.Core needs (issue #46).
# For each assembly folder: every cross-assembly type reference resolves (nothing the
# kept code uses was trimmed or left out of the bundle), and if PenBridge.Core's
# PairingSession is bundled, BouncyCastle's J-PAKE types are too. Build the head first.
#
# Usage: scripts/check-bundle.sh [--require-pairing] [--mac <Debug|Release>] [--android <Debug|Release>] [--ios <Debug|Release>] [<path>...]
#   scripts/check-bundle.sh --mac Debug                 # every PenBridge.app under src/PenBridge.Mac/bin/Debug
#   scripts/check-bundle.sh --android Release           # ILLink output packed into the APK (obj/.../<rid>/linked)
#   scripts/check-bundle.sh path/to/PenBridge.app some/assembly/dir
#   --require-pairing   also fail if PairingSession is absent (once the head calls pairing)
#
# Paths: a macOS .app (checks Contents/MonoBundle, per RID for universal bundles), an iOS
# .app, or any folder of assemblies. The APK packs assemblies into a compressed store, so
# --android checks the trimmed assemblies that go into it instead. Android Debug builds do
# not trim; --android Debug checks obj/Debug/.../android/assets/<abi>.
# Exit: 0 all pass, 1 a check failed, 2 usage error / nothing found.
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

checker="$REPO_ROOT/scripts/lib/check-bundle.cs"
extra=()
paths=()

add_matches() {
  local label="$1"; shift
  local found=0 p
  for p in "$@"; do
    [[ -e "$p" ]] || continue
    paths+=("$p"); found=1
  done
  (( found )) || { printf '%serror:%s no %s build output found; build it first\n' "$C_RED" "$C_RST" "$label" >&2; exit 2; }
}

valid_config() { [[ "$1" == Debug || "$1" == Release ]] || { echo "error: configuration must be Debug or Release, got '$1'" >&2; exit 2; }; }

shopt -s nullglob
while (( $# )); do
  case "$1" in
    -h|--help) usage_from_header "$0"; exit 0 ;;
    --require-pairing) extra+=("$1"); shift ;;
    --mac)
      [[ $# -ge 2 ]] || { echo "error: --mac needs Debug or Release" >&2; exit 2; }
      valid_config "$2"
      add_matches "macOS $2" "$REPO_ROOT"/src/PenBridge.Mac/bin/"$2"/net10.0-macos/PenBridge.app "$REPO_ROOT"/src/PenBridge.Mac/bin/"$2"/net10.0-macos/*/PenBridge.app
      shift 2 ;;
    --android)
      [[ $# -ge 2 ]] || { echo "error: --android needs Debug or Release" >&2; exit 2; }
      valid_config "$2"
      if [[ "$2" == Release ]]; then
        add_matches "Android Release" "$REPO_ROOT"/src/PenBridge.Android/obj/Release/net10.0-android/*/linked
      else
        add_matches "Android Debug" "$REPO_ROOT"/src/PenBridge.Android/obj/Debug/net10.0-android/android/assets/*/
      fi
      shift 2 ;;
    --ios)
      [[ $# -ge 2 ]] || { echo "error: --ios needs Debug or Release" >&2; exit 2; }
      valid_config "$2"
      add_matches "iOS $2" "$REPO_ROOT"/src/PenBridge.iOS/bin/"$2"/net10.0-ios/*/*.app
      shift 2 ;;
    -*) echo "error: unknown option $1 (see --help)" >&2; exit 2 ;;
    *) paths+=("$1"); shift ;;
  esac
done
shopt -u nullglob

(( ${#paths[@]} )) || { usage_from_header "$0" >&2; exit 2; }
require_dotnet

step "check-bundle ${paths[*]#"$REPO_ROOT"/}"
# File-based app; -c Release keeps the (cached) checker build quiet and fast.
exec dotnet run --file "$checker" -c Release -- ${extra[@]+"${extra[@]}"} "${paths[@]}"
