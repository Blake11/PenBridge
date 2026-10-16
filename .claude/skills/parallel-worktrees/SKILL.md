---
name: parallel-worktrees
description: How to run parallel subagent workstreams in separate git worktrees for PenBridge — pre-creating named worktrees, building in them, fix rounds with fresh agents, merging back via PRs (scripts/ship-pr.sh), and cleaning up worktrees, plus gotchas. Use when delegating two or more workstreams that commit code, or when a worktree build/merge/cleanup problem appears.
---

# Parallel worktrees

One branch + one worktree per workstream. Merge back via PRs (squash) — never by local merges into `main` unless the user asks.

## Create (architect, before spawning)
```bash
git fetch origin
git worktree add .claude/worktrees/<branch> -b <branch> origin/main
```
Pass the absolute path in the prompt. Don't use the Agent tool's `isolation: "worktree"`: its auto-named branch and path are unpredictable and it gets cleaned up behind you.
- `<branch>`: dash-only, Conventional type prefix: `feat-udp-input-core`, `fix-pen-up-watchdog`.
- `.claude/worktrees/` must be gitignored (check `.gitignore`).
- Stacked work: avoid it with contract-first issues (skill `feature-workflow` step 3). If a head truly needs unmerged Core code, branch from the Core branch (`-b feat-x-mac feat-x-core`) and note the dependency in progress.md and the PR.

## Work in it
- The subagent uses absolute paths and `git -C <path>` (cwd resets between bash calls). A `cd` into a worktree in the shared shell moves the session's working directory: a PreToolUse hook warns about it (`.claude/hooks/warn-cd-worktree.sh`).
- Each worktree has its own `bin/`/`obj/`: first build is a full restore/build. Don't share output dirs.
- Serena: each agent type has its own inline server name (`serena-core|mac|android|ci|review|review-quick`), and each agent calls `activate_project` with its worktree path. `activate_project` is global to a server, so a server shared by two running agents (same type in parallel) can switch under them (#195: an edit landed in the wrong tree). Rules: `get_current_config` check before Serena calls; edits with Edit/Write on absolute paths; Serena read-only when two agents of one type run in parallel. Whether distinct names give separate processes is verified after a Claude Code restart: count `~/.serena/logs/<date>/mcp_*.txt` files per run and the `activate_project` lines in each (one process per agent = isolated). The worktree's `.serena/cache` and `memories` are gitignored and go away with it.
- Agents' Bash: no `rm -rf` in chained one-liners (`.claude/settings.json` asks on `rm -rf *`, and a buried one gives an unreadable prompt); use `mktemp -d` scratch dirs, or a standalone `rm -rf <literal path>`.
- `.claude/progress/` is untracked: the agent writes its `ws-<id>.md` at the path given in the prompt (usually `<worktree>/.claude/progress/<feature>/`); the architect copies it to the main tree.
- Mac app runs from a worktree get a different bundle path; the Accessibility grant is per signed identity + path in practice — prefer running the Mac app from one place (the main tree) or re-grant.
- Two worktrees can't check out the same branch.

## Fix rounds: fresh agent per round
- Each review/fix round gets a **fresh agent** in the same worktree, briefed from the previous `ws-<id>.md`: branch head, uncommitted files, what's done, the findings to fix. Huge resumed contexts cost tokens and stall (stream watchdog, "no progress for 600s").
- Resume the previous agent (SendMessage) only for small changes (< ~50 lines).
- Tell agents to keep tool calls short and background long app/SimClient runs.

## Merge back
`scripts/ship-pr.sh <worktree> --title … --body-file … [--merge] [--no-test] [--dry-run]` (after the user approves pushing) does it end to end: rebase onto origin/main, refuse AI-attribution lines, `scripts/test.sh`, push (refusing a stale remote branch), `gh pr create`, wait for every check, squash-merge (with `--merge`, only when the user approved merging), copy `.claude/progress/**/ws-*.md` into the main tree, remove the worktree and its local branches. `--help` for flags.

By hand:
1. In the worktree: `scripts/test.sh`, `dotnet build PenBridge.slnx`.
2. Rebase on fresh main if needed: `git -C <wt> fetch origin && git -C <wt> rebase origin/main`.
3. PR via skill `pr` (user approval to push).
4. Stacked branches + squash merge + no force-push: don't open a stacked PR you'd have to force-push. After the base PR squash-merges, publish the dependent work as a **fresh branch from origin/main** carrying only its own commits (`git rebase --onto origin/main <old-base-tip> <branch>` on a never-pushed branch, or cherry-pick), and push it under a new name.

## Clean up (after merge)
```bash
cp -R <wt>/.claude/progress/<feature>/ws-*.md .claude/progress/<feature>/   # first: the worktree copy is the only one
git worktree remove .claude/worktrees/<branch>   # refuses if dirty — inspect first, never --force blindly
git branch -D <branch>                            # -D: squash merges aren't ancestors; confirm the PR merged first
git worktree prune
```
