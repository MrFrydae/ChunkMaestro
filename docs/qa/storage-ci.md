# Storage CI: fast feedback and explicit qualification

Issue 3; Q189–195. Routine pushes should not start hours of mutation testing.
The owner's 90% line, 85% branch and 80% actual mutation requirements still apply
before accepting safety-critical storage changes.

## Ordinary pushes

`Storage checks` runs the full Windows/macOS storage test suite and Coverlet
coverage. Existing managed CI keeps locked restore, Release build, formatting and
style checks. The fast storage path does not restore Stryker, run mutations,
create full-volume images or perform native mutation qualification. Its distinct
`coverage-manifest.json` / `COVERAGE_COLLECTED` result cannot satisfy the full gate.
There is no filtered subset pretending to be the normal runtime suite.

The runtime command has a ten-minute absolute bound; its containing step allows
12 minutes and its job allows 20. Failures retain command output, TRX, coverage
and a human-readable job summary. These are bounds, not predicted completion times.

## During development

For a focused mutation diagnostic, use the pinned SDK and restored repository
tool from `tooling/quality/storage`, for example:

```sh
"$CHUNKMAESTRO_DOTNET" tool restore
"$CHUNKMAESTRO_DOTNET" stryker --config-file replay/persistence.json --mutate 'LocalFleetStore.cs' --output ../../../.artifacts/storage-targeted
```

Use a fresh output directory. This selects only the changed file for a developer
feedback run; it is not qualification evidence and cannot replace the complete
module catalogue or count as a full-module score. Serialize local mutation and
build processes because they modify ignored build outputs.

## Final candidate

Freeze the candidate commit, then have the repository owner add the PR label
`storage-qualify`. Remove/re-add the label to request another candidate after a
push. The label event starts full Windows/macOS coverage, Domain/Application
mutations, the official/private Persistence pair, real full-volume tests, macOS
native qualification and the aggregate gate. Normal synchronize events do not
start or cancel these long runs. A result for an outdated SHA cannot qualify a
newer commit.

Requested qualification and unrelated label events have separate concurrency
groups, even at the same SHA. Adding a documentation/triage label cannot cancel
the long requested run. Repeated genuine requests for the same SHA still deduplicate.

The workflow also supports `workflow_dispatch` once available on the default
branch; select the candidate branch. The label route supports initial PR setup
before that workflow has reached main. Optional profiling/concurrency diagnostics
remain dispatch-only and off by default.

Full-run names are explicitly `Storage qualification`; unrelated label events
are named `Storage qualification not requested`. The QA merge check authenticates
GitHub's latest requested run for the exact head, requires successful Windows and
macOS measurement jobs and the actual aggregate gate, then independently requires
the lead-attested role verdicts. Skipped jobs, fast checks, stale runs, failed or
pending qualification do not pass. Read-only Actions metadata permission supports
this verification; no workflow execution token gains write permission.

After qualification and independent reviews finish, the lead publishes the
current-head QA aggregate and reruns `qa-evidence`. Full results stay in the
platform artifacts and gate JSON. A collection job succeeding is not evidence
that the combined safety thresholds passed.

## Apparent hangs

Long mutation commands emit a heartbeat at most every 30 seconds, including
elapsed/remaining time, age of the last observed log change, observed completed
test-session count and the latest timestamped Stryker event when available.
A test-session count is not a unique-mutant count or kill score. Console excerpts
show new bytes rather than repeating stale tails. Log activity alone does not
prove useful progress.

A mutation command with no console or main Stryker debug-log update for 600
seconds stops with `log-inactivity`. Absolute deadlines remain unchanged and stop
with `absolute-deadline`. Both use bounded process-tree termination and preserve
raw evidence; an unconfirmed termination is reported explicitly. Ordinary quiet
build/test commands retain their absolute bounds rather than this mutation-only
inactivity rule. A genuinely silent mutation phase lasting ten minutes becomes
unresolved and requires investigation, not automatic success or a blind retry.

Failure records and job summaries show the reason, elapsed time, last-log age
and retained log filename. Neither type of timeout receives mutation kill credit.
Full Windows commands remain bounded to 5400 seconds official / 7200 seconds
private; the explicit campaign can still be long. Scheduling avoids paying that
cost on every edit, while progress explains the work during qualification.

## Validation and recovery

Local macOS fast-path execution passed 195 tests with coverage. This does not
establish new Windows runtime or full qualification. Synthetic contracts cover
routing, coverage-only refusal, exact-head/full-job requirements, stale/skipped/
failed evidence, inactivity termination and activity reset. No new mutation score
or measured hosted speedup is claimed.

Reverting the scheduling/progress change restores the former execution path.
It does not alter databases, migrations, secrets, product behavior or thresholds.
