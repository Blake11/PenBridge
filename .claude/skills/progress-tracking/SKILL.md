---
name: progress-tracking
description: Template and update rules for .claude/progress/<feature>/ — the architect's progress.md plus one ws-<id>.md per workstream agent — the per-feature session state. Use when starting work on any feature or phase, when resuming a session, when delegating or finishing a workstream, or whenever status, decisions, or open questions change.
---

# Progress tracking

Every feature keeps a folder `.claude/progress/<feature>/` in the **main worktree** (untracked, gitignored — never commit it). Worktrees don't have it: subagents in worktrees write to the main-tree path given in their prompt.

GitHub issues are the plan of record (ADR-0014, skill `issues`); these files are local session state for resuming, and link the issues.

## Files and owners (one writer per file)

| File | Owner | Contents |
|---|---|---|
| `progress.md` | architect (main session) only | goal, decisions, workstream table with Issue column, open questions, next steps |
| `ws-<id>.md` (e.g. `ws-p1-d.md`) | that workstream's agent only | status, notes, follow-ups, learnings |

**Agents never edit `progress.md`.** Concurrent agents editing one shared file lost each other's sections several times; separate files remove the race. The architect reads the `ws-*.md` files and copies status into the table.

A SessionStart hook lists existing progress files. On resume, read `progress.md` and the `ws-*.md` files first.

## `progress.md` template

```markdown
# Progress — <feature> (<phase>)

Started <YYYY-MM-DD>. Architect: main session. Epic: #<n>.

## Goal
<1–3 sentences: outcome and why>

## Decisions
- <decision> (ADR-xxxx / user, date)

## Workstreams
| WS | Issue | Owner | Branch / worktree | Files owned | Status |
|---|---|---|---|---|---|
| P1-D Net | #9 | core-dev | feat-core-net | src/PenBridge.Core/Net/** | todo / in progress / review / done / blocked |

Details per workstream: `ws-<id>.md`.

## Open questions
- [ ] <question> — default if unanswered: <…>

## Next steps
1. …
```

## `ws-<id>.md` template

```markdown
# WS-<ID> <name> — #<issue>

- Status: todo / in progress / review / done / blocked
- Branch / commits: …
- Notes: … (what was done, test results, device verified on)
- Follow-ups: … (bugs not fixed here → ask the architect to file issues, or file them per skill `issues`)
- Learnings: … (candidates for skill `capture-skill`)
```

## Rules
- One writer per file. The architect never writes a `ws-*.md` that an agent owns while it's running; an agent writes only its own `ws-<id>.md`.
- Use targeted edits (Edit tool) rather than rewriting a file, even your own.
- Update at every state change, not just at the end; keep it terse — it's for resuming, not a report.
- Record which device verified a change (emulator, S23, Mac + Krita).
- Status also lives on GitHub: the architect sets `status: in progress` / `status: blocked` labels and links PRs on the issue.
- When the feature is merged, set all rows to done with PR links; delete the folder once nothing is pending (ask the user).
