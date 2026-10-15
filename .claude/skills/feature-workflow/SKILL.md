---
name: feature-workflow
description: Architect workflow for building any PenBridge feature or roadmap phase end-to-end — clarify with the user (incl. a feel walkthrough against the reference device), write ADR/spec, create epic/workstream GitHub issues and progress files, split into workstreams with file ownership and contracts, pre-create worktrees, delegate to subagents (model per task), review (tiered), open PRs, capture learnings. Use at the start of any non-trivial feature, phase, or multi-file change, and whenever planning or delegating work.
---

# Feature workflow (main session = architect)

The main session does **not** implement. It clarifies, specifies, delegates, reviews.

**One architect session per phase.** Start each phase in a fresh session; the handoff is `.claude/progress/<feature>/progress.md` plus the epic issue (keep both current enough to resume from). Don't carry a finished phase's context into the next.

## 1. Clarify (hard gate)
- Read `CLAUDE.md`, the relevant `docs/architecture.md` sections, the ADR index at the top of `docs/decisions.md` (then only the ADRs that matter), the phase's epic issue (`gh issue list --label "type: epic"`), and any existing `.claude/progress/<feature>/`.
- List every ambiguity (behaviour, UX, protocol impact, platform limits, testing on which device, scope in/out). Ask the user — batch questions, offer options with a recommendation.
- **Feel walkthrough (input features):** before the ADR, walk through every input with the user against the reference device (Mac trackpad, Wacom, iPad…): pen (contact, hover, side button, lift), 1, 2, 3 and 4 fingers (tap, double tap, drag, hold, mixed with the pen), hover, and screen edges/corners. For each: what the reference does, what we do, what's out of scope. Record the answers in the ADR.
- Do not delegate until every open question that affects the design is answered. Unanswerable ones go in progress.md "Open questions" with an explicit default.

## 2. Specify
- Decisions with lasting impact → ADR (skill `adr`). Design changes → `docs/architecture.md`.
- Wire-format changes → follow skill `protocol-change`.
- UI or visual assets (icons): have an agent render 2–3 concepts as a self-contained preview HTML plus PNG renders, `open` it locally for the user to pick, and iterate before wiring anything in.

## 3. Track (issues first — ADR-0014, skill `issues`)
- Create or update the **epic** issue (`type: epic`, phase milestone) and one **workstream issue** per workstream from step 4 (template `workstream.yml`: goal, owned files, acceptance, tests, dependencies; labels `type: feature` + `area: *`; same milestone). Add each to the epic's task list.
- **Contract first (cross-layer features):** the workstream issues fix the contract up front — event kinds and their semantics, Core API signatures, wire fields and capability bits, who emits/consumes what. Then the Core and head agents start **together** against it instead of heads waiting for Core to merge. A contract change mid-flight goes back through the architect (edit the issues, tell every agent).
- Create/update `.claude/progress/<feature>/progress.md` with the Issue column (skill `progress-tracking`). `<feature>` is dash-case, e.g. `udp-input`.

## 4. Split into workstreams
- Each workstream: an issue, owner agent, **disjoint file globs**, acceptance criteria, test plan.
- **Small features (< ~150 changed lines)** that span Core and one head: one cross-head agent owns both (pick the agent whose side is harder; its definition covers the other side's rules). Split only when it's bigger or parallelism pays.
- Prefer one branch + worktree per workstream (skill `parallel-worktrees`). Shared tree only when ownership is strictly disjoint and no commits are needed.
- **Devices:** only one agent uses a physical device (S23, Mac injection) at a time; the architect grants turns in the prompt. Parallel emulator agents each get their own AVD and ApplicationId (skill `android-build-run`, "Parallel agents").

## 5. Delegate
- **Pre-create the worktree and named branch** before spawning (not `isolation: "worktree"`): `git fetch origin && git worktree add .claude/worktrees/<b> -b <b> origin/main`, then pass the absolute path.
- Launch independent workstreams in one message (parallel).
- **Model per task** (Agent `model` overrides the definition):

| Task | Agent / model |
|---|---|
| Implementation, design, protocol/security/injection work | `core-dev`/`mac-dev`/`android-dev`/`ci-dev` (opus) |
| Full review | `reviewer` (opus) |
| Small review (< ~50 lines, no protocol/security/pairing/crypto/injection) | `reviewer-quick` (sonnet) |
| Device prep, code/issue searches, log analysis | `general-purpose`/`Explore` with `model: "sonnet"` — or a script (`scripts/device-test-setup.sh`) |
| Pure log digging (grep a capture, count events) | `Explore` with `model: "haiku"` |

The agent definitions already carry the stable rules (commits, no attribution, no push, ws file, no `cd`, report format, out-of-scope bugs, comment-only fixes). The prompt carries only the task:

```
Workstream <ID> "<name>", issue #<n>, feature <feature>.
Worktree: <abs path> (branch <b>) | shared tree.
Context: ADR-xxxx (section …), architecture.md "<section>", skill <x>. progress.md: <abs path> (read-only).
Goal: <what and why>.  Contract: <from the issue, if cross-layer>.
You own ONLY: <file globs>.
Acceptance: <criteria>. Tests: <which>.
Device: <none | emulator AVD/app-id | S23/Mac injection turn granted — run scripts/device-preflight.sh first>.
ws file: <abs path>/ws-<id>.md.  Commit: <yes, in logical commits | no>.
```

## 6. Review
- Read the diff (`git -C <worktree> diff origin/main...HEAD`). Review before **every** PR: `reviewer-quick` for small diffs outside protocol/security/pairing/crypto/injection (it escalates if the gate fails), the full `reviewer` otherwise.
- Verify: `scripts/test.sh`, `dotnet build PenBridge.slnx`, device checks per skill `mac-build-run` / `android-build-run`. Ask the user to do device checks you can't do.
- **Interactive device tests:** `scripts/device-test-setup.sh` preps both devices (preflight, Release Mac app, Android install, reconnect check, latency capture, filtered captures) and prints the PIDs and stop command; give the user a numbered checklist; analyse the captures afterwards (a sonnet/haiku agent for big logs, `scripts/latency-report.sh`).
- **Fix rounds: a fresh agent per round**, briefed from the previous agent's `ws-<id>.md` (state, branch head, uncommitted files, findings to fix) — not a resumed agent with a huge context. Resume (SendMessage) only for small changes (< ~50 lines). Never fix in the main session, minor findings included.
- Reviewer findings not fixed in the same PR become issues (`type: bug` or `type: chore`, right milestone) — never only a note in progress.md.

## 7. Ship
- PR per workstream or per feature (skill `pr`, or `scripts/ship-pr.sh`) — only after the user approves pushing. The body says `Closes #<workstream issue>`.
- After merge: copy the worktree's `ws-*.md` into the main tree, then remove the worktree and branch (skill `parallel-worktrees`; `ship-pr.sh` does both).
- Set `status: in progress` on the issue while in review; update progress.md; tick the epic task list when merged. Close the epic when all its tasks are done.
- **Phase delivered → release it** (ADR-0021, skill `release`):
  1. Verify the phase on devices.
  2. Update `README.md` (status, features, usage, install) in a `docs:` PR. User rule. If the pipeline or packaging changed, ask the user to dry-run `build-release.yml` with the target version and test those artifacts.
  3. The user merges the README PR: with `--body "Release-As: X.Y.Z"` when the version needs forcing (1.0.0, or a fix-only phase), else a normal merge. Never merge yourself.
  4. Trigger CI on the release PR (close/reopen, skill `release`); the user merges it once green.
  5. Verify the published assets (skill `release`); the user does the install smoke tests.
  6. Close the epic, the release issue and the milestone.
  7. Disable debug captures (`scripts/latency-report.sh --disable`).
- **Bug fixes between phases** get a patch release (1.1.1 …): after the fix is verified, re-raise the release PR with the user. If `main` already has the next phase's `feat` commits, follow skill `release` ("Versioning").

## 8. Learn
- Run skill `capture-skill` on anything that surprised you or the subagents (flagged "learnings").
- Then hand the next phase to a fresh architect session (top of this skill).
