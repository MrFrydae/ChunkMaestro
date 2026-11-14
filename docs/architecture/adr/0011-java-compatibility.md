# ADR 0011: Per-family Java bridge and edition compatibility

Status: Proposed — no implementation or security proof at bootstrap.

Source: [Q026](../../product/requirements-matrix.md#q026), [Q027](../../product/requirements-matrix.md#q027), [Q029](../../product/requirements-matrix.md#q029), [Q045](../../product/requirements-matrix.md#q045), [Q047](../../product/requirements-matrix.md#q047), [Q048](../../product/requirements-matrix.md#q048), [Q071](../../product/requirements-matrix.md#q071), [Q091](../../product/requirements-matrix.md#q091), [Q152](../../product/requirements-matrix.md#q152), [Q203](../../product/requirements-matrix.md#q203). Dependent checkpoint: M5; OD-03/09.

## Decision

Pin Gradle Wrapper, Java toolchain and bytecode target per tested server-family adapter. Maintain independent MC/Java/server API/Bedrock/Geyser/Floodgate/Multiverse addon compatibility records; N-2 policy remains undefined until approved. Optional Paper/Spigot bridge cannot stand in for Vanilla/Fabric or proxy adapters. Folia requires separate region-safe dispatch and tests. Negotiate typed scoped game capabilities, authenticate bridge channel locally and prevent bridge/plugin code from gaining host helper authority. Preserve authoritative edition/player identity. Catalog JAR/modpacks enter staging and validate loader/dependencies/licensing before apply.

## Alternatives

One Java 25 target excludes older supported runtimes; loading Paper API everywhere breaks compatibility; assuming installed JAR equals loaded state lies about runtime health.

## Consequences

Exact releases and installed integrations are unverified here. No broad universal bridge support; failed/disabled/unknown plugin state stays distinct. World controls/permission groups require detected exact compatible integrations.

## Failure and recovery

Wrong bytecode/API, absent addon, old Geyser, malicious bridge message, Folia wrong-thread access or identity collision disables affected controls with explanation, not entire fleet or invented metrics. Unknown TPS is unavailable. Unsupported reload requires safe restart.

## Test strategy

JUnit adapter contracts plus real disposable platform fixtures; wrong MC/Java/loader, Vanilla without bridge, Folia region-thread assertion, Velocity/Bungee separation, Multiverse addon absent, Geyser UDP vs Java TCP and equal-name edition IDs. Evidence must pin all versions.

All cases are planned. Implementation must attach exact commands, sanitized logs, environment/version and independent verdicts to the matrix before claiming behavior. Design acceptance belongs to the lead/owner at the dependent slice; this ADR grants no new privilege.
