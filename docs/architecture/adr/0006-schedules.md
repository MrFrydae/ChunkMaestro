# ADR 0006: Agent-local durable schedule authority

Status: Proposed — no implementation or security proof at bootstrap.

Source: [Q025](../../product/requirements-matrix.md#q025), [Q097](../../product/requirements-matrix.md#q097), [Q100](../../product/requirements-matrix.md#q100), [Q101](../../product/requirements-matrix.md#q101), [Q102](../../product/requirements-matrix.md#q102), [Q103](../../product/requirements-matrix.md#q103). Dependent checkpoint: M4; OD-05.

## Decision

Only running host agent executes approved local schedule revisions; desktop configures and approves but never races it. Persist schedule/revision/action/hash/window/artifacts, occurrence ID and each job stage. Proposed DST policy: stored IANA zone, skip nonexistent civil times, run repeated civil time once with explicit offset occurrence identity; timezone edits create new approved revision effective after current occurrence. Proposed overlap policy skips rather than queues disruptive overlapping work; per-instance lock also covers manual deploy/restart. Host missed window records missed, never late catch-up. Require artifacts/recovery/preflight ready at occurrence time. Cross-host controller-dependent plans stop when coordination unavailable. Delegated schedules need explicit validity and cancellation delivery state.

## Alternatives

Desktop scheduler cannot work while off; dual schedulers duplicate jobs; catch-up disruptive jobs violate source; universal UTC-only scheduling hides civil-time/DST intent.

## Consequences

DST/overlap details remain proposed pending OD-05. Revocation during disconnection is bounded by preapproved validity, not instantaneous. Agent journal needs capacity and retention bounds.

## Failure and recovery

Persist warning start/remaining-stage evidence across crash. On ambiguous crash during restart reconcile rather than restart countdown/effect blindly. Five real minutes precede kick/save/graceful stop/start; failed save pauses without force kill. Superseded revisions cannot launch new occurrences; cancel-too-late is explicit.

## Test strategy

Desktop asleep vs host off; spring gap/fall duplicate; clock rollback/timezone edit; overlapping policy/manual operation; revision supersession; expired approval; missed window; crash at each countdown/stop/start boundary; disk full journaling. Assert one executor and no duplicate service restart on real Debian.

All cases are planned. Implementation must attach exact commands, sanitized logs, environment/version and independent verdicts to the matrix before claiming behavior. Design acceptance belongs to the lead/owner at the dependent slice; this ADR grants no new privilege.
