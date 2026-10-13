---
name: feature-workflow
description: Architect workflow for building any PenBridge feature or roadmap phase end-to-end — clarify with the user, write ADR/spec, create progress.md, split into workstreams with file ownership, set up worktrees, delegate to subagents, review, open PRs, capture learnings. Use at the start of any non-trivial feature, phase, or multi-file change, and whenever planning or delegating work.
---

# Feature workflow (main session = architect)

The main session does **not** implement. It clarifies, specifies, delegates, reviews.

## 1. Clarify (hard gate)
- Read `CLAUDE.md`, `docs/architecture.md`, `docs/decisions.md`, and any existing `.claude/progress/<feature>/progress.md`.
- List every ambiguity (behaviour, UX, protocol impact, platform limits, testing on which device, scope in/out). Ask the user — batch questions, offer options with a recommendation.
- Do not delegate until every open question that affects the design is answered. Unanswerable ones go in progress.md "Open questions" with an explicit default.

## 2. Specify
- Decisions with lasting impact → ADR (skill `adr`). Design changes → `docs/architecture.md`.
- Wire-format changes → follow skill `protocol-change`.

## 3. Track
- Create/update `.claude/progress/<feature>/progress.md` (skill `progress-tracking`). `<feature>` is dash-case, e.g. `udp-input`.

## 4. Split into workstreams
- Each workstream: owner agent (`core-dev`, `mac-dev`, `android-dev`, `ci-dev`), **disjoint file globs**, acceptance criteria, test plan.
- Order dependencies: Core contracts first (types/interfaces), then heads in parallel.
- Prefer one branch + worktree per workstream (skill `parallel-worktrees`). Shared tree only when ownership is strictly disjoint and no commits are needed.

## 5. Delegate
Launch independent workstreams in one message (parallel). Prompt template:

```
You are <agent> implementing workstream <ID> "<name>" of feature <feature> for PenBridge.
Read first: CLAUDE.md, docs/architecture.md, docs/decisions.md (ADR-xxxx), .claude/progress/<feature>/progress.md, skill <relevant skill>.
Goal: <what and why>.
You own ONLY: <file globs>. Do not touch anything else; if you need a change elsewhere, stop and report it.
Acceptance: <criteria>. Tests: <which; scripts/test.sh must pass>.
Branch/worktree: <name or "shared tree, do not commit">.
Update your workstream section in progress.md (status, notes, follow-ups).
Do not push or open PRs. <Commit with Conventional Commits, no AI attribution | Do not commit>.
Report in <200 words: what changed (paths), test results, deviations, open questions, learnings worth a skill.
```

## 6. Review
- Read the diff (`git diff`, or the worktree branch). Run `reviewer` agent for hot-path perf, security (pairing/injection), protocol versioning.
- Verify: `scripts/test.sh`, `dotnet build PenBridge.slnx`, device checks per skill `mac-build-run` / `android-build-run`. Ask the user to do device checks you can't do.
- Send fixes back to the same agent (SendMessage) rather than fixing in the main session.

## 7. Ship
- PR per workstream or per feature (skill `pr`) — only after the user approves pushing.
- Update progress.md status; mark done when merged.

## 8. Learn
- Run skill `capture-skill` on anything that surprised you or the subagents (flagged "learnings").
