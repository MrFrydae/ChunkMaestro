# Store failure contract implementation evidence — issue 3 / PR 6

This is local implementation test evidence, not independent QA or current-head
cross-platform mutation qualification. Source: Q149, Q153, Q163–164, Q189–195.
Branch: `test/3-store-failure-mutation-contracts`; parent:
`4cd923c7059d1a3a1c789ce0cfd81e957b0a13f9`. The signed commit containing this
file identifies the delivered test head.

## Added behavior

`tests/ChunkMaestro.PersistenceTests/StoreFailureMutationContractTests.cs` adds
six cases through `LocalFleetStore` and real disposable, owner-scoped SQLite:

- A history containing the later migration without its initial prefix is refused
  before backup creation; restoring the original history explicitly permits reopen.
- A missing migration-history column is categorized UnsupportedSchema, preserved,
  and recoverable after explicit fixture repair.
- An unexpected view is refused and preserved; removing it explicitly allows reopen.
- `user_version` equal to the supported migration count permits populated reopen
  without creating another migration backup.
- A real exclusive SQLite lock denies startup as StorageUnavailable; releasing the
  writer permits reopen with the original inventory and no new backup.
- A live synthetic trigger referencing a nonexistent table causes an operational
  endpoint write failure. No instance or endpoint survives the failed transaction.
  Removing the trigger does not replay the operation; an explicit retry succeeds
  and survives reopen with the supplied identity and endpoints.

Failure cases require nonempty sanitized guidance and no raw exception, sensitive
fixture path, SQL error, or synthetic input disclosure. No arbitrary message wording
is required. Preservation checks hash the actual database and retain backup lists.
Recovery through public reopen also verifies controller/resource release.

Existing StorageTests, ManagedStorageQualificationTests,
DatabaseConstraintContractTests and PopulatedUpgradeTests already cover corruption,
newer versions, cancellation, constraint rollback, migration interruption, endpoint
ownership mismatch and other schema damage. Those cases were retained unchanged;
this suite does not duplicate their individual acceptance assertions. No production
change or actual production defect was identified.

## Validation

Environment: macOS 27.2.1, Darwin arm64, pinned SDK 10.0.404 at
`/Users/derek/.local/share/chunkmaestro/dotnet/dotnet`.
All third-party build/test execution used `replay_ci.child_environment(os.environ)`
with explicit selected SDK/root/PATH. That allowlist forces
`DOTNET_SYSTEM_NET_DISABLEIPV6=1`, `MSBUILDDISABLENODEREUSE=1` and telemetry opt-out.
No credential values were inspected or printed.

Commands after the final C# changes:

```text
dotnet --version
dotnet restore tests/ChunkMaestro.PersistenceTests --locked-mode
dotnet test tests/ChunkMaestro.PersistenceTests -c Release --no-restore --filter FullyQualifiedName~StoreFailureMutationContractTests --logger trx;LogFileName=store-failure.trx --results-directory /tmp/pr6-store-failure-results
python3 tooling/build/build.py verify
git diff --check
```

The logger argument above is one argument (quote it when invoking in a shell).
Targeted suite: **6 passed / 0 failed / 0 skipped**.
Repository verify: locked restore, Release build (0 warnings / 0 errors), syntax
policy, generated-inclusive format verification, and **139 passed / 0 failed /
0 skipped**. No files were reformatted by verification.

SHA256 artifacts:

| Artifact | SHA256 |
| --- | --- |
| `/tmp/pr6-store-failure-targeted.log` | `88bba75d8aa5874aaf074de389a2a2983f50bad851f7b562c17e206020676e83` |
| `/tmp/pr6-store-failure-verify.log` | `5e3167a12a8f587fa4a75dbf25919c6d0398a21b76730af5be773cd70290d13c` |
| `/tmp/pr6-store-failure-results/store-failure.trx` | `60e827f3fb7b7a91f61d3be906cbb36067ef6c5cdd1c757669543a4dcf386aa3` |
| `/tmp/pr6-store-failure-results/repository-verify.trx` | `46e40f2625275c137481343e31cce8d17bbff3e13b4f41677b2c32c51ab822eb` |
| New test source | `c96b96a09f3520a596b37e1e9d219fecc717a7bfc5dfd11a5077f25e6e1bfc2d` |
| Unchanged LocalFleetStore.cs | `4b7eb30c73019a51b8c042226cde27e747ffc017c7d4bece1e702e89f655165f` |
| Unchanged SupportedSchema.cs | `5c61e379905f354d0ac480c3a542ab1e0e220e23f7c932b61cd157186101a74e` |

## Limits and handoff

No private or official mutation diagnostic was executed. No new kill count is
claimed. The supplied ba8b1b9 artifacts and corrected-parser preflight remain
historical evidence only. No source, SDK, dependencies, locks, signing configuration,
mutants, existing tests or thresholds were changed. Minimum 90% line / 85% branch /
80% actual mutation kills remain binding; timeouts receive no kill credit.

Windows execution, matched-platform current-head full qualification and all three
independent QA roles remain lead-owned and unexecuted here. Linux's normal suite
excludes runtime test files and cannot qualify these cases. No host/world/user data,
secrets, live settings, dashboards or HTML reports were used. Rollback is a reviewed
revert of the new test and this evidence document. No push, PR mutation, watch or
hosted polling was performed.
