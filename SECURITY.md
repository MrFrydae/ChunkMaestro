# Security

No production security guarantees are established by the bootstrap. Do not include credentials, keys, world data or unredacted logs in public issues or PRs.

Report vulnerabilities through the repository's GitHub private vulnerability reporting when enabled. If unavailable, contact @MrFrydae through an owner-provided private channel before disclosing exploit details; this repository does not invent an email or promise a response SLA.

PR CI runs untrusted code on disposable GitHub-hosted runners with read-only repository access, no persisted checkout credentials, no production/signing secrets and no release jobs. Actions use immutable commit pins; review upstream changes before updating them. Dependency locks constrain resolution, not trust or license approval. Restore audits all dependencies; advisories fail under warnings-as-errors.

Independent security/reliability review is required alongside functional review. High-risk changes also require UX/accessibility/performance review. Evidence files are assertions to inspect, not authenticated GitHub approvals. The lead owns reviewer assignment, identity validation and merge decisions.
