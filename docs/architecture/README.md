# Proposed native architecture

The [source](../product/requirements-source.md) overrides all design proposals. Baseline `ab631d4` contains source documents only; no native product, protocol, database or security enforcement has been demonstrated. This task adds specification tooling only. Avalonia/.NET 10/SQLite/EF Core and a restricted C# Linux agent are selected requirements; exact SDK/package patch pins belong to the build owner and OD-08, not invented in this baseline.

The desktop UI invokes local application services. Domain invariants and authorization precede persistence/transport. Agent-facing outbound-initiated gRPC/mTLS terminates at a narrowly exposed LAN/VPN desktop listener. The Linux agent owns approved local execution and durable journals; the root helper owns only reviewed privileged operations. Unprivileged Minecraft JVMs and bridges supply game capabilities without root authority. Optional providers and sandboxed extensions negotiate scoped contracts. Secrets live in OS stores, SQLite contains references, staging/deployment plans contain hashes and explicit approvals.

These boundaries are **proposed** pending implementation/security review. No architecture change or expanded privilege is authorized by this document. Read the [threat model](../security/threat-model.md), [capabilities](../product/capability-matrix.md), [decisions](../product/open-decisions.md) and [acceptance/evidence rules](../qa/specification-verification.md) with the ADRs.

| ADR | Boundary |
| --- | --- |
| [ADR 0001](adr/0001-ownership-migration.md) | Single controlling desktop and explicit fleet migration |
| [ADR 0002](adr/0002-private-ca.md) | Application private CA and enrollment |
| [ADR 0003](adr/0003-reconnect.md) | Versioned streams and durable operation reconciliation |
| [ADR 0004](adr/0004-helper.md) | Structured restricted helper authorization |
| [ADR 0005](adr/0005-file-roots.md) | Race-resistant registered file roots |
| [ADR 0006](adr/0006-schedules.md) | Agent-local durable schedule authority |
| [ADR 0007](adr/0007-deployments.md) | Immutable approved per-target deployments |
| [ADR 0008](adr/0008-backups.md) | Consistent recovery points and key recovery |
| [ADR 0009](adr/0009-sqlite-migrations.md) | Exclusive local EF Core schema migration |
| [ADR 0010](adr/0010-providers.md) | Evidence-backed optional provider capabilities |
| [ADR 0011](adr/0011-java-compatibility.md) | Per-family Java bridge and edition compatibility |
| [ADR 0012](adr/0012-extension-confinement.md) | Enforced extension grants and authenticated IPC |
| [ADR 0013](adr/0013-desktop-lifecycle.md) | Native windows and explicit background controller lifecycle |

M1 requires ownership identity, trust, operation journal, helper validation, safe roots used by adoption, and pre-migration backup foundations before mutating anything. M2 completes explicit controller migration; M3 depends on verified recovery facilities for risky deployment; M4 completes autonomous schedule/backup workflows; M5 adds exact Minecraft adapters; M6 adds providers/confinement; M7 requires native packaging and measured acceptance. These checkpoints do not remove selected v1.0 scope. Stable release remains blocked by unapproved licensing and absent signing/QA evidence.

Recovery for this documentation PR is a docs/tooling-only signed revert by the lead. No deployed state/schema exists to roll back. Later slices require their own operation-specific recovery narrative.

## Later owner policy: explicit C# types

On 2026-11-13, the owner explicitly required **no C# `var` anywhere**, including production, tests and checked-in generated code. Use explicit types throughout. This later instruction supplements Q145/Q151/Q155/Q186 and takes precedence over any inferred coding default without altering the immutable 205-question source. Planned acceptance: analyzer/configuration rejects an introduced `var` in each owned C# category and repository inspection finds none; generated code must be configured/processed to comply before check-in. The lead reported AGENTS.md and IDE0008 error enforcement planned for PR5 and conversion work for PR6. Those changes are outside this assignment and are not verified or claimed implemented here. This docs/Python-only PR adds no C# or build configuration.
