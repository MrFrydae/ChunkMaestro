# ADR 0008: Consistent recovery points and key recovery

Status: Proposed — no implementation or security proof at bootstrap.

Source: [Q051](../../product/requirements-matrix.md#q051), [Q066](../../product/requirements-matrix.md#q066), [Q094](../../product/requirements-matrix.md#q094), [Q095](../../product/requirements-matrix.md#q095), [Q096](../../product/requirements-matrix.md#q096), [Q098](../../product/requirements-matrix.md#q098), [Q099](../../product/requirements-matrix.md#q099), [Q104](../../product/requirements-matrix.md#q104). Dependent checkpoint: M4; OD-04.

## Decision

Use supported world save/freeze coordination or stop before copy; guarantee consistency only for verified method/platform. Recovery manifest records versions, files, hashes, consistency method, provenance and encryption/key references. Encrypt protected backups with reviewed authenticated encryption and per-set nonce/key management; exact library/format remains to be selected, not a custom crypto implementation. Configure agent-reachable NAS or quota-bounded local fallback for desktop-off jobs; mark VM-local-only risk. Verified transfer/readability at destination is required for off-host complete. Default daily 14 days, weekly eight weeks; pin recovery points needed by active plans. Owner must prove backup-key recovery independently of desktop loss.

## Alternatives

Raw live copy is not consistency; desktop-only target cannot back up while desktop off; hypervisor snapshot alone is not application-consistent; putting plaintext key beside backup defeats protection.

## Consequences

Integrity verification is separate from isolated server boot test. Encrypted backups become unrecoverable without keys; key recovery/destination decisions block guarantees, not UI/core work.

## Failure and recovery

Destination unavailable/full uses only approved bounded fallback or explicit failure. Interrupted transfer stays incomplete. Hash/key failure blocks restore. Quota pruning never deletes required pinned recovery; refuse new work with actionable capacity error. Selective/world/full overwrite needs stopped destination, compatibility, typed confirmation and reviewed recovery plan.

## Test strategy

World writes during capture, missing NAS, desktop off, local full, incomplete transfer, corrupt archive/hash, lost key, retention boundary/pinned recovery, incompatible runtime restore and restore interruption. Periodic isolated extraction plus optional real disposable boot test; never label one as the other.

All cases are planned. Implementation must attach exact commands, sanitized logs, environment/version and independent verdicts to the matrix before claiming behavior. Design acceptance belongs to the lead/owner at the dependent slice; this ADR grants no new privilege.
