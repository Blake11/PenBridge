---
name: parallel-worktrees
description: How to run parallel subagent workstreams in separate git worktrees for PenBridge — creating, naming, building in, merging back via PRs, and cleaning up worktrees, plus gotchas. Use when delegating two or more workstreams that commit code, or when a worktree build/merge/cleanup problem appears.
---

# Parallel worktrees

One branch + one worktree per workstream. Merge back via PRs (squash) — never by local merges into `main` unless the user asks.

## Create
Option A — Agent tool `isolation: "worktree"` (auto-created, auto-cleaned if unchanged).

Option B — explicit (predictable path, good for long workstreams):
```bash
git fetch origin
git worktree add .claude/worktrees/<branch> -b <branch> origin/main
```
- `<branch>`: dash-only, Conventional type prefix: `feat-udp-input-core`, `fix-pen-up-watchdog`.
- `.claude/worktrees/` must be gitignored (check `.gitignore`).
- Stacked work (heads need Core contracts not yet on main): branch the head workstream from the Core branch (`-b feat-x-mac feat-x-core`) and note the dependency in progress.md and the PR.

## Work in it
- Give the subagent the absolute worktree path; it must use absolute paths (cwd resets between bash calls).
- Each worktree has its own `bin/`/`obj/`: first build is a full restore/build. Don't share output dirs.
- `.claude/progress/` is untracked → only in the main tree. Pass its absolute path.
- Mac app runs from a worktree get a different bundle path; the Accessibility grant is per signed identity + path in practice — prefer running the Mac app from one place (the main tree) or re-grant.
- Two worktrees can't check out the same branch.
- An agent's `cd` into a worktree can change the architect's primary working directory: the architect uses `git -C <path>` and absolute paths.
- Long-running agents with huge contexts can stall (stream watchdog, "no progress for 600s"). Don't resume the stalled one again: hand off to a fresh agent in the same worktree with a compact, self-contained brief (state, branch heads, uncommitted files, remaining tasks). Tell agents to keep tool calls short and background long app/SimClient runs.

## Merge back
1. In the worktree: `scripts/test.sh`, `dotnet build PenBridge.slnx`.
2. Rebase on fresh main if needed: `git fetch origin && git rebase origin/main`.
3. PR via skill `pr` (user approval to push).
4. Stacked branches + squash merge + no force-push: don't open a stacked PR you'd have to force-push. After the base PR squash-merges, publish the dependent work as a **fresh branch from origin/main** carrying only its own commits (`git rebase --onto origin/main <old-base-tip> <branch>` on a never-pushed branch, or cherry-pick), and push it under a new name.

## Clean up
```bash
git worktree list
git worktree remove .claude/worktrees/<branch>   # refuses if dirty — inspect first, never --force blindly
git branch -d <branch>                            # -D only after confirming it was squash-merged
git worktree prune
```
