# ADR 0001: Single controlling desktop and explicit fleet migration

Status: Proposed — no implementation or security proof at bootstrap.

Source: [Q014](../../product/requirements-matrix.md#q014), [Q153](../../product/requirements-matrix.md#q153), [Q163](../../product/requirements-matrix.md#q163), [Q164](../../product/requirements-matrix.md#q164), [Q171](../../product/requirements-matrix.md#q171). Dependent checkpoint: M2; OD-06.

## Decision

Bind each fleet to controller identity and monotonic authority epoch. Keep active SQLite on local disk. Proposed handoff: lock local mutations; reconcile in-flight operations; take consistent DB backup/export; package nonsecret metadata and encrypted, owner-mediated credential/key handoff; verify schema and backup-key recovery on destination; authorize agents to switch trust/epoch; revoke old authority; unlock destination only after acknowledgments. Offline agents remain blocked for interactive control until individually handed off. The migration manifest records each agent phase; fleet-wide success requires every intended agent. Never use a NAS live DB. Old scheduled revisions need an explicit preserve/cancel inventory in the handoff; migration cannot create another executor.

## Alternatives

Shared NAS SQLite risks corruption and two writers; concurrent active controllers require a distributed consensus design outside scope; re-enroll every host is a recovery path with more operator work.

## Consequences

Credential references are not portable key material. Backups, CA authority and agent trust must migrate deliberately; per-agent fencing means partial migration is visible and temporarily reduces availability.

## Failure and recovery

Crash after one agent transfers: remain in migration mode and resume from durable manifest, never grant both controllers. Old epoch rejects writes. Lost source key requires owner-driven re-enrollment/rekey and backup-key recovery, not bypass. Fully compromised old controller/agent may defeat cooperative fencing; key revocation and local recovery are required.

## Test strategy

Two-controller write race; restore an old export; offline agent during transfer; crash before/after each acknowledgment; wrong schema; missing keychain secret/backup key; old certificate rejected. Use two desktop fixtures and real native key stores before claiming migration.

All cases are planned. Implementation must attach exact commands, sanitized logs, environment/version and independent verdicts to the matrix before claiming behavior. Design acceptance belongs to the lead/owner at the dependent slice; this ADR grants no new privilege.
