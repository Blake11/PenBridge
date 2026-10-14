#!/usr/bin/env bash
# Regenerate every PenBridge icon asset from the master SVGs in assets/icon (issue #101).
#
# Usage: scripts/gen-icons.sh [--check] [--preview <dir>]
#   scripts/gen-icons.sh                  # write the generated files (commit them), then validate with actool
#   scripts/gen-icons.sh --check          # regenerate into a scratch dir and fail if the committed files differ
#   scripts/gen-icons.sh --preview /tmp/i # also render PNG previews (app icon sizes, menu-bar glyphs) into /tmp/i
#
# Outputs are vector/text only, so no bitmaps are committed:
#   Android  src/PenBridge.Android/Resources/drawable/ic_launcher_{foreground,background,monochrome}.xml
#            src/PenBridge.Android/Resources/mipmap-anydpi-v26/ic_launcher{,_round}.xml   (adaptive + themed icon)
#   macOS    src/PenBridge.Mac/Resources/AppIcon.icon            (Icon Composer; actool renders it at build time)
#            src/PenBridge.Mac/Resources/Assets.xcassets/MenuBar*.imageset   (template SVGs for the status item)
# The conversion runs in scripts/lib/gen-icons.cs (a .NET file-based app). Validation and previews need Xcode's
# actool; previews also use iconutil and qlmanage (all part of macOS + Xcode).
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

check=false
preview=""
while (( $# )); do
  case "$1" in
    -h|--help) usage_from_header "$0"; exit 0 ;;
    --check) check=true ;;
    --preview) [[ $# -ge 2 ]] || die "--preview needs a directory"; preview="$2"; shift ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
  shift
done

require_dotnet
[[ "$(uname -s)" == Darwin ]] || die "gen-icons.sh needs macOS (actool validates the Mac assets)"
xcrun --find actool >/dev/null 2>&1 || die "actool not found: install Xcode and select it (xcode-select -s /Applications/Xcode.app)"
if [[ -n "$preview" ]]; then
  for tool in iconutil qlmanage; do
    have "$tool" || die "$tool not found (needed for --preview)"
  done
fi

assets="$REPO_ROOT/assets/icon"
generator="$REPO_ROOT/scripts/lib/gen-icons.cs"
mac_res="$REPO_ROOT/src/PenBridge.Mac/Resources"
outputs=(
  src/PenBridge.Android/Resources/drawable/ic_launcher_foreground.xml
  src/PenBridge.Android/Resources/drawable/ic_launcher_background.xml
  src/PenBridge.Android/Resources/drawable/ic_launcher_monochrome.xml
  src/PenBridge.Android/Resources/mipmap-anydpi-v26
  src/PenBridge.Mac/Resources/AppIcon.icon
  src/PenBridge.Mac/Resources/Assets.xcassets
)

scratch="$(mktemp -d "${TMPDIR:-/tmp}/penbridge-icons.XXXXXX")"
trap 'rm -rf "$scratch"' EXIT

if $check; then
  step "Regenerating into a scratch directory"
  dotnet run --file "$generator" -c Release -- "$assets" "$scratch/out" >/dev/null
  stale=0
  for path in "${outputs[@]}"; do
    if ! diff -r "$scratch/out/$path" "$REPO_ROOT/$path" >/dev/null 2>&1; then
      fail "$path is out of date"
      stale=1
    fi
  done
  (( stale == 0 )) || die "generated icon assets are stale: run scripts/gen-icons.sh and commit the result"
  ok "generated icon assets match assets/icon"
  mac_res="$scratch/out/src/PenBridge.Mac/Resources"
else
  step "Generating icon assets from assets/icon"
  # Drop outputs first so a renamed layer or state can't leave a stale file behind.
  for path in "${outputs[@]}"; do rm -rf "${REPO_ROOT:?}/$path"; done
  dotnet run --file "$generator" -c Release -- "$assets" "$REPO_ROOT"
fi

# The same actool call the macOS workload makes (ACTool task): app icon + catalog, deployment target 14.0.
step "Validating the Mac assets with actool"
compiled="$scratch/actool"
mkdir -p "$compiled"
actool_log="$scratch/actool.log"
if ! xcrun actool --compile "$compiled" --platform macosx --minimum-deployment-target 14.0 \
     --app-icon AppIcon --output-partial-info-plist "$compiled/partial.plist" \
     --output-format human-readable-text --errors --warnings --notices \
     "$mac_res/AppIcon.icon" "$mac_res/Assets.xcassets" >"$actool_log" 2>&1; then
  cat "$actool_log" >&2
  die "actool failed"
fi
if grep -qE ': (warning|error):' "$actool_log"; then
  cat "$actool_log" >&2
  die "actool reported warnings"
fi
for f in Assets.car AppIcon.icns; do
  [[ -f "$compiled/$f" ]] || { cat "$actool_log" >&2; die "actool produced no $f"; }
done
ok "actool: Assets.car + AppIcon.icns (macOS 14+ fallback)"

if [[ -n "$preview" ]]; then
  step "Rendering previews into $preview"
  mkdir -p "$preview"
  iconutil -c iconset -o "$preview/AppIcon.iconset" "$compiled/AppIcon.icns"
  for f in "$assets"/menubar-*.svg "$assets"/mark.svg; do
    qlmanage -t -s 288 -o "$preview" "$f" >/dev/null 2>&1 || warn "qlmanage could not render $f"
  done
  ok "previews: $preview/AppIcon.iconset/*.png, $preview/*.svg.png"
fi
