# Issue 3 storage safety qualification

Current owner decision: hosted storage workflows are retired. Use
[local storage qualification](local-storage-qualification.md). Scheduling and
workflow examples below are historical; do not dispatch them.


Current scheduling and hang diagnostics: [Storage CI](storage-ci.md). Full
qualification is explicitly requested for a candidate; ordinary pushes run coverage/tests.

This is implementation evidence, not independent QA or merge acceptance. Q149,
Q153, Q163–164, Q189–195 and directive §13 govern. Historical functional,
security/reliability and UX/performance verdicts against 9122a15 remain FAIL.

The qualification branch integrates production correction 06c91add (locally
cherry-picked as 70e952f). It changes no production code itself. At that historical worker baseline only InitialFleet existed. The current
forward constraint migration and its populated-version evidence are described
below; no predecessor or cosmetic version was invented.

## Commands and artifacts

Select the private SDK with CHUNKMAESTRO_DOTNET. Run these sequentially; collectors
and mutation tools modify ignored build outputs and must not run concurrently.
Use a fresh, nonexistent output directory per run:

```sh
python3 tooling/build/build.py verify
python3 -m unittest discover -s tooling/quality/storage -p 'test_*.py' -v
python3 tooling/quality/storage/qualify.py run .artifacts/storage/macos
# Explicit local owner-verified private collection.
python3 tooling/quality/storage/replay_ci.py .artifacts/storage/macos --local
python3 tooling/quality/storage/volume.py .artifacts/storage/macos/volume
python3 tooling/quality/storage/native.py .artifacts/storage/macos/native
```

The initial collector measures coverage and Domain/Application mutations. The
trusted replay collector supplies the required official/private Persistence pair;
initial collection alone cannot qualify storage. Windows uses the same commands,
with a separate output directory.
Native helper measurement is macOS only; the helper is not compiled on Windows.
Combine the actual downloaded Windows/macOS artifacts at the SAME checkout head:

```sh
python3 tooling/quality/storage/qualify.py gate .artifacts/storage/windows .artifacts/storage/macos --output .artifacts/storage/gate.json
```

The storage-safety workflow executes both platforms, preserves reports even
on failures and combines evidence in a separate job. Existing ci.yml is untouched.
No protection settings or external checks were changed. Linux remains the existing
single unsupported-controller contract; it is not storage runtime evidence.

Each collection preserves command arguments/exit codes/logs, exact git head,
clean-tree flag, runtime source hashes, TRX and OpenCover attachments, per-module
Stryker JSON/HTML, configuration and mutation outcomes. Missing evidence, dirty
collection, stale sources/heads, missing modules and threshold failures fail.
TRX can copy a collector attachment: only byte-identical duplicates are accepted.
Artifacts are ignored local outputs; CI uploads them. Do not run the gate against
old platform evidence after changing source or integrating another commit.

## Measured scope and denominators

Coverlet includes Domain, Application and Persistence, including handwritten
StorageLocation/StorageIdentity/SupportedSchema, DbContext mapping, exceptions,
all orchestration and migration Up/Down methods. Design factory is retained.
Only EF `Migrations/*.Designer.cs` and `*ModelSnapshot.cs` are excluded: generated
declarative model metadata duplicates the committed model and migration schema.
The production schema verifier and model-consistency tests remain included.
No production failure path is excluded to improve a score.

The gate unions visited sequence lines and individual IL branch paths from actual
Windows and macOS reports, using normalized source paths and method/offset/path
identity. It does not add percentages or infer unexecuted platform branches.
Each nonempty module denominator requires 90% lines and 85% branches; no branches
is N/A, never fabricated 100%. Missing instrumented lines fails investigation.

Stryker runs all three modules through MutationTests.csproj, which links the
actual test sources and directly references all three modules because Stryker
cannot select transitive references. Its separate lock contains the same pinned
test dependency graph. There are no ignore-methods, ignore-mutations, survivor
baselines or handwritten file exclusions. Applicable mutations require 80%.
The combined gate credits a mutation only when actually Killed on at least one
platform. Survived, NoCoverage, Timeout and tool-generated Ignored get no credit;
only consistently CompileError mutations leave the applicable denominator.
This conservative score may be lower than Stryker's displayed score. Application
currently has executable exception construction/accessors but no generated
mutants: that measured empty mutation set is N/A, never a claimed 100% score.

The 48-line native authority helper is safety critical. .NET metrics say nothing
about C. native.py compiles the exact unedited source into an instrumented copy
of the ignored test-output dylib, executes real storage tests, merges raw profiles
and exports LLVM coverage JSON. It restores the original dylib in finally.
Native lines and branches have their own 90/85 gate; Stryker.NET cannot mutate C.
The aggregate gate now validates exact-head compiled native mutation evidence
against the checked-in catalogue, source hashes and failed-assertion records.
No native exclusion or implicit waiver is granted.

## Actual failure cases and limits

StorageSafetyAcceptanceTests adds five normal Windows/macOS cases:

- Populated current supported schema backup, separate secure copy recovery,
  exact fleet/host/instance IDs, relationships, endpoint, service and unknown state.
- Bounded SQLite page-limit FULL during actual EF initial migration: real DDL,
  rollback, consistent original and completed pre-migration backup.
- Process kill after actual EF CREATE TABLE executes inside its transaction,
  before commit: parent observes a post-execution signal, kills its own child
  tree and verifies reopened rollback/integrity and completed backup integrity.
- Bounded SQLite page-limit FULL during actual native backup: source ID/payload
  preserved and subsequent completed backup verified.
- Process kill after one actual sqlite3_backup_step with remaining pages:
  committed WAL bytes unchanged, source ID/payload preserved, incomplete candidate
  distinct from previously verified completed copy and completed data intact.

That historical standalone case tested the SQLite backup engine, and did not
prove interruption of the then-one-shot production publication. The original
required correction was: use an
internal incremental backup driver, expose a test observer after a successful
native step, keep candidates under an unmistakable partial name, verify then
atomically publish the completed name. Pause the actual driver in a child and
kill before publication; assert incomplete files cannot be mistaken for completed
recovery points. That correction is now integrated as described below; historical reports are unchanged.

volume.py creates ONLY a new unique private 64 MiB HFS+ disk image on macOS, or
new fixed 64 MiB VHD on Windows via diskpart. It never selects a physical disk.
It builds first, mounts its own image, invokes the separate volume test suite,
and detaches only its own generated image. Writes are capped at 128 MiB. The test
fills the filesystem, verifies backup refusal, invokes actual EF MigrateAsync,
then releases only its filler and verifies original/backup IDs and integrity plus
absence of partially committed fleet schema. Full-volume journal creation may
report SQLite CANTOPEN (14) rather than FULL (13); both are recorded accurately.
This is real filesystem exhaustion, separate from max_page_count injection.
macOS detached fixtures are removed; Windows retains its bounded fixture path for
inspection rather than recursively deleting a potentially mounted directory.
A failed detach fails evidence and retains its exact owned fixture for recovery.
On macOS, cleanup reads the disk-image inventory without logging unrelated host
paths and matches the exact generated image, device and sole fixture mount. A
normal detach returning resource-busy (16) permits one forced detach only after
rechecking that same identity. Changed/ambiguous attachments, other errors and
uncertain final state fail evidence and preserve the fixture. A real disposable
image probe with an open file reproduced the busy return and verified this
bounded cleanup; synthetic refusal cases cover changed and unrelated identities.
Requires hdiutil/Xcode CLI or an elevated Windows runner with diskpart/VHD support;
unsupported setup fails, never skips. No Docker, live hosts or user data is used.

Initial local integrated execution: 43 normal tests pass, no skips; actual macOS
full-volume case passes. Native execution measured 37/37 lines (100%) but 39/56
branches (69.64%): FAIL. Native mutation is unqualified. Early 9122a15 mutation
experiments showed low scores; those are baseline diagnostics, superseded by the
final clean integrated-head run artifacts. Original concurrent diagnostic output
is not qualification evidence. Windows has not been locally executed. The owner
reported its corefix hosted run failing Windows; do not infer success here.

At the original worker baseline populated upgrade and production publication
interruption were missing. The justified migration and production driver below
now supply cases that the gate requires in each platform TRX. For future versions,
start from populated InitialFleet, validate backup, upgrade all IDs/data, interrupt
inside that real migration, and restore the verified original separately. A
synthetic second migration would not satisfy this requirement.

## Tool provenance

Pinned coverlet.collector **10.1.0**, PrivateAssets=all, is the VSTest collector;
[NuGet](https://www.nuget.org/packages/coverlet.collector/10.1.0) documents net8+
compatibility. SDK 10.0.404 and Test SDK 18.12.0 execute it on net10.0 here.
[Coverlet VSTest integration](https://github.com/coverlet-coverage/coverlet/blob/master/Documentation/VSTestIntegration.md)
documents runsettings and OpenCover output. Only the affected test lock gains this
collector; locked restore is preserved.

Pinned dotnet-stryker **5.0.0** runs natively on net10.0 (installed tool payload
`tools/net10.0/any/Stryker.CLI.dll`). The tool reports newer 5.1.0 is available;
5.0.0 is a deliberate reproducible tested pin, not a latest-version claim.
[Official configuration](https://stryker-mutator.io/docs/stryker-net/configuration/)
and installed `--help` establish project selection, JSON reports and break=80.
Output is a CLI option in 5.0.0, not a JSON configuration key; the runner uses it.

Native instrumentation uses the host Xcode clang/LLVM tools, with compiler version
in commands.log and raw profiles retained. These are OS toolchain provenance,
not a portable pinned package. [LLVM's official guide](https://www.llvm.org/docs/CommandGuide/llvm-cov.html)
documents instrumentation flags, dynamic-library profiling and JSON export.

No green runtime suite, collector result or this document grants merge acceptance.
All applicable thresholds, unresolved safety cases and fresh independent QA gates
must actually pass at the final integrated head.

## Lead integration checkpoint

The qualification tooling is integrated with the production fixes through 7b18f1c
and rebased onto the merged project skills/C# conventions. Checked-in generated
namespaces are normalized without changing migration operations or model metadata.
The measurements above remain historical implementation evidence at their stated
heads; the new integrated head requires fresh Windows/macOS collection. The safety
gate remains FAIL until the measured thresholds and real recovery cases pass.

## Windows fixture corrections after 0040146

The hosted managed and measurement jobs both failed the WAL interruption fixture
at `File.ReadAllBytesAsync`: its default sharing mode conflicts with SQLite's
open writer on Windows. The fixture now reads using an explicit shared-read handle
(`ReadWrite | Delete`) while retaining the before/after byte comparison and the
actual process-kill checks. This does not weaken production locking.

The full-volume job mounted its new image successfully, but the fixture reached
it through a directory reparse point, correctly rejected by production ancestor
validation. The runner now obtains the newly mounted volume GUID through
[`mountvol /L`](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mountvol)
and passes that volume root to the test. Cleanup queries only its exact new image
with [`Get-DiskImage`](https://learn.microsoft.com/en-us/powershell/module/storage/get-diskimage?view=windowsserver2025-ps)
before detaching, then verifies detachment. An already detached image is distinguished
from a query/detachment failure; failures still fail evidence and preserve the image.
These Windows changes require hosted confirmation. No production permission or
path policy was relaxed. Safety collection and gate checkout now select the PR
head explicitly so artifact heads describe the actual reviewed commit rather than
a synthetic GitHub merge commit.

## Integrated production backup publication

The signed backup-worker delivery caa52cb is integrated with the Windows fixture
corrections. `LocalFleetStore.CreateBackupAsync` now uses the production
`IncrementalBackupDriver`: actual native steps copy 32 pages, cancellation and
step/finish errors stop publication, and the destination remains under an explicit
`.backup-*.partial` name. Integrity/FK validation, close and flush precede atomic
publication of a completed name. Failures are sanitized; existing databases and
completed backups are preserved. Abrupt power-loss/directory-sync durability is
not established by an atomic rename claim.

Seven new tests cover real step interruption, cancellation, caller responsiveness,
native refusal without retry, destination failures and WAL-consistent public
recovery of 4,096 rows. The crash case identifies and kills the actual test worker
after a successful production step with pages remaining. It checks that no new
completed name appeared and that the original DB/WAL/prior completed bytes survive;
SQLite shared-memory read marks legitimately change, so only its preservation and
size are asserted. The full-volume fixture now expects the production sanitized
StorageUnavailable category while retaining real volume exhaustion and recovery
assertions. Fresh Windows execution, combined coverage/mutation and independent
QA are still required; the worker's targeted coverage is diagnostic, below the
required thresholds. This section does not declare aggregate safety acceptance.

## Integrated endpoint migration and native gate

The forward EnforceEndpointInvariants migration enforces the existing application
rules at SQLite: port 1–65535 and edition Java/Bedrock (0/1). It preserves the
committed InitialFleet migration and uses one transactional leaf-table rebuild;
future incoming foreign keys to Endpoints require revisiting that assumption.
Six cases prove populated old-version upgrade and compatible backup recovery,
invalid-row refusal without repair, and rollback after killing the actual worker
following the executed transactional DROP TABLE. Existing startup/model tests
now compare the full migration history and test downgrade refusal from the latest
version. Combined normal macOS verification passes 68 tests, zero skips.

Native qualification now keeps the production/test dylib untouched and compiles
isolated helper copies. The worker's 39 real cases measured 47/56 branches and
correctly failed. Three added read-only probes of existing root-owned /private/tmp
and /private/etc exercise actual sticky/ownership decisions without privilege,
ownership or permission changes. Local diagnostic measurement is 42 real cases,
37/37 lines, 51/56 branches (91.07%), plus separately labeled 10 injected cases.
Compiled mutation results are 24/28 killed (85.71%); four survivors remain in the
denominator. No injected branch is counted toward real filesystem coverage.
Fresh clean-head collection is required after integration; these diagnostics are
not Windows/Intel or overall storage qualification.

The gate now checks the actual real-filesystem LLVM report and compiled mutation
catalogue/artifacts, source/runner/resource identity, preservation and clean head.
It also requires passed production-backup crash/recovery and populated-upgrade
crash/recovery cases in each platform's TRX. Missing/stale/injected substitute or
incomplete evidence fails. Six parser regressions include synthetic contract
fixtures only; those fixtures never supply runtime acceptance evidence. The
managed coverage/mutation thresholds, same-head Windows/macOS collection,
full-volume cases and all independent QA roles remain mandatory.

## Follow-up Windows owner/path failures

Review at 74f139e correctly found that the previous volume-GUID correction still
violated the production path contract: Prepare rejects the extended namespace
as well as UNC paths. The fixture now enumerates occupied letters, chooses a free
normal drive path and asks DiskPart to assign it to only the newly created VHD.
An occupied-letter race fails assignment; no existing mapping is removed. The
GUID from mountvol is diagnostic, not the path given to storage. See Microsoft's
[assign contract](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/assign).
Cleanup queries and dismounts the exact image in one PowerShell process using
[Dismount-DiskImage](https://learn.microsoft.com/en-us/powershell/module/storage/dismount-diskimage?view=windowsserver2025-ps),
then verifies attachment state; uncertainty still fails and retains the image.

The production-step crash test also reached recovery but was refused by the
Windows owner check on a leftover backup file. Elevated SQLite journal creation
uses the default owner, so the driver now reserves its new rollback journal with
explicit user ownership before the first native step. The crash test checks every
partial candidate/sidecar's scope while the actual worker is paused. No existing
file is re-owned and no production ACL/path rejection is weakened. Windows
confirmation remains required. Drive-letter parser tests use synthetic inventories
only and are not Windows runtime evidence.

## Managed behavioral regression integration

The signed worker delivery 8fe1521 adds 33 registration, cancellation, disposal,
corruption and permission-denial cases. Integrated with the endpoint migration
and journal fix, normal local verification passes 101 tests, zero skips. Windows
managed CI passed all 68 tests at the preceding b6b3a39 head; it does not certify
the newly added cases. The worker's before/after measurements are on its stated
74f139e-based source, not the later integrated schema/driver.

The aggregate mutation key now normalizes absolute checkout paths just like line
coverage, so one source mutation is unioned across Windows/macOS. Previously the
absolute paths could incorrectly count the same mutation twice. A regression
checks cross-platform identity and distinct replacements. This fixes the union
calculation without changing thresholds or survivor treatment.

The worker's 162 Ignored mutants all report Stryker's automatic "Removed by block
already covered filter" reason. They remain uncredited, not waived. With the
worker's 747 applicable strict denominator, those skipped executions alone limit
the possible score to 585/747 (78.31%) even if every other mutation is killed.
Further work must investigate actual execution of those mutations; neither a
score redefinition nor relabeling Ignored as Killed is acceptance evidence. The
33 added tests killed 38 additional mutants, but actual combined qualification
and independent QA remain incomplete.

## Windows live-permission fixture restoration

Hosted Windows managed validation at 250ec1b executed 101 tests: 95 passed and
six live-permission expansion cases failed at the subsequent recovery read.
Production correctly kept rejecting the expanded ACL. The fixture had attempted
to persist an unmodified FileSecurity snapshot; that snapshot does not mark its
DACL as changed. The test now captures descriptor bytes, explicitly loads the
access section into a fresh FileSecurity object, persists it, and compares the
resulting access SDDL with the captured original before testing recovery. This
only restores the test's own altered fixture; no production ACL repair or relaxed
permission validation is introduced. Hosted Windows confirmation remains required.

## Windows ACL metadata comparison and full-volume confirmation

At 09c273c Windows did restore the original owner-only access entries, but the
new exact-SDDL assertion failed in all six cases: expected `D:P(A;;FA;;;LA)` and
actual `D:PAI(A;;FA;;;LA)`. The extra auto-inherited metadata flag is distinct
from the ACL protection bit and access entries. The fixture now compares owner,
DACL protection and exact DACL binary ACE content, rather than the complete SDDL
header. It retains the production read/write recovery assertions. The change
neither accepts additional principals/rights nor relaxes production validation.
Local normal verification passes 101 tests, zero skips; Windows confirmation of
this assertion correction remains required.

The actual 09c273c Windows full-volume artifact is PASS: real 64 MiB VHD exhaustion,
backup/migration refusal, recovery and verified detach succeeded via the normal
Z: drive root. Evidence is storage-windows-2025 artifact14144011617 from run
43669845281. The measurement job failed earlier on the six ACL assertions, so
this individual fixture pass is not overall qualification or current-head proof.

## Migration interruption handshake correction

Windows measurement at 35a0aaf passed 100 tests and failed the populated migration
crash fixture: the parent observed the marker's existence before its PID line was
written, causing IndexOutOfRange and premature fixture cleanup with a live worker.
The child now writes a private pending marker and atomically publishes it only
when complete, then sends its actual worker PID through a named-pipe handshake
after the executed transactional DROP and data assertions. The parent reads the
complete fixed-size PID message, kills and waits for that worker, and also kills/
waits it in failure cleanup before disposing fixture files. It no longer parses
partially written marker content or relies on launcher exit as worker completion.
The real Windows 64 MiB full-volume case passed again at 35a0aaf; the measurement
failure was the interruption fixture, not a mutation-score result. Updated
Windows handshake execution remains pending.

## Actual product replay and model-test isolation

Actual frozen 4f263fc macOS arm64 product collection completed in 26.2 minutes.
Coverage, clean baseline and restoration each passed 101 tests, zero skips;
tracked inputs and official report bytes remained unchanged. Persistence coverage
was 617/732 lines and 166/272 branches. Official mutations were 309/765 credited
under strict accounting. Real private execution added 98 attributed kills for
407/765 (53.20%), still below 80%. Both mutation commands returned threshold
failure. There are 165 surviving, 181 uncovered and 12 timeout outcomes retained.
This is macOS-only measurement, not a Windows union or independent QA verdict.
Full evidence: /tmp/pr6-product-mutation-4f263fc-delivery.json and .md.

All 42 FleetDbContext mutations survived in that full run. The existing model/
snapshot compatibility test reused EF's process-wide service-provider model cache.
It now builds that test context with EnableServiceProviderCaching(false), using
EF's [documented test-isolation option](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.dbcontextoptionsbuilder.enableserviceprovidercaching?view=efcore-10.0).
Production caching/performance is unchanged. A focused diagnostic run on identical
production file bytes killed 27 previously surviving mutants; 15 remain surviving,
without waivers. That diagnostic temporarily selected only FleetDbContext; its
other-file exclusions are not applied to repository qualification or counted as
a whole-module score. Evidence: /tmp/pr6-model-cache-diagnostic.json and
/tmp/pr6-model-fresh-cache-focused. The compatibility test also now proves direct
downgrade refusal from every committed version, not only the latest. Full combined
platform measurement and fresh independent QA remain required after these changes.
