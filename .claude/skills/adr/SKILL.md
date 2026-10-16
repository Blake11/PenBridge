---
name: adr
description: How to add, supersede, or amend an Architecture Decision Record in docs/decisions.md for PenBridge, and keep its index current. Use whenever a design decision with lasting impact is made or changed (technology, protocol, security model, platform approach, distribution, conventions), or when the user decides between alternatives.
---

# ADRs (`docs/decisions.md`)

- An ADR is **editable in place until the release that contains it ships** (amend, below). After that it is append-only: supersede, never rewrite.
- Shipped? Find the commit that added it, `git log -S 'ADR-00NN —' --format=%h -- docs/decisions.md | tail -1`, then `git tag --contains <sha>`: any `v*` tag = released.
- The file starts with a one-line-per-ADR **index** (number, title, status). Update it in the same commit as every add, amend or supersede. Prompts and reviews cite ADR numbers and sections from it rather than "read decisions.md".

## Add
1. Next number: `grep -o 'ADR-[0-9]\{4\}' docs/decisions.md | sort | tail -1` → +1.
2. Append at the bottom:
```markdown
## ADR-00NN — <short decision title>
**Status:** accepted (<YYYY-MM-DD>)
- **Context:** <forces, constraints; 1–3 lines>
- **Decision:** <what we do>
- **Why not <alternative>:** <reason>
- **Consequences / deferred:** <follow-ons, what's revisited when>
```
3. Add its index row: `| 00NN | <title> | accepted |`.
4. If the design changes, update `docs/architecture.md` in the same change and reference the ADR there.
5. Record the ADR id in progress.md "Decisions".

## Amend (unreleased ADRs only)
- Edit the body in place and add a line under the status: `**Amended YYYY-MM-DD (#n):** <what changed>` (`#n` = the issue or PR driving it). One line per amendment.
- Update the index row if the title or status changed.
- Released ADR → supersede instead.

## Supersede
- New ADR with `**Status:** accepted (<date>), supersedes ADR-00XX` and why the old one no longer holds.
- In the old ADR, change only the status line: `**Status:** superseded by ADR-00NN (<date>)`. Leave its body intact. Update both index rows.

## Rules
- Only the user accepts decisions; the architect drafts, confirms with the user, then writes `accepted`. Use `proposed` if pending.
- Keep each ADR short — rationale, not documentation.
- Commit as `docs: add ADR-00NN <title>` (or bundle with the feature PR).
