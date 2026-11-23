# Credential purpose and serialized recovery contracts

Issue [#11](https://github.com/MrFrydae/ChunkMaestro/issues/11), PR
[#16](https://github.com/MrFrydae/ChunkMaestro/pull/16). Implementation-only,
uncommitted test patch on frozen root `effd6fd032fa0cea3817fa7c3e8587656f8d081c`.
The credential implementation is the supplied PR head
`97d2a6074bbd6d75697206ce81b549deade58ddd`; the root also contains the stacked
issue-12 work. Lead owns source changes, integration, publication and acceptance.

## Source bindings and scope

- Q163: canonical nonsecret references recover the exact secret; caller storage
  remains intact while core-owned copies are zeroed. Two owned macOS Keychain
  cases establish native acceptance across closing/reopening the store.
- Q168: fleet, target and purpose isolate identical reference IDs. A mismatched
  reference refuses before dispatch; an internally consistent different scope
  cannot read the original item. Creating/deleting that separate item preserves
  the original. The exact adapter locator is also asserted.
- Q164: recovery is explicit at the reserved locator, with no implicit retry or
  replay after an ambiguous create. This is a credential-core contract, not
  acceptance of remote offline execution or controller migration.
- Q165–167: injected tests exercise the trusted local core; they establish no
  account, reauthentication or future role UI behavior. Q169's destructive
  workflow approval requirements remain outside this test-only slice.
- Q189/Q191: xUnit, portable deterministic adapter injection, controlled failures,
  and owned macOS native acceptance. Windows native execution remains unclaimed
  until CI; these tests establish no desktop accessibility or installer result.
- Q190: meaningful acceptance/recovery evidence only. No coverage percentage,
  mutation score, terminal survivors or full-module qualification is claimed.

The immutable pilot report and assessment were read at
`/Users/derek/.t3/worktrees/ChunkMaestro/t3-10585537/.artifacts/qa/credential-core-pilot/`.
That single pilot is **UNRESOLVED**, terminated at 180 seconds, with no terminal
report. Its provisional traces motivated these contracts; they are not measured
survivor verdicts. Originals were not modified. Stryker was not repeated.

Only `tests/ChunkMaestro.CredentialsTests/PurposeAndSerializationContractTests.cs`
and this document are owned. No production, shared, dependency, solution, CI or
existing-test files changed. Existing `FailureContractTests` already covers
invalid purposes, all-`a` size boundaries, isolated failure recovery, callback
reentry, and queued cancellation. The new contracts extend acceptance to `z`,
`0`, `9` and a literal 64-character mixed purpose, reconstruct the locator from
its fields, and queue a real successor *before* the first operation completes.

## Observable recovery

The recording adapter commits a create before its injected exception. The
waiting read must return those exact bytes at the reserved locator after either
create success or `Unavailable/Unknown`; only one create dispatch is permitted.
Other rows block a read and then throw from the adapter or consumer: a waiting
read must succeed afterward. A final delete proves continued lifecycle use.
Read refusal returns no buffer to the core; there is no owned returned buffer to
zero in that branch. Every buffer actually returned, including the failing
callback's buffer, is retained by the probe and checked for zeroing.

Cancellation rows queue create/use/delete behind a blocked read and also queue a
live successor. Cancellation must complete without an extra adapter effect,
without invoking a canceled callback or creating the canceled locator. Releasing
the active call lets the successor recover the exact bytes and then delete.
Caller arrays remain byte-for-byte unchanged. The canceled create's private
pre-dispatch copy is not exposed by the adapter: this does **not** independently
prove its zeroing; existing source review is still needed for that copy.

`TaskCompletionSource` with asynchronous continuations and `ManualResetEventSlim`
barriers establish entry and release. `WaitAsync` and barrier waits use two-second
bounds, with no sleeps or elapsed-time inference. There is no observable public
hook between acquiring the semaphore and dispatching native work. These tests
therefore do not independently prove the second cancellation check at that
instant. An already-canceled token is also refused by `WaitAsync`, so a redundant
check cannot be distinguished through that input. No stronger post-dispatch
cancellation guarantee or production seam was introduced.

Injected-store disposal is run on a task with a bounded wait. A deliberately
wedged gate can make cleanup itself time out (and mask the earlier wait's timeout
in the displayed stack); the external supervisor terminates its owned process
group. A normal run disposes each store and every native fixture. The native cases
use only `CredentialTests.NativeFixture`, its explicit owned Keychain handle and
random temporary path; fixture disposal deletes the entire owned Keychain even
if an assertion fails. No default Keychain, trust, search-list mutation or
production credential is requested.

## Commands and results

Local execution: 2026-11-23, macOS arm64, pinned SDK `10.0.404` verified using
`/Users/derek/.local/share/chunkmaestro/dotnet/dotnet --version` (exit 0).
Python: `/opt/homebrew/bin/python3.13`.
Evidence directory, relative to this checkout:
`.artifacts/qa/credential-purpose-and-serialization/`.

```sh
/Users/derek/.local/share/chunkmaestro/dotnet/dotnet test tests/ChunkMaestro.CredentialsTests -c Release --filter FullyQualifiedName~PurposeAndSerializationContractTests --logger 'trx;LogFileName=purpose.trx' --results-directory .artifacts/qa/credential-purpose-and-serialization
CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet /opt/homebrew/bin/python3.13 tooling/build/build.py verify
```

First affected run: exit 1, 11 passed / 2 failed. The supposed 64-character
literal was accidentally 68 characters and correctly refused with WrongScope.
`first-test.log` and `first-test.trx` preserve this failure. Corrected literal is
64 characters. Corrected run: exit 0, 13 passed / 0 failed / 0 skipped, including
two real owned macOS Keychain cases (`affected.log`, `purpose.trx`).

Root verify: exit 0 (`root-verify-first.log`), locked restore, Release build with
0 warnings/errors, syntax guard and generated-inclusive format verification all
passed. Suites: Avalonia 28, controller identity 33, credentials 74, persistence
329; total 464 passed, zero failed/skipped. This includes existing tests and is
local implementation evidence, not independent QA approval.

## External negative controls

A disposable external directory was populated from tracked inputs plus the new
test file. `external-copy.txt` records its exact path. One hand-edited change was
applied to the external `CredentialStore.cs` at a time, restoring original source
between controls. No worktree production source or runtime was instrumented.
`negative-controls.py`, `negative-controls.json`, supervision logs and per-control
logs retain exact command arrays, source hashes, exits and durations. These are
small manual negative controls, not another mutation campaign or a score.

Every command uses the absolute SDK above with
`test tests/ChunkMaestro.CredentialsTests -c Release --filter FILTER`.
Controls append `--no-restore`; the external baseline restores/builds normally.
Filters:

- Baseline: `FullyQualifiedName~CanonicalPurposeRecoversExactLocatorAndIsolatesScopes|FullyQualifiedName~WaitingOperationRecoversAfterActiveOutcomeWithoutReplay|FullyQualifiedName~CanceledWaiterDoesNotConsumeHandoffOrReplay`.
- Character controls: `FullyQualifiedName~CanonicalPurposeRecoversExactLocatorAndIsolatesScopes`.
- Broad release controls: `FullyQualifiedName~WaitingOperationRecoversAfterActiveOutcomeWithoutReplay|FullyQualifiedName~CanceledWaiterDoesNotConsumeHandoffOrReplay`.
- Narrow release follow-ups: `FullyQualifiedName~WaitingOperationRecoversAfterActiveOutcomeWithoutReplay&DisplayName~create-success`.

| Control | Exit | Observed result | Wall seconds |
| --- | ---: | --- | ---: |
| Unchanged external baseline | 0 | 11 portable cases passed | 1.356 |
| `<= 'z'` to `< 'z'` | 1 | 4 acceptance cases failed WrongScope | 0.969 |
| `>= '0'` to `> '0'` | 1 | 3 failed, 1 passed | 1.017 |
| `<= '9'` to `< '9'` | 1 | 2 failed, 2 passed | 0.961 |
| Remove create-path release, broad selection | -9 | Supervisor timeout, no credit | 15.016 |
| Remove shared-execution release, broad selection | -9 | Supervisor timeout, no credit | 15.016 |
| Remove create-path release, single row | 1 | 1 failed with bounded TimeoutException | 5.035 |
| Remove shared-execution release, single row | 1 | 1 failed with bounded TimeoutException | 5.012 |
| Remove create-path release, 14-second deadline | -9 | Supervisor timeout, no credit | 14.021 |
| Remove shared-execution release, 14-second deadline | -9 | Supervisor timeout, no credit | 14.011 |

The broad selections contain seven serial failing rows, so they exceed the
15-second command budget despite each individual wait being bounded. Both were
terminated at the supervisor deadline with owned process-group SIGKILL. Recorded
wall time includes approximately 16 ms of deadline/termination overhead beyond
15 seconds; no intentional extension occurred. Neither receives kill credit.
The original 15-second wait left insufficient room for termination overhead.
`negative-deadline-correction.py` repeats only those two hand-edited controls
with a 14-second deadline: both owned groups terminate within the hard 15-second
limit, with exits -9 and no credit. First results remain preserved.
A final process inventory found no external-copy dotnet/testhost processes;
the external source was verified restored to its frozen hash.
The single-row follow-ups completed naturally within five seconds with an xUnit
failure: missing create release prevents the queued read; missing shared release
prevents the subsequent delete after that read. They demonstrate actual loss of
continued lifecycle use, not a private-line assertion. Their bounded timeout
failures are not reported as a mutation score or terminal Stryker kills.

## Recovery, rollback and remaining acceptance

There is no production behavior change or migration. Rollback removes the two
new files. All temporary secrets are synthetic. Normal native fixture disposal
owns native cleanup; wedged negative runs are injected-only and own no Keychain
fixture. The external copy is retained for audit with original source restored;
no remaining run should be reused as a native store. The evidence hash inventory
and patch are in the local evidence directory. Lead must integrate the dirty
patch, rerun affected checks on its resulting head, obtain independent functional,
security/reliability and UX/performance verdicts, and inspect Windows CI. No hosted
dispatch, GitHub mutation, commit, push, approval or monitoring was performed.

## Lead integration

The lead verified all seven delivered source/patch bindings and placed only these
two files on the credential branch, independently of stacked identity changes.
The two native-purpose cases now execute the same real lifecycle on Windows as
well as owned macOS Keychains. On Linux they assert the real Unsupported refusal;
they do not return an untested success. A failed assertion after native creation
still cleans up its exact owned reference. Windows execution remains pending.

Integrated macOS arm64 verification passes 372 tests: desktop 28, credentials 74,
persistence 270, zero failures/skips. Locked restore, Release build, style/syntax
and formatting pass. The implementation bytes remain unchanged. This is test
evidence, not a mutation score or independent acceptance verdict.

## Hosted startup scheduling correction

At `cca89c3`, hosted Windows and Linux credential suites passed 67 cases each.
Intel macOS passed 73/74: the canceled-create handoff row timed out at the wait
for its offloaded adapter to enter, before cancellation assertions. Both native
purpose cases passed on Windows and macOS. Raw digest-verified TRX is retained in
`.artifacts/qa/pr16-cca89c3-notification/`; the failed run is not platform acceptance.

Other fixture collections were concurrently executing blocking native/barrier
tests. This collection now runs separately from them, while each test's explicit
concurrent operations remain required. Only adapter entry gets a ten-second
startup wait, matching existing fixture startup waits; handoff, cancellation,
continuation and disposal assertions keep their two-second bounds. This does not
assert a product latency guarantee or extend the CI command/job deadlines.

Corrected macOS arm64 root verification passes 372 tests with zero failures/skips,
including all 74 credential cases. Build/style/format pass. The first local compile
rejected the collection-definition type's `Collection` suffix (CA1711); it was
renamed without suppressing the analyzer, and that log is preserved. The earlier
negative controls remain historical evidence at their recorded source hashes.
Fresh hosted execution and final independent acceptance remain pending.
