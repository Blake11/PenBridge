#!/usr/bin/env bash
# Build and run the CGEvent probe (tools/cgevent-probe): a listen-only event recorder and a scripted event poster.
#
# Usage: scripts/cgevent-probe.sh build                     # compile both into .build/cgevent-probe/ (git-ignored)
#        scripts/cgevent-probe.sh record [record options…]  # build if needed, then run the recorder
#        scripts/cgevent-probe.sh post [post options…]      # build if needed, then run the poster
#        scripts/cgevent-probe.sh path                      # print the output folder
#
# record: [--moves] [--all] [--hid] [--raw] [--types 22,29]; needs Input Monitoring for the terminal.
# post:   [-n] [script-file | -e 'cmd; cmd']; needs Accessibility for the terminal. Script syntax: tools/cgevent-probe/README.md.
# The binaries are plain executables for this Mac's architecture (uname -m) with no repo dependencies: copy them to another Mac (README, "MacBook over LAN").
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

SRC_DIR="$REPO_ROOT/tools/cgevent-probe"
OUT_DIR="$REPO_ROOT/.build/cgevent-probe"

build_one() {  # $1 = record|post
  local src="$SRC_DIR/$1.swift" bin="$OUT_DIR/$1"
  if [[ ! -x "$bin" || "$src" -nt "$bin" ]]; then
    have xcrun || die "xcrun not found (install the Xcode command line tools)"
    mkdir -p "$OUT_DIR"
    xcrun swiftc -O -target "$(uname -m)-apple-macos14.0" -o "$bin" "$src" || die "swiftc failed for $src"
    ok "built $bin"
  fi
}

cmd="${1:-}"
case "$cmd" in
  -h|--help|"") usage_from_header "$0"; [[ -n "$cmd" ]]; exit $? ;;
  build) build_one record; build_one post ;;
  record|post) shift; build_one "$cmd"; exec "$OUT_DIR/$cmd" "$@" ;;
  path) echo "$OUT_DIR" ;;
  *) die "unknown command: $cmd (build|record|post|path)" ;;
esac
