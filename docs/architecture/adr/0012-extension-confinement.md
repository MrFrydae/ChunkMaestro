# ADR 0012: Enforced extension grants and authenticated IPC

Status: Proposed — no implementation or security proof at bootstrap.

Source: [Q080](../../product/requirements-matrix.md#q080), [Q110](../../product/requirements-matrix.md#q110), [Q157](../../product/requirements-matrix.md#q157), [Q196](../../product/requirements-matrix.md#q196), [Q204](../../product/requirements-matrix.md#q204). Dependent checkpoint: M6; OD-11.

## Decision

Executable third-party extensions run outside controller/helper with versioned manifests, curated/manual offline install and explicit user grants. Broker only typed capability-scoped IPC using OS peer authentication plus per-launch identity; revoke channels and terminate workers on uninstall/revocation. Provide extension-owned roots and redacted data. Proposed OS adapters must enforce filesystem/network/process restrictions using separate constrained identity and supported sandbox/ACL facilities; exact Windows/macOS/Linux mechanisms require prototypes before untrusted execution. A shared-user child process without OS confinement is not a supported sandbox. Fail closed if required restriction cannot be enforced; do not provide credentials by default. AI workers may explain/draft only; external data transfer needs opt-in.

## Alternatives

In-process dynamic loading crosses all core privileges; separate process under same unrestricted user can read files/keychain and is insufficient; container-only dependency violates native baseline.

## Consequences

Untrusted executable catalog feature is blocked until OS confinement is proven on each supported OS. Built-in reviewed modules can ship independently. Signed publisher identity is provenance, not permission or code safety.

## Failure and recovery

Manifest tamper/version mismatch, worker crash, path/network escape, forged IPC or revoked grant fails closed; no auto-restart into broader scope. Runtime grant change invalidates prior channels and artifacts need revalidation. Logs must exclude tokens and private data.

## Test strategy

Real OS deny-read controller DB/keychain and outside root; deny egress unless granted; deny helper access/child escape; spoof IPC, crash/restart, revoke mid-call and offline install tamper. Include native tests per OS and never certify confinement from mocks alone.

All cases are planned. Implementation must attach exact commands, sanitized logs, environment/version and independent verdicts to the matrix before claiming behavior. Design acceptance belongs to the lead/owner at the dependent slice; this ADR grants no new privilege.
