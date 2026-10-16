#!/bin/sh
# PreToolUse(Bash): warn, never block, when a command cds into .claude/worktrees.
# A cd in the shared Bash shell moves the session's working directory into that
# worktree; later relative commands then run in the wrong tree. Fail-open: any
# error exits 0 with no output.
input=$(cat 2>/dev/null) || exit 0
if command -v jq >/dev/null 2>&1; then
  cmd=$(printf '%s' "$input" | jq -r '.tool_input.command // empty' 2>/dev/null) || exit 0
else
  cmd=$input
fi
[ -n "$cmd" ] || exit 0
printf '%s\n' "$cmd" | grep -Eq '(^|[;&|{[:space:]])(cd|pushd)[[:space:]]+[^;&|]*\.claude/worktrees' || exit 0
msg='This command cds into .claude/worktrees: the shared shell stays there for later commands. Use absolute paths or git -C <worktree> instead (skill parallel-worktrees).'
if command -v jq >/dev/null 2>&1; then
  jq -cn --arg m "$msg" '{systemMessage: $m, hookSpecificOutput: {hookEventName: "PreToolUse", additionalContext: $m}}' 2>/dev/null
else
  printf '{"systemMessage":"%s","hookSpecificOutput":{"hookEventName":"PreToolUse","additionalContext":"%s"}}\n' "$msg" "$msg"
fi
exit 0
