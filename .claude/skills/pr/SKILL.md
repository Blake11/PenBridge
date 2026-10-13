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
Run the `reviewer` agent on non-trivial diffs.

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

## After
- `gh pr checks <n>`, `gh run view <id> --log-failed` for failures. Every check must be green before merging, not just the required ones: a non-required iOS check once let a broken trimmed build merge (#27). All head builds (macOS, Android, iOS) are now required; check with `gh api repos/Blake11/PenBridge/branches/main/protection/required_status_checks --jq .contexts`.
- Merge only on user request: `gh pr merge <n> --squash --delete-branch`.
- Update progress.md with the PR link and status; tick the workstream in the epic's task list.
