# Direct CI safety checks

Owner decision, 2026-11-22: replace the storage mutation gate with direct safety
tests and rewrite CI rather than retrying the qualification collector. This
supersedes the earlier storage-specific requirement for full mutation campaigns
before every merge. The [later owner testing policy, 2026-11-23](../product/requirements-source.md#owner-testing-policy--2026-11-23)
also replaces the native credential mutation gate with direct safety tests and
makes coverage/mutation scores diagnostic across the application. Coverage
reporting and independent functional, security/reliability and UX/performance
review remain required. No mutation score is claimed by the replacement.

## Execution graph

The single `.github/workflows/ci.yml` runs ordinary PR and main-branch checks.
There is no manual qualification dispatch, private Stryker build, repeated
official/private mutation campaign, nested campaign scheduler or storage receipt.
The four previously retired Storage workflows stay retired. Agent guidance and
tooling validation now share this workflow instead of a duplicate workflow.

Managed jobs restore/build/style/format Windows, macOS and Linux. Linux executes
the explicit unsupported-controller contract; it never supplies Windows/macOS
storage acceptance.

Hosted macOS jobs use the standard `macos-15-intel` image. GitHub repeatedly
cancelled Apple Silicon jobs without assigning a runner on 2026-11-23, explicitly
reporting arm64 capacity constraints. Intel is also a selected product target.
The managed matrix keeps its `macos-15` check/artifact key stable while its
separate runner field selects Intel hardware. Storage/native/volume check names,
commands, test inventories and failure behavior are unchanged by runner selection.
See [GitHub's standard runner table](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
Intel execution must pass on that runner; prior Apple Silicon proof does not
establish Intel execution. Local native desktop and storage evidence remains
bound to its recorded Apple Silicon host. This runner change does not establish
both-architecture installers or release acceptance, nor guarantee queue latency.

Storage has four independent jobs on each supported OS:

| Group | Direct evidence |
| --- | --- |
| inventory | registration, timestamps, inventory projection, ordinary storage |
| integrity | committed schema, constraints, canonical addresses/GUIDs, integer ports |
| recovery | real backup publication/interruption, populated transactional upgrade, refusal and recovery |
| authority | competing locks, OS permissions, retained identities, platform boundaries |

These are test partitions, not mutation shards. Each compiled test class belongs
to exactly one group per supported platform. Every applicable case executes once,
with no sampling, fail-open filters or repeated whole-suite initialization.
`tooling/ci/storage-plan.json` lists the complete class partition and expected
case counts (270 macOS / 279 Windows for the current source). New classes, missing
cases or changed Theory inventories require an explicit plan update. A failed,
skipped, missing, duplicate, wrong-class or incomplete TRX fails validation.
Before execution, VSTest discovers the exact method/Theory display identities
for that partition without running its tests. The raw inventory is retained and
hashed with execution results. Validation compares the discovered and executed
sets and rejects duplicate semantic cases even when their opaque IDs differ.

The jobs independently perform a locked restore and one storage-project build;
they do not transfer executable binaries between runners or operating systems.
NuGet caching uses the existing lock files. Jobs execute their partition through
ordinary `dotnet test --no-build --no-restore`, with live test output, Coverlet,
VSTest blame and a 60-second per-test hang timeout. A four-minute test-step limit
and twelve-minute job limit bound startup and process failures. The limits are
upper bounds, not measured completion promises or speed claims.
The command wrapper also enforces a local process deadline, because a hosted
macOS recovery step remained running beyond its configured GitHub timeout.
Discovery gets 45 seconds and test execution 150 seconds, with an elapsed-time
heartbeat every 15 seconds. Timeout or interruption terminates only the owned
process tree and retains scratch; it cannot return success. Termination waits
are bounded too. On POSIX, remaining descendants in the owned process group are
also stopped after normal command exit, so inherited output cannot keep a
completed step open. Other wrapper commands default to a 180-second local limit.
These are direct command limits, with no campaign scheduler or automatic retry.

Disk-full runs as a separate eight-minute job on each OS. It retains the reviewed
64 MiB disposable-image fixture and its 128 MiB write bound, image/drive identity
checks, raw executed test and bounded cleanup. Only the fixture was extracted to
`tooling/ci/volume.py`; it imports no qualification or mutation collector. The
gate rechecks the exact passed case and input/raw hashes, never status alone.
The filler writes and flushes 1 MiB blocks, then uses 4 KiB writes for the last
available pages. This preserves actual exhaustion and the write bound while
avoiding thousands of synchronous VHD flushes. The executed case has a 60-second
hang timeout and the test process a 120-second limit. An interruption triggers
bounded termination of the owned test process tree. If successful test completion
is unconfirmed, the helper retains the attached fixture for disposable-runner
teardown rather than detaching a volume that test descendants may still use.

Native macOS authority runs in a separate five-minute job. It compiles the actual
C helper once for 42 real filesystem cases and once for ten explicitly injected
OS-error/metadata cases. LLVM coverage comes only from real filesystem execution.
Injected cases are separate fault-path evidence, not natural OS branch credit.
There are no native mutation variants in this pipeline.

Product commands receive an explicit noncredential environment. Windows temporary
objects live under the invoking user's LOCALAPPDATA; macOS uses short owned
directories beneath `/private/tmp` to respect named-pipe socket path limits.
Checkout credentials are not persisted. Product tests never receive the QA
metadata step's GitHub token, SSH agent or artifact-service credentials.

Windows command scratch stays until disposable-runner teardown because Roslyn
servers may retain open analyzer DLLs after a successful build. Generic scratch
cleanup cannot change a command's successful exit into failure. Actual fixture
detach/verification failures still fail the disk-full job and preserve its files.
Drive enumeration uses the native
[GetLogicalDrives bitmask](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getlogicaldrives),
without starting a shell. Image identity and detach queries use the runner's
PowerShell 7, preserving its nonsecret module path; Microsoft lists Storage as
[natively compatible](https://learn.microsoft.com/en-us/powershell/windows/module-compatibility?view=windowsserver2025-ps).

New Windows scratch is configured with the actual invoking user's SID and owner,
before a product command runs. Python 3.13's mode-0700 directory ACL uses the
OWNER RIGHTS alias (`S-1-3-4`); the production ancestor check deliberately accepts
explicit trusted SIDs instead. The fixture replaces that alias only on its newly
created empty directory, removes inherited grants and logs the resulting ACL.
It never repairs existing storage, changes parent-directory permissions or weakens
production checks. SID/ACL setup failure prevents launching the product command.
See the [upstream implementation](https://github.com/python/cpython/blob/v3.13.7/Modules/posixmodule.c#L5284).

## Automatic gates and failure reports

`storage-safety` downloads only this workflow run's artifacts and independently
validates every platform/group, prerequisite job success, checked-out commit,
tracked-input digest, raw file digest, exact executed cases, native assertions and
real volume proof. It combines coverage by source/point identity across partitions
and operating systems, reporting counts and percentages as diagnostic metrics.
Native coverage counts and percentages are retained too. A low percentage alone
does not fail the gate. Missing, malformed or unbound evidence produces a failing
JSON verdict and a job summary stating the actual evidence failure. Zero branches in the current
interface-only Application module are N/A, never invented 100% coverage.
Inventories must exactly match their artifact directories before any TRX or
coverage is consumed; extra coverage files are errors. Native execution binds
all raw reports, profiles and libraries by digest, and assertions must match the
committed `native-plan.json` identity sets. JSON readers are size bounded and
malformed native coverage still produces the promised FAIL verdict and summary.

Artifacts contain TRX, OpenCover, hang diagnostics and the small machine verdicts;
they do not archive private mutation-tool source/build trees. Upload steps run on
failure too. Runner loss can still prevent an upload; the aggregate then fails
closed for missing evidence. There are no automatic retries.

`qa-evidence` checks the actual same-workflow `storage-safety` outcome and the
existing independent lead-attested role verdicts. A manual storage qualification
receipt is neither requested nor accepted as a substitute. Green direct tests
alone do not establish full-PR independent acceptance, native UI or release safety.
Reviewers use uncovered paths and mutation survivors to identify useful tests.
Acceptance depends on behavioral and security evidence, not a numeric score.
Environmental limitations must be documented with practical alternative proof;
failed tests, omitted cases and unsafe behavior remain failures.

## Local verification

Use the SDK pinned in `global.json`. Build the test project once, then use the
same filters as CI:

```sh
python3 tooling/ci/storage.py filter --group recovery
python3 tooling/ci/command.py -- dotnet test tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj -c Release --no-build --no-restore --filter '<printed filter>' --collect 'XPlat Code Coverage' --settings tooling/quality/storage/coverage.runsettings --blame-hang --blame-hang-timeout 60s --logger trx --results-directory .artifacts/ci/recovery
python3 -m unittest discover -s tooling/ci -p 'test_*.py' -v
```

`record` is a post-execution validator, not a test runner or receipt publisher.
It requires a clean committed checkout and the actual executing platform. Keep
dirty implementation proof distinct from clean CI evidence. Historical mutation
reports and FAIL/UNRESOLVED results remain historical; the policy change does not
turn them into passing mutation scores or relabel their commit heads.

Dependency addition: download-artifact v5 is pinned at
`634f93cb2916e3fdff6788551b99b062d0335ce0`, verified against the official upstream
tag. It runs only in the Ubuntu artifact-aggregation job, downloading from the
same run with the existing Actions runtime identity. Other Action and SDK/package
pins are preserved. See the [upstream action](https://github.com/actions/download-artifact/tree/634f93cb2916e3fdff6788551b99b062d0335ce0).
