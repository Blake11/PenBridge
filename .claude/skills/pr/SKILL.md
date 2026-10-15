---
name: pr
description: Create or update a GitHub pull request for PenBridge following repo conventions — fresh branch from origin/main, dash-only branch name, Conventional Commits title and commits, concise description (what/why, testing incl. devices, notes, Closes #N), gh pr create, squash merge. Use when asked to commit, push, open/update a PR, or prepare changes for review. Never push without user approval.
---

# Pull requests (GitHub, `gh`)

**Pushing, `gh pr create`, and `gh pr merge` require explicit user approval in this conversation.** Prepare everything, show the title/description, then ask.

## Branch
- New work: `git fetch origin && git switch -c <branch> origin/main` (or a worktree, skill `parallel-worktrees`).
- Dash-only names: `feat-pairing-qr`, `fix-udp-replay`, `ci-cache-workloads`. No slashes.

## Commits
- Conventional Commits: `<type>(<optional scope>): <imperative subject>`; types feat, fix, perf, refactor, test, docs, build, ci, chore, style. Breaking: `feat!:` or `BREAKING CHANGE:` footer (bumps major via release-please).
- Scopes (optional; enforced by `.githooks/commit-msg` and `pr-title.yml`): `core`, `protocol`, `security`, `input`, `net`, `simclient`, `mac`, `android`, `ios`, `windows`, `ci`, `deps`, `docs`, `spec`, `release`.
- **No AI attribution**: no `Co-Authored-By: Claude`, no "Generated with" lines, no session trailers.
- `.githooks/commit-msg` validates; enable with `git config core.hooksPath .githooks` if not set.

## Before pushing
```bash
scripts/test.sh && dotnet build PenBridge.slnx
git log --oneline origin/main..HEAD
git diff origin/main...HEAD --stat
```
Review before every PR: `reviewer-quick` (sonnet) for diffs under ~50 lines that touch no protocol, security, pairing/crypto or input injection; the full `reviewer` (opus) otherwise or when the quick one escalates.

**Stale remote branch:** check `git ls-remote --heads origin <branch>` first. If one is left from an earlier attempt, the push is rejected while `gh pr create` still opens a PR against the old remote commit. Force-push is denied, so reconcile without rewriting:
```bash
git fetch origin <branch>
git merge -s ours origin/<branch> -m "chore: reconcile with stale remote branch"   # keeps our tree
git diff <reviewed-sha> HEAD          # must be empty: the pushed tree is the reviewed one
```

## Ship with `scripts/ship-pr.sh` (worktree workstreams)
```bash
scripts/ship-pr.sh <worktree> "<type>(scope): subject" <body-file>   # after the user approves pushing
```
Rebases onto origin/main, refuses AI-attribution lines, runs `scripts/test.sh`, pushes (refusing a stale remote branch, see above), `gh pr create`, waits for every check, squash-merges only with `--merge` (only when the user approved merging), copies `.claude/progress/**/ws-*.md` into the main tree and removes the worktree and its local branches. The manual steps below are the fallback.

## Create
```bash
git push -u origin <branch>           # after approval
gh pr create --base main --title "<type>(scope): subject" --body-file <file>
```
PR title = Conventional Commit (CI `pr-title` validates; it becomes the squash commit and release-please input).

Description (concise, no fluff, don't restate the title). It **must** reference its issue (ADR-0014): `Closes #N` on its own line when the PR finishes the issue, `Refs #N` when it doesn't. No issue yet → create one first (skill `issues`).
```markdown
## What / why
- …
## Testing
- scripts/test.sh: pass
- Devices: Mac (Krita pressure ✓) / S23 Ultra / emulator / SimClient / not device-tested (why)
## Notes
- Protocol version impact, migration, dependencies on other PRs / merge order, follow-ups (as issue links)

Closes #N
```
Editing an existing PR: `gh pr view <n> --json body --jq .body > /tmp/body.md`, append the line, `gh pr edit <n> --body-file /tmp/body.md`.

## CI path filters (`ci.yml`, issue #52)
A `Detect changes` job (`dorny/paths-filter@v3`) gates the jobs with job-level `if:`. Never use workflow-level `paths:`: a required check that never reports blocks the PR, while a job skipped by `if:` reports "skipped" (passing). Pushes to `main`, `workflow_dispatch`, and a failed `Detect changes` run everything. Full table: CONTRIBUTING.md "CI jobs".

| Changed paths | Jobs |
|---|---|
| `.github/workflows/**`, `.github/actions/**`, `scripts/**`, `.githooks/**` | all |
| `src/PenBridge.Core/**`, `tests/**`, `tools/**`, `build/**`, `**/Directory.Build.*`, `**/Directory.Packages.props`, `global.json`, `PenBridge.slnx`, `.editorconfig` | all |
| `src/PenBridge.Mac/**` | macOS build |
| `src/PenBridge.Android/**` | Android build |
| `src/PenBridge.iOS/**` | iOS build (placeholder) |
| docs, `.claude/**`, `LICENSE`, issue/PR templates | none (only Conventional Commit title) |

Keep required check names (`name:`) unchanged when editing jobs, and add new projects or build-affecting root files to the filters.

## After
- `gh pr checks <n>`, `gh run view <id> --log-failed` for failures. Before fixing a PR with auto-merge on, `gh pr merge <n> --disable-auto`, so it can't merge half-fixed. Every check must be green before merging, not just the required ones: a non-required iOS check once let a broken trimmed build merge (#27). All head builds (macOS, Android, iOS) are now required; check with `gh api repos/Blake11/PenBridge/branches/main/protection/required_status_checks --jq .contexts`.
- Merge only on user request: `gh pr merge <n> --squash --delete-branch`.
- Update progress.md with the PR link and status; tick the workstream in the epic's task list.
