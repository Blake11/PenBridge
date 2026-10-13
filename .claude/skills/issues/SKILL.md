---
name: issues
description: How PenBridge tracks work as GitHub issues (ADR-0014) — creating, labelling and milestoning epics, workstream issues, bugs and later ideas with gh; the label taxonomy; the epic task-list convention; and the bug-report flow for agents and reviewers. Use when planning a phase or workstream, before delegating, when a bug or review finding turns up, when opening a PR that needs Closes #N, or when asked about issues, labels, milestones or epics.
---

# Issues (GitHub, `gh`, repo `Blake11/PenBridge`)

Issues are the plan of record: phases → epics, workstreams → issues before delegation, bugs → `type: bug`. PRs reference them (`Closes #N` / `Refs #N`, skill `pr`). Creating/editing issues and labels is fine without asking; pushing and PRs still need approval.

## Labels
| Group | Labels | Rule |
|---|---|---|
| Type (exactly one) | `type: feature`, `type: bug`, `type: chore`, `type: docs`, `type: epic` | |
| Area (one or more) | `area: core`, `area: protocol`, `area: security`, `area: input`, `area: net`, `area: mac`, `area: android`, `area: ios`, `area: ci`, `area: docs` | |
| Status (optional) | `status: in progress`, `status: blocked` | in progress also covers "in review"; blocked → say on what in the body |
| Other | `later` | post-MVP; milestone "Later" |

Defaults `bug`, `enhancement`, `documentation` stay for issues filed from the public templates; triage adds the `type:`/`area:` labels.

## Milestones
"Phase 1 — Core" … "Phase 6 — Release", "Later" — one per roadmap phase (`docs/architecture.md#roadmap`). `gh api repos/Blake11/PenBridge/milestones --jq '.[].title'`.

## Commands
```bash
R=Blake11/PenBridge
# check first (idempotent)
gh issue list -R $R --state all --search "in:title \"P1-D\"" --json number,title
# create
gh issue create -R $R --title "P2-A: Menu bar + run-at-startup" --body-file /tmp/body.md \
  --label "type: feature,area: mac" --milestone "Phase 2 — Mac server"
# status / links
gh issue edit 9 -R $R --add-label "status: in progress" --remove-label "status: blocked"
gh issue comment 9 -R $R --body "PR #12"
gh issue view 10 -R $R            # epic + task list
gh issue list -R $R --milestone "Phase 1 — Core"
```
Write bodies to a temp file (`--body-file`) — inline backticks get mangled by the shell.

## Epic convention
- Title = milestone name (`Phase 2 — Mac server`), labels `type: epic` + main areas.
- Body: Goal (link roadmap/spec/ADRs), `## Workstreams` task list of issue refs, `## Bugs / chores found`, Depends on.
  ```markdown
  ## Workstreams
  - [ ] #9 P1-D Net engine + SimClient (after #6, #7)
  ```
  GitHub renders progress from the task list; tick an item when its PR merges. Close the epic when all are done.
- Scope items not yet split into workstreams stay as plain checklist lines; replace them with issue refs when you create the workstream issues.

## Workstream issue
Template `.github/ISSUE_TEMPLATE/workstream.yml`, or the same sections in a body file: Goal, Owned files (globs), Acceptance (task list), Tests, Dependencies, `Part of #<epic>`. Title `<WS-ID>: <name>`. Put the number in the subagent prompt and the progress.md table.

## Bug flow (agents and reviewers)
1. Found something broken outside your scope, or a review finding that won't be fixed in this PR? Don't fix it silently and don't leave it only in a report.
2. Check for a duplicate: `gh issue list -R $R --state all --search "<keywords>"`.
3. File it: title `bug(<area>): <what's wrong>`, labels `type: bug` + `area: *`, milestone of the phase it blocks (none if unsure). Body: what's wrong (file:line), how to reproduce/evidence, expected, suggested fix, `Found in #<issue/PR>`.
4. Add it to the epic's "Bugs / chores found" list and mention the number in your report.
5. The fix PR says `Closes #<bug>`.

Subagents without permission to run `gh` list the bug in their report with the same fields; the architect files it.
