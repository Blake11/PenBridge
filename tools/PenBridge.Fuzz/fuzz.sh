#!/usr/bin/env bash
# Build the fuzz harness, instrument its copy of PenBridge.Core.dll, then run a command (README.md).
#   tools/PenBridge.Fuzz/fuzz.sh fuzz control --minutes 15
#   tools/PenBridge.Fuzz/fuzz.sh repro control artifacts/fuzz/control/findings/crash-…
# Corpus and findings go to artifacts/fuzz/<target>/ (gitignored). Only the harness's copy of Core is
# instrumented; src/ and the test build are untouched.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
BIN="$ROOT/tools/PenBridge.Fuzz/bin/Release/net10.0"

dotnet build "$ROOT/tools/PenBridge.Fuzz/PenBridge.Fuzz.csproj" -c Release --nologo -v q -clp:ErrorsOnly
dotnet "$BIN/penbridge-fuzz.dll" instrument "$BIN/PenBridge.Core.dll"
if [ "$#" -eq 0 ]; then
  exit 0
fi

cd "$ROOT"
exec dotnet "$BIN/penbridge-fuzz.dll" "$@"
