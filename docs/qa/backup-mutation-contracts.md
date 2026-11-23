# Backup mutation contracts (issue 3, implementation evidence)

Requirements Q149/153/163–164/189–195 govern these tests. The unchanged safety
gates remain 90% line, 85% branch and 80% actual mutation kills, plus all three
independent QA roles. This work does not qualify PR 6 or reinterpret historical
`ba8b1b9` hosted evidence as current-head evidence.

## Behavioral scope

`BackupMutationContractTests.cs` adds four cases using real SQLite and disposable
owner-scoped paths. Every failure preserves byte hashes of the live database,
committed WAL and a previously completed real backup. After resolving the fixture
failure, a fresh explicit request produces a backup that opens through the public
store and contains all 4,096 seeded fleets.

- Cancellation competes with a new transaction on the source after an actual
  incomplete native step. Cancellation must win before a further native step
  can encounter contention. This distinguishes bounded-step cancellation from
  merely noticing cancellation after copying finishes.
- Two cases damage only the copied candidate through a second SQLite connection
  after the final native step: an orphan host, or an invalid CHECK-constrained
  row. Actual SQLite checks establish the damage. Production verification must
  reject publication, remove the candidate, and preserve the healthy source and
  prior backup. No verifier or successful copy is replaced by a stand-in.
- A directory at the candidate journal pathname produces a real cleanup refusal
  after copying finishes. Synthetic candidate WAL/SHM files exercise remaining
  cleanup. The cancellation exception must survive the refusal, the other files
  must still be removed, and the refused directory remains for explicit fixture
  cleanup. These files are cleanup fixtures, not evidence of SQLite creating WAL
  sidecars for this DELETE-mode destination.

Existing `BackupPublicationTests` already covers destination creation failure,
publication collision, source foreign-key corruption, successful WAL/public
recovery, process death, final-copy cancellation and native step contention.
Those cases were assessed and retained rather than duplicated.

No production code, existing tests, dependencies, SDK, locks, privileges, settings
or hooks changed. No real hosts, worlds, user data, mounted disks, dashboards or
HTML reports were used. There is no new disk-full claim.

## Local validation

Parent: `4cd923c7059d1a3a1c789ce0cfd81e957b0a13f9`.
Test commit: `7a9f59c26eaf1db8955523510d70b865f7cf3b57` (SSH signed).
Environment: macOS 27.2.1, Darwin arm64, pinned .NET SDK 10.0.404.

Every .NET invocation used `/tmp/pr6-backup-run.py`, which constructs the explicit
noncredential `replay_ci.child_environment` allowlist, selects
`/Users/derek/.local/share/chunkmaestro/dotnet/dotnet`, sets its DOTNET_ROOT and
retains the required IPv6-disable/no-node-reuse settings. No credential values
were inspected or printed. Commands from the checkout root:

```sh
python3 /tmp/pr6-backup-run.py python3 tooling/build/build.py restore
python3 /tmp/pr6-backup-run.py /Users/derek/.local/share/chunkmaestro/dotnet/dotnet test tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj -c Release --no-restore --filter FullyQualifiedName~BackupMutationContractTests --logger 'trx;LogFileName=backup-contracts.trx' --results-directory /tmp/pr6-backup-contract-results
python3 /tmp/pr6-backup-run.py python3 tooling/build/build.py verify
python3 -m unittest discover -s tooling/build -p 'test_*.py' -v
python3 tooling/specification/check.py
```

Final new suite: 4 passed, 0 failed, 0 skipped. Final repository verifier: locked
restore, Release build, syntax and generated-inclusive formatting all passed;
137 tests passed, 0 failed, 0 skipped. Earlier attempts (recorded in the implementation tool transcript; final logs
supersede iteration logs): missing
assets prevented execution; then CA1861 prevented compilation; then 3 passed and
1 failed because the CHECK-damage fixture had not turned its connection's
`ignore_check_constraints` back off before checking. That fixture was corrected
before both final test runs. These were implementation iterations, not product
failures or mutation kills.

| Artifact | SHA256 |
| --- | --- |
| New test source | `e2cf9582a6494c6f25c064c6052c66bdea653dbc303e8d453107afd11c4131bd` |
| Unchanged driver source | `a5b47079856f4bfe43141a76fff0c9c813ec2db885aef2d0b9c2c5df804602c4` |
| `/tmp/pr6-backup-contract-tests.log` | `a1fa1e36794f7956c3cb2f71099e88aad3a9f0cc8f6ed475f4b00a454aa15b71` |
| `/tmp/pr6-backup-contract-results/backup-contracts.trx` | `8eb2710d53a96d91bd6616355e1dcf5aaa9ecddaea0807c6edd6301af9bce194` |
| `/tmp/pr6-backup-verify.log` | `e929f80b8b41711357f7bdd64eb0364d739beef56a4a3d0968e292744f378a4f` |
| `/tmp/pr6-backup-tool-validation.json` | `49b7524817a3523ab4660a881382053ba19593b698798ace2083ade8adaf3c77` |

## Remaining limits

Windows and Intel execution are unmeasured here. No safe deterministic native
backup-init/finish failure fixture was established; those survivors remain
unresolved. Durability/flush mutants need evidence beyond ordinary recovery,
and this suite does not claim to kill them. No exception text is matched to its
exact implementation wording. There are no equivalence waivers, removed tests,
removed mutants or timeout-as-kill claims. Final matched-platform full collection,
aggregation and independent QA remain the lead's work. No actual production defect
was observed. Build tooling regressions passed 15/15, and the specification checker
passed. SSH signing was preserved; normal Git signature trust verification on this
host is unavailable because `gpg.ssh.allowedSignersFile` is not configured. No
signing or trust configuration was changed.

## Focused private diagnostic

One private diagnostic ran at test commit `7a9f59c`, with the same test/production
bytes as this documentation commit. `/tmp/pr6-backup-focused.py` used the explicit
noncredential child environment and the existing verified local build from
`/tmp/pr6-replay-private-lead-v2/provenance.json`. The current replay builder's
hash differs from that historical build's builder hash; this was not presented
as a fresh build. Instead, validation reopened all 15 local compiler files, 370
outputs/dependencies, and 869 source entries, with no mismatches, and checked the
pinned archive and sole one-line factory patch. Hosted compiler paths were not
used as local proof. The advisory review's warnings/conditional-use limits remain.

The exact tool command (working directory `tooling/quality/storage`) is retained
in `/tmp/pr6-backup-focused/commands.jsonl`: pinned dotnet, verified private CLI,
`--config-file <checkout>/tooling/quality/storage/replay/persistence.json`,
`--mutate '**/IncrementalBackupDriver.cs'`,
`--output /tmp/pr6-backup-focused/private`, `--log-to-file`. The mutation project
was first restored with `dotnet restore tooling/quality/storage/MutationTests.csproj
--locked-mode`. No tracked config or exclusion changed. Only the diagnostic scope
selected one source file; this is not a full-module run or gate measurement.

The initial run found 137 tests. Completion took 117.2 seconds, within the
600-second command bound, with actual threshold-failure exit 2, no command
timeout. The target source report contains 49 Killed, 11 Survived, 12 NoCoverage,
6 CompileError and 1 Timeout. No coverage/qualification percentage is claimed.
The complete generated catalogue (including other-source filtered entries), test
attribution, JSON/HTML and debug reports are retained; HTML was not opened.

All 23 original survivor identities were retained and matched uniquely using
location, mutator and replacement. Report source bytes match the local driver
and both historical macOS replay reports. **Original IDs 82, 83 and 88 now have
actual Killed observations**, each attributed to
`CleanupRefusalDoesNotMaskCancellationOrPreventOtherSidecarCleanup`.
IDs 47, 49, 58, 59, 63, 64, 66, 67, 68, 69, 81, 94, 95, 106, 109, 110,
115, 116, 117 and 121 remain unresolved. ID 106 is Timeout, never a kill.
Nothing here updates the historical aggregate or qualifies the current PR head.

After mutation, normal repository verification restored outputs and again passed
137 tests, 0 failed, 0 skipped, with locked restore/build/format/syntax passing.
The mutation project output is disposable diagnostic output; no instrumented
product binary is shipped. No production defect was observed.

| Retained diagnostic artifact | SHA256 |
| --- | --- |
| `/tmp/pr6-backup-focused/private/reports/mutation-report.json` | `cb7184fb7f491fcb2e5a83c262b37af65f75cbd41b819336ef645276b3907357` |
| `/tmp/pr6-backup-focused/binding.json` | `80fb7b2397419537e8b8bd516219f656b7de7a22c2dfe6e5e7bda1be7b49e10e` |
| `/tmp/pr6-backup-focused/result.json` | `78af8fdcd4e58809285ea4b3c4a98f1521d0857c51ca865be6d516a807b242bf` |
| `/tmp/pr6-backup-focused/original-survivor-outcomes.json` | `3730854d0a00b375423460ce9f59720d35d0c8dda39499ee25a59fd85e61631b` |
| `/tmp/pr6-backup-focused/original-union-survivors.json` | `94791311eb2f3881affa6e8cc5b0a5943712bd421d2035d6cb86038cea473362` |
| `/tmp/pr6-backup-focused/summary.json` | `660b345ce4affd9e9ebf2eb9176a902bab10804a2b28881ac9506b9d59c41aa6` |
| `/tmp/pr6-backup-restoration-verify.log` | `6f55f728f28230ef05a86ab3529a9dadbd7dccdc73d7f766d1fc0a1f691eda08` |
