# Source mutation regions: planner and accounting contract

Issue 3 / PR 6, Q190–195. This standalone diagnostic does not run qualification,
change the 90/85/80 thresholds, or publish global PASS. Full mutation qualification,
native, volume, recovery and independent review gates remain required before the
storage PR merges. The cancelled campaign is not evidence. No workflow changes
or product mutation campaigns belong to this patch.

## Mechanism and API

`tooling/quality/storage/shards.py` imports existing `replay.catalogue`, UTF-16
location handling and artifact-path guards. It is read-only. API exceptions are
failures, never an empty successful result. JSON serialization is `canonical()`:
UTF-8 JSON with sorted keys, compact separators and ASCII escaping; `sha()` hashes
those bytes. All artifact SHA-256 values hash raw bytes instead.

1. `read_catalogue(report_path, checkout_root)` returns `rows`, complete reported
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
Only those actual owner kills receive credit. No averaging, partial sets,
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
No disjoint region coalescing is performed: combining an ancestor exclusion with
an owned descendant could remove that descendant from execution. Every in-scope
identity has exactly one owner; original excluded rows and compile eligibility
remain retained. Planning an all-Ignored empty-span catalogue is INDEXED only,
never COLLECTED or qualifying evidence.

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

## Integration limits and rollback

This is reviewable mechanism code, not production-ready qualification integration.
No product INDEX run, Windows fixture, official/private cross-platform mask
comparison, matrix collector authentication or complete module campaign ran.
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
