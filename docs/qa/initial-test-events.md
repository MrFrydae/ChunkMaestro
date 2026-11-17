# Initial test event evidence (bounded, unintegrated)

This adds an affirmative initial-case predicate for PR 6, based on parent
`909557a2345fc63b15c9b9bba4eb34f757a21d17`. It does not award kills, change a gate,
or resume the two-cell pilot. Q190's 80% actual mutation kills, 90% line/85% branch,
Windows/macOS, native/volume/recovery and all-role acceptance remain mandatory.
The fresh partial/Skipped findings remain integration blockers until independent
review passes. No hosted run is alleged to have entered those states; hosted
parent confirmation was not collected here.

## What actually emits the events

Official archive SHA-256:
`80702658bac0989e9416e43239ee9d0ca971cea79628bed2b7b1917edd07b269`, commit
`6e77a3451bac4793e9c839c3ff4055c1b30ca3af`, verified read-only before extraction.
The parser pins the hashes of eight relevant archive files in `SOURCE_HASHES`.
No upstream source, private binaries, factory removal, dependency, project, lock,
C# product fixture, existing collector or workflow was changed.

Archive-bound `LoggingInitializer.SetupLogOptions` passes **DiagMode**, rather than
console verbosity, as `traceToFile` to `ApplicationLogging.ConfigureLogger`.
`--log-to-file` alone therefore gives a Debug file; `--verbosity trace` alone is
console verbosity. `--diag` selects Trace for the file logger. Explicit
`--log-to-file` is also required to populate the runner's `LogOptions.LogToFile`
path in `VsTestContextInformation.DetermineConsoleParameters`. Its diagnostic
console level becomes Verbose and its path is
`<explicit output>/logs/Runner 0-log.txt`. No stdout path is inferred.

Actual fresh commands used the installed SDK executable
`/Users/derek/.local/share/chunkmaestro/dotnet/dotnet` (10.0.404), installed official
`/Users/derek/.nuget/packages/dotnet-stryker/5.0.0/tools/net10.0/any/Stryker.CLI.dll`,
`--config-file <absolute config> --output <absolute output> --diag --log-to-file
--verbosity trace`. Configuration selects Release, JSON reporter, concurrency 1,
one test project, `**/Counter.cs`, break-on-initial-test-failure true. Exact argv,
exit code, timeout, stdout hash and elapsed time are in each `commands.json`.
No signature/audit suppression, HTML opening, credentials or hosted collection.

**Important observation:** `RunEventHandler.HandleRawMessage` has the advertised
`{RunnerId}: {RawMessage} [RAW]` Trace statement, but emitted **zero** `[RAW]`
records in these official-tool experiments, including with full diagnostic flags.
Its existence is not evidence that the translation layer calls it. The supported
observation here is the existing VSTest diagnostic file, not a fabricated RAW hook.
An integration restricted to `[RAW]` must remain UNRESOLVED. The first `--diag`
attempt, lacking explicit file logging, retained only Stryker's Trace file and
cannot provide the required result proof. It is preserved separately.

## Observed schema and binding

The supported input is the console-side Verbose TpTrace record with process,
thread, local date/time (milliseconds), monotonic clock, `vstest.console.dll`, and
one of these exact log sites:

- `TestRequestSender.StartTestRun: Sending test run with message: <JSON>`
- `TestRequestSender.OnExecutionMessageReceived: Received message: <JSON>`

JSON envelope: `Version: 7`, `MessageType`, `Payload`. StartWithSources carries
AdapterSourceMap, RunSettings XML and TestExecutionContext. Initial settings lack
both TestSessionTimeout and InProcDataCollectors. Later coverage/mutation settings
include collectors; their results cannot qualify as initial results.
StatsChange carries NewTestResults, cumulative TestRunStatistics and ActiveTests.
Completed carries TestRunCompleteArgs (IsCanceled, IsAborted, Error, statistics),
LastRunTests with final NewTestResults/statistics/ActiveTests, and ExecutorUris.
Each result carries TestCase (Id GUID, FullyQualifiedName, DisplayName, Source,
ExecutorUri), numeric Outcome, result DisplayName, error fields and start/end
instants. Outcome **1** matches the five passing baseline rows; real skip results
are **3**. Other numbers/strings/bools/None are unsupported or nonpassing.

The pinned source's InitialisationProcess logs the initial start before calling
InitialTestProcess; that process logs output after awaiting the initial run.
VsTestRunnerPool calls VsTestRunner.InitialTest synchronously; that executes
RunTestSession before returning. The parser binds these source hashes, full phase
and wire file bytes, command/argv/cwd/stdout, tool/SDK/config bytes, exact assembly
source path and expected FQN+display set. It requires exactly one source session
inside that timestamp window, consistent process and canonical clock ordering,
no retry, no collector or session timeout, and completed all-Passed results.
Phase offsets and original marker lines are retained. Result instants must fall
between session start and their message reception. Edge ambiguity fails closed.

Only canonical console receive records count. Host traces and other log sites
contain legitimate copies, but are separate inputs and are never deduplicated
into results. Disjoint StatsChange/final chunks are accumulated; repeated cases
or GUIDs, even identical, fail. Conflicting active/result IDs fail. No claim that
protocol-wide identical duplicates can be safely ignored has been established.
Raw wire GUIDs are returned as `wire_id`; no report ID is equated to one. The
fixture plan comes from independent full baseline TRX FQN/display rows, not from
execution events or mutation report IDs. Report identity mapping remains the
separate existing catalogue contract.

## API and integration contract

`initial_events.validate_initial_events(binding)` is read-only and returns either
`status: PASS, initial_cases_passed: true` with the exact observed cases/events,
or `status: UNRESOLVED, initial_cases_passed: false` with a reason. **Both return
credit: false.** It never grants mutation credit by itself.

`initial_event_pilot/check.py` demonstrates the binding fields and public API.
Caller-provided expected hashes/plan must be trusted inputs from the lead's
source-bound collector, not hashes regenerated from an untrusted artifact.
Source hashes must also match the built-in archive pins. The lead must independently
validate the entire selected-case plan against full discovery and source/TRX;
all Theory rows of a selected method are atomic. Omitting a row from both an
untrusted plan and evidence cannot be detected without that separate contract.
The fixture checker derives its plan from all five independently run baseline
rows, then tests a shortened plan as a denial. Artifact hashes authenticate bytes
against trusted expected values; they are not digital collection attestations.

Keep full discovery/source/catalogue, CompileError accounting, true kill predicates,
external passing controls, restoration, provenance, and original qualification
gates separately. No existing caller imports this module. Lead integration and
independent review are still required. Unsupported adapters, protocol versions,
missing files/fields, ambiguous/retried/concurrent sessions, count-only evidence,
nonpassing cases, unfinished active cases and tamper return no affirmative proof.
Only single runner/single assembly/xUnit executor is supported; final null chunks,
other adapters and cross-host clock correlation are deliberately unsupported.
The current diagnostics are not a general authenticated hostile-log protocol.

## Reproduce and retained evidence

From the checkout:

```sh
python3 tooling/quality/storage/initial_event_pilot/run.py --dotnet /Users/derek/.local/share/chunkmaestro/dotnet/dotnet
python3 tooling/quality/storage/initial_event_pilot/check.py <fresh-output> <verified-extracted-source-root> --output-directory <new-derived-directory>
python3 -m unittest discover -s tooling/quality/storage -p 'test_*.py' -v
python3 tooling/specification/check.py
```

The runner copies only checked-in toy source/locked graph, adds two Theory rows,
uses an environment allowlist with disposable HOME/public package cache, absolute
100-second command limits and 300-second total budget with bounded process-tree
termination. It never executes old binaries or the product. A delayed row creates
a real StatsChange before completion, without adding test identities.

Fresh machine evidence:

- `/var/folders/8p/klwq9s9n3jg2hm6pqx99hc3w0000gn/T/cm-initial-events-o_ul8hn1`:
  first diagnostic attempt (no runner wire file); retained without retry overwrite.
- `/var/folders/8p/klwq9s9n3jg2hm6pqx99hc3w0000gn/T/cm-initial-events-9_en8spn`:
  explicit file logging correction, five passing results in the final chunk.
- `/var/folders/8p/klwq9s9n3jg2hm6pqx99hc3w0000gn/T/cm-initial-events-_unj6thc`:
  delayed fixture, initial StatsChange of two results plus final three; five exact
  passing rows accepted. Passing/skipped commands both exit 0 without timeout.
  Real skipped case outcomes and skip diagnostics are retained; validator rejects.
- `/tmp/cm-initial-delivery-proof`: final public API results/bindings and synthetic
  binding denials. The runtime skipped verdict first rejects its warning-level
  diagnostic; strict per-case and completion checks independently deny skips.
- `/tmp/cm-initial-storage-final-tests.log`: 158 passed (12 new test methods).
  `/tmp/cm-initial-spec.log`: specification PASS. Synthetic tests cover partial,
  canceled, aborted, runtime error, skip/None/failed, duplicate/conflicting/extra,
  unknown adapter/version, count-only, missing trace and tamper. They are not
  claims of real runtime cancellation or timeout reproduction.

Passing/skipped delayed toy commands took about 10.6/10.1 seconds. Their output
folders were 994,834/787,323 bytes, excluding command stdout and fixture builds.
Diagnostic volume scales with test cases and mutation sessions; there is no
speedup or product/global qualification claim. Parsing loads files/lines in memory
and JSON per message. Production-scale cost is unmeasured. Retain original raw
bytes privately with SHA-256 manifests; logs can include local paths, case data
and exception text. Use synthetic inputs and sanitized summaries for sharing;
redacted files must have distinct hashes and cannot replace the bound raw proof.
The final delivery directory additionally preserves logs, source hashes, bundle
and manifest; machine-local paths are not a permanent hosted archive.
