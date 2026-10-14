# PenBridge

Turns a phone (primary: Samsung Galaxy S23 Ultra + S Pen) into a pressure-sensitive drawing tablet for macOS over the LAN. C# / .NET 10: shared platform-free `PenBridge.Core`, a menu-bar Mac server, and an Android client.

## Doc map (read before changing things)

| Doc | What |
|---|---|
| `docs/architecture.md` | Components, protocol, platform details, roadmap (phases 0–5, each ends with a release) |
| `docs/decisions.md` | ADRs — append-only; supersede, never rewrite |
| `docs/development.md` | Toolchain setup, build/run/debug per platform |
| `docs/releasing.md` | release-please + build-release pipeline |
| `CONTRIBUTING.md` | Human-facing conventions |
| `.claude/skills/*/SKILL.md` | Project how-tos (living documents) |
| `.claude/progress/<feature>/` | Per-feature session state (untracked): `progress.md` (architect) + `ws-<id>.md` (one per workstream agent) |

## Layout

```
PenBridge.slnx
src/PenBridge.Core/        net10.0         protocol, serialization, crypto, pairing, mapping, gestures
src/PenBridge.Mac/         net10.0-macos   menu-bar server, CGEvent tablet injector (LSUIElement)
src/PenBridge.Android/     net10.0-android client: draw surface, S Pen, discovery, pairing
src/PenBridge.iOS/         net10.0-ios     placeholder only
tests/PenBridge.Core.Tests xUnit v3, no workloads needed
tools/PenBridge.SimClient  (phase 1) fake phone for testing the Mac server
scripts/                   bootstrap, doctor, test, mac-*, android-*
.github/workflows/         ci, pr-title, release-please, build-release
```

## Key commands

```bash
scripts/bootstrap.sh            # JDK, Android SDK, hooks; prints sudo workload/Xcode steps for the user (skill toolchain)
scripts/doctor.sh               # read-only toolchain check
scripts/test.sh                 # Core tests (fast, any OS) — preferred
dotnet test --project tests/PenBridge.Core.Tests   # raw form (Microsoft.Testing.Platform; `dotnet test <path>` is rejected)
dotnet build PenBridge.slnx
scripts/check-bundle.sh         # assert trimmed head outputs still contain the code pairing needs (e.g. BouncyCastle J-PAKE)
scripts/smoke-trimmed-pairing.sh [--aot]  # trimmed SimClient pairs + draws against an in-process server
scripts/mac-dev-cert.sh [--check|--delete]      # one-time "PenBridge Dev" cert (user runs: password prompt)
scripts/mac-run.sh [--release] [--no-build] [--reset-tcc] [--logs]
scripts/android-emulator.sh [--headless|--stop|--recreate]   # AVD PenBridge_Tablet
scripts/android-device.sh pair <ip:port> <code> | connect <ip:port>|--auto | list   # wireless adb to the S23
scripts/android-run.sh [--emulator|--device <ip:port>] [--apk] [--app-id <id>] [--logs|--logs-only]   # AVD_NAME=… per agent
```

## How we work: the main session is the architect

- **Do not implement in the main session.** Clarify every ambiguity with the user *before* delegating — this is a hard gate. Ask, don't assume.
- Record decisions as ADRs (`docs/decisions.md`, skill `adr`); update `docs/architecture.md` when the design changes.
- **Issues drive the work** (ADR-0014, skill `issues`): each phase is an epic issue + milestone; each workstream gets an issue *before* delegation, and its number goes in the subagent prompt. Bugs found by agents/reviewers and not fixed in the same PR become `type: bug` issues.
- Keep `.claude/progress/<feature>/` for every feature/part being developed (skill `progress-tracking`). `progress.md` is the **architect's only**: goal, decisions, workstream table with issue numbers, open questions, next steps. Each workstream agent writes only its own `ws-<id>.md` (status, notes, follow-ups, learnings) and never edits `progress.md`.
- Split work into workstreams with **disjoint file ownership**, then delegate to subagents (`.claude/agents/`: core-dev, mac-dev, android-dev, ci-dev, reviewer) to save context. Review the results; run `reviewer` before a PR.
- Multi-agent sessions: one branch + worktree per workstream (Agent `isolation: "worktree"` or `git worktree add .claude/worktrees/<branch> -b <branch> origin/main`), merged back via PRs (skill `parallel-worktrees`).
- End-to-end flow: skill `feature-workflow`.
- Anything learned with long-term value becomes or updates a project skill (skill `capture-skill`). When a skill is wrong, fix it.

## Coding rules

- **Core is platform-free and testable**: no platform APIs; anything testable without a device lives in Core with tests. Platform heads are thin glue.
- **Hot input path** (sampling → serialize → encrypt → UDP → decrypt → inject): allocation-free, `Span<T>`/`stackalloc`/pooled buffers, `BinaryPrimitives`. No LINQ, boxing, closures or logging-string formatting per sample.
- **Trim/AOT-safe**: no reflection-based serialization (no `System.Text.Json` reflection mode, no `BinaryFormatter`); source generators or hand-written code only. Keep trim warnings at zero.
- **Protocol changes** need version handling (major/minor rules) and tests on both sides — skill `protocol-change`.
- **Security**: only paired devices may inject input. Never weaken pairing, cert pinning, or encryption for convenience; no plaintext fallback. Treat every network byte as untrusted (bounds-check lengths, reject unknown types).
- Idle Mac server ≈ 0% CPU: event-driven, no polling.
- No comments that pin behaviour to a package version; describe the invariant.

## Conventions

- GitHub, default branch `main`, PRs via `gh`, squash merge (skill `pr`).
- Branch from fresh `origin/main` (`git fetch origin && git switch -c <name> origin/main`); **dash-only branch names** (`feat-udp-input`, no slashes).
- **Every phase ends with a release** (ADR-0021): Phase 3 = 1.0.0, each later phase bumps minor, bug fixes between phases bump patch; a major needs the user (skill `release`).
- Conventional Commits for commits and PR titles (`feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `ci:`, `build:`, `chore:`, `perf:`); validated by `.githooks/commit-msg` and CI. release-please derives versions from them (skill `release`).
- Every PR references its issue in the body: `Closes #N` (or `Refs #N` if it doesn't finish it).
- When a phase is delivered (its epic closes), update `README.md` (status, features, usage).
- **No Claude/AI attribution** in commits or PRs (no `Co-Authored-By: Claude`, no "Generated with").
- **Never push, create PRs, or create remotes/repos without explicit user approval.** Subagents never commit unless told.
- The user's global GitLab/`master`/`glab`/MR/prod rules do **not** apply here — only dash branches, Conventional Commits and no attribution carry over. There is no "Production" environment.

## Testing devices

- **Core**: `scripts/test.sh` — always run before handing back Core changes.
- **Setup**: sudo workload installs, Xcode 27 setup, `mac-dev-cert.sh` and adb pairing are user steps (skill `toolchain`).
- **Mac**: needs the Accessibility permission (TCC). Sign with the dev cert so rebuilds keep the grant; `--reset-tcc` if it gets stuck. Verify pressure/tilt in a pressure-aware app (Krita). Skill `mac-build-run`.
- **Android**: emulator (`PenBridge_Tablet`) for UI/flow — no stylus, no mDNS (Mac = `10.0.2.2`, manual host entry is dev-only). **Real S23 Ultra over wireless adb** for stylus and discovery. Skill `android-build-run`.
- Without a phone: `tools/PenBridge.SimClient` drives the Mac server.
- Only one agent uses a physical device at a time (the architect grants turns); parallel emulator agents use their own AVD and ApplicationId.
