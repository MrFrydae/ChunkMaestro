# ADR 0010: Evidence-backed optional provider capabilities

Status: Proposed — no implementation or security proof at bootstrap.

Source: [Q018](../../product/requirements-matrix.md#q018), [Q024](../../product/requirements-matrix.md#q024), [Q105](../../product/requirements-matrix.md#q105), [Q108](../../product/requirements-matrix.md#q108), [Q109](../../product/requirements-matrix.md#q109), [Q110](../../product/requirements-matrix.md#q110), [Q111](../../product/requirements-matrix.md#q111), [Q112](../../product/requirements-matrix.md#q112). Dependent checkpoint: M6.

## Decision

Version provider contracts/manifests with separate discovery/status/lifecycle/files/console/metrics/backup/schedule/bridge capabilities. Detect authority and versions at enrollment, enforce again at execution, report unsupported/unavailable/stale/unknown separately. Native systemd is first adapter; SSH/SFTP is reduced and never implies autonomous schedules. Implement selected Pterodactyl/Pelican/Multicraft and XCP-ng only from verified documented interfaces. Hypervisor credentials/VM operations are separate from game service authority. DNS read-only by default; delegated OpenTofu records produce reviewed workflow changes. Apex excluded v1.0.

## Alternatives

A universal provider base class with successful no-op methods fakes support; VM power bundled with Minecraft restart expands authority; plaintext FTP fallback leaks credentials.

## Consequences

Capabilities vary by version/account/grant; unavailable credentials or real environments leave contract tests only and cannot certify live access. Container support remains optional post-initial provider work.

## Failure and recovery

Handle auth expiry, rate limit/retry-after, pagination, timeout and protocol errors explicitly. Retry safe reads boundedly; ambiguous mutations reconcile provider operation/state rather than replay. No insecure transport downgrade; exceptional FTP needs explicit approval plus trusted encrypted tunnel.

## Test strategy

Contract fixtures for each selected adapter with missing capability, revoked grant, bad token, rate limit, malformed/paged response, timeout after power apply; test service-vs-VM privilege separation, DNS ownership, no autonomous SSH schedule and no credential logging.

All cases are planned. Implementation must attach exact commands, sanitized logs, environment/version and independent verdicts to the matrix before claiming behavior. Design acceptance belongs to the lead/owner at the dependent slice; this ADR grants no new privilege.
