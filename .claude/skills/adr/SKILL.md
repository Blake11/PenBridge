---
name: adr
description: How to add, supersede, or amend an Architecture Decision Record in docs/decisions.md for PenBridge. Use whenever a design decision with lasting impact is made or changed (technology, protocol, security model, platform approach, distribution, conventions), or when the user decides between alternatives.
---

# ADRs (`docs/decisions.md`)

Append-only. **Never rewrite an accepted ADR** — supersede it.

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
3. If the design changes, update `docs/architecture.md` in the same change and reference the ADR there.
4. Record the ADR id in progress.md "Decisions".

## Supersede
- New ADR with `**Status:** accepted (<date>), supersedes ADR-00XX` and why the old one no longer holds.
- In the old ADR, change only the status line: `**Status:** superseded by ADR-00NN (<date>)`. Leave its body intact.

## Rules
- Only the user accepts decisions; the architect drafts, confirms with the user, then writes `accepted`. Use `proposed` if pending.
- Keep each ADR short — rationale, not documentation.
- Commit as `docs: add ADR-00NN <title>` (or bundle with the feature PR).
