#!/usr/bin/env bash
# Ship a worktree's branch as a pull request: rebase, check, test, push, open (or reuse) the PR, wait for every
# check, and with --merge squash-merge it and clean up the worktree. Needs the user's approval to push / merge.
#
# Usage: scripts/ship-pr.sh <worktree> --title "<conventional title>" --body-file <file> [--merge] [--no-test]
#                           [--timeout <min>] [--dry-run]
#   <worktree>          the branch's worktree (e.g. .claude/worktrees/feat-x); its branch is shipped
#   --title <title>     PR title = the squash commit: Conventional Commit, same rule as .githooks/commit-msg and
#                       the pr-title check (allowed types/scopes, lowercase subject)
#   --body-file <file>  PR description; must reference its issue (Closes #N / Refs #N) and carry no AI attribution
#   --merge             after every check passes: gh pr merge --squash --delete-branch, copy the worktree's
#                       .claude/progress/**/ws-*.md into the main checkout, remove the worktree and its local
#                       branches (incl. worktree-agent-* ones it contains), git pull --ff-only in the main checkout
#   --no-test           skip scripts/test.sh
#   --timeout <min>     give up waiting for the checks after this many minutes (default 45)
#   --dry-run           run the local checks (and the tests), then print what would happen; no rebase, push or gh
#                       write
#
# Steps: refuse a dirty worktree, a detached HEAD or main; git fetch origin; refuse if origin/<branch> exists and is
# not an ancestor of HEAD (stale remote branch — prints the reconcile steps; never force-pushes); rebase onto
# origin/main when the branch was never pushed (a pushed branch is not rebased: that would need a force-push);
# refuse commit messages with Co-Authored-By / "Generated with"; scripts/test.sh; git push -u origin <branch>;
# gh pr create (or reuse the branch's open PR); wait for the checks (polled every 15 s; done after two quiet polls).
# Exit status: 0 shipped (or dry run passed), 1 on any refusal or failure.
set -euo pipefail
source "$(dirname "$0")/lib/common.sh"

WT=""; TITLE=""; BODY=""; MERGE=0; TEST=1; DRY=0; TIMEOUT_MIN=45
while [[ $# -gt 0 ]]; do
  case "$1" in
    --title) TITLE="${2:-}"; [[ -n "$TITLE" ]] || die "--title needs a value"; shift ;;
    --body-file) BODY="${2:-}"; [[ -n "$BODY" ]] || die "--body-file needs a path"; shift ;;
    --merge) MERGE=1 ;;
    --no-test) TEST=0 ;;
    --timeout) TIMEOUT_MIN="${2:-}"; [[ "$TIMEOUT_MIN" =~ ^[1-9][0-9]*$ ]] || die "--timeout needs whole minutes"; shift ;;
    --dry-run) DRY=1 ;;
    -h|--help) usage_from_header "$0"; exit 0 ;;
    -*) die "unknown argument: $1 (see --help)" ;;
    *) [[ -z "$WT" ]] || die "one worktree only (got '$WT' and '$1')"; WT="$1" ;;
  esac
  shift
done
[[ -n "$WT" ]] || die "pass the worktree path (see --help)"
[[ -n "$TITLE" ]] || die "pass --title \"<type>(<scope>): <subject>\""
[[ -n "$BODY" ]] || die "pass --body-file <file>"
[[ -f "$BODY" ]] || die "no such body file: $BODY"
# Absolute: gh runs from inside the worktree.
BODY="$(cd "$(dirname "$BODY")" && pwd)/$(basename "$BODY")"

[[ -d "$WT" ]] || die "no such directory: $WT"
WT="$(git -C "$WT" rev-parse --show-toplevel 2>/dev/null)" || die "$WT is not inside a git checkout"
BRANCH="$(git -C "$WT" symbolic-ref --short -q HEAD)" || die "$WT has a detached HEAD — switch to its branch"
[[ "$BRANCH" != main ]] || die "$WT is on main — ship a feature branch"
MAIN="$(git -C "$WT" worktree list --porcelain | sed -n '1s/^worktree //p')"
[[ -n "$MAIN" ]] || die "could not find the main checkout of $WT"
if [[ $MERGE -eq 1 && "$WT" == "$MAIN" ]]; then die "--merge removes the worktree: pass a linked worktree, not the main checkout"; fi
RUN() { if [[ $DRY -eq 1 ]]; then info "would run: $*"; else "$@"; fi; }
ghw() { (cd "$WT" && gh "$@"); }

# ---- title + body ---------------------------------------------------------------------
step "Title and description"
[[ "$TITLE" != *$'\n'* ]] || die "the title must be one line"
tmp="$(mktemp "${TMPDIR:-/tmp}/ship-pr.XXXXXX")"
trap 'rm -f "$tmp"' EXIT
printf '%s\n' "$TITLE" >"$tmp"
# The commit-msg hook is the single source of the type/scope rule (pr-title.yml mirrors it).
if ! hook_out="$(sh "$REPO_ROOT/.githooks/commit-msg" "$tmp" 2>&1)"; then
  printf '%s\n' "$hook_out" >&2; die "title is not a Conventional Commit"
fi
# The hook lets Merge/Revert/fixup! subjects through and only warns on uppercase; the pr-title check rejects both.
[[ "$TITLE" =~ ^[a-z]+(\([a-z]+\))?!?:\ [^A-Z] ]] \
  || die "title must be <type>[(<scope>)][!]: <subject> with a lowercase subject: $TITLE"
ok "title: $TITLE"
[[ -s "$BODY" ]] || die "body file is missing or empty: $BODY"
if grep -Eiq 'co-authored-by|generated with' "$BODY"; then die "body file has an AI attribution line (Co-Authored-By / Generated with): remove it"; fi
grep -Eq '^(Closes|Fixes|Resolves|Refs) #[0-9]+' "$BODY" \
  || die "body must reference its issue on its own line: Closes #N (or Refs #N) — ADR-0014"
ok "body references $(grep -Eo '^(Closes|Fixes|Resolves|Refs) #[0-9]+' "$BODY" | tr '\n' ' ')"

# ---- worktree state --------------------------------------------------------------------
step "Branch $BRANCH in ${WT/#$HOME/~}"
[[ -z "$(git -C "$WT" status --porcelain --untracked-files=no)" ]] || die "uncommitted changes in $WT — commit or stash them"
untracked="$(git -C "$WT" status --porcelain | grep -c '^??' || true)"
[[ "$untracked" -eq 0 ]] || warn "$untracked untracked file(s) in the worktree are not shipped (git -C $WT status)"
git -C "$WT" fetch --quiet origin || die "git fetch origin failed"

# Checked before any rebase, so a refusal leaves the branch untouched (a rebase keeps the messages).
n="$(git -C "$WT" rev-list --count origin/main..HEAD)"
[[ "$n" -gt 0 ]] || die "no commits on $BRANCH beyond origin/main"
# grep reads a captured copy: under pipefail, `git log | grep -q` can fail with SIGPIPE on a match.
if grep -Eiq 'co-authored-by|generated with' <<<"$(git -C "$WT" log --format=%B origin/main..HEAD)"; then
  git -C "$WT" log --format='%h %s' origin/main..HEAD >&2
  die "a commit message on $BRANCH has an AI attribution line (Co-Authored-By / Generated with); reword it before pushing"
fi
ok "$n commit(s), no AI attribution"
git -C "$WT" log --format='      %h %s' origin/main..HEAD

remote_sha="$(git -C "$WT" ls-remote --heads origin "$BRANCH" | awk '{print $1}')"
if [[ -n "$remote_sha" ]]; then
  git -C "$WT" fetch --quiet origin "+refs/heads/$BRANCH:refs/remotes/origin/$BRANCH"
  if ! git -C "$WT" merge-base --is-ancestor "$remote_sha" HEAD; then
    fail "origin/$BRANCH (${remote_sha:0:9}) is not an ancestor of HEAD: a stale remote branch (force-push is never used)"
    cat >&2 <<HINT
      Reconcile without rewriting (skill pr), then re-run:
        git -C $WT fetch origin $BRANCH
        git -C $WT merge -s ours origin/$BRANCH -m "chore: reconcile with stale remote branch"
        git -C $WT diff <reviewed-sha> HEAD     # must be empty: the pushed tree is the reviewed one
HINT
    exit 1
  fi
  ok "origin/$BRANCH is an ancestor of HEAD"
  if ! git -C "$WT" merge-base --is-ancestor origin/main HEAD; then
    warn "already pushed, so not rebased (that would need a force-push); it is behind origin/main — the squash merge"
    hint "takes care of it unless GitHub reports a conflict (then: git -C $WT merge origin/main)"
  fi
else
  behind="$(git -C "$WT" rev-list --count HEAD..origin/main)"
  if [[ "$behind" -eq 0 ]]; then
    ok "up to date with origin/main"
  elif [[ $DRY -eq 1 ]]; then
    if git -C "$WT" merge-tree --write-tree origin/main HEAD >/dev/null 2>&1; then
      info "would rebase onto origin/main ($behind commit(s) behind; no conflicts predicted)"
    else
      die "rebasing onto origin/main would conflict ($behind commit(s) behind) — resolve by hand: git -C $WT rebase origin/main"
    fi
  else
    if git -C "$WT" rebase --quiet origin/main >/dev/null 2>&1; then
      ok "rebased onto origin/main ($behind commit(s) behind)"
    else
      git -C "$WT" rebase --abort 2>/dev/null || true
      die "rebase onto origin/main conflicts — aborted; resolve by hand: git -C $WT rebase origin/main"
    fi
  fi
fi


# ---- tests -----------------------------------------------------------------------------
if [[ $TEST -eq 1 ]]; then
  step "scripts/test.sh"
  log="$(mktemp "${TMPDIR:-/tmp}/ship-pr-test.XXXXXX")"
  if "$WT/scripts/test.sh" >"$log" 2>&1; then
    ok "$(grep -Eo 'Test run summary: .*|total: [0-9]+.*|succeeded: [0-9]+' "$log" | head -n 1 || echo passed)"
    rm -f "$log"
  else
    tail -n 30 "$log" >&2; die "tests failed (full log: $log)"
  fi
else
  warn "tests skipped (--no-test)"
fi

# ---- dry run ends here -------------------------------------------------------------------
if [[ $DRY -eq 1 ]]; then
  step "Dry run — would now"
  info "git -C $WT push -u origin refs/heads/$BRANCH:refs/heads/$BRANCH"
  info "gh pr create --base main --head $BRANCH --title \"$TITLE\" --body-file $BODY   (or reuse the open PR for $BRANCH)"
  info "wait up to $TIMEOUT_MIN min for every check (polled every 15 s); fail on any failed/cancelled check"
  if [[ $MERGE -eq 1 ]]; then
    info "copy $WT/.claude/progress/**/ws-*.md → $MAIN/.claude/progress/ (newer files only)"
    info "git worktree remove $WT   (refused if it has uncommitted or untracked work, or HEAD is not pushed)"
    info "gh pr merge <n> --squash --delete-branch   (from $MAIN)"
    info "delete local branch $BRANCH and worktree-agent-* branches it contains; git -C $MAIN pull --ff-only"
  fi
  ok "dry run passed"
  exit 0
fi

# ---- push + PR -------------------------------------------------------------------------
step "Push"
# Explicit refspec without "+": a non-fast-forward push is rejected, never forced.
git -C "$WT" push --quiet -u origin "refs/heads/$BRANCH:refs/heads/$BRANCH" || die "push rejected (never force-pushed) — see the stale-branch steps in skill pr"
ok "pushed $(git -C "$WT" rev-parse --short HEAD) to origin/$BRANCH"

step "Pull request"
pr_url="$(ghw pr list --head "$BRANCH" --state open --json url --jq '.[0].url // ""')" \
  || die "gh pr list failed (gh auth status; does origin point at GitHub?)"
if [[ -n "$pr_url" ]]; then
  ok "reusing $pr_url"
  cur_title="$(ghw pr view "$pr_url" --json title --jq .title)"
  [[ "$cur_title" == "$TITLE" ]] || warn "its title is \"$cur_title\" (not changed; gh pr edit $pr_url --title …)"
else
  pr_url="$(ghw pr create --base main --head "$BRANCH" --title "$TITLE" --body-file "$BODY" | tail -n 1)" \
    || die "gh pr create failed"
  ok "created $pr_url"
fi
pr="${pr_url##*/}"
[[ "$pr" =~ ^[0-9]+$ ]] || die "could not read the PR number from '$pr_url'"

# ---- checks ----------------------------------------------------------------------------
step "Checks (every 15 s, up to $TIMEOUT_MIN min)"
deadline=$(( $(date +%s) + TIMEOUT_MIN * 60 ))
for i in $(seq 1 20); do   # checks register a few seconds after the push; give them up to 5 minutes
  [[ -n "$(ghw pr checks "$pr" --json name --jq '.[].name' 2>/dev/null || true)" ]] && break
  [[ $i -lt 20 && $(date +%s) -lt $deadline ]] || die "no checks reported for $pr_url after $(( i * 15 / 60 )) min"
  sleep 15
done
# Done after two consecutive polls with nothing pending: a job created late (after "Detect changes") shows up
# between them.
quiet=0
while :; do
  # gh pr checks exits non-zero while checks are pending or failed, even with --json: judge by the output.
  pending="$(ghw pr checks "$pr" --json bucket --jq '[.[] | select(.bucket == "pending")] | length' 2>/dev/null || true)"
  [[ "$pending" =~ ^[0-9]+$ ]] || die "gh pr checks failed for $pr_url"
  if [[ "$pending" -eq 0 ]]; then quiet=$((quiet + 1)); else quiet=0; fi
  [[ $quiet -ge 2 ]] && break
  [[ $(date +%s) -lt $deadline ]] \
    || die "$pending check(s) still pending on $pr_url after $TIMEOUT_MIN min (gh pr checks $pr; re-run with --timeout <min>)"
  sleep 15
done
bad="$(ghw pr checks "$pr" --json name,bucket,link --jq '"ok", (.[] | select(.bucket == "fail" or .bucket == "cancel") | "\(.name)  \(.link)")' 2>/dev/null || true)"
[[ "$(head -n 1 <<<"$bad")" == ok ]] || die "gh pr checks failed for $pr_url"
bad="$(tail -n +2 <<<"$bad")"
if [[ -n "$bad" ]]; then
  fail "failed checks on $pr_url:"; printf "%s\n" "$bad" | sed "s/^/      /"
  hint "gh run view <run-id> --log-failed"
  exit 1
fi
ok "all $(ghw pr checks "$pr" --json name --jq 'length') checks passed or skipped"

if [[ $MERGE -eq 0 ]]; then
  step "Ready: $pr_url (not merged; re-run with --merge once the user approves)"
  exit 0
fi

# ---- merge + clean up --------------------------------------------------------------------
step "Merge"
# GitHub computes mergeability lazily: UNKNOWN for a few seconds after a push or a check finishing.
for _ in 1 2 3 4 5 6; do
  state="$(ghw pr view "$pr" --json mergeable,mergeStateStatus --jq '"\(.mergeable) \(.mergeStateStatus)"')" \
    || die "gh pr view failed for $pr_url"
  [[ "$state" == *UNKNOWN* ]] || break
  sleep 15
done
case "$state" in
  "MERGEABLE CLEAN"|"MERGEABLE HAS_HOOKS") ok "mergeable ($state)" ;;
  *) die "PR is not mergeable yet ($state; UNSTABLE = a check is pending or failing) — see $pr_url" ;;
esac
[[ "$(git -C "$WT" rev-parse HEAD)" == "$(git -C "$WT" rev-parse "origin/$BRANCH")" ]] || die "HEAD differs from origin/$BRANCH — push first"
tip="$(git -C "$WT" rev-parse HEAD)"
base="$(git -C "$WT" merge-base HEAD origin/main)"

# Progress notes are untracked: keep the worktree's copies that are newer than (or missing from) the main checkout's.
if [[ -d "$WT/.claude/progress" ]]; then
  while IFS= read -r f; do
    rel="${f#"$WT"/}"; dst="$MAIN/$rel"
    if [[ ! -e "$dst" || "$f" -nt "$dst" ]]; then
      mkdir -p "$(dirname "$dst")"; cp -p "$f" "$dst"; ok "copied $rel"
    fi
  done < <(find "$WT/.claude/progress" -type f -name 'ws-*.md')
fi

# Remove the worktree before merging: gh can't delete a local branch that a worktree has checked out. Re-check
# right before: nothing uncommitted or untracked (ignored files such as .claude/progress don't count, and don't
# block `git worktree remove`), and HEAD is exactly what the remote has. Never --force.
dirty="$(git -C "$WT" status --porcelain)"
if [[ -n "$dirty" ]]; then
  printf '%s\n' "$dirty" | sed 's/^/      /' >&2
  die "the worktree has uncommitted or untracked work (above) — left in place, not merged; commit or remove it, then re-run"
fi
remote_now="$(git -C "$WT" ls-remote --heads origin "$BRANCH" | awk '{print $1}')"
[[ "$remote_now" == "$tip" ]] \
  || die "origin/$BRANCH (${remote_now:-missing}) is not HEAD ($tip) — worktree left in place, not merged; push first"
git -C "$MAIN" worktree remove "$WT" || die "git worktree remove refused (see above) — worktree left in place, not merged"
ok "removed worktree ${WT/#$HOME/~}"
if ! (cd "$MAIN" && gh pr merge "$pr" --squash --delete-branch); then
  fail "gh pr merge failed; the branch is kept"
  hint "restore the worktree: git -C $MAIN worktree add $WT $BRANCH"
  exit 1
fi
ok "squash-merged $pr_url"

step "Local clean-up"
git -C "$MAIN" worktree prune
checked_out="$(git -C "$MAIN" worktree list --porcelain | sed -n 's/^branch refs\/heads\///p')"
for b in "$BRANCH" $(git -C "$MAIN" for-each-ref --format='%(refname:short)' 'refs/heads/worktree-agent-*'); do
  git -C "$MAIN" show-ref --verify --quiet "refs/heads/$b" || continue
  grep -qxF "$b" <<<"$checked_out" && { warn "$b is checked out in another worktree — kept"; continue; }
  # A worktree-agent-* branch is this one's when the shipped tip contains it and it has commits beyond main.
  if [[ "$b" == "$BRANCH" ]] || { git -C "$MAIN" merge-base --is-ancestor "$b" "$tip" \
       && ! git -C "$MAIN" merge-base --is-ancestor "$b" "$base"; }; then
    git -C "$MAIN" branch -D --quiet "$b" && ok "deleted local branch $b"
  fi
done
if [[ "$(git -C "$MAIN" symbolic-ref --short -q HEAD || true)" == main ]]; then
  git -C "$MAIN" pull --ff-only --quiet && ok "main checkout at $(git -C "$MAIN" log -1 --format='%h %s')"
else
  warn "main checkout is not on main — not pulled (git -C $MAIN fetch origin main:main)"
fi
step "Done: $pr_url ($(cd "$MAIN" && gh pr view "$pr" --json state --jq .state))"
# throwaway
