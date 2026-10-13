---
name: capture-skill
description: When and how to turn a learning into a new or updated PenBridge project skill in .claude/skills/. Use at the end of a feature or workstream, when a subagent reports "learnings", after debugging something non-obvious (toolchain, signing, TCC, adb, S Pen, CI quirks), or when an existing skill turned out wrong or incomplete.
---

# Capture a skill

Skills are living documents. Reality wins: if a skill was wrong, fix it in the same session.

## Capture when
- It took more than one attempt to figure out, and will recur (build/sign/permission/device/CI quirks, platform API gotchas).
- A repeatable procedure emerged (a checklist, a command sequence).
- A subagent flagged a learning in its report.

## End of a phase
Sources: `.claude/progress/<feature>/progress.md`, `progress.log.md`, every `ws-*.md` ("Learnings", "Follow-ups"), merged PR bodies (`gh pr list -R Blake11/PenBridge --state merged`, `gh pr view <n> --json body`), reviewer reports. Dedupe against existing skills, put each learning in the skill whose trigger matches, and fix skills the phase proved wrong. Commit `docs: capture <phase> learnings into skills`.

## Don't capture
- One-off facts about a single change (→ commit/PR message).
- Decisions (→ ADR, skill `adr`). Design (→ `docs/architecture.md`). Human setup docs (→ `docs/development.md`).
- Anything already in code/scripts — improve the script instead and keep the skill pointing at it.

## Update vs new
- Prefer updating an existing skill (`ls .claude/skills`). New skill only for a distinct trigger/task.
- Keep each `SKILL.md` short and actionable (aim < 100 lines); move long reference material to a sibling file in the skill folder and link it.

## Format
```markdown
---
name: <dash-case, same as folder>
description: <what it does + exactly when to use it; concrete trigger words — this is what Claude matches on>
---

# <Title>
<steps / commands / gotchas; imperative; current facts only>
```
- No version-pinned commentary ("as of v1.2"); state the behaviour.
- Mention new skills in `CLAUDE.md` only if they change the workflow.
- Skill files are tracked: include them in the relevant PR (`docs:` or `chore:` commit) — after user approval to commit.
