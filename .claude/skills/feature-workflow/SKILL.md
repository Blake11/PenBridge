---
name: feature-workflow
description: Architect workflow for building any PenBridge feature or roadmap phase end-to-end — clarify with the user, write ADR/spec, create epic/workstream GitHub issues and progress files, split into workstreams with file ownership, set up worktrees, delegate to subagents, review, open PRs, capture learnings. Use at the start of any non-trivial feature, phase, or multi-file change, and whenever planning or delegating work.
---

# Feature workflow (main session = architect)

The main session does **not** implement. It clarifies, specifies, delegates, reviews.

## 1. Clarify (hard gate)
- Read `CLAUDE.md`, `docs/architecture.md`, `docs/decisions.md`, the phase's epic issue (`gh issue list --label "type: epic"`), and any existing `.claude/progress/<feature>/`.
- List every ambiguity (behaviour, UX, protocol impact, platform limits, testing on which device, scope in/out). Ask the user — batch questions, offer options with a recommendation.
- Do not delegate until every open question that affects the design is answered. Unanswerable ones go in progress.md "Open questions" with an explicit default.

## 2. Specify
- Decisions with lasting impact → ADR (skill `adr`). Design changes → `docs/architecture.md`.
- Wire-format changes → follow skill `protocol-change`.

## 3. Track (issues first — ADR-0014, skill `issues`)
- Create or update the **epic** issue (`type: epic`, phase milestone) and one **workstream issue** per workstream from step 4 (template `workstream.yml`: goal, owned files, acceptance, tests, dependencies; labels `type: feature` + `area: *`; same milestone). Add each to the epic's task list.
- Create/update `.claude/progress/<feature>/progress.md` with the Issue column (skill `progress-tracking`). `<feature>` is dash-case, e.g. `udp-input`. Agents write `ws-<id>.md`, never `progress.md`.

## 4. Split into workstreams
- Each workstream: an issue, owner agent (`core-dev`, `mac-dev`, `android-dev`, `ci-dev`), **disjoint file globs**, acceptance criteria, test plan.
- Order dependencies: Core contracts first (types/interfaces), then heads in parallel.
- Prefer one branch + worktree per workstream (skill `parallel-worktrees`). Shared tree only when ownership is strictly disjoint and no commits are needed.

## 5. Delegate
Launch independent workstreams in one message (parallel). Prompt template:

```
You are <agent> implementing workstream <ID> "<name>" (issue #<n>) of feature <feature> for PenBridge.
Read first: CLAUDE.md, docs/architecture.md, docs/decisions.md (ADR-xxxx), issue #<n> (`gh issue view <n>`), .claude/progress/<feature>/progress.md (read-only), skill <relevant skill>.
Goal: <what and why>.
You own ONLY: <file globs>. Do not touch anything else; if you need a change elsewhere, stop and report it.
Acceptance: <criteria>. Tests: <which; scripts/test.sh must pass>.
Branch/worktree: <name or "shared tree, do not commit">.
Write your state ONLY to <main-tree>/.claude/progress/<feature>/ws-<id>.md (status, notes, follow-ups, learnings); never edit progress.md.
Bugs you find outside your scope: report them (or file per skill `issues`, `type: bug`), don't fix them.
Do not push or open PRs. <Commit with Conventional Commits, no AI attribution | Do not commit>.
Report in <200 words: what changed (paths), test results, deviations, bugs found, open questions, learnings worth a skill.
```

## 6. Review
- Read the diff (`git diff`, or the worktree branch). Run `reviewer` agent for hot-path perf, security (pairing/injection), protocol versioning.
- Verify: `scripts/test.sh`, `dotnet build PenBridge.slnx`, device checks per skill `mac-build-run` / `android-build-run`. Ask the user to do device checks you can't do.
- Send fixes back to the same agent (SendMessage) rather than fixing in the main session.
- Reviewer findings not fixed in the same PR become issues (`type: bug` or `type: chore`, right milestone) — never only a note in progress.md.

## 7. Ship
- PR per workstream or per feature (skill `pr`) — only after the user approves pushing. The body says `Closes #<workstream issue>`.
- Set `status: in progress` on the issue while in review; update progress.md; tick the epic task list when merged. Close the epic when all its tasks are done.

## 8. Learn
- Run skill `capture-skill` on anything that surprised you or the subagents (flagged "learnings").
