---
name: "chunkmaestro-persistence"
description: "Change or review ChunkMaestro SQLite storage, EF migrations, local controller authority, backup integrity and recovery behavior."
---

# Change persistence safely

Read [AGENTS.md](../../../AGENTS.md), source answers Q14/Q149/Q153/Q163–164 and
related affected answers in the [source](../../../docs/product/requirements-source.md),
plus the storage/testing sections of the
[directive](../../../docs/product/orchestration-directive.md). Consult the current
storage operations document when present on the target branch. Product and
platform capabilities must be established from that branch, not another PR.

Resolve the supported schema versions and actual migration mechanism first.
Preserve committed/shared migrations; add a justified forward migration for a
real invariant or feature. Use the project's compatible EF tooling, review the
generated DDL/model/snapshot, then normalize generated C# to repository policy.
Do not manufacture a predecessor or cosmetic migration as upgrade evidence.

Treat path authority, controller authority and database integrity separately:

- Validate native owner/ACL/mode and ancestor rename/delete authority before use.
  A leaf-only permission check or lock at a swappable pathname is insufficient.
  Bind native identities and session/lock lifecycle; preserve unsafe existing data
  instead of repairing its ownership or permissions during startup.
- Inspect real SQLite sidecar creation, sharing and deletion on each supported OS,
  including elevated Windows ownership. Never truncate or re-own an existing WAL
  as a shortcut. Keep network/sync locality assumptions explicit.
- Matching migration history and `quick_check` do not prove the supported schema
  or valid foreign keys. Reject incompatible structure and logical corruption
  before returning usable inventory. Do not silently recreate missing constraints.
- A backup must include committed WAL data, finish verification and be usable for
  reviewed recovery. Partial work is not a completed recovery point. Keep original
  database/sidecars and completed backups on failure; do not auto-downgrade/restore.
- SQLite async APIs can block synchronously. Verify caller responsiveness and
  serialized background I/O at the native service boundary. Categorize operational
  failures without raw SQL, paths or input values; preserve cancellation and avoid
  automatic mutation replay after an ambiguous outcome.

Use real SQLite through public storage contracts for positive/denial/recovery
tests. Include populated supported-version transitions, competing processes,
schema/row corruption, full disk and actual interrupted migration/backup when
applicable. Empty initialization, injected exceptions and idle lock-holder death
prove narrower cases; identify them accurately. Disposable fixtures must not
touch production files, real disks or unrelated volumes.

For safety modules, the directive's applicable minimums remain 90% line,
85% branch and 80% mutation with measured artifacts. Account for native code and
OS-specific branches explicitly; exclusion or an unsupported suite is not proof.
Use [independent QA](../chunkmaestro-independent-qa/SKILL.md); storage/backup changes
require all three roles. Report missing qualification as a merge blocker.
