# Backup recovery and identity lifetime boundaries

This slice adds behavioral tests for Q14/Q149/Q153/Q164 and directive storage
safety requirements. No production defect was demonstrated; production code,
migrations, dependencies and qualification thresholds remain unchanged.

`BackupRecoveryBoundaryTests` uses real SQLite and disposable directories:

- An uncommitted source write refuses backup without committing or rolling back
  the caller's transaction. Explicit rollback permits a new backup request.
- Cancellation immediately after real candidate verification prevents publication.
  The existing verifier callback controls this boundary; this is controlled
  injection, not an observed OS cancellation race or crash test.

Both cases verify source/database WAL and prior-backup preservation, cleanup of
unpublished candidates, and separate recovery from prior and fresh backups using
the public store APIs.

`StorageIdentityLifetimeTests` observes real macOS descriptors using stock `lsof`.
It checks immediate cleanup after rejected construction and partial binding,
exact controller-path matching, repeated disposal, retained caller-owned flock,
and successful SQLite sentinel reopen. Positive live-handle observations guard
against an empty or broken descriptor observer. These tests inspect disposable
fixture paths only; they do not use reflection or direct variadic native calls.

Both test projects select the descriptor suite only on macOS. Windows does not
compile these macOS instrumentation cases; they do not become skipped Windows
qualification results or establish Windows lifetime behavior. The backup tests
remain in both supported-platform suites. Linux retains its separate unsupported
controller contract.

Combined macOS arm64 validation with the pinned SDK: locked restore, Release
build, syntax/style and generated-inclusive formatting passed; 245 tests passed,
zero failures/skips. The two implementation deliveries' initial fixture failures
are retained separately, including removal of an unsafe test-only descriptor
probe; no production defect is inferred from them.

No fresh coverage/mutation result or kill credit is claimed. The previous
359d8cb measurement belongs to its original test inputs. Full exact-head Windows
and macOS qualification, 90/85/80 thresholds and independent product acceptance
remain blocked. Hosted Storage Actions remain disabled and removed.

Rollback removes the two new test files and their macOS project-selection entries;
there is no production database/schema rollback.
