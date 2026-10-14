#!/usr/bin/env bash
# Run the PenBridge.Core unit tests (no workloads or devices needed).
#
# Usage: scripts/test.sh [extra dotnet test args…]
#   scripts/test.sh
#   scripts/test.sh -c Release
#   scripts/test.sh --filter-class "*Protocol*"     # xUnit v3 / MTP filter syntax
#
# On failure, the names of the failing tests are listed after the run summary
# (read from the JUnit report in tests/PenBridge.Core.Tests/TestResults/junit.xml).
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

case "${1:-}" in -h|--help) usage_from_header "$0"; exit 0 ;; esac
require_dotnet
[[ -e "$CORE_TESTS_PROJECT" ]] || die "test project not found: $CORE_TESTS_PROJECT"

step "dotnet test --project tests/PenBridge.Core.Tests $*"
cd "$REPO_ROOT"

report_dir="$CORE_TESTS_PROJECT/TestResults"   # git-ignored; overwritten by each run
report="$report_dir/junit.xml"
rm -f "$report"

rc=0
dotnet test --project "$CORE_TESTS_PROJECT" \
  --report-xunit-junit --report-xunit-junit-filename junit.xml --results-directory "$report_dir" \
  "$@" || rc=$?

# Print the name of every <testcase> that holds a <failure> or <error> element.
list_failures() {
  awk 'BEGIN { RS = "<testcase " }
       NR > 1 && /<(failure|error)[ >\/]/ {
         if (match($0, /name="[^"]*"/)) print substr($0, RSTART + 6, RLENGTH - 7)
       }' "$1" |
    sed -e 's/&quot;/"/g' -e "s/&apos;/'/g" -e 's/&lt;/</g' -e 's/&gt;/>/g' -e 's/&amp;/\&/g'
}

if (( rc != 0 )); then
  failures=""
  [[ -s "$report" ]] && failures="$(list_failures "$report")"
  if [[ -n "$failures" ]]; then
    printf '\n'
    fail "failing tests:"
    while IFS= read -r name; do info "$name"; done <<<"$failures"
  else
    warn "no failing test names found (build error, crash, or no JUnit report)"
  fi
fi
exit "$rc"
