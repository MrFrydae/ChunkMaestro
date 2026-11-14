# Contributing

Read the [source requirements](docs/product/requirements-source.md) and [development guide](docs/operations/development.md). Source answers override the directive. No license has been approved; do not add one or assume redistribution rights.

Use a bounded issue and `feat/<issue>-<slug>`, `fix/…`, `test/…`, `docs/…` or `chore/…` branch. Respect assigned file ownership and keep source inputs intact. Preserve configured SSH commit signing; never disable signing to get a commit through.

Run `python3 tooling/build/build.py verify` (Windows: `python`). Commit updated central package versions and project `packages.lock.json` files together after an intentional `update-locks`. Include commands, environment, observed results, limitations, threat/failure narrative and recovery plan in the PR. No empty/skipped suite counts as feature proof.

Two independent QA-role verdicts including security/reliability are required for regular code PRs; high-risk changes require all three roles. Implementers cannot review their own work. Agents sharing an account do not represent distinct GitHub approvals. The lead configures protected-branch checks and merges; no release is authorized by this workflow.

Use explicit C# types throughout; `var` is prohibited, including in tests and generated files. IDE0008 is an error in `.editorconfig`; see [AGENTS.md](AGENTS.md).

Before pushing, run `python3 tooling/build/build.py secret-scan` with Gitleaks 8.30.1 installed. Independent exact-head QA must be lead-attested and pass the CI evidence gate before merge; see development documentation.
