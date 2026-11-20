# Local storage qualification

Owner decision, 2026-11-17: stop using Storage safety GitHub Actions. All four
dedicated storage workflows are disabled and removed, including both hosted
pilots and the duplicate coverage workflow. Ordinary managed build/style/product
tests and secret scanning remain in CI. Do not re-enable a campaign with labels,
dispatch, scheduled execution or another hosted workflow.

The replacement is an explicit local command on real Windows and macOS machines.
It removes hosted runner queues and lets the operator inspect/stop processes and
raw files directly. Mutation computation still takes time; no speedup or current
qualification PASS is claimed. The existing 90% line / 85% branch / 80% actual
mutation-kill thresholds and exact-head premerge requirement remain in force.

Commit changes first, use Python 3.13+ and the pinned SDK, and use a new ignored
output directory. Run only one collector per checkout. Private tool collection
checks the owner's authenticated `gh api user` identity before building/running
it; credentials are removed from the child process environment. Local mode is
explicit. All local command entry points refuse GitHub Actions before any stage,
checkout subprocess, output directory creation or evidence gate begins. The
private replay collector retains its own additional guard.

Quick runtime/coverage feedback, without mutation:

```sh
python3 tooling/quality/storage/local.py check .artifacts/storage-local/check-01
```

Full collection, once on each supported operating system at the same commit:

```sh
python3 tooling/quality/storage/local.py collect .artifacts/storage-local/windows-01
# On macOS, use a different destination:
python3 tooling/quality/storage/local.py collect .artifacts/storage-local/macos-01
```

The collector runs coverage and Domain/Application mutations, the pinned-source
official/private Persistence pair with complete baseline/restoration controls,
full-volume tests and (on macOS) native C qualification. Existing command deadlines
and 30-second heartbeats remain. `local-session.json` identifies the failing
phase; individual raw logs retain command exit, timeout and progress details.
Failures stop the pipeline without automatic retry. COLLECTED is not PASS.
Cancellation or interrupted evidence never earns kill credit.

Copy both complete evidence directories without changing their bytes to the
machine where the same exact checkout will validate them. Preserve originals.
Then prepare a receipt in a new directory outside those platform directories:

```sh
python3 tooling/quality/storage/local.py receipt \
  --windows /retained/windows-01 --macos /retained/macos-01 \
  --output .artifacts/storage-local/receipt-01
```

This reruns the existing raw-evidence aggregate validator: both platforms,
coverage, full mutation catalogues, original eligibility, actual attributed kills,
native C, full-volume, populated upgrade/recovery and exact source/head binding.
It emits a failure gate report rather than a PASS receipt if any requirement fails.
Successful preparation writes inventories, their SHA256 digests, `receipt.json`
and `attestation.md`. Inventory digests bind the retained files, not archive ZIPs.

The lead inspects the raw evidence and retention locations, then publishes the
prepared storage attestation in a PR comment using the repository-owner account.
Do not publish a synthetic receipt or claim measurements not executed. The fast
`qa-evidence` metadata check authenticates the latest owner-attested receipt,
exact commit, platform references and thresholds; it does not download or rerun
the raw evidence. This deliberately uses the same accountable lead-attestation
boundary as independent QA. Independent functional/security/UX product verdicts
remain a separate requirement. A storage receipt alone never approves a merge.

Historical hosted pilot artifacts and FAIL/UNRESOLVED reports remain unchanged.
They do not become local PASS evidence. The earlier sharding/pilot documents are
historical mechanism research, not instructions to restart GitHub Actions.

## One owner-authorized hosted Windows exception (2026-11-20)

The owner's later decision authorizes **one deliberate hosted Windows run** for
PR #6 because no local Windows machine is available. This narrowly overrides the
hosted prohibition above for `ci.yml` only. It does not restore
the four retired workflows, pilots, labels, automatic PR/push/scheduled campaigns,
or permission for another dispatch after failure. The parent integrates and
commits the reviewed patch, obtains the final exact head, and performs the single
dispatch on a ref resolving to that same head, with inputs `head=<full SHA>` and
`authorization=PR6-one-Windows-run`. No dispatch is part of local implementation
validation. The workflow must be available for manual dispatch in GitHub first;
workflow availability on the default branch is an integration prerequisite, not
permission to merge unqualified product changes. Never dispatch an older workflow
against newer source. First-attempt enforcement refuses reruns; the parent must
also enforce the one-dispatch authorization (the workflow has no durable global
counter and cannot prevent an owner from issuing a second new dispatch).

The manual exception lives in the existing CI workflow so it can be dispatched
against the reviewed feature ref without merging the storage PR first. Ordinary
managed/secret-scan jobs are excluded from manual dispatch; the qualification job
is excluded from ordinary CI. Manual runs have unique concurrency keys so another
event cannot silently cancel this evidence collection. Skipped ordinary jobs use
distinct names during manual dispatch so they cannot replace required PR check
contexts with skipped results.

The fixed budget is 60 minutes total: three setup steps capped at two minutes
each, 48 minutes of collection shared by coverage/Domain/Application and complete
official/private Persistence replay (full-volume additionally caps itself at
3 minutes within that same remaining budget), up to two
minutes for bounded process-tree termination/overhead, and four minutes artifact
upload. Collection emits 30-second heartbeats using the existing command runner;
raw child diagnostics stream to artifact log files. No stage or upload is retried
by this workflow. No test filters, sharding, threshold changes, or private-tool
provenance changes are introduced. The sequential full replay has **no measured
Windows completion time at this head** and may exceed this cap; historical
macOS replay collection at `359d8cb` took 1,635 seconds (27.3 minutes), which
supports trying a bounded 37-minute replay allowance but supplies no current
Windows timing guarantee or transferred qualification credit; this budget is a
bounded attempt, not an assurance genuine qualification fits within an hour.
Failure or deadline exhaustion leaves `UNRESOLVED`, never PASS or credited kills.
Do not dispatch again without a new owner decision.

The authorized run44780501245 stopped during runtime coverage: its temporary
fixture directory was beneath the shared runner workspace and correctly failed
ancestor-authority validation. Its partial artifacts are retained, with no
qualification credit. The corrected collector requires its evidence and scratch
root under `LOCALAPPDATA/ChunkMaestroQualification/storage-windows-<run-id>`;
the workflow uploads that same root even on failure. It never weakens production
ancestor checks or repairs shared runner directories. This correction does not
authorize a second dispatch.

The collector verifies GitHub-hosted Windows, repository/owner sender and actor,
manual event, authorization input, checkout/workflow/head equality, clean checkout
and first run attempt. It never calls local collection or alters `GITHUB_ACTIONS`
to evade its guard. Existing private-tool subprocess credential isolation remains
in force after hosted trust validation. All replay temporary trees, partial
builds, raw logs and available manifests stay under the unique uploaded root;
`hosted-session.json` remains incomplete on process termination. An Actions
infrastructure cancellation, whole-job hard timeout, artifact service failure or
runner loss can prevent upload; logs/files that were never retained are not
qualification evidence. Uploaded raw tool trees may be large. The artifact is
retained for 14 days and must be downloaded/retained before expiry.

After the single run, retain the entire downloaded artifact unmodified. Windows
raw evidence is its `evidence/` subdirectory; scratch and session files remain
alongside it. On the same final-head checkout with complete **local macOS**
evidence, prepare the mixed receipt locally:

```sh
python3.13 tooling/quality/storage/local.py receipt \
  --windows /retained/windows-run/evidence --macos /retained/macos-final-head \
  --windows-hosted-session /retained/windows-run/hosted-session.json \
  --output .artifacts/storage-local/mixed-receipt-01
```

The receipt requires completed non-timeout stages and matching replay run
provenance, then executes the unchanged full raw gate (90% line, 85% branch,
80% actual Killed; original eligibility, complete catalogues, native macOS,
full-volume, recovery and source/head bindings). It binds the raw session digest,
owner/head/workflow/run URL/attempt and both evidence inventories. The lead must
inspect/download the actual run artifacts and cross-check the GitHub run before
publishing; the metadata attestation still authenticates the lead, not GitHub's
execution independently. Both-local receipt support and all local hosted-refusal
guards remain. Current/historical coverage-only or old mutation measurements
cannot supply final-head qualification credit. Collection is never qualification
PASS, and a mixed receipt does not replace independent QA.

Full-volume tools receive the same explicit noncredential environment allowlist
as mutation tools. Only the owned disposable-volume path is added for the test.
Success requires a retained bounded TRX containing exactly the intended executed
full-volume case, with one passed result and no failures or skips. The collector
records its raw digest and fixture/tool input digests; receipt gating independently
rechecks the raw result, identity and hashes. A successful process exit or a
standalone `volume.json` PASS cannot satisfy this requirement. Historical volume
metadata without this execution binding is insufficient.
On Windows, collection also verifies that the selected drive partition and volume
GUID belong to the newly created image before the bounded exhaustion test writes.

## Corrected Windows deadline failure and bounded correction (2026-11-20)

Both owner-authorized Windows runs have now been consumed. Corrected run
[44793283079, job 136486240441](https://github.com/MrFrydae/ChunkMaestro/actions/runs/44793283079/job/136486240441)
measured `f74f144d9e1495f2d4be53e1e1b28fa5c37312c2`. Its retained
`hosted-session.json` records an absolute stage timeout at 480.489 seconds
(480.807 seconds including collector bookkeeping), not a 60-minute job timeout.
The raw root is
`/Users/derek/Projects/ChunkMaestro/.artifacts/qa/windows-corrected-authorized-once/raw`.
The runtime TRX passed 279/279 cases; Domain completed with six actual Killed
mutants. Application created zero mutants but was terminated during coverage
capture, without a completed report. Persistence replay and full-volume never
started. This remains UNRESOLVED and cannot supply a receipt.

| Command/phase | Actual Windows seconds | Result |
| --- | ---: | --- |
| Runtime locked restore | 36.0 | exit 0 |
| Tool restore | 6.3 | exit 0 |
| Mutation fixture locked restore | 1.7 | exit 0 |
| Full runtime with coverage | 113.5 | 279/279 passed |
| Domain mutation command | 178.8 | complete report |
| Domain initial-test start to mutant coverage start | 73 | console timestamps 18:33:02–18:34:15 |
| Domain coverage start to selection summary | 82 | console timestamps 18:34:15–18:35:37 |
| Application start to coverage start | 83.54 | debug timestamps 18:35:43.433–18:37:06.975; no completion |

Only Application now sets JSON `coverage-analysis: off`. The pinned official
Stryker 5.0.0 source archive at commit
`6e77a3451bac4793e9c839c3ff4055c1b30ca3af` was downloaded and SHA256-verified
against the existing replay pin
`80702658bac0989e9416e43239ee9d0ca971cea79628bed2b7b1917edd07b269`.
`CoverageAnalysisInput.cs` maps this supported JSON option to `OptimizationModes.None`;
`CoverageAnalyser.DetermineTestCoverage` then assigns every test to each mutant
and returns before `CaptureCoverage`. `InitialisationProcess.GetMutationTestInputsAsync`
still runs initial tests. No initial-test skip exists here. `MutationTestProcess`
disables mixing without coverage selection, so applying this to the larger
Domain/Persistence campaigns could greatly increase execution work; their
configuration is unchanged. Future Application mutants would each run against
the complete test suite and remain subject to the existing absolute deadlines.

This preserves the separate full runtime/coverage baseline, Stryker build and
initial tests, all source candidates and required completed Application report.
The existing source-bound report validator still requires the complete frozen
Application inventory and successful non-timeout exit for measured N/A; a missing
report never establishes zero mutants. No scoring exclusions, eligibility,
actual-Killed accounting, thresholds, platform/native/volume/recovery requirements,
source/head checks, credential isolation or independent merge gates change.

| Budget | Before seconds | After seconds |
| --- | ---: | ---: |
| Coverage / Domain / Application | 480 | 480 |
| Persistence official/private replay | 2220 | 2220 |
| Full-volume | 180 | 180 |
| Collector total | 2880 | 2880 |
| Overall hosted job cap | 3600 | 3600 |

Local macOS arm64 validation used SDK 10.0.404. A disposable one-interface,
one-test fixture ran the official pinned Stryker with default coverage and with
coverage off: 4.4 and 4.2 seconds respectively. Both performed initial tests and
produced genuine complete source-bound zero-mutant reports accepted by the
unchanged validator. The off log omits coverage capture. These tiny timings
validate behavior, not Windows throughput. The new regression rejects the prior
Application configuration; collection scheduling tests still require both complete
module reports and the full unfiltered coverage baseline. At this historical
correction, hosted budget tests pinned the stage allocations; the later
shared-budget correction below supersedes those allocations. Validation artifacts, commands, source hashes,
first fixture setup errors and machine evidence are under
`.artifacts/qa/application-coverage-fix/` in the implementation checkout.

Guaranteed by pinned tool behavior: Application's redundant mutation-coverage pass
is removed. Unproven: the new Windows first-stage duration, complete Persistence
replay duration, and completion within 60 minutes. The observed Application
coverage start was approximately 420 seconds into collection; bypassing that pass
leaves roughly 60 seconds of the existing stage allowance for report finalization
if earlier timings repeat. This is an estimate, not an executed Windows result.
The parent must review/integrate the signed patch, obtain independent reviews at
the final head, and request fresh explicit run authority before any further
Windows dispatch. Final-head Windows and macOS raw qualification and the full
receipt gate remain required. No campaign or hosted run was executed for this fix.

## Shared collection deadline after run 44807864834

The third separately authorized Windows attempt at `7ee5574` also failed at the
480-second first-stage cutoff. Its 1,268 retained raw files show successful runtime
tests and completed Domain mutation, followed by Application's initial tests when
the wrapper killed the process tree. Persistence and full-volume never started.
Removing Application's second coverage pass was insufficient: the fixed first
stage allocation itself prevented using the remaining collection budget.

Coverage/small modules and Persistence now share the existing 2,880-second
wall-clock collection deadline. Each child receives at most the time remaining;
time already spent is subtracted before starting the next stage. Full-volume
keeps its additional 180-second cap within that remaining time. This does not
grant each stage a fresh 48 minutes. All command deadlines and mutation inactivity
watchdogs remain; exhaustion or any child failure stops collection without retry.
The overall 3,600-second job limit, setup/upload allowances, credential isolation,
raw-evidence requirements, initial tests and qualification thresholds are unchanged.
The collector logs each effective stage budget and its terminal phase/error.

A deterministic scheduler regression proves an initial phase lasting ten minutes
can continue into replay while later stages receive only the remaining total.
It is a synthetic scheduling proof, not Windows execution or a speedup claim.
Windows completion and the complete cross-platform qualification remain unproven.
The third single-run authorization is consumed; this correction does not authorize
another dispatch or rerun. Existing exact-head macOS evidence remains historical
for any new correction commit. Never relabel it or manufacture a PASS receipt.
