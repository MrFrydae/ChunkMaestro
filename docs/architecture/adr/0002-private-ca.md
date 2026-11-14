# ADR 0002: Application private CA and enrollment

Status: Proposed — no implementation or security proof at bootstrap.

Source: [Q017](../../product/requirements-matrix.md#q017), [Q019](../../product/requirements-matrix.md#q019), [Q163](../../product/requirements-matrix.md#q163), [Q170](../../product/requirements-matrix.md#q170), [Q171](../../product/requirements-matrix.md#q171), [Q172](../../product/requirements-matrix.md#q172). Dependent checkpoint: M1; OD-06.

## Decision

Use SSH-key installation with verified host key and reviewed package. Copy command carries only bounded-lifetime single-use enrollment proof, never a private key. Pin controller identity through the authenticated bootstrap. Agent generates its own key and exchanges proof for fleet/host-scoped identity certificate; consume token atomically. Keep CA key protected by OS credential facilities, separate signing from ordinary UI use, and never install the CA globally. Proposed short certificate validity/rotation overlap must be pinned in the protocol before rollout. Check SAN identity, controller/fleet binding, validity and revocation per connection/action; deny mutation if trust cannot be established.

## Alternatives

Public PKI alone authenticates endpoint names but not fleet authorization; permanent copied tokens leak reusable authority; system-global private CA broadens trust unnecessarily.

## Consequences

Desktop must be reachable over approved LAN/VPN. Rotation/revocation needs durable identity state. Compromised CA requires fleet rekey; no claim of hardware-backed storage on every OS.

## Failure and recovery

Enrollment/rotation interruption retains bounded old/new state and resumes safely. Expired cert while offline does not authorize reconnect mutations; use owner-mediated renewal. Revocation cannot instantly reach a disconnected agent: stored schedules are already delegated authority and need expiry policy (OD-05). No silent public binding on address change.

## Test strategy

Expired/replayed token, duplicate host enrollment, wrong host/controller/SAN, clock skew, revoked cert, interrupted rotation, SSH host-key mismatch, unauthorized LAN peer. Inspect args/logs for credentials in disposable fixtures; test Keychain/Windows storage on matching OS.

All cases are planned. Implementation must attach exact commands, sanitized logs, environment/version and independent verdicts to the matrix before claiming behavior. Design acceptance belongs to the lead/owner at the dependent slice; this ADR grants no new privilege.
