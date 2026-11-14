# ADR 0007: Immutable approved per-target deployments

Status: Proposed — no implementation or security proof at bootstrap.

Source: [Q053](../../product/requirements-matrix.md#q053), [Q055](../../product/requirements-matrix.md#q055), [Q058](../../product/requirements-matrix.md#q058), [Q059](../../product/requirements-matrix.md#q059), [Q064](../../product/requirements-matrix.md#q064), [Q065](../../product/requirements-matrix.md#q065), [Q066](../../product/requirements-matrix.md#q066), [Q067](../../product/requirements-matrix.md#q067), [Q197](../../product/requirements-matrix.md#q197). Dependent checkpoint: M3 with M4 recovery subset.

## Decision

Store approved immutable plan revision with exact targets, artifact hashes, remote expected hashes/revision, validated fields, capability/version requirements, recovery references, window and restart needs. Any local edit creates a new revision needing approval. Recheck remote identity/state/capacity/version/recovery at execution under instance lock. Transfer bounded verified content to safe temporary paths and replace deliberately. Model draft/awaiting approval/scheduled/running/verifying/completed/failed/unknown/rolled back and safe cancellation as explicit transitions. Watchers and Git imports only create drafts. Per-target durable results/recovery, no fleet-wide atomic transaction.

## Alternatives

Auto-sync violates review; mutable approved plans permit approval bypass; automatic post-health rollback can damage changed worlds; cross-host transaction claims lack an atomic substrate.

## Consequences

Approval may expire through drift, missing recovery or changed capabilities. Risky changes depend on verified backup facilities even if broad backup milestone ships later. Generic reload and arbitrary shell hooks remain unavailable.

## Failure and recovery

Changed local/remote file, hash mismatch, lost transport, partial fleet failure and full disk pause/fail target with evidence. Health failure offers explicit scoped recovery and no automatic rollback/restart. Unknown outcomes reconcile journal first. Rollback is itself reviewed and verified.

## Test strategy

Edit after preview/approval, drift just before write, deleted target, checksum corruption, atomic rename failure, lock collision, incompatible JAR/runtime, missing backup, two-host partial failure and post-health failure. Assert unchanged unapproved bytes and per-target result truth.

All cases are planned. Implementation must attach exact commands, sanitized logs, environment/version and independent verdicts to the matrix before claiming behavior. Design acceptance belongs to the lead/owner at the dependent slice; this ADR grants no new privilege.
