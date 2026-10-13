#!/usr/bin/env bash
# Run the PenBridge.Core unit tests (no workloads or devices needed).
#
# Usage: scripts/test.sh [extra dotnet test args…]
#   scripts/test.sh
#   scripts/test.sh -c Release
#   scripts/test.sh --filter-class "*Protocol*"     # xUnit v3 / MTP filter syntax
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

case "${1:-}" in -h|--help) usage_from_header "$0"; exit 0 ;; esac
require_dotnet
[[ -e "$CORE_TESTS_PROJECT" ]] || die "test project not found: $CORE_TESTS_PROJECT"

step "dotnet test --project tests/PenBridge.Core.Tests $*"
cd "$REPO_ROOT"
exec dotnet test --project "$CORE_TESTS_PROJECT" "$@"
