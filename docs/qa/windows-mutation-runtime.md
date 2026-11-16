# Windows mutation runtime investigation — issue #3, PR #6

Implementation/performance investigation, not independent QA approval. Investigated
head: `94b30ab7027ce7f6580b150e2007d2ba2b64e1cb`. Requirements: Q149/Q153,
Q163–164, Q189–195; all three independent QA roles remain required. No production,
test, tooling, dependency, trust or signing configuration was changed. Only this
report is checked in. No Windows tests were executed locally.

## Conclusion and proposed next change

Do not change the tests on this evidence. The retained evidence establishes slow,
continuously progressing Windows test sessions, substantial timeout abort cost,
and broad selected-test sets. It does not establish a fixture leak, a broken
synchronization primitive, or corruption in the runner. Removing denial/recovery
checks, changing deadlines, forcing GC, sharing seeded databases, or reducing
fixture contents would be unjustified. The four newly added assertions are not
evidence of the cause.

The next reviewable change belongs to the tooling owner: add a bounded native
Windows ordinary-test concurrency diagnostic before authorizing another mutation
pair. Use the exact same unmodified MutationTests project, pinned SDK and full
suite in every process. Build once, then run two waves of 1, 2 and 4 simultaneous
`dotnet test tooling/quality/storage/MutationTests.csproj -c Release --no-build
--no-restore --logger trx --results-directory <unique external directory>`
processes. Each process must have its own results directory. Keep the existing
GUID-based disposable database paths; do not share a fixture between processes.
Retain exit codes, full TRX, process start/end times, SDK/OS/image/processor count,
CPU time, peak working set and disk I/O counters where available. Bound the whole
diagnostic to 15 minutes and report incomplete waves honestly. This is 14 ordinary
suite invocations, no mutations, no filtering, no retries and no gate credit.
Expected cost is roughly ten minutes if waves take 100 seconds each; this is a
planning estimate, not an executed result. Abort the experiment rather than
increasing that bound if it cannot complete.

Compare both total completed tests per wall second and per-test timing by stable
TRX test ID, including cold/warm order. If two processes improve full-suite
throughput over four without failures or missing tests in both waves, propose
Windows concurrency 2 in the configuration generators in `qualify.py` and
`replay_ci.py` together, with a regression that checks both generated official
and private configurations retain identical test/mutant scope and thresholds.
Concurrency 2 is a hypothesis, not a proven fix: Stryker warns that 4 exceeds its
recommendation of 2. Do not commit that change solely on the warning. If ordinary
throughput does not degrade at four, profile one instrumented session's per-test
start/end and native I/O before proposing a fixture or product fix. Preserve
private tool provenance; do not rebuild or patch it for this diagnostic.

Only a demonstrated correction justifies another expensive full official/private
Windows pair. It must retain actual-Killed-only credit, denominator, all tests,
all mutants, denial/recovery behavior, 90/85/80 thresholds and independent QA.
This proposal reduces the next investigation's cost and resolves a specific
runner-contention hypothesis instead of spending another multi-hour campaign on
an unmeasured assumption. It does not claim an achieved runtime improvement.

## Retained evidence and provenance

- Run `43891179933`, Windows job `133382473460`, artifact `14247141714`:
  `/tmp/pr6-94b30ab-windows.zip`, SHA256
  `f2d28ecfb0affb37a9064f4c1366d844a0d323c929ab1c8af17ea99b10694261`.
  Independently rehashed locally; archive read as text/XML/JSON only.
- Raw job `/tmp/pr6-94b30ab-windows.log`; gate
  `/tmp/pr6-94b30ab-gate.log`; exported main log
  `/tmp/pr6-94b30ab-official-debug.txt`. Archive member
  `mutation-Persistence/logs/log-20261116.txt` is the authoritative session source.
- External machine-readable results: `/tmp/pr6-runtime-analysis/sessions.json`,
  `requests.json`, `coverage-tests.json`, `host-summary.json`, `joined.json`,
  `local-tests.json`, `pr-state.json`; scripts `analyze.py`, `summarize.py`,
  `join.py`; text `summary.txt`, `join-summary.txt`, `local-restore.log`,
  `local-build.log`, `local-test.log`; original local TRX under `local-tests/`.
  These paths are local evidence, not portable repository attachments. The lead
  must retain/upload them with the original archive before disposable cleanup.
  `manifest.json` records input/output hashes. No historical archive was analyzed.

## Session accounting and timeout cost

Official command: 15:05:13 UTC to 16:35:14 UTC, 5401.3 seconds (outer 5400-second
limit). Initial build ran 15:05:22.630–15:05:27.885 (5.255 seconds). Initial test
run ran 15:05:33.828–15:07:17.865 (104.037 seconds); subsequent instrumented
coverage completed at 15:08:27.242. Mutation sessions began 15:08:27.533. Most
of the budget was therefore spent executing sessions, not restore/build.

There are 303 mutation session starts, 299 completed session records and four
unfinished sessions, plus two earlier baseline/coverage completions (301 total
`Test session finished` records). The earlier 302-distinct-ID estimate only
captures the first ID in each comma-separated batch. Parsing every ID gives
417 distinct mutant IDs, independently matching the VSTest request payloads:
441 mutant assignments in 303 requests, with 24 IDs assigned twice. Forty-two
completed sessions contain multiple IDs. These counts are work observations,
not completed mutant verdicts or score evidence.

Example: at 15:08:34.933 Runner 2 launches a 31-mutant batch beginning
`88,104,129,147,152,160,...`; at 15:09:25.454 it says `Not all mutants were tested`
and launches a reduced 20-ID batch beginning `129,147,152,160,...`.
At 15:10:00.151 it launches `13,206,577,217`, then at 15:10:44.183 launches `13`
alone. This establishes internal incomplete-batch re-execution, not a CI retry.
No identical mutant-ID tuple is repeated in the retained requests. It does not
prove retries are the dominant cost.

Completed mutation sessions sum to 20,248.594 runner seconds (overlapping runners,
not elapsed wall time), median 33.581 seconds. There are 132 under 10 seconds,
39 from 10 to under 60, 53 from 60 to under 120, and 75 at least 120 seconds.
The last group consumes 13,899.239 summed runner seconds (68.64%).

Thirty-three hosts receive `TestExecution.Abort`; all match completed sessions
within three seconds of the configured session timeout. They consume 6,147.682
summed runner seconds. This is consistent with timeout cost, not 33 kills.
The remaining long sessions include real completions: mutant 455 takes 209.576
seconds and its host records xUnit Finished without Abort. Long duration alone
cannot classify a timeout or failure. The main log's generic `failed`/`success`
session labels cannot replace actual per-mutant report verdicts.

Examples below use UTC on 2026-11-16. Member names are relative to
`mutation-Persistence/logs/`; full fields/line references are retained in JSON.

| Mutant | Runner | Start → completion | Seconds | Timeout ms | Host suffix |
| --- | --- | --- | ---: | ---: | --- |
| 68 | 0 | 16:28:54.496 → 16:32:28.071 | 213.575 | 212924 | `Runner 0-log.host.26-11-16_16-28-55_05373_4.txt` |
| 69 | 2 | 16:28:56.093 → 16:32:29.462 | 213.369 | 212924 | `Runner 2-log.host.26-11-16_16-28-56_45869_8.txt` |
| 49 | 2 | 16:25:22.892 → 16:28:56.093 | 213.200 | 212924 | `Runner 2-log.host.26-11-16_16-25-22_95128_8.txt` |
| 480 | 0 | 16:09:44.137 → 16:13:14.608 | 210.471 | 209250 | `Runner 0-log.host.26-11-16_16-09-44_16719_13.txt` |

Mutant 68 selects 151 tests. xUnit starts at 16:28:56.699; host Abort arrives
16:32:28.007. It records successive real test-result warnings through
16:32:27.407, including InitialEndpointConstraintTests at 16:32:19.252,
21.780, 24.194 and 26.492. Thus this example spends almost its whole interval in
the test run, with ongoing completion events rather than an idle startup hang.
The `TestRunCache: InProgressTests is null` warnings concern VSTest bookkeeping;
they do not establish SQLite or package-cache corruption. The logs lack reliable
individual start/end/duration pairs for every mutation-session test. Adjacent
completion timestamps must not be presented as individual test durations because
test classes can overlap.

Of 305 VSTest requests (including two baseline/coverage), 228 explicitly select
at least 100 tests, 68 select fewer than 100, and nine request all sources.
Seven of those nine are mutation sessions. `KeepAlive=false` means a fresh host
is launched for each request. Startup is observable overhead but does not explain
the 211 seconds between xUnit start and abort in the mutant-68 example.

At outer cutoff unfinished IDs are 106 (Runner 2, 16:32:32.936), 115 (Runner 0,
16:32:38.114), 116 (Runner 3, 16:32:39.993), 117 (Runner 1, 16:33:27.791).
There is no complete Persistence mutation report or private replay collection.
Neither the new score nor the individual fate of messages 212/213 is established.

## Actual per-test timing evidence

Archive member `coverage/_runnervmm7ud1_2026-11-16_14_58_36_net10.0.trx`
records 185 executed/passed, zero failed/skipped. These are Windows ordinary
coverage test durations, **not mutation-session durations**. Full raw test and
execution IDs/timestamps are in `coverage-tests.json`.

| Test (abbreviated) | Stable test ID | UTC start → end | Seconds |
| --- | --- | --- | ---: |
| HeldWriterReturnsTaskPromptlyAndFailureIsSanitizedAtomicAndRecoverable | `730b8a01-a1ca-3628-179c-986700fa7d2d` | 14:59:19.255 → 14:59:23.826 | 4.570260 |
| KillAfterNewMigrationDropsPopulatedEndpointsRollsBackAndRecoversSeparately | `8257b941-5c6f-bdfd-8a6d-8fb52fa45016` | 14:59:10.822 → 14:59:15.088 | 4.265543 |
| InitialEndpointRefusal… HostId | `3f682115-1636-ff91-33cd-8a6857cfcece` | 14:59:01.785 → 14:59:04.643 | 2.857401 |
| CandidateLogicalDamage… foreignKeyDamage true | `a21d706b-e687-24fe-daf9-10396d6d9245` | 14:58:59.631 → 14:59:02.431 | 2.799766 |
| CandidateLogicalDamage… foreignKeyDamage false | `f308ce24-d28c-e1c6-9fec-327d25ddc733` | 14:59:02.431 → 14:59:05.225 | 2.793233 |

Summed durations by class: StorageTests 54.610 seconds/43 cases,
DatabaseConstraintContractTests 49.498/32, ManagedStorageQualificationTests
23.684/21, InitialEndpointConstraintTests 20.976/9, BackupPublicationTests
17.673/9, AuthorityMutationContractTests 17.459/32. Total across 185 cases is
231.396 seconds; these sums overlap in wall time. The four backup contract cases
sum to 10.159 seconds. Expensive coverage spans multiple real SQLite fixtures,
not just the latest regression. The held-writer test deliberately exercises the
production three-second busy timeout; shortening it would change the tested
behavior rather than demonstrate a lifecycle optimization.

## Environment and unresolved causes

Retained job identifies Windows Server 2025 Datacenter 10.0.26100,
`windows-2025-vs2026` image `20261103.298.1`, runner 2.338.2, .NET runtime
10.0.15 and Stryker 5.0.0. Stryker explicitly warns about concurrency 4 versus
recommended 2. Host settings also show MaxCpuCount=1/DisableParallelization=true;
these are not proof of xUnit's effective internal scheduling policy.

The job prints `cache: false` for setup-dotnet; generic runner `Cache mode: write`
does not establish a package cache hit. There is no retained A/B cache experiment,
CPU/disk/memory trace, defender trace, or native syscall profile. Filesystem/ACL
cost, oversubscription and cold EF model setup are plausible hypotheses only.
Native confinement and backup checks perform actual I/O; their platform behavior
must stay intact. Static inspection found no justified resource-lifecycle fix.

## Local verification, limits and rollback

Local environment: Darwin arm64; SDK `/Users/derek/.local/share/chunkmaestro/dotnet/dotnet`
reported exactly 10.0.404. The brief's path ending `dotnet10.0.404` does not
exist; the existing private installation was used without configuration changes.
Every .NET command used explicit `CHUNKMAESTRO_DOTNET` and `TMPDIR=/private/tmp`.
Commands (from repository root):

```sh
export CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet
export TMPDIR=/private/tmp
"$CHUNKMAESTRO_DOTNET" restore tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj --locked-mode
"$CHUNKMAESTRO_DOTNET" build tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj -c Release --no-restore
"$CHUNKMAESTRO_DOTNET" test tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~BackupMutationContractTests|FullyQualifiedName~HeldWriterReturnsTaskPromptlyAndFailureIsSanitizedAtomicAndRecoverable|FullyQualifiedName~OpenReturnsBeforeSynchronousMigrationWorkCompletes' --logger trx --results-directory /tmp/pr6-runtime-analysis/local-tests
python3 /tmp/pr6-runtime-analysis/analyze.py
python3 /tmp/pr6-runtime-analysis/summarize.py
python3 /tmp/pr6-runtime-analysis/join.py
```

Locked restore succeeded; Release build succeeded with zero warnings/errors;
bounded ordinary tests: 6 passed, 0 failed, 0 skipped. Local TRX timestamps are
2026-11-16 11:46:30–11:46:34 -05:00. Held writer: 3.090999 seconds; synchronous
migration responsiveness: 0.587471; backup logical damage true/false:
0.590423/0.060389; cleanup refusal: 0.060801; cancellation: 0.056714.
This subset, cold-start order, and macOS differ from the Windows full-suite
coverage workload; these numbers are context, not a causal speedup comparison.
The filter is only for this bounded ordinary reproduction and is not proposed
for mutation qualification. No C# changed, so full repository verify/format was
not rerun; no mutation or Windows concurrency experiment was executed.

Read-only `gh pr view 6 --repo MrFrydae/ChunkMaestro --json
headRefOid,state,reviewDecision,statusCheckRollup` confirmed investigated head
and OPEN state. Windows measurement, storage gate and qa-evidence fail; managed
Linux/Windows/macOS, secret-scan, agent-skills and macOS measurement succeed.
ReviewDecision is empty. This is a snapshot, not independent approval. Gate log
explicitly reports incomplete Windows ordinary collection, missing Persistence
mutation report and missing replay-collection.json. Historical d064403's
622/778 = 79.9486% remains historical, below 80%, and supplies no current pass.

Rollback: revert this documentation-only commit; no data migration, storage
repair or runtime behavior changes. Remaining work: bounded Windows diagnosis,
validated correction if any, complete matching current-head official/private
measurement, actual-Killed-only gate, and all three independent QA role verdicts.

## Guarded integration of the diagnostic

The investigation above is preserved from signed worker commit
`42f3a5cdf0362803d37f2dd3888bd033b09569d1`; its measured head remains
`94b30ab`. Later integration adds the ordinary-suite experiment ahead of Windows
mutation collection. It is bounded to 900 seconds and executes two waves of the
full suite at one, two and four simultaneous processes, with unique results and
no filters or retries. Resource counters describe the root dotnet processes
only; child test-host resources are explicitly excluded.

Windows mutation concurrency two is conditional on all fourteen full-suite runs
passing with identical test identities and greater completed-tests-per-second
than concurrency four in both waves. Missing, failed, stale or inconclusive
evidence blocks collection. Both the initial collector and private replay
revalidate the proof, and the aggregate independently reopens its raw artifacts.
The frozen Windows replay configuration and generated official configuration
must match; their only difference from macOS is concurrency two versus four.
No test/mutant scope, threshold, deadline or timeout-credit rule changes.

This is a guarded hypothesis test, not a demonstrated Windows runtime improvement.
If the ordinary-suite experiment fails to support the hypothesis, Windows
collection stops with retained diagnostics; it does not proceed with an unproven
concurrency change. Native Windows execution and final qualification are pending.

### Windows unit-fixture correction

The [Windows job at `230a804`](https://github.com/MrFrydae/ChunkMaestro/actions/runs/43914369548/job/133464444953)
stopped in the tooling regressions, before the concurrency experiment. The
deadline test supplied a finite mocked clock sequence; Windows tree termination
needs an additional clock read, which exhausted the mock and raised
`StopIteration`. This is no evidence for or against the concurrency hypothesis.

The fixture now holds its synthetic clock at the expired-work time after launch.
A separate synthetic Windows-branch test covers successful tree termination,
fallback after a still-running process, and taskkill timeout, requiring bounded
tree kill and final wait in every case. It replaces the module's OS view without
changing the host OS or executing native taskkill. Production deadlines and
termination logic are unchanged. All 42 storage-tooling regressions passed
locally; native Windows execution remains pending.

### Inconclusive experiment and CPU trace follow-up

The [Windows experiment at `1b164ea`](https://github.com/MrFrydae/ChunkMaestro/actions/runs/43915305867/job/133467720396)
stopped at its work deadline after 870.8 seconds and retained `INCONCLUSIVE`.
Five of six groups completed; the second four-process group was terminated.
The first complete wave did not support the required throughput improvement
from four processes to two. No recommendation or mutation score follows from
these partial results, and the existing gate correctly blocked collection.
Windows artifact `14250367573` has SHA-256
`811b5ee8480f1096f6a5ef2e1d4e5e8ee52898d5f7618adf38694dd382677e89`;
the lead reopened its raw result JSON and verified the archive hash.

The next diagnostic is one unfiltered Windows suite under the runtime's built-in
[EventPipe startup tracing](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventpipe).
It installs no package or profiler and changes no runner permissions. A separate
read-only workflow job performs locked restore/build and runs the suite with
controlled CPU/runtime providers and per-process `{pid}` trace filenames. Only
the test command receives the explicit tracing variables; inherited credentials
and startup hooks remain excluded. The whole capture is bounded to five minutes,
with time reserved for process-tree termination. Raw traces, TRX, commands, hashes
and limitations are retained separately from qualification artifacts.

Tracing affects execution timing. A nonempty trace is not proof of complete
sampling: dropped events and traces from deliberately killed fixture processes
must be assessed during decoding. This diagnostic supplies no throughput,
mutation, coverage or independent-QA credit. The concurrency hypothesis remains
unproven, and all existing safety gates stay blocking.

### Current CI execution policy

The mandatory experiment above describes the historical investigation pipeline.
Normal PR qualification now omits the fourteen-suite concurrency experiment and
the CPU profile. Both remain available as independent opt-in jobs through the
Storage safety workflow's `diagnostics` dispatch input. They are not qualification
prerequisites. Windows mutation concurrency stays two; macOS stays four.

The complete official Persistence campaign now runs once, within the validated
official/private pair, rather than once in each collector. The aggregate uses
that original report without changing scope, eligibility, thresholds, kill-credit
rules or cross-platform requirements. See `mutation-replay.md` for the current
artifact contract. Removing duplicate execution is not a claimed measured
throughput gain or a qualification PASS.
