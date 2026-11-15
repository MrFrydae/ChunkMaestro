# Refusal guidance and schema reference contracts (issue 3, PR 6)

Implementation test evidence for Q149/Q153/Q163–164/Q189–195 and the storage/testing
sections of the orchestration directive. This is not independent QA or current
cross-platform qualification. The 90% line / 85% branch / 80% mutation gates remain.

## Scope and behavior

Parent: `3bc99c26edc3448b3ffdc474ea953bedc92dddf9`.
Test implementation: `d296ec800549d75610cacd3f42f49c8d41a8e837`, branch
`test/3-refusal-guidance-contracts`. Production, migrations, models, locks,
dependencies and configuration are byte-preserved. Only disposable synthetic
fixtures were used; no live hosts, worlds, credentials or user data were used.

Existing registration helpers now require nonblank, control-free diagnostics,
stable InvalidRegistration categorization, no nested raw error or backup claim,
and no fixture-path leakage. Managed cases additionally check synthetic secret and
password markers. Existing controller contention, newer history/version, failed
migration and corruption cases require nonblank sanitized diagnostics. Existing
atomic refusal, controller release, database/backup preservation and recovery
assertions remain. The abandoned migration lock must remain present; a refusal
must not certify a backup. These assertions do not depend on exact message text.

The new SchemaReferenceContractTests case migrates a real in-memory SQLite
database, verifies the valid schema, inserts a host with a genuinely missing
fleet parent with foreign-key enforcement disabled, and calls
SupportedSchema.VerifyAsync directly. SQLite integrity_check remains `ok`.
The validator must return CorruptDatabase with sanitized nonblank guidance twice,
leave the orphan untouched, and create no parent or backup claim. After explicit
fixture-owned deletion of the orphan, validation must succeed. This exercises the
independent validator without LocalFleetStore's earlier integrity check masking it.

## Validation

Environment: macOS 27.2.1 arm64; private official SDK 10.0.404 at
`/Users/derek/.local/share/chunkmaestro/dotnet/dotnet` (runtime 10.0.15).
Every third-party launch used replay_ci.child_environment's explicit noncredential
allowlist, absolute SDK/DOTNET_ROOT/PATH, IPv6 disabled, no MSBuild node reuse and
telemetry opt-out. Launch scripts and full logs are retained externally.

Commands, through `/tmp/pr6-refusal-run.py`:

- `dotnet restore tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj --locked-mode`: passed.
- `dotnet test tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj -c Release --no-restore --logger trx --results-directory /private/tmp/pr6-refusal-tests`: **159 passed, 0 failed, 0 skipped**.
- `python3 tooling/build/build.py verify`: locked restore, Release build,
  syntax/style guard, generated-inclusive format verification and tests passed;
  **159 passed, 0 failed, 0 skipped**, build zero warnings/errors.

The first no-restore test attempt stopped with NETSDK1004 because this checkout had
no assets file. The locked restore above resolved that prerequisite. No test
failure or skipped test was relabeled as a pass. TRX identity comparison retains
all 158 parent cases and adds exactly one case. No existing case was renamed or
removed. The inherited 35 storage Python passes are parent evidence, not rerun here.

Local SSH signing remains configured and the implementation commit has an SSH
signature. `git verify-commit HEAD` cannot establish signer trust locally because
`gpg.ssh.allowedSignersFile` is unavailable. No signing/trust settings were changed.

## Diagnostic boundary

One focused private diagnostic is retained at
`/private/tmp/pr6-refusal-diagnostic-u6h069tc`. Its binding records the exact test
commit, all tracked input hashes, OS, thresholds, provenance hash and upstream
character-span selection masks. The LocalFleetStore selection targets refusal
messages; SupportedSchema is selected in full. Other-source filtered rows remain
in the complete raw report. This selection is diagnostic only, never a gate
exclusion or a full-module/current combined score.

The local private tool was reopened against
`/private/tmp/pr6-3bc99c2-measure-a2x304v_/private/provenance.json`:
15 local compiler hashes, 370 output/dependency hashes, 869 post-build source
hashes, official archive contents, current builder, SDK and sole factory deletion
matched. No hosted compiler path was used. Provenance SHA256:
`b01f5c09603b20807b567d9d3de6239f93fd1a8f00b69f0acdf80825ea5a6cdf`.
Advisories in `/tmp/pr6-private-tool-advisory-review.md` remain applicable and
unsuppressed. HTML was not opened; no dashboard/private feed was used. Upstream
instrumentation was used without handwritten mutation/repair substitutes.

The parent's full measurement remains at
`/private/tmp/pr6-3bc99c2-measure-a2x304v_`: official 376 Killed, private 494 Killed,
actual union 495/774. Those are prior-head measurements. Historical Windows results
cannot qualify this head, and Timeout is never credited as a kill. No equivalence
waiver, new exclusion, current Windows/Intel evidence or final score is claimed.
All three independent storage QA roles and current qualification remain lead-owned.

### Focused outcome and retained limitation

The single private command completed in 66.5 seconds, exit 0, without a command
timeout. Its raw report SHA256 is
`d51efa0a11aff21ef77a9a0039887b6845b234b65e88e9df8751d13177ef8aee`.
It reports **49 Killed, 7 Survived, 1 Timeout, 26 CompileError, 614 Ignored**
(697 reported rows). All reported mutant rows bind to unchanged source bytes and
UTF16 spans; every Killed row has upstream killedBy attribution. Complete raw
JSON, HTML, logs, original IDs/statuses and filtered rows remain intact. The log
says 800 candidates were generated; two filtered migration files contain the
upstream `File ignored by mutate filter` source marker and zero reported mutants.
The ordinary strict catalogue parser therefore refused those source bytes. Its
failure is retained; no parser or raw report was repaired. `audited-rows.json`
separately preserves every reported mutant row and `audit-summary.json` records
the zero-mutant placeholders and the refusal. This is not a complete-module
catalogue or qualification artifact.

Comparison with `/tmp/pr6-3bc99c2-remaining-historical.json` and the parent's
source-bound private report identifies **12 historical survivors now Killed**:
LocalFleetStore IDs 130, 148, 161, 177, 196, 215, 246, 388; SupportedSchema IDs
781, 783, 784, 785. IDs happen to match this diagnostic; identity comparison uses
path, unchanged source, mutator, replacement and original location. Both original
historical rows and new observations are retained in
`newly-killed-historical.json`; historical statuses were not overwritten.

Baseline and restoration each passed **159 tests, 0 failed, 0 skipped**, with
identical identities and unchanged tracked inputs. `commands.jsonl` records all
commands with 1000-second finite deadlines, environment allowlisting and full log
hashes. `restoration.json` records preservation. External audit script and artifact
hash inventory are retained alongside them. No full-module run or retry occurred.

Unresolved selected rows: LocalFleetStore 176 Timeout, 208/211/212/213 Survived;
SupportedSchema 771/793/796 Survived. Timeout receives no kill credit. Guidance
wording substitutions can remain observable-equivalent under these broad contracts;
that is no equivalence waiver. SupportedSchema 793 is the endpoint-host mismatch
message; its existing owning test is DatabaseConstraintContractTests, outside this
worker's permitted edit files. No duplicate public API case or out-of-scope edit
was added. No genuine production defect was established. Windows, Intel, current
line/branch coverage, full current mutation qualification, dynamic third-party
assembly loading and independent QA remain unmeasured by this work.
