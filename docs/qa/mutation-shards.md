# Source mutation regions: planner and accounting contract

Issue 3 / PR 6, Q190–195. The planning/accounting layer does not run qualification,
change the 90/85/80 thresholds, or publish global PASS. Full mutation qualification,
native, volume, recovery and independent review gates remain required before the
storage PR merges. The cancelled campaign is not evidence. The bounded hosted
pilot below measures one batch per platform; it cannot publish qualification.

## Mechanism and API

`tooling/quality/storage/shards.py` reuses existing replay identity conventions,
UTF-16 location handling and artifact-path guards. It is read-only. API exceptions are
failures, never an empty successful result. JSON serialization is `canonical()`:
UTF-8 JSON with sorted keys, compact separators and ASCII escaping; `sha()` hashes
those bytes. All artifact SHA-256 values hash raw bytes instead.

1. `read_catalogue(report_path, checkout_root, host_root=None)` returns `rows`, complete reported
   `sources` (including files without mutants), and raw `report_sha256`. Every
   report source must match checkout bytes exactly. Reject mapped `#line`, empty
   spans, escaping/symlink paths, ambiguous identities and unsafe glob filenames.
   Canonical identity retains relative file, source SHA, original location,
   mutator and replacement, as in replay.py. Numeric Stryker IDs are not identity.
2. `plan(catalogue, baseline_config, binding, target=100, weights=None)` returns
   schema 1 `INDEXED`, immutable original rows, sources/report hash, original
   denominator, frozen config/binding/weights, ordered `regions`, invocation count
   and `plan_sha256` (hash of the object before adding that field). Baseline mutate
   must be exactly `**/*.cs`, `!**/Migrations/*.Designer.cs`,
   `!**/Migrations/*ModelSnapshot.cs`; unsupported scope fails closed.
3. `configuration(plan, region_id)` copies the complete baseline config and
   replaces only mutate with the canonical region patterns.
4. `validate(plan, records, root, expected_binding, original_report,
   expected_tests)` independently reconstructs the entire plan and selections,
   re-parses the original raw report, then requires every region exactly once.
   `expected_tests` is the complete baseline method FQN set, independently derived
   from full passing TRX (Theory rows still need the ordinary collector's complete
   case-level checks). Return is `ACCOUNTED` / `qualification: NOT_EVALUATED`,
   original denominator and sorted attributed killed identities. Never PASS.

Binding has exactly `head` (40 hex), `sdk_version` (`10.0.404`), `sdk_sha256`,
`tool_sha256` (64 hex each), `upstream_commit` (pinned Stryker commit below), and
`platform`. Collector must freeze/authenticate that binding and plan hash before
workers start. Head means measured source candidate; a later documentation commit
must not silently relabel measurement evidence. Separate plans are required for
each tool/platform. Validate expected platform and tool against trusted policy.

Each record is a JSON object with `region_id`, `plan_sha256`, complete generated
`config`, local collector-supplied `report` path, `report_sha256`, `exit_code`,
`timeout`, `cancelled`. Paths are local API arguments, not trusted remote paths.
Only exit 0 or completed threshold-failure 2 is accepted; cancellation, command
timeout and failure deny accounting. Raw report files must retain the entire
catalogue and source inventory. CompileError mask must equal the original mask
on every shard. Selected noncompile owners must finish Killed, Survived or
NoCoverage; Timeout and Ignored deny. Out-of-owner noncompile rows must be Ignored
with exact reason `Removed by mutate filter`. Actual Killed requires nonempty
killedBy IDs bound to source-verified test definitions and baseline method names.
Only those actual owner kills receive credit. The original region validator
retains this stricter selected-Timeout refusal; the batch API below accepts
completed per-mutant Timeouts as eligible and uncredited. No averaging, partial sets,
Timeout credit, denominator shrinkage or union of compile exclusions is allowed.

The collector still owns authenticity: exact SDK executable/tool dependency and
private-build provenance; complete source discovery; configs/command arguments;
trusted head and artifact hashes; full baseline/restoration TRX, case identities
and binaries; source/test/config inventories; bounds and process-tree termination;
raw logs and completed execution attestation. This module cannot authenticate a
self-reported JSON command record. Preserve existing replay gate checks rather
than replacing them with this narrower mechanical validator. Aggregate must
require all intended tool/platform plan hashes before computing existing gates.

## Partition algorithm and cost

Group equal OriginalNode spans, then build a laminar containment forest; crossing
spans fail. Bottom-up children join their parent region only while positive
integer work sum fits target. Delegated child roots become explicit exclusions;
exclusions of absorbed children propagate upward. Each root gets one invocation.
Equal-span groups cannot split and may exceed target. Counts are the default
weights; frozen measured weights can be supplied for every in-scope identity.
This deterministic greedy algorithm targets balance, not an optimal partition.

Every region includes its root and excludes delegated descendant roots using
full UTF16Span.Contains. Ancestor block mutants remain with their ancestor.
The original `plan()` does not coalesce disjoint regions: combining an ancestor
exclusion with an owned descendant could remove that descendant from execution.
The separate batch planner below proves compatibility before coalescing them. Every in-scope
identity has exactly one owner; original excluded rows and compile eligibility
remain retained. A complete all-Ignored catalogue is INDEXED only, never COLLECTED or qualifying
evidence. An empty-span selection is unsafe for nested files; see the mandatory
reporting workaround below.

Propose an initial matrix width **at most 4 macOS jobs**, reserving one of the
owner-reported five standard-plan macOS slots; keep total concurrency at most 20
and coordinate other jobs. Region count is invocation/queue cost, not matrix
width. Workers need separate checkout/build outputs. Freeze a bounded queue;
do not launch 100 jobs blindly. Use **180 seconds per diagnostic command**, stop
on first timeout without retry; the included fixture uses 150 seconds and at
most 12 regions. Final qualification needs separately justified job budgets;
these diagnostic bounds do not replace full qualification. Repeated full-source
instrumentation/compilation, baseline/coverage, queue waves, giant equal-span
groups, slow tests and platform skew can dominate critical path. No product
speedup or hosted Windows latency has been measured.

## Bounded compatible batches and portable raw reports

`batch_plan(plan_value, max_batches)` independently rebuilds the source plan and
returns schema 1 `BATCHED_INDEX`: `source_plan_sha256`, `max_batches`, ordered
`batches`, and `batch_plan_sha256`. Each batch has `id`, `region_ids`, `owners`,
`patterns`, and `weight`. Deterministic weighted graph coloring packs compatible
regions into at most the requested width. An exclusion containing another
region's owner creates a conflict. The merged selection is independently checked
against the exact owner union, with every original region represented once.
Failure to color is an error, never permission to create unbounded matrix jobs.
This greedy packing can refuse a feasible width; no optimality is claimed.

`batch_configuration(plan_value, batches, batch_id)` rebuilds/checks the canonical
batch plan, copies the complete baseline configuration, and replaces only mutate.
The baseline EF exclusions remain mandatory. Workers must use separate checkout
and build outputs; the number of batches bounds the matrix's rows for each tool
and platform. Concurrent runner slots are a separate limit, not a measured
runtime or an overall runner entitlement.

**Stryker 5 reporting workaround:** every batch prepends `**/*.cs{0..1}`.
The planner refuses any input with an in-scope original mutation contained in
UTF-16 interval `[0, 1]`, proving this selector executes no additional identity.
A zero-length `{0..0}` selector can make nested files disappear behind source
`File ignored by mutate filter` placeholders. The actual product probe lost two
migration catalogues this way (708 identities instead of 811). The nonempty
selector retained all 811 identities, including 27 CompileError rows, in a
27-second INDEX probe; all other rows were Ignored. This is catalogue generation,
not mutation execution. Every worker still must return the complete byte-bound
source inventory and catalogue. Never fill missing/placeholder source from the
checkout or fabricate omitted identities. Strict rejection is intentional.

`read_catalogue(..., host_root=None)` and test-definition binding accept an
explicit collector-authenticated producer workspace prefix, for example
`D:\a\ChunkMaestro\ChunkMaestro`. Only that exact prefix can map an absolute
foreign filename to a safe repository-relative path in memory. Relative local
paths remain supported. Unknown prefixes, traversal, aliases, symlinks, source
changes and duplicate identities deny accounting. Raw report bytes are never
rewritten; their original SHA-256 is retained. Test source and complete baseline
method definitions use the same mapping and byte checks. A prefix from an
untrusted report is not authentication: the collector must bind it to the actual
producer workspace and must independently verify the inventory/head.

Stryker also emits absolute test filenames with the literal Compile Include
spelling `tooling/quality/storage/../../../tests/ChunkMaestro.PersistenceTests/`.
For transferred test definitions only, the binder maps that exact prefix plus
one safe `.cs` filename to the canonical tests directory. It checks the local
project-directory path for symlinks and then applies the ordinary source-byte,
symlink and duplicate-file guards. Arbitrary `..` paths and aliased duplicate
test definitions still deny. Raw report bytes and their digests are unchanged.

`validate_batches(plan_value, batches, records, root, expected_binding,
original_report, expected_tests, original_host_root=None)` independently rebuilds
both plans and parses every raw report. Records contain `batch_id`, `plan_sha256`,
`batch_plan_sha256`, complete `config`, local `report` path, `report_sha256`,
`exit_code`, `timeout`, `cancelled`, and optional authenticated `workspace_root`.
The original report can separately use `original_host_root`. Every batch must
appear exactly once; both hashes, source/test bytes, full identities, original
CompileError mask and generated configuration must match. Killed owners need
actual `killedBy` IDs bound to complete source-verified baseline test definitions.
Owners may finish Killed, Survived, NoCoverage or per-mutant Timeout; Timeouts stay
in the fixed denominator, receive no kill credit and are listed separately as
`uncredited_mutant_timeouts`. Selected Ignored rows deny because this API expects
the private unfiltered tool. Out-of-owner rows must be original CompileError or
mutate-filter Ignored. Whole-command timeout, cancellation, missing/partial runs
and exits other than completed 0/2 always deny. The result is `ACCOUNTED` /
`qualification: NOT_EVALUATED`, fixed denominator, killed identities and invocation
count. Existing official/private reconciliation and qualification remain separate.

Mechanical packing of the frozen production diagnostic plan (811 identities,
27 CompileError, 53 containment regions, target 100) succeeded at both bounds:

| Maximum batches | Actual batches | Weights in canonical batch order |
| --- | --- | --- |
| 12 | 12 | 99, 62, 83, 63, 63, 63, 63, 63, 63, 63, 63, 63 |
| 4 | 4 | 203, 202, 203, 203 |

Input: `/private/tmp/cm-product-index-trivia-q7ctgz4h/plan.json`. These are work
counts only. No production speedup, hosted concurrency or Windows runtime was
measured. This does not replace complete same-head qualification before merge.

## Bounded hosted timing pilot

`.github/workflows/storage-shard-pilot.yml` runs only after the owner adds
`storage-shard-pilot`, or explicitly dispatches the workflow at the candidate
branch. It is separate from `storage-safety.yml` and has no qualification gate.
Ordinary pushes do not launch this pilot or the full mutation campaign. An
unrelated label has a different concurrency key and cannot cancel a requested
pilot. Each planning and diagnostic job has a ten-minute hard cap.

`python tooling/quality/storage/shard_ci.py prepare OUTPUT --batches N` freezes
the complete passing runtime cases, source inputs, official catalogue index,
original CompileError mask and deterministic private-tool batch plan. It builds the
private tool once per platform and retains its pinned archive, sole source patch,
compiler/output provenance and advisory logs. The planning job has a shared
420-second command budget and each index command is capped at 180 seconds.
Preparation must return `PREPARED_DIAGNOSTIC`; a missing source, placeholder,
failing/skipped baseline or failed command is unresolved. The original catalogue
comes from the official tool; the execution-plan binding separately names the
private tool's verified hash. Each completed private shard must return the entire
official catalogue and identical original CompileError mask before any outcome
is accounted. A second private planning-only index is unnecessary; the actual
private execution report supplies that comparison. This does not remove either
tool from the existing full-qualification workflow or change its merge gate.

`python tooling/quality/storage/shard_ci.py probe BUNDLE OUTPUT` verifies the
unchanged bundle, raw command ledger/logs, trusted same-workflow run/attempt,
head/platform/SDK and complete workspace/source/test bytes before private
execution. It runs only the heaviest packed batch: at most 16 batches are planned
per platform. Planning 16 macOS batches does not launch 16 jobs: the pilot still
executes one, and a future full matrix must cap macOS concurrency at four. The
probe has a shared 480-second budget, caps
the mutation command at 300 seconds, and reserves 90 seconds for a clean rebuild
and full baseline restoration. A completed batch reports only
`PARTIAL_DIAGNOSTIC` / `qualification: NOT_EVALUATED`. Timeout/cancellation,
changed inputs, missing owner outcomes or failed restoration remain unresolved.
Per-mutant Timeouts stay eligible and receive no kill credit.

The existing command runner prints elapsed/remaining time, last log activity and
the retained raw-log path. Failure metadata, original reports, byte inventories
and human outcome summaries are uploaded even after a failed step, unless the
job itself is cancelled. Summaries are written outside the sealed bundle so they
cannot change its inventory. Green pilot jobs measure preparation and a partial
batch only; they cannot satisfy the full premerge workflow or independent QA.

Local integration validation: **113 storage-tooling tests**, **16 build-tooling
tests**, Python compilation and the specification checker passed. No hosted
Windows/macOS pilot or production speedup has yet been measured by this change.

The first hosted macOS preparation at `cccfee0` completed in 169.4 seconds and
retained 811 identities, 27 CompileError rows and all 195 runtime cases. Transfer
preflight then refused the emitted test-path spelling before mutation execution.
The corrected binder passed offline checks against the original hash-verified
98.6 MB artifact, including both indices, deterministic plans, raw command logs,
private-build provenance and all 1,438 unchanged inventoried files. No private
tool execution or SDK rebinding occurred during that offline check. **115 storage
contracts passed**, including Windows/POSIX emitted-path and alias/traversal
denials. A new hosted timing diagnostic remains necessary; no speedup or score
is inferred from preparation or offline checks.

Bounded review corrections retain the original FAIL reports. Private-build
validation now checks the exact SDK executable, pinned source project, Release,
no-incremental, forced locked-restore arguments and working directory against
the same policy function used by the builder. Agreement between two JSON records
alone is insufficient. Regression probes change both matching records together
and still require refusal for changed compiler, project, flags or cwd.

Restoration inventories every file in the relevant project `bin/Release` trees,
including managed dependencies, native SQLite/storage libraries, runtime config,
apphosts and exact file membership. It excludes the transferred private-tool
artifact tree. Changed native bytes and added/removed dependencies deny equality.
The corrected storage tooling suite passed **116 tests**. A local macOS clean
baseline and clean restoration each passed all **195 cases** and produced
identical **311-file** runtime inventories. No mutation ran during that check.
Logs/result: `/var/folders/8p/klwq9s9n3jg2hm6pqx99hc3w0000gn/T/cm-runtime-restoration-ey0jyi8r`.
Fresh independent review and current-head hosted evidence remain required.

At `67adf04`, Windows passed tooling and runtime coverage, built the private tool,
and completed the official index in 172.6 seconds. The redundant private index
then reached the remaining 83-second shared deadline; preparation refused at
421 seconds with explicit `absolute-deadline` and retained logs. It was not an
unexplained hang and receives no mutation credit. Preparation now performs six
pinned command phases and only one official index. The 420-second shared budget,
180-second index cap, complete official eligibility, private-report matching and
all premerge gates remain unchanged. This removes a repeated planning command;
hosted end-to-end duration and full parallel speedup are still unmeasured.

The `67adf04` macOS private probe reached mutation execution, hit its explicit
300-second deadline, then completed clean/build/test restoration and reported
`baseline_restored`. This predates the complete 311-file inventory correction;
it is historical diagnostic evidence, not full restoration qualification or
mutation credit. Its four-batch plan assigned about 203 identities to a batch.
The pilot now plans 16 batches on either platform with target weight 50, still
executing only one heaviest batch per platform and preserving all deadlines.
Batch count is distinct from the eventual concurrency limits: at most four
macOS and 16 Windows runners, with queued batches where necessary.

An offline replan of the immutable original official index, matched to repository
C# source bytes, retained all 811 identities and 27 original CompileError rows.
It produced 62 regions and 16 complete batches with weights 50 or 51, with no
mutation execution, runtime prediction or qualification. Original measurement
head remains `cccfee0`; comparison source head is `cbe546a`, with raw bytes and
bindings preserved. Artifacts:
`/var/folders/8p/klwq9s9n3jg2hm6pqx99hc3w0000gn/T/cm-finer-batch-plan-lr2xy2vl`.
The next signed candidate must regenerate its own index and obtain fresh hosted
timing; neither this historical replan nor the bounded role PASS at `cbe546a`
accepts the newer local optimization or timestamp correction.

## Evidence and reproduction

Prior verified research (read before implementation):
`/private/tmp/cm-source-shard-pilot-efru8r22/{report.md,research.json,artifact-sha256.json}`
and `/private/tmp/cm-test-shard-research-gvs39co_/report.md`. Its nested 10/1
executed sets were disjoint, union 11/11; naive character splits lost outer/crossing
mutants. Retain those original artifacts; the first prior harness's overwritten
registry is explicitly incomplete, as its report documents.

Pinned primary source is Stryker.NET 5.0.0 commit
`6e77a3451bac4793e9c839c3ff4055c1b30ca3af`:
[FilePattern Contains](https://github.com/stryker-mutator/stryker-net/blob/6e77a3451bac4793e9c839c3ff4055c1b30ca3af/src/Stryker.Configuration/FilePattern.cs),
[filter includes/excludes](https://github.com/stryker-mutator/stryker-net/blob/6e77a3451bac4793e9c839c3ff4055c1b30ca3af/src/Stryker.Core/Stryker.Core/MutantFilters/FilePatternMutantFilter.cs),
[compile before filtering and final CompileError](https://github.com/stryker-mutator/stryker-net/blob/6e77a3451bac4793e9c839c3ff4055c1b30ca3af/src/Stryker.Core/Stryker.Core/MutationTest/CsharpMutationProcess.cs).

Commands (repository root):

```sh
python3 -m unittest discover -s tooling/quality/storage -p test_shards.py -v
python3 tooling/quality/storage/shard_pilot/run.py --dotnet /Users/derek/.local/share/chunkmaestro/dotnet/dotnet --provenance /tmp/pr6-replay-private-lead-v2/provenance.json --prior /private/tmp/cm-source-shard-pilot-efru8r22
```

The pilot accepts only the existing historical private binary after verifying its
original archive, entire recorded source/dependency/output inventory, sole patched
factory, SDK executable and compiler hashes. This conditional use is fixture
mechanism evidence, not current-head hosted private provenance. Original logs,
provenance and advisory warnings remain untouched; no audit/trust suppression.
Child environment is explicit and noncredential. Disposable fixtures copy all
original tests and add assertions; source includes nested/equal-span mutations,
astral Unicode and CRLF. All raw configs, logs, reports, command bounds, full
plan, result and SHA inventory remain in the printed temporary directory.
No live/user data, product source, lock graph or workflow is modified.

Executed on macOS arm64 with SDK 10.0.404, source parent
`39a001af3c4ce31d9b2849ebd71e572808803f2c`:

- `python3 -m unittest discover -s tooling/quality/storage -p 'test_*.py' -q`:
  **85 passed**, including 27 new contracts and 100 deterministic weighted-tree
  trials. Existing synthetic timeout/failure logs are expected denial tests.
- `python3 tooling/specification/check.py`: PASS; `git diff --check`: clean.
- First disposable proof `cm-shard-proof-52ak_cg5`: 17 identities, six regions,
  zero CompileError; retained as a narrower successful probe, not hidden/retried.
- Final disposable proof below: **26 identities**, **2 real CompileError** entries
  identical in full and every shard, **24 fixed denominator**, **22 attributed
  kills**, eight regions (weights 2/2/3/3/6/8/1/1), one equal-span multimutator
  group, all five baseline/restoration cases passing with unchanged identities.
  Pattern-variable negation exercises compiler rollback; Unicode/CRLF source is
  byte-bound. Full run 10.59s; region commands 6.63–8.24s, executed serially.
  These are tiny fixture measurements, not production/Windows latency claims.
- Final module revalidated the retained raw reports offline and rejected **14
  real-artifact tamper/partial/failure probes**, including changed compile mask,
  missing/duplicate shards, refiltered originals, cancellation, command timeout,
  owner Timeout/Ignore, changed identity, unbound kills and outside execution.

Raw proof directory:
`/var/folders/8p/klwq9s9n3jg2hm6pqx99hc3w0000gn/T/cm-shard-proof-r5hudrzd`.
Offline denial evidence:
`/var/folders/8p/klwq9s9n3jg2hm6pqx99hc3w0000gn/T/cm-shard-accounting-pwz1b8tf`.

```sh
python3 tooling/quality/storage/shard_pilot/check.py /var/folders/8p/klwq9s9n3jg2hm6pqx99hc3w0000gn/T/cm-shard-proof-r5hudrzd
```

Plan content hash: `6b72380b2c0584233b4a5b15f3f5dd312860efaa136bfdffddc2c8f80a061fe8`.
Raw plan file SHA: `02bb6ee24cf3a2c8517e0fad970498f25ae3ef264b7cda4bc6d17a0f0708fe46`.
Proof inventory file SHA: `1b63db19aa0b383540b2f9965b60e1d863bf844c9b1441b8b14ebcbc2a965d07`.
Historical provenance SHA: `aa5aa525dbd3cdc68452fce44282923c7a78df6957b7228681ec3e9b8fa78b48`.
Original raw report/config/log/source hashes are listed in that proof inventory.
The helper's later additions strengthen preflight provenance, process-tree timeout
termination and baseline method-name binding; the executed fixture and original
logs remain untouched, and final accounting was re-run over raw evidence.

## Fresh merged-batch mechanism proof

The portable/batch extension passed **37 shard contracts** (the original 27 plus
10 grouped regressions), and the full storage tooling suite passed **96 tests**. They cover ownership conflicts, bounded packing,
UTF-16/equal/nested spans, explicit Windows prefix mapping, unchanged raw bytes,
source/test/identity/mask tampering, traversal/alias/symlink refusal, partial sets,
failed/cancelled commands, per-mutant Timeout accounting and placeholder refusal.
A symlink creation probe executes only where the host permits creating one;
no Windows symlink privilege or runtime claim is made.

A fresh disposable macOS arm64 fixture moved one source into a nested folder and
executed a full private-tool run followed by **four merged batch invocations**:

- All 26 original identities and both CompileError entries survived in every raw
  report; the denominator stayed 24. All four batches retained nested source.
- Actual attributed kills matched the unsharded run exactly: **22 / 24**.
- All five baseline and restoration cases passed with identical identities;
  Unicode/CRLF and three equal-span groups were retained.
- Commands used the independently verified historical private tool and pinned SDK,
  an explicit noncredential child environment, no retries and a 150-second bound
  per command. No product source, tests, locks or original evidence was changed.

Evidence: `/var/folders/8p/klwq9s9n3jg2hm6pqx99hc3w0000gn/T/cm-shard-proof-okkzumjh`.
Its `commands.json`, raw reports/configs, `plan.json`, `batches.json`, `result.json`
and SHA inventory preserve the completed execution. The inventory SHA-256 is
`a73bbdb1ea268d73aa53b9724b4dc2c635e82797493569d15495897620124229`;
the raw result SHA-256 is
`19cda9c2778bbb263a30f28df6cdb64c065765723ddc710ffe9cd207d4937e9e`. The external diagnostic
launcher is `/tmp/cm-merged-batch-proof.py` (SHA-256
`c808ebb24835e72577fabccc4ed3735a275bd80a7624261c739fd06fc58124a5`);
it adapts the existing standalone pilot to call the batch APIs and nest the source
file. This bounded fixture is
not a product/Windows campaign, official/private supplement reconciliation,
current-head hosted provenance, independent QA or a speedup measurement.

## Integration limits and rollback

This is reviewable mechanism code, not production-ready qualification integration.
A product INDEX catalogue was inspected mechanically; no product mutation
campaign, Windows fixture, official/private cross-platform mask comparison or
matrix collector authentication was executed by this bounded change.
Official block-filtered owner rows deliberately deny rather than guess eligibility;
a collector must preserve the existing official/private supplement policy as a
separate layer. A baseline config with additional filters is unsupported and must
not be silently accepted. Reported source discovery completeness remains a
collector responsibility; no JSON-only validator can prove omitted files were
instrumented. Test case completeness, process provenance, restored binaries and
all existing native/volume/recovery gates remain the lead's integration work.
An authenticated frozen plan hash is mandatory; self-consistent JSON is not an
execution attestation. No readiness or speedup is promised from this fixture.
Reverting these new standalone files removes the diagnostic without changing
existing replay, gates, workflow, dependencies or product behavior.
