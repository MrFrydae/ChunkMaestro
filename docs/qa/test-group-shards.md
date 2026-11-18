# Test-method partition mechanism (PR 6 / issue 3, Q190–195)

Current owner decision: hosted storage workflows are retired. Use
[local storage qualification](local-storage-qualification.md). Scheduling and
workflow examples below are historical; do not dispatch them.


This standalone experiment does not integrate with collectors, workflows, replay
or qualification gates. Full pre-merge mutation qualification remains mandatory:
90% line, 85% branch, 80% actual kills; no Timeout kill credit, denominator change,
native/volume/recovery/current Windows waiver or independent three-role waiver.
No global PASS is emitted. Historical evidence is never added to the kill union.

## Integration API

`tooling/quality/storage/test_groups.py` uses the existing source-bound catalogue,
UTF-16 identity, path and pinned binding validators from `shards.py`. It executes
nothing. API failures raise exceptions; they do not return partial accounting.
Canonical JSON and object hashes use `shards.canonical` / `shards.sha`.

1. `read_trx(path)` returns sorted `cases` of `{method, display}`, positive integer
   microsecond `weights` per method, and raw `trx_sha256`. Require complete passed
   counters/results; reject duplicate case identities, failed/skipped/unbound
   cases and invalid durations. TRX IDs are local to a run. Unused raw definitions
   do not establish execution; only passed results establish cases.
2. `inventory(root, names)` returns raw SHA256 by safe repository-relative path.
   The collector supplies the complete source/test/project/lock/config inventory,
   including empty source files and all test sources, before execution.
3. `plan(catalogue, config, binding, baseline, inputs, group_count,
   duration_weights=False)` returns `TEST_GROUP_INDEX`, complete frozen original
   catalogue/mask, config, binding, inputs, full baseline, groups, fixed original
   denominator and `plan_sha256`. `catalogue` comes from
   `shards.read_catalogue(original_official_report, root, authenticated_host_root)`.
   Baseline config must keep the full module's existing `BASE_PATTERNS` and have
   no preexisting `test-case-filter`. No source partition is applied. Binding is
   the existing exact six-field pinned SDK/tool/head/platform contract; tool here
   identifies the private worker tool. Authenticate the official tool separately.
4. `configuration(plan, group_id)` copies that entire config, adding only
   `test-case-filter`. Use exactly the same filter in selected baseline/restored
   commands. Clauses are `FullyQualifiedName=Namespace.Class.Method`, OR joined
   with `|`; pass as one argv element, never through a shell. Only ASCII dotted
   identifiers are supported. Nested/generic names, row display names, whitespace,
   operators and escaping requirements fail closed. No substring matching.
5. `validate(plan, records, root, expected_binding, original_report,
   full_baseline, full_restored, original_host_root=None)` reconstructs the plan,
   verifies current input bytes and original report, exact full baseline hash,
   identical full restored case set, and every group exactly once. It returns
   `ACCOUNTED`, `qualification: NOT_EVALUATED`, fixed `original_denominator`,
   sorted unique `attributed_killed`, `uncredited_mutant_timeouts`, and group count.
   It never computes a gate score, averages shard scores or publishes PASS.

Each record has `group_id`, `plan_sha256`, complete generated `config`, local
`report` path, raw `report_sha256`, actual integer `exit_code` (0 or 2), boolean
`timeout`/`cancelled` (both false), selected `baseline`/`restored` TRX paths,
`test_definitions` (`selected` or `all`), and optional authenticated
`workspace_root`. All group reports must contain the entire original source-byte
inventory, identities and original CompileError mask. Private rows may only be
Killed, Survived, NoCoverage, Timeout or original CompileError. Ignored or incomplete
private rows deny accounting. NoCoverage/Survived/Timeout remain eligible without
credit. Only actual Killed with nonempty `killedBy`, all bound to that group's
methods, enters the union. Each identity counts once after **all** groups validate.

Report test IDs are also local, and Theory rows may have separate IDs sharing a
method FQN. Require exact method multiplicities against full baseline cases in
`all` mode, or group cases in `selected` mode; no assumption that all definitions
ran. An all-definitions report cannot authorize a kill by an out-of-group test.
Selected baseline and restoration must contain precisely the planned case set,
including every Theory row. Adapters whose definition representation differs
must supply a separately reviewed identity contract; this helper refuses guesses.

The collector must freeze/authenticate the plan hash, complete inventory and
original official catalogue before launching workers. It still owns completed
command attestation, exit-score consistency, complete source discovery, raw
command/config/report/test binding hashes, original official/private provenance,
SDK/compiler/dependencies, noncredential environment, process-tree deadlines,
baseline/restoration binary identity and exact final runtime membership. JSON
hashes alone cannot establish authenticity or detect an omitted instrumented file.
Existing ordinary coverage, native, volume, recovery and all platform/role gates
remain separate. This API is not a replacement for `replay_gate.py`.

## Partition and cost

All rows of a method are atomic. Deterministic longest-weight-first allocation
uses lexical method ties and least current group weight then numeric group ID.
Default weight is case count; optionally freeze full-baseline TRX duration sums
in integer microseconds (minimum one). Width must be 1–20 and cannot exceed method
count. This is a reproducible heuristic, not optimal balancing or a speed model.
Duration of an unchanged test need not predict mutation work.

Each group instruments and compiles the **full** source module, discovers tests,
runs initial tests/coverage, and executes overlapping covered mutants. Early kills
can execute again in other groups. Thus total compute, runner minutes, report size
and setup work increase; wall time depends on independent checkout capacity,
queue waves and the slowest group. Use isolated outputs per worker. Respect the
previously proposed maximum four macOS jobs and overall twenty-job ceiling after
coordinating actual capacity. No product or Windows speedup is established.

## Fresh executed fixture evidence

macOS arm64, pinned SDK 10.0.404, parent source
`59e2dd1cc966bed4595c91d39fe4165bdc1d8e0d`. One corrected experiment built a **fresh**
private Stryker from the pinned archive using current `replay.build_private`,
unchanged upstream locks and the sole block-filter factory patch. All fresh
source/compiler/SDK/dependency/output hashes and raw advisory warnings remain in
`private/provenance.json` and its build log. No historical binary was executed or
rebound. Prior nested fixture **source bytes only** were copied into a disposable
fixture, and a two-row Theory was added. No product source/tests/locks changed.
The environment used a disposable HOME, public package cache, explicit machine
paths and no inherited credentials. HTML reports remained unopened.

Successful raw evidence directory:
`/var/folders/8p/klwq9s9n3jg2hm6pqx99hc3w0000gn/T/cm-test-group-proof-zdfiu90c`.

- Official full, private full and both test-filtered private reports each retained
  **26 identities / 2 CompileError / fixed denominator 24**, including nested
  blocks. Two groups held **1 and 5 cases**; both Theory rows stayed together.
- Private full and the actual attributed group union matched: **22 kills**.
  All **6 full baseline/restoration cases** passed with identical case identities;
  selected baseline/restoration matched each group precisely.
- Mutation commands: official full 8.13s, private full 8.59s, groups 4.99s and
  8.48s, executed serially. Their sum exceeds the single full run. Recorded build,
  restore, tests and mutations total 39.62s; the experiment finished in under a
  minute, below six minutes. Commands were capped at 180s and the runner checked
  a shared 360s deadline. Executed builder metadata probes finished promptly;
  the final runner additionally bounds those metadata subprocesses explicitly.
- Byte-identical `.dll.stryker-unchanged` was preserved as backup evidence, then
  removed from the runtime directory. Offline final verification required **exact**
  runtime membership and hashes equal to the initial inventory. Original raw
  `result.json` retains the pre-cleanup inventory; finalization is a separate
  `final-runtime-verification.json`. Final runner automates that same step.
- `commands.json`, configs, raw report trees, plan, TRX, executed runner copy,
  implementation hashes and inventories retain the command/config/report/test
  binding bytes. `offline-accounting.json` records **12 real-artifact denials**:
  missing/duplicate groups, command timeout/cancel/failure, wrong binding/filter,
  selected baseline mismatch, compile mask/identity/attribution/definition changes.

The first attempt stopped before compilation with a NuGet-Migrations mutex error
because the disposable environment lacked TMPDIR. Its unchanged failure/logs are
retained at `cm-test-group-proof-acnjkoh7` under the same temporary parent and
`/tmp/cm-test-group-launch.log`. One explicit TMPDIR correction enabled the run;
no failed mutation command was retried and no failure received credit.

Hashes (raw bytes):

| Artifact | SHA256 |
| --- | --- |
| plan.json | `2bc6cae24838c2cdbfe5167d95074e0701ed8c261427343d92eb4b0bc005dea9` |
| result.json | `ac0dd4cc15eb2ce4942936aee2f5aa6a75831475303c90090b21e0bdc64fd058` |
| private/provenance.json | `fd00b70568b7f4087b070960bb671cc83201354121f2eb19b98e1d5016d589b7` |
| offline-accounting.json | `0921cafec7a61c1dc03e2c23db2a4cfc455515417ca997cc0b919a3b0ede17f1` |
| final-runtime-verification.json | `61760d75ed8fe4751e5c99d6e773d1b1a7bde8405abec7ae52330273541bb1ed` |
| final-artifact-sha256.json | `323866376557b21d31529eb00a1a6116c53b76783340e94cd6a08abc6f4c66d8` |

Offline check (does not execute tools):

```sh
python3 tooling/quality/storage/test_group_pilot/check.py /var/folders/8p/klwq9s9n3jg2hm6pqx99hc3w0000gn/T/cm-test-group-proof-zdfiu90c
python3 -m unittest discover -s tooling/quality/storage -p 'test_*.py' -q
python3 tooling/specification/check.py
```

Local validation: **130 storage-tool tests passed**, including twelve new grouped
contracts with multiple negative subcases and 50 deterministic partition trials; specification validation passed.
Python-only tooling was added; no repository C#/project/formatter input changed,
so no product build/format/runtime campaign was run. Synthetic refusal tests and
this fixture do not establish native acceptance. The final runner's metadata bound
and automatic backup finalization were reviewed/compiled but not used for a second
mutation experiment. Executed runner bytes remain separately preserved.

No hosted retry, workflow/label trigger, push, watch, approval, aggregate or
independent QA was performed. The Windows pilot remains timed-out UNRESOLVED with
no kill credit; cancelled macOS capacity waiting remains unmeasured. Full current
Windows/macOS qualification and native/volume/recovery/three-role acceptance remain
required before merge. Reverting these new files removes the mechanism without
changing live collection or gates.
