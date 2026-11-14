# ADR 0009: Exclusive local EF Core schema migration

Status: Proposed — no implementation or security proof at bootstrap.

Source: [Q014](../../product/requirements-matrix.md#q014), [Q083](../../product/requirements-matrix.md#q083), [Q149](../../product/requirements-matrix.md#q149), [Q153](../../product/requirements-matrix.md#q153), [Q163](../../product/requirements-matrix.md#q163), [Q173](../../product/requirements-matrix.md#q173). Dependent checkpoint: M1/M2.

## Decision

Keep SQLite local; EF Core owns ordered schema migrations and version metadata. Acquire exclusive application/migration lock, quiesce writes, create and verify compatible consistent pre-upgrade backup (SQLite backup API/checkpoint-aware method, not copying only main file with live WAL), then migrate transactionally where supported. Refuse newer unknown schema and do not blindly downgrade. Store secret references only. Separate telemetry batching/retention with measured parameterized SQL and capped growth. Audit append APIs/hash chain expose tampering limits.

## Alternatives

Shared NAS DB risks corruption/two-controller writes; automatic schema down migrations may destroy data; copying a WAL-active DB file alone loses committed state.

## Consequences

Upgrade needs free disk and restore instructions; stale credential references need cleanup without exposing secrets. Local OS attackers can rewrite DB and audit chain unless external anchoring is separately introduced.

## Failure and recovery

Second instance/migration contention waits boundedly then explains lock. Crash/corruption requires verified backup restore with matching app version; failed upgrade never starts core on partially understood schema. Missing keychain item is unavailable credential, not empty secret.

## Test strategy

Cold start, live WAL backup, lock race, killed migration, disk full, corrupted DB, newer schema, restore backup, schema version compatibility, credential reference cleanup, audit tamper, long telemetry retention/cap workload on Windows/macOS.

All cases are planned. Implementation must attach exact commands, sanitized logs, environment/version and independent verdicts to the matrix before claiming behavior. Design acceptance belongs to the lead/owner at the dependent slice; this ADR grants no new privilege.
