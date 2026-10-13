#!/usr/bin/env bash
# Check that a built head still contains the code PenBridge.Core needs (issues #46, #42).
# For each assembly folder: every cross-assembly type reference resolves (nothing the
# kept code uses was trimmed or left out of the bundle), and if PenBridge.Core's
# PairingSession is bundled, BouncyCastle's J-PAKE types are too. Build the head first.
#
# Usage: scripts/check-bundle.sh [--require-pairing] [--mac <Debug|Release>] [--android <Debug|Release>] [--ios <Debug|Release>] [<path>...]
#   scripts/check-bundle.sh --mac Debug                 # every PenBridge.app under src/PenBridge.Mac/bin/Debug
#   scripts/check-bundle.sh --mac Release               # the NativeAOT app from `dotnet publish -c Release`
#   scripts/check-bundle.sh --android Release           # ILLink output packed into the APK (obj/.../<rid>/linked)
#   scripts/check-bundle.sh path/to/PenBridge.app some/assembly/dir
#   --require-pairing   also fail if PairingSession is absent (once the head calls pairing)
#
# Paths: a macOS .app (checks Contents/MonoBundle, per RID for universal bundles), an iOS
# .app, or any folder of assemblies. The APK packs assemblies into a compressed store, so
# --android checks the trimmed assemblies that go into it instead. Android Debug builds do
# not trim; --android Debug checks obj/Debug/.../android/assets/<abi>.
#
# NativeAOT macOS apps (the Release publish) have no assemblies in the bundle: the managed
# code is compiled into Contents/MacOS/<exe>. For those the check reads the native symbols
# per architecture (of the executable, or of the sibling <app>.dSYM when the executable is
# stripped, as the universal one is): managed code from PenBridge.Mac is there, and whenever
# PairingSession code is compiled in, so is the J-PAKE code it calls (JPakeParticipant,
# JPakePrimeOrderGroups, Sha256Digest, BigInteger). With --mac, the ILLink output that ILC
# compiled (obj/<config>/net10.0-macos/<rid>/linked) also gets the reference-closure check.
# Needs Xcode's nm and lipo.
# Exit: 0 all pass, 1 a check failed, 2 usage error / nothing found.
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

checker="$REPO_ROOT/scripts/lib/check-bundle.cs"
extra=()
paths=()
require_pairing=0
mac_configs=()

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
    --require-pairing) extra+=("$1"); require_pairing=1; shift ;;
    --mac)
      [[ $# -ge 2 ]] || { echo "error: --mac needs Debug or Release" >&2; exit 2; }
      valid_config "$2"
      add_matches "macOS $2" "$REPO_ROOT"/src/PenBridge.Mac/bin/"$2"/net10.0-macos/PenBridge.app "$REPO_ROOT"/src/PenBridge.Mac/bin/"$2"/net10.0-macos/*/PenBridge.app
      mac_configs+=("$2")
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

# A macOS .app whose managed code is compiled in (NativeAOT): an executable, no .dll anywhere in MonoBundle.
is_nativeaot_app() {
  local app="${1%/}"
  [[ "$app" == *.app && -d "$app/Contents/MacOS" ]] || return 1
  [[ -z "$(find "$app/Contents/MonoBundle" -name '*.dll' -print -quit 2>/dev/null)" ]]
}

# NativeAOT (ILC) symbol prefixes: _<assembly>_<namespace>_<type>__<member>, dots as underscores.
readonly AOT_HEAD_PREFIX="_PenBridge_PenBridge_Mac_"
readonly AOT_PAIRING_PREFIX="_PenBridge_Core_PenBridge_Core_Security_PairingSession__"
readonly AOT_JPAKE_PREFIXES=(
  "_BouncyCastle_Cryptography_Org_BouncyCastle_Crypto_Agreement_JPake_JPakeParticipant__"
  "_BouncyCastle_Cryptography_Org_BouncyCastle_Crypto_Agreement_JPake_JPakePrimeOrderGroups_"
  "_BouncyCastle_Cryptography_Org_BouncyCastle_Crypto_Digests_Sha256Digest__"
  "_BouncyCastle_Cryptography_Org_BouncyCastle_Math_BigInteger__"
)

# Prints OK/FAIL lines like the assembly checker; returns 1 on failure.
check_nativeaot_app() {
  local app="${1%/}" exe symfile arch names errors=() prefix
  exe="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Contents/Info.plist" 2>/dev/null || true)"
  [[ -n "$exe" && -f "$app/Contents/MacOS/$exe" ]] || { echo "FAIL $app: no executable in Contents/MacOS"; return 1; }
  symfile="$app/Contents/MacOS/$exe"
  # Capture first: `nm | grep -q` fails under pipefail when grep exits early (SIGPIPE).
  names="$(nm -j "$symfile" 2>/dev/null || true)"
  if ! grep -q "^$AOT_HEAD_PREFIX" <<<"$names"; then
    symfile="$app.dSYM/Contents/Resources/DWARF/$exe"
    [[ -f "$symfile" ]] || { echo "FAIL $app: no PenBridge.Mac symbols in the executable (stripped, or not a PenBridge NativeAOT build) and no $(basename "$app").dSYM next to it; check the build output instead"; return 1; }
  fi

  local ok=1
  for arch in $(lipo -archs "$symfile"); do
    names="$(nm -arch "$arch" -j "$symfile" 2>/dev/null)" || { echo "FAIL $app [$arch]: nm failed on ${symfile#"$REPO_ROOT"/}"; ok=0; continue; }
    errors=()
    grep -q "^$AOT_HEAD_PREFIX" <<<"$names" || errors+=("no PenBridge.Mac code in the executable (not a PenBridge NativeAOT build?)")
    if grep -q "^$AOT_PAIRING_PREFIX" <<<"$names"; then
      for prefix in "${AOT_JPAKE_PREFIXES[@]}"; do
        grep -q "^$prefix" <<<"$names" || errors+=("PairingSession is compiled in but ${prefix#_BouncyCastle_Cryptography_} is not (J-PAKE pairing would fail)")
      done
    elif (( require_pairing )); then
      errors+=("--require-pairing: PairingSession is not compiled in")
    fi

    if (( ${#errors[@]} )); then
      echo "FAIL $app [$arch, NativeAOT]: ${#errors[@]} problem(s)"
      printf '       %s\n' "${errors[@]}"
      ok=0
    else
      local pairing="pairing code not referenced by the head"
      grep -q "^$AOT_PAIRING_PREFIX" <<<"$names" && pairing="J-PAKE pairing code present"
      echo "OK   $app [$arch, NativeAOT]: managed code compiled in, $pairing (symbols: ${symfile#"$REPO_ROOT"/})"
    fi
  done
  (( ok ))
}

assembly_paths=()
aot_apps=()
for p in "${paths[@]}"; do
  if is_nativeaot_app "$p"; then aot_apps+=("$p"); else assembly_paths+=("$p"); fi
done

# The ILLink output ILC compiled: reference closure of what went into the NativeAOT executable.
if (( ${#aot_apps[@]} && ${#mac_configs[@]} )); then
  shopt -s nullglob
  for cfg in "${mac_configs[@]}"; do
    for d in "$REPO_ROOT"/src/PenBridge.Mac/obj/"$cfg"/net10.0-macos/*/linked; do assembly_paths+=("$d"); done
  done
  shopt -u nullglob
fi

status=0
if (( ${#aot_apps[@]} )); then
  step "check-bundle (NativeAOT) ${aot_apps[*]#"$REPO_ROOT"/}"
  { have nm && have lipo; } || die "nm/lipo not found: install the Xcode command line tools"
  for app in "${aot_apps[@]}"; do
    check_nativeaot_app "$app" || status=1
  done
fi

if (( ${#assembly_paths[@]} )); then
  require_dotnet
  step "check-bundle ${assembly_paths[*]#"$REPO_ROOT"/}"
  # File-based app; -c Release keeps the (cached) checker build quiet and fast.
  dotnet run --file "$checker" -c Release -- ${extra[@]+"${extra[@]}"} "${assembly_paths[@]}" || status=$?
fi

exit "$status"
