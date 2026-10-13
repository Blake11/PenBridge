#!/usr/bin/env bash
# Publish the SimClient trimmed (or Native AOT) and run a real J-PAKE pairing plus a short
# stroke against its in-process server. Proves the BouncyCastle code pairing reaches survives
# ILLink/ILC and that the trimmed build has no trim warnings (warnings are errors). A proxy
# for the trimmed heads: it exercises the same Core + BouncyCastle code, not the Mac/Android
# bundling (scripts/check-bundle.sh covers that). No workloads needed; runs on macOS and Linux.
#
# Usage: scripts/smoke-trimmed-pairing.sh [--trim-mode partial|full]... [--aot] [--rid <rid>]
#   scripts/smoke-trimmed-pairing.sh                       # partial and full trimming (default)
#   scripts/smoke-trimmed-pairing.sh --trim-mode partial   # one mode (the heads' trimmed mode)
#   scripts/smoke-trimmed-pairing.sh --aot                 # also a Native AOT publish (needs clang / Xcode CLT)
# Output goes to artifacts/smoke-trimmed-pairing/<mode> (overwritten each run).
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

modes=()
aot=0
rid=""
while (( $# )); do
  case "$1" in
    -h|--help) usage_from_header "$0"; exit 0 ;;
    --trim-mode)
      [[ $# -ge 2 && ( "$2" == partial || "$2" == full ) ]] || die "--trim-mode needs partial or full"
      modes+=("$2"); shift 2 ;;
    --aot) aot=1; shift ;;
    --rid)
      [[ $# -ge 2 ]] || die "--rid needs a runtime identifier (e.g. osx-arm64)"
      rid="$2"; shift 2 ;;
    *) die "unknown argument $1 (see --help)" ;;
  esac
done
(( ${#modes[@]} )) || modes=(partial full)
(( aot )) && modes+=(aot)

if [[ -z "$rid" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) rid=osx-arm64 ;;
    Darwin-x86_64) rid=osx-x64 ;;
    Linux-x86_64) rid=linux-x64 ;;
    Linux-aarch64|Linux-arm64) rid=linux-arm64 ;;
    *) die "unsupported host $(uname -s)-$(uname -m); pass --rid" ;;
  esac
fi

require_dotnet
project="$REPO_ROOT/tools/PenBridge.SimClient/PenBridge.SimClient.csproj"
[[ -e "$project" ]] || die "SimClient project not found: $project"
out_root="$REPO_ROOT/artifacts/smoke-trimmed-pairing"

for mode in "${modes[@]}"; do
  out="$out_root/$mode"
  props=(-p:PublishTrimmed=true -p:TrimMode=full)
  case "$mode" in
    partial) props=(-p:PublishTrimmed=true -p:TrimMode=partial) ;;
    aot) props=(-p:PublishAot=true) ;;
  esac

  step "publish SimClient ($mode, $rid)"
  dotnet publish "$project" -c Release -r "$rid" --self-contained "${props[@]}" -o "$out" -nologo -v:q \
    || die "trimmed publish failed ($mode); trim warnings are errors, see above"

  if [[ "$mode" != aot ]]; then
    [[ -f "$out/BouncyCastle.Cryptography.dll" ]] || die "$mode: BouncyCastle.Cryptography.dll missing from the publish output"
  fi

  # A random high port so parallel runs and CI neighbours don't collide.
  port=$(( 20000 + RANDOM % 20000 ))
  step "pair + draw against the in-process server (port $port)"
  log="$out_root/$mode.log"
  if ! "$out/penbridge-sim" draw line --in-process-server --port "$port" >"$log" 2>&1; then
    tail -n 40 "$log" >&2
    die "$mode: trimmed pairing/draw failed; full log: $log"
  fi
  grep -q "Paired with server" "$log" || { tail -n 40 "$log" >&2; die "$mode: no pairing in the log: $log"; }
  ok "$mode: pairing and input work trimmed; $(grep -m1 "^Server:" "$log" || true)"
done
