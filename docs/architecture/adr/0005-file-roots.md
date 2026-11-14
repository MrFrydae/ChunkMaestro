# ADR 0005: Race-resistant registered file roots

Status: Proposed — no implementation or security proof at bootstrap.

Source: [Q020](../../product/requirements-matrix.md#q020), [Q052](../../product/requirements-matrix.md#q052), [Q059](../../product/requirements-matrix.md#q059), [Q062](../../product/requirements-matrix.md#q062), [Q068](../../product/requirements-matrix.md#q068), [Q079](../../product/requirements-matrix.md#q079). Dependent checkpoint: M3.

## Decision

Register per-instance allowed roots with read/write categories; protect privileged paths outside them. Resolve paths relative to retained directory handles with no-follow semantics and verify ownership/type at use, not only preview. Use bounded temporary writes in same safe directory followed by deliberate atomic replacement and mode preservation. Reject traversal, absolute archive entries, symlinks/hardlinks escaping roots, device files, Unicode/case collisions and ambiguous names. Preview bounded archive manifest before extraction; cap bytes/files/depth/compression expansion. Apply OS-specific path rules for Windows/macOS staging and Linux host. Exclude active DB/secrets from ordinary staging/export.

## Alternatives

String prefix checks permit sibling roots and traversal; canonicalize-then-open alone is TOCTOU vulnerable; following arbitrary symlinks makes target approval meaningless.

## Consequences

Cross-platform file semantics require separate adapters and fixtures. Legitimate symlink workflows may be unavailable until explicitly modeled. Atomicity is per file, not across fleet.

## Failure and recovery

Symlink swap or permission change between check/use rejects operation; full disk/rename failure preserves original plus explicit cleanup state. Missing target/remote hash drift stops mutation pending revised plan. No permission widening to make a write succeed.

## Test strategy

Traversal, root prefix sibling, hidden symlink pivot, hardlink, absolute/UNC/drive path, archive bomb, case/Unicode collision, root-owned file, disk full at each phase, permission race and parallel root replacement; assert no outside-root bytes change.

All cases are planned. Implementation must attach exact commands, sanitized logs, environment/version and independent verdicts to the matrix before claiming behavior. Design acceptance belongs to the lead/owner at the dependent slice; this ADR grants no new privilege.
