# Bounded Windows two-cell diagnostic (issue 3 / PR 6, Q190–195)

Current owner decision: hosted storage workflows are retired. Use
[local storage qualification](local-storage-qualification.md). Scheduling and
workflow examples below are historical; do not dispatch them.


This is a new version 1 diagnostic contract, separate from source-only `shard_ci`
and standalone `test_groups.validate`. Neither existing validator is relaxed.
There is no full mutation score or qualification PASS. All candidate manifests
carry `qualification: NOT_EVALUATED`. Both expected cells must validate before
`PARTIAL_DIAGNOSTIC` accounting; missing, failed or command-timed-out cells are
`UNRESOLVED`. Mutant Timeout remains eligible and receives no kill credit.

Full current-head Windows/macOS qualification remains mandatory: 90% line,
85% branch, 80% **actual Killed**, original fixed denominator, native, volume,
populated recovery and all three independent QA roles. This patch is neither
independent QA nor authorization to run a product mutation campaign.

## Architecture and API

`tooling/quality/storage/test_cell_ci.py` provides:

- `prepare OUTPUT`: owner-authenticated Windows only. Calls unchanged
  `shard_ci.prepare(OUTPUT/source, 16)` with its 420-second command budget,
  180-second official planning index, full passing runtime/coverage evidence and
  fresh pinned private tool. The sealed source bundle is never edited. New plans,
  configs and `cell-bundle.json` are siblings outside it. Source bundle hash,
  canonical plan and second exact inventory seal bind them together.
- `make_plan(bundle, catalogue, config, baseline, root)`: freezes two exhaustive
  duration-weighted method groups through `test_groups.plan(..., 2, True)`, with
  atomic Theory rows and full raw TRX digest/cases. Independently derives a
  canonical `shards.plan(..., target=10)` from the complete original official
  catalogue. Chooses the smallest executable region of at most ten owners wholly
  inside `LocalFleetStore.OpenWorkerAsync`, tie-breaking by span then region ID.
  Explicit source anchors delimit this startup/IO method; unsupported anchors,
  overlapping spans or absence of a suitable region deny without fallback.
  Original full source/test unions, all catalogue identities, CompileError mask,
  UTF-16 span, rationale, owners, plan hashes and workspace inputs remain frozen.
- `configuration(plan, group)`: baseline config plus only exact FQN test filter
  and canonical region mutate patterns, with the additional safety requirement
  `break-on-initial-test-failure: true`. Patterns start with `**/*.cs{0..1}` and
  original EF exclusions, then positive selected region and negative child
  exclusions. This keeps every original file/catalogue identity in each report.
  Execution retains pinned default `perTest`; `off` is isolated to source index.
  Each cell uses one inner runner for the bounded initial-event protocol; the
  two independent workers still have matrix concurrency two.
- `probe BUNDLE OUTPUT --group 0|1`: reopens both seals, full raw TRX, original
  report, command ledgers/configs and SDK/compiler/source/dependency/tool evidence
  using existing trusted provenance helpers before private execution. Runs locked
  restore and fresh unfiltered Release clean/nonincremental build/test baseline.
  Whole workspace inputs are rechecked before execution. Executes one private
  cell, then attempts full clean/build/test restoration even after baseline or
  private failure. Reuses the parent 59e verified backup-retention helper and
  requires complete runtime/native bytes **and exact membership** restored.
  Private execution uses at most 180 seconds within a shared 480-second worker
  command budget, reserving 120 seconds for restoration; controls individually
  cap at 180 seconds. Existing process-tree termination, heartbeat and raw logs
  are reused. There is one attempt and no blind retry or timeout credit.
- `validate_cell(folder, plan, bundle, context, root)`: read-only raw evidence
  validation. Reopens exact eight-command ledger, logs, complete baseline/restored
  TRX, exact commands/options/cwd, config, runtime inventories, retained backup
  bytes, full report identities/source bytes/CompileError mask and provenance
  bindings. Successful exit must be integer 0/2, agree with raw Stryker threshold
  semantics, and remain within the command bound. Raw threshold consistency may
  count Timeout as Stryker does; diagnostic kill accounting does not. Only
  `cell.json` is excluded from its own inventory; even an unexpected root
  `bundle.json` is detected. The private entry point must be the exact prepared
  bundle path relative to the producer workspace, rather than a matching suffix.
  The pre-cleanup restored inventory is retained separately from the final one.
- `account BUNDLE OUTPUT CELL0 CELL1`: revalidates preparation and both cell
  contracts against current authenticated context. Requires group IDs exactly
  `[0, 1]`. Emits selected owner union, unique attributed actual kills,
  uncredited mutant timeouts, and separate global fixed denominator. It emits no
  percentage or full-source acceptance. CLI errors write explicit `UNRESOLVED`
  `outcome.json`, including after failed dependencies or missing downloads.
- `summary OUTPUT`: prints available manifests, error/failure phase, restoration
  error and raw log directory, and appends human text to GitHub's step summary.
  It writes nothing inside any sealed evidence bundle.

`source.validate_bundle` remains the authority for exact owner, repository,
head, platform/architecture, workflow run/attempt, SDK executable/compiler,
private archive/sole factory deletion, generated reporter, dependencies and
artifact member hashes. No historical private binary is executed or rebound.
The new layer additionally requires explicit diagnostic label or dispatch.
Self-consistent JSON alone cannot substitute for raw evidence plus authenticated
same-run GitHub artifact transport. This is not a standalone hostile-evidence
signature system; copied arbitrary local bundles are not trusted.

## Selected-test representation and refusal

No extra selected-filter Dotnet test commands are added. Full baseline and full
restoration execute all cases. `derived_group_view` is explicitly **DERIVED** from
those full TRX results and binds their raw digests and selected cases. It is not
separate execution evidence. The standalone full-scope group validator requires
selected TRX and is deliberately never called or fed fabricated TRX here.

The new contract supports only the pinned VsTest representation: raw report
selected method definitions must have precisely the planned case multiplicities,
with byte-bound test sources and every `killedBy` ID within those definitions.
The exact execution configuration must set `break-on-initial-test-failure` to
true; missing or false values are refused. This option alone does not reject
skipped or incomplete initial outcomes. The collector now additionally requires
affirmative per-case evidence from the existing VSTest diagnostic file, with
`--diag --log-to-file --verbosity trace` and one inner runner. Its source-bound
initial-phase window must contain one matching assembly/filter/session, the
exact selected FQN/display set with every outcome Passed, consistent cumulative
statistics, and completion without cancel/abort/error or unfinished cases.
Missing, partial, skipped, duplicate, ambiguous or unsupported evidence refuses
both collection and final accounting. Raw logs and reconstructed summary hashes
are retained; a summary cannot replace them. See
[initial event protocol and filtered proof](initial-test-events.md).

The stdout start/coverage/final markers and explicit failure checks remain
additional consistency checks. They never substitute for actual initial outcomes.
Extra/all discovery definitions, differing Theory representation, missing markers or other adapters
are unsupported and yield `UNRESOLVED`. This representation does not provide
per-case initial-run TRX; its exact FQN filter, full-case partition, multiplicity
and affirmative diagnostic-event predicate are the bounded integration contract
requiring independent review. No generic log parser or count-only substitute for
report attribution is accepted.

## Workflow and integration

`.github/workflows/storage-test-cell-pilot.yml` is a new workflow only. It accepts
owner dispatch or owner-added explicit `storage-test-cell-pilot` label on an
owner-authored same-repository PR. No open/synchronize event starts mutation work.
Permissions are contents-read only; existing Action commit pins, no persisted
checkout credentials, exact-head clean checkout and Python/.NET setup are reused.
Concurrency includes head SHA **and request eligibility**, so an unrelated label
cannot cancel a requested run. Prepare, probes and final accounting each have a
10-minute job cap. Two `windows-2025` probes use matrix `[0, 1]`, maximum parallel
2, isolated checkout/output and failure-independent scheduling. Same-run/attempt
artifact names and download actions without a foreign run/token bind transport.
Workers upload only their own results, never a second copy of the giant tool
bundle. Final accounting runs even after failed dependencies and preserves denial.
Queue capacity/concurrent runner admission is not guaranteed by matrix width.

After lead review, integrate this signed local commit onto the intended parent
with `git cherry-pick -S <implementation-commit>` and rerun the tooling checks.
The lead must obtain authorization for **one** bounded hosted pilot, publish the
reviewed exact head/workflow under their existing authority, then request it with
either the explicit label or (where the workflow is available on that branch):

```sh
gh workflow run storage-test-cell-pilot.yml --repo MrFrydae/ChunkMaestro --ref <reviewed-branch>
```

Do not run the old source pilot again. Do not change qualification workflows or
labels as part of this implementation. Collection CLI (hosted trusted context
only; do not launch locally):

```sh
python tooling/quality/storage/test_cell_ci.py prepare .artifacts/cell-bundle
python tooling/quality/storage/test_cell_ci.py probe .artifacts/cell-input .artifacts/cell-result --group 0
python tooling/quality/storage/test_cell_ci.py probe .artifacts/cell-input .artifacts/cell-result --group 1
python tooling/quality/storage/test_cell_ci.py account .artifacts/cell-input .artifacts/cell-outcome <downloaded-cell-0> <downloaded-cell-1>
python tooling/quality/storage/test_cell_ci.py summary .artifacts/cell-outcome
```

The workflow supplies isolated paths for the two commands on separate workers.
Revert the four new files to remove this diagnostic; no existing gate changes.

## Evidence and unexecuted gaps

Research `/tmp/cm-stryker-baseline-overhead-59e2dd1.md` and companion JSON are
observed/source findings, not execution authority or qualification. Historical
Windows 3e baseline/private initial/coverage were approximately 76/88/103 seconds;
that incomplete 53-mutant probe earned no credit. The old byte-identical backup
membership refusal was corrected in parent 59e, not retroactively qualified.
macOS jobs lacked runner admission; no Mac code-failure/speedup claim follows.

Local integration validation is Python-only on macOS. Tests cover public contract
success and tamper/denial paths: all cells mandatory, duplicates, omissions,
rehashed group overlap, extra options, unbound kills, source bytes, compile mask,
foreign roots, raw ledgers, deadlines, full restoration and backup evidence,
prepared-plan reconstruction, and workflow structure/Action pins. The storage
suite totals **171 passing tests**; specification validation passes. Ruby Psych
parsed the YAML successfully (its YAML 1.1 parser treats `on` as boolean; Actions
uses its own workflow schema). No actionlint installation was available.

The first hosted Windows tooling run at `5d55469` refused all twelve standalone
group fixtures before runtime coverage: default text writes translated their
declared LF source into CRLF bytes. The strict source validator correctly denied
that mismatch. Fixtures now write explicit bytes; the validator is unchanged.
An emulated Windows text-write run reproduced all twelve setup errors before
the correction and passed all twelve contracts afterward. This local translation
probe does not replace hosted Windows execution; the original failure is retained.

All three independent bounded reviews at `5d55469` also found that the original
marker-only check accepted Stryker's failed-initial-run continuation path. A new
complete-validator regression fails against that original implementation. The
corrected contract requires the break option and rejects the explicit warning;
omitted/disabled-option regressions are also denied. The `1a17384` pilot was
cancelled after this finding; none of its partial results establish acceptance.
Fresh independent review and a corrected hosted execution remain outstanding.

Fresh reviews at `9120734` then established skipped and partial-initial-result
gaps despite the break option. The new affirmative event predicate addresses
these separately; the prior FAIL originals remain historical. The secure-backup
fixture correction at `909557a` subsequently passed both hosted storage jobs and
all three managed jobs. The new event integration still needs exact-head review
and hosted execution. The pilot remains paused.

The `fb29e05` UX review found whole-file diagnostic reads before the size guard.
Inventory, command-log and retained-runtime hashes now stream 1 MiB chunks;
phase/wire and mutation stdout readers enforce the 32 MiB bound before loading
through producer, accounting and the standalone API. Open-file size changes are
refused, hashes remain checked and oversized raw files remain retained. Three
synthetic I/O regressions cover complete-validator and API entry points. The
historical FAIL remains unchanged; fresh review is required before the pilot.

The same-head functional review found ambiguous canonical session starts at
phase edges were ignored. Both collection and accounting now refuse canonical
messages whose millisecond timestamp bucket touches either phase edge. A new
public-boundary regression covers start/completion messages at exact and
sub-millisecond-overlapping edges. The original FAIL remains historical.

The first reviewed Windows run `44155608934` at `f2c9890` finished both private
commands and full baseline/restoration controls. Both cells remained UNRESOLVED
because the initial-event parser expected the macOS console basename. Retained
Windows logs also exposed an initial active-only zero-result progress event and
distinct host/console wall-clock timestamps. The narrow compatibility correction
and unchanged refusal checks are documented in `initial-test-events.md`. Original
raw cell outcomes remain unchanged and receive no kill credit. Read-only derived
initial proof is not full cell accounting. Further pilots are paused pending
fresh independent review; deadlines and qualification thresholds are unchanged.

Historically, the stdout consistency parser accepted both immutable logs
from `cm-test-group-proof-zdfiu90c`, with selected counts 1/5 and unchanged SHA256
`78fd2598b0b5874da1ff21ab7beac4e5eb00d727cf7d5354c00ec655eee092f8` /
`70240026c301820a5313eb01cd11e7266f70ff1fc19b9032aab4943b136ccab3`.
That existing fixture has 26 identities, 2 CompileError, denominator 24, 22 actual
kill union and six full cases with atomic Theory rows. This read-only parser check
does **not** establish the new source-region/product/Windows collector end to end.
Original evidence was not rewritten or given new kill credit. Stdout consistency
alone no longer suffices: actual initial events are additionally mandatory.

The event integration runs no product mutation or hosted pilot and changes no
product C#, projects, packages or qualification gates. Earlier storage runtime
and build checks are reused only for unchanged C# inputs. Product speedup and
native/volume/recovery acceptance remain unproven. Hosted event artifact
relocation, Windows paths/compiler matching, full control duration, report Theory
multiplicities and admission of both concurrent workers remain unexecuted gaps.
A 180-second private bound may still be insufficient; return UNRESOLVED rather
than expanding the deadline or dropping controls.
