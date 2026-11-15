# Issue 3 storage safety qualification

This is implementation evidence, not independent QA or merge acceptance. Q149,
Q153, Q163–164, Q189–195 and directive §13 govern. Historical functional,
security/reliability and UX/performance verdicts against 9122a15 remain FAIL.

The qualification branch integrates production correction 06c91add (locally
cherry-picked as 70e952f). It changes no production code itself. Only initial
migration 20261114034833_InitialFleet exists. No supported predecessor, v0,
cosmetic migration, or populated version-to-version upgrade is invented.

## Commands and artifacts

Select the private SDK with CHUNKMAESTRO_DOTNET. Run these sequentially; collectors
and mutation tools modify ignored build outputs and must not run concurrently.
Use a fresh, nonexistent output directory per run:

```sh
python3 tooling/build/build.py verify
python3 -m unittest discover -s tooling/quality/storage -p 'test_*.py' -v
python3 tooling/quality/storage/qualify.py run .artifacts/storage/macos
python3 tooling/quality/storage/volume.py .artifacts/storage/macos/volume
python3 tooling/quality/storage/native.py .artifacts/storage/macos/native
```

Windows uses the same run and volume commands, with a separate output directory.
Native helper measurement is macOS only; the helper is not compiled on Windows.
Combine the actual downloaded Windows/macOS artifacts at the SAME checkout head:

```sh
python3 tooling/quality/storage/qualify.py gate .artifacts/storage/windows .artifacts/storage/macos --output .artifacts/storage/gate.json
```

The new storage-safety workflow executes both platforms, preserves reports even
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
The aggregate gate explicitly fails the unqualified native mutation requirement.
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

The last case tests the SQLite backup engine used by production. It does NOT
claim interruption of LocalFleetStore.CreateBackupAsync's one-shot publication.
That API has no incremental progress boundary. Required core hook/change: use an
internal incremental backup driver, expose a test observer after a successful
native step, keep candidates under an unmistakable partial name, verify then
atomically publish the completed name. Pause the actual driver in a child and
kill before publication; assert incomplete files cannot be mistaken for completed
recovery points. Core ownership is respected; no such production change is made.

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
Requires hdiutil/Xcode CLI or an elevated Windows runner with diskpart/VHD support;
unsupported setup fails, never skips. No Docker, live hosts or user data is used.

Initial local integrated execution: 43 normal tests pass, no skips; actual macOS
full-volume case passes. Native execution measured 37/37 lines (100%) but 39/56
branches (69.64%): FAIL. Native mutation is unqualified. Early 9122a15 mutation
experiments showed low scores; those are baseline diagnostics, superseded by the
final clean integrated-head run artifacts. Original concurrent diagnostic output
is not qualification evidence. Windows has not been locally executed. The owner
reported its corefix hosted run failing Windows; do not infer success here.

The aggregate also explicitly fails pending populated supported-version upgrade
and production backup-publication interruption. There is no legitimate populated
older version fixture yet. When an independently justified real migration exists,
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
