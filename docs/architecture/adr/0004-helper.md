# ADR 0004: Structured restricted helper authorization

Status: Proposed — no implementation or security proof at bootstrap.

Source: [Q009](../../product/requirements-matrix.md#q009), [Q020](../../product/requirements-matrix.md#q020), [Q030](../../product/requirements-matrix.md#q030), [Q031](../../product/requirements-matrix.md#q031), [Q035](../../product/requirements-matrix.md#q035), [Q162](../../product/requirements-matrix.md#q162), [Q168](../../product/requirements-matrix.md#q168). Dependent checkpoint: M1/M2.

## Decision

Keep agent identity separate from Minecraft identity. Root owns helper, policy, unit templates and executable; agent cannot modify any. Use authenticated local IPC/OS peer identity or tightly scoped sudo entry invoking fixed helper only, with framed typed requests. Helper independently checks caller, action, registered instance ID, root-owned mapping to exact unit/root, allowed runtime fields, range and ownership. Never accept raw shell, command paths, service users, ExecStart, arbitrary flags/environment or caller-selected unit names. Provision only reviewed templates with validated ports and preapproved layout. Exact IPC choice and sudo policy require security review before any grant.

## Alternatives

Root agent or wildcard sudo is unacceptable; direct systemd editing makes file access host-root execution; UI-only validation is not an authorization boundary.

## Consequences

New fields/templates require policy version and explicit review; optional privileged actions remain unsupported until approved. File edits still execute code as unprivileged Minecraft user.

## Failure and recovery

Identity mismatch, alternate unit, replaced helper or stale mapping fails closed. Partial provisioning records created resources and offers reviewed cleanup; never removes preexisting data. Helper update uses separately authorized package path, not agent self-replacement.

## Test strategy

Real Debian negative tests for arbitrary sudo/shell, injected JVM flags, alternate service, helper replacement, policy tamper, cross-instance ID, malformed IPC and permission race; prove allowed exact service action still succeeds with recorded audit.

All cases are planned. Implementation must attach exact commands, sanitized logs, environment/version and independent verdicts to the matrix before claiming behavior. Design acceptance belongs to the lead/owner at the dependent slice; this ADR grants no new privilege.
