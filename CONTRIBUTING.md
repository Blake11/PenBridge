# Contributing to PenBridge

Read `docs/architecture.md` and `docs/decisions.md` first. Toolchain setup and per-platform build/run steps are in `docs/development.md`.

## One-time setup

```bash
scripts/bootstrap.sh                  # workloads, tools, and git hooks
git config core.hooksPath .githooks   # if you skipped bootstrap
```

The `commit-msg` hook rejects commit subjects that are not Conventional Commits.

## Branches

- Start every branch from a fresh `origin/main`:

  ```bash
  git fetch origin && git switch -c <name> origin/main
  ```

- **Branch names use dashes only, no slashes**: `feat-udp-input`, `fix-pairing-timeout`. Slashes break artifact and tag names in CI.
- Keep a branch to one topic. Rebase on `origin/main` instead of merging `main` into it.

## Commits and PR titles: Conventional Commits

```
<type>[(<scope>)][!]: <subject>
```

| | Allowed values |
|---|---|
| type | `feat` `fix` `refactor` `perf` `docs` `test` `build` `ci` `chore` `style` `revert` |
| scope (optional) | `core` `protocol` `security` `input` `net` `simclient` `mac` `android` `ios` `windows` `ci` `deps` `docs` `spec` `release` |

- Write the subject in lowercase imperative mood, with no trailing period and 100 characters at most.
- Mark a breaking change with `!` (`feat(protocol)!: ...`) or a `BREAKING CHANGE:` footer.
- `Merge ...`, `Revert "..."`, `fixup!`, `squash!` and `amend!` commits are let through by the hook. They disappear on squash merge anyway.
- **No AI attribution**: no `Co-Authored-By: Claude` or similar trailers, and no "Generated with ..." lines, in commits or PR descriptions.

Version bumps come from these types. release-please treats `feat` as minor, `fix`/`perf` as patch, and `!` as major. Before 1.0, a breaking change bumps the minor version instead. See `docs/releasing.md`.

## Pull requests

```bash
git push -u origin <name>
gh pr create --fill          # then edit the title and body to match the template
```

- The PR title must be a Conventional Commit. The `PR title` workflow checks it, because it becomes the squash commit message and the changelog entry.
- Fill in the template: what changed and why, the testing you did including the **device(s) used** (Mac model/macOS, S23 Ultra over wireless adb, emulator, SimClient), and deploy notes such as a protocol bump, new permissions or re-pairing.
- PRs are **squash-merged**: `gh pr merge --squash --delete-branch`.

## CI jobs

| Job | Runner | Blocking |
|---|---|---|
| Core tests (`dotnet test --project tests/PenBridge.Core.Tests`) | ubuntu | yes |
| macOS build (Release) | macos | yes |
| Android build (Release APK, unsigned) | ubuntu | yes |
| iOS build (simulator, unsigned) | macos | **yes**: required, even while iOS is a placeholder, so head build/trim regressions can't merge. |
| PR title | ubuntu | yes |

Before pushing, run `scripts/test.sh` locally.

## Issues and progress tracking

GitHub issues are the plan of record (ADR-0014). Each roadmap phase is an epic issue with a matching milestone, each workstream has its own issue, and bugs are `type: bug` issues. Every PR references its issue with `Closes #N` (or `Refs #N`).

Each feature in progress also has local working notes in `.claude/progress/<feature>/`: `progress.md` (goal, decisions, workstreams with issue numbers, open questions, next steps) and one `ws-<id>.md` per workstream. These files are **untracked**, so don't commit them. Durable decisions go to `docs/decisions.md` as ADRs.
