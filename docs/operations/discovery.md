# Discovery — 2026-11-13

## Verified facts

- Repository started empty: no commits, implementation, CI, or repository instructions.
- Authoritative source found at `/Users/derek/Downloads/ChunkMaestro-Questions-and-Answers.md`; copied unchanged into `docs/product/requirements-source.md`. Exactly 205 ordered numbered questions verified.
- Lead: Codex `gpt-6.1-sol`, reasoning `high`; confirmed by real orchestrator capabilities. Approved Codex model is available for focused tasks. Claude is available but has not been selected. Other listed providers are disabled.
- Maximum concurrency: lead plus three workers/reviewers. No numeric cost cap is configured; no additional paid service is authorized.
- Runtime: macOS 27.2, Apple Silicon arm64, full-access filesystem, network enabled; no shell approval prompts available.
- Initially installed: .NET SDK 11.0.100 and Java 8. A private .NET 10 SDK install was attempted but did not complete before interruption; verify before use. Official .NET 10 release metadata reported SDK 10.0.404 / runtime 10.0.15.
- GitHub CLI authenticates as MrFrydae with repo/workflow/project access. Public repository admin access verified. Milestones 1 (Phase 0) and 2 (Phase 1) and issues 1–4 created.
- Git commit signing is enabled with an SSH signing public key. Signing must succeed; do not disable it to bypass a failure.
- Branch protection discovery originally returned 404 because main had no commits. Enforcement is unconfigured pending bootstrap and actual checks. Only one GitHub identity is verified; agent verdicts are not independent GitHub user approvals.
- No disposable Debian VM tooling (multipass/limactl/qemu-system-aarch64) was found in PATH. No real systemd/native Windows/installer tests have run.
- No agent has been enrolled. Existing Minecraft host has not been contacted or modified.

## Recorded assumptions

- Next reboot in update requirements means next approved Minecraft service restart.
- Existing host address and paths in source are historical inventory, not verified live state.

## Dependent blockers

- Stable release: approved license, signing identities, platform minima, required QA and packaging evidence.
- Real systemd failure injection: disposable Debian 13 environment not yet established.
- Host-local autonomy: schedule and durable restart semantics require implementation and real-agent evidence.
- Listener endpoint/migration, backups and integration versions: open decisions in source; no guessed credentials, paths, or API capabilities.

## Bootstrap exception

The initial source-only commit establishes main so worktree tasks can branch. It contains no product code and is not a feature acceptance or public release. All implementation PRs require the directive's independent QA gates before merge.
