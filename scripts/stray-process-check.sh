#!/usr/bin/env bash
# Warn about processes an agent may have left running: an Android emulator
# (qemu-system) and a PenBridge Mac app launched from a worktree build.
# Read-only, silent and exit 0 when nothing is found; never fails.
#
# The user's own app does not warn: only an executable whose path contains
# /.claude/worktrees/ counts. /Applications/PenBridge.app and the main
# checkout's src/PenBridge.Mac/bin/... build are left alone.
#
# Usage: scripts/stray-process-check.sh [--json] [--help]
#   --json  emit a SubagentStop hook payload (additionalContext + systemMessage)
#           instead of plain text, so the warning reaches the architect session.
set -uo pipefail

case "${1:-}" in
  -h|--help) sed -n '2,/^set /p' "$0" | sed '$d;s/^# \{0,1\}//'; exit 0 ;;
  ""|--json) ;;
  *) echo "unknown option: $1 (see --help)" >&2; exit 0 ;;
esac

lines=""
while read -r pid etime cpu rss comm; do
  [[ -n "${pid:-}" ]] || continue
  exe=${comm%% *}
  case "$exe" in
    qemu-system*|*/qemu-system*)
      lines+="Android emulator running: pid $pid, up $etime, cpu ${cpu}%, rss $((rss / 1024)) MB. Stop: scripts/android-emulator.sh --stop"$'\n' ;;
    */.claude/worktrees/*/PenBridge.app/Contents/MacOS/PenBridge)
      lines+="PenBridge Mac app from a worktree build: pid $pid, up $etime, cpu ${cpu}%, rss $((rss / 1024)) MB ($exe). Stop: menu-bar Quit, or kill $pid"$'\n' ;;
  esac
done < <(ps -axww -o pid=,etime=,%cpu=,rss=,command= 2>/dev/null | awk '{c=$0; sub(/^ *[0-9]+ +[^ ]+ +[^ ]+ +[0-9]+ +/, "", c); split($0, f, " "); print f[1], f[2], f[3], f[4], c}')

[[ -n "$lines" ]] || exit 0
msg="Stray processes left by an agent (stop them before the next device turn):"$'\n'"$lines"

if [[ "${1:-}" == "--json" ]]; then
  esc=$(printf '%s' "$msg" | sed 's/\\/\\\\/g; s/"/\\"/g' | awk 'BEGIN{ORS="\\n"} {print}')
  printf '{"systemMessage":"%s","hookSpecificOutput":{"hookEventName":"SubagentStop","additionalContext":"%s"}}\n' "$esc" "$esc"
else
  printf '%s' "$msg"
fi
exit 0
