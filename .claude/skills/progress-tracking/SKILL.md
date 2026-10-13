---
name: progress-tracking
description: Template and update rules for .claude/progress/<feature>/progress.md, the per-feature session state file. Use when starting work on any feature or phase, when resuming a session, when a subagent finishes a workstream, or whenever status, decisions, or open questions change.
---

# Progress tracking

Every session developing a feature keeps `.claude/progress/<feature>/progress.md` in the **main worktree** (untracked, gitignored — never commit it). Worktrees don't have it: subagents in worktrees edit the main-tree path given in their prompt.

A SessionStart hook lists existing progress files. On resume, read the relevant one first.

## Template

```markdown
# Progress — <feature> (<phase>)

Started <YYYY-MM-DD>. Architect: main session.

## Goal
<1–3 sentences: outcome and why>

## Decisions
- <decision> (ADR-xxxx / user, date)

## Workstreams
| WS | Owner | Branch / worktree | Files owned | Status |
|---|---|---|---|---|
| A <name> | core-dev | feat-x-core | src/PenBridge.Core/Protocol/** | todo / in progress / review / done / blocked |

### WS-A <name>
- Status: …
- Notes: … (what was done, test results, device verified on)
- Follow-ups: …

## Open questions
- [ ] <question> — default if unanswered: <…>

## Next steps
1. …
```

## Rules
- Architect owns Goal, Decisions, the table, Open questions, Next steps.
- A subagent edits **only its `### WS-<id>` section** and its row's Status cell.
- Update at every state change, not just at the end; keep it terse — it's for resuming, not a report.
- Record which device verified a change (emulator, S23, Mac + Krita).
- When the feature is merged, set all rows to done and add the PR links; delete the folder once nothing is pending (ask the user).
