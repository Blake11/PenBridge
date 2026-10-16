#!/usr/bin/env bash
# Starts a Serena MCP server on stdio for a Claude Code subagent (inline `serena-*` servers in
# .claude/agents/*.md). No project is given: the agent activates its own worktree first.
# stdout is the MCP channel, so this script writes nothing but errors (to stderr).
#
# Usage: scripts/serena-mcp.sh [extra start-mcp-server args...]
#        scripts/serena-mcp.sh --help
#
# Serena is optional tooling: `brew install uv && uv tool install -p 3.13 serena-agent`.
set -euo pipefail

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'
  exit 0
fi

# The language servers need `dotnet` (csharp) and `node` (bash). Append their usual macOS
# locations so a client started with a minimal PATH still finds them; the caller's PATH wins.
PATH="$PATH:/usr/local/share/dotnet:$HOME/.dotnet:/opt/homebrew/bin:/usr/local/bin"

if serena="$(command -v serena)"; then
  :
elif [[ -x "$HOME/.local/bin/serena" ]]; then
  serena="$HOME/.local/bin/serena"
else
  echo "serena-mcp.sh: serena not found on PATH or in ~/.local/bin" >&2
  echo "  install: brew install uv && uv tool install -p 3.13 serena-agent" >&2
  exit 127
fi

exec "$serena" start-mcp-server --context claude-code --agent-interface tools --enable-web-dashboard False "$@"
