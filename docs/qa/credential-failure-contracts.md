# Credential refusal, recovery and lifetime contracts

Test implementation handoff for issue [#11](https://github.com/MrFrydae/ChunkMaestro/issues/11)
and draft [PR #16](https://github.com/MrFrydae/ChunkMaestro/pull/16).
Production base: `ceb644ae1bbb3221aa564a74b5a77cbb315b15a5`.
Delivery branch: `test/11-credential-failure-contracts`; uncommitted patch, no push.
This is test evidence, not an independent QA verdict or feature acceptance.

Sources read: AGENTS.md, DOTNET.md, requirements Q165–168,172,181,189–193,
orchestration directive, development commands and slice/.NET/worktree/QA skills.
Q168 drives exact scope isolation; Q165–166 constrain the local OS-user boundary;
Q172 grants no listener/network scope. Q189–191 require meaningful refusal,
recovery, lifetime and platform evidence. Q181 and Q190 retain three independent
QA roles and applicable 90% line / 85% branch / 80% mutation obligations. Q193
prohibits successful placeholders. Retired storage mutation/manual receipts do
not waive qualification of this new credential module.

## Files and observable assertions

- `tests/ChunkMaestro.CredentialsTests/FailureContractTests.cs` (new): 34 injected
  core cases, using a stateful byte store behind the existing internal native seam.
  Public `CredentialStore` operations are the subject of each case.
- `tests/ChunkMaestro.CredentialsTests/CredentialTests.cs`: two new native size/
  recovery cases, and stronger create/delete assertions in the existing locked case.
- This new report. No production, project, solution, CI, dependency or lock edits.

The scope matrix creates real state in the injected adapter, refuses mismatched
fleet, target, purpose and empty reference on create/use/delete, asserts
WrongScope/NotStarted, zero additional adapter calls and unchanged original bytes.
Invalid fleet/target and null/empty/space/separator/NUL/Unicode/uppercase/65-character
purposes cannot reserve or dispatch. Null arguments cannot dispatch. Separate
boundary cases reject zero and 2561 bytes, accept 1 and 2560 bytes and purpose
lengths 1 and 64, check the caller's input remains intact and the owned copy clears.

Cancellation tests cover create/use/delete with an already canceled token and
while queued behind an explicitly blocked callback. They require cancellation,
no adapter call or callback, then successful access to the unchanged original.
The active callback always releases in finally. Post-dispatch create cancellation
is tested with success and with persistence followed by injected Unavailable:
the former returns the same reservation; the latter reports Unknown. Both can
read and delete at the exact reservation afterward. No compensation deletion or
post-effect canceled result is accepted.

Injected Denied/Locked/Unavailable before persistence report Unknown and leave
Absent at the reservation, followed by a successful explicit retry. An injected
failure after persistence reports Unknown while preserving the exact bytes;
retrying the same reservation reports Conflict/NotCreated and cannot replace them.
An unrelated ID remains Absent. UnauthorizedAccessException is translated to
Denied/Unknown with an exact sanitized message, no inner exception and a cleared
owned write buffer. Read/delete faults cannot invoke the consumer, delete data or
poison subsequent access after the fault is removed. These are core contracts,
not evidence of actual OS access denial or OS failure atomicity.

Callback tests preserve the exact thrown exception object, observe cleared read
buffers and require InvalidOperationException for create/use/delete/dispose
reentry. The store remains usable afterward. Disposal is observed during a bounded
100ms interval with a callback held active, then must finish, clear the read buffer,
dispose the adapter exactly once and refuse all three subsequent operations with
ObjectDisposedException. Event waits are bounded at ten seconds; collection has
a 150-second process deadline and a 60-second hang deadline.

## Native proof and limits

On macOS the existing fixture creates only a fresh disposable file Keychain,
compares search-list snapshots without inspecting item names, and deletes/releases
only that Keychain and its unique directory. It never changes the user search
list/default or persistent OS policy. Tests use synthetic bytes only.

Six cases actually exercised Keychain on this machine: the original lifecycle,
collision, locked and separate-process contention cases, plus two new boundary
cases (1 and 2560 bytes). New cases use 64-character purposes, verify a pure
reservation is initially Absent, create it, verify public Conflict/NotCreated and
preserved bytes, then reconstruct all reference components and reopen the store.
Mismatched scopes fail WrongScope; internally consistent different fleet/target/
purpose references with the same ID return native Absent on use AND delete.
A different ID is Absent. The original remains readable, then exact deletion and
repeat deletion/use report Absent. Cleanup uses only the exact reserved locator;
Absent during cleanup is allowed, other errors fail. The locked fixture now
requires Locked on read/delete and Locked/Unknown on create, without prompting.

Windows and macOS Intel were unavailable and were not executed. Linux Unsupported
is a conditional constructor contract in the suite, not native Linux evidence or
an executed result here. Real native Denied/Unavailable and failure after an OS
write remain unexecuted. The injected stateful adapter cannot qualify Win32 mutex,
OS memory cleanup, process crash/power loss or durability guarantees.

## Executed validation and retained evidence

Environment: macOS 27.2.1 (26B131), arm64, pinned SDK 10.0.404.
Evidence root: `.artifacts/credential-failure-contracts/` in the assigned checkout.
Original lead evidence at `.artifacts/credentials-11-delivery/` in the lead checkout
was not changed. The original alias-failure history remains there.

Set `CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet`
for repository commands and set `DOTNET_ROOT` to that executable's directory for
direct .NET commands. Commands executed:

```sh
python3 tooling/build/build.py verify
# Baseline: 316 passed (28 desktop, 18 credentials, 270 persistence).
# Final: 352 passed (28 desktop, 54 credentials, 270 persistence).
# Both: zero failures/skips; locked restore, Release build, style and format passed.

dotnet format whitespace ChunkMaestro.slnx --no-restore --include tests/ChunkMaestro.CredentialsTests/FailureContractTests.cs tests/ChunkMaestro.CredentialsTests/CredentialTests.cs

python3 tooling/ci/command.py --timeout-seconds 150 -- /Users/derek/.local/share/ChunkMaestro/dotnet/dotnet test tests/ChunkMaestro.CredentialsTests -c Release --no-restore --blame-hang-timeout 60s --logger trx --results-directory .artifacts/credential-failure-contracts/final2/coverage --collect 'XPlat Code Coverage' -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include='[ChunkMaestro.Credentials]*'
# Final: 54 passed, zero failures/skips; six native, 48 injected/core cases.
```

The baseline collector used the same filter with `--no-build --no-restore` and
`baseline/coverage` as results directory. Baseline raw log, TRX and Cobertura XML
are retained, alongside final logs/TRX/XML, environment, source/evidence SHA-256
manifest and patch. Intermediate logs remain: the first new-test build failed
because xUnit forbids Assert.Throws on a Task-returning call; corrected to awaited
ThrowsAsync. No product regression failed and no production defect was established.
An intermediate expanded suite passed 47 cases; final measurements supersede it.
Root verifier TRX files are copied into the evidence directory for preservation.

Coverage is from this single macOS run, not merged across platforms:

| Collector scope | Baseline line / branch | Final line / branch |
| --- | --- | --- |
| Credential module | 190/256 (74.21%) / 64/106 (60.37%) | 193/256 (75.39%) / 69/106 (65.09%) |
| CredentialStore class | 86.53% / 76.92% | 86.53% / 84.61% |
| Generated create state machine | 95.23% / 50% | 100% / 100% |
| Generated create closure | 71.42% / 100% | 100% / 100% |
| MacCredentials | 97.02% / 80.95% | 97.02% / 80.95% |
| WindowsCredentials | 10.16% / 16.66% | 10.16% / 16.66% |

These are collector class scopes, not interchangeable aggregate core scores.
Do not combine coverage with test counts into an acceptance verdict. Module
coverage remains below the applicable thresholds. Uncovered core lines include
default native constructor/OS selection, deliberately avoided on macOS because it
opens the normal default Keychain. Mac gaps include default-Keychain acquisition,
allocation failure and refusal of malformed stored payload lengths; Windows native
operations remain unexecuted. Queue cancellation does not expose the private
never-dispatched copy to the adapter, so its erasure is not directly observed.
Native immutable OS copies and crash-time erasure are also not observed.

## Bounded mutation follow-up (not executed)

Use the pinned repository Stryker.NET tool only in a disposable checkout containing
this patch. Keep configuration/results outside tracked source. First validate the
installed tool's supported flags; do not change the tool/package/lock graph.
Select `src/ChunkMaestro.Credentials/ChunkMaestro.Credentials.csproj` and only
`tests/ChunkMaestro.CredentialsTests/ChunkMaestro.CredentialsTests.csproj`; exclude
all storage projects and mutation tooling. Initial mutate glob: CredentialStore.cs
only. Limit concurrency to one and the entire pilot to 180 seconds, retain JSON/HTML,
raw log, selected mutation locations, source hashes and timeout exit. No automatic
retry, expansion or survivor exclusion. A timed-out/partial pilot has no module
score; subsequent budget must be chosen explicitly. Run MacCredentials separately
with the owned fixture, WindowsCredentials only on Windows. Do not claim a pilot
subset meets the module-wide 80% requirement.

Catalogue of specific behavioral changes the contracts should expose:

- Remove each fleet/target/purpose/reference/size guard: refusal category, no native
  calls and original-byte preservation should fail; native scope tests also catch
  lookup-name component omission.
- Replace create-only with replacement or conflate Conflict with Unknown: preserved
  winner bytes and NotCreated assertions should fail.
- Ignore queued cancellation or apply cancellation after persistence: call counts,
  callback guards and exact-reference recovery assertions should fail.
- Delete on ambiguous create or allocate a replacement locator: recovered bytes,
  same-reference result and unrelated-reference Absent assertions should fail.
- Remove owned-buffer clearing: exact nonempty retained-array assertions should fail.
- Remove callback reentry/disposal gate or reuse disposed adapter: exact exception,
  blocked-disposal, adapter-lifetime and post-disposal call assertions should fail.

Uncovered zeroization of OS-owned buffers, process kill boundaries and Windows
cross-session mutex behavior need platform-specific evidence rather than extra
status-map assertions. All three independent exact-head QA roles remain required
before integration/merge; this author cannot approve these test changes.

Rollback of this uncommitted patch is removal of the two new files and reversal
of the scoped CredentialTests.cs additions after preserving evidence. No native
fixture survives successful cleanup. Never enumerate/delete credentials by prefix;
retain an uncertain production reservation and reconcile only that exact locator.

## Lead integration

The lead verified all 26 delivery manifest entries and retained the original
source/evidence separately before copying these three owned files. Integrated
macOS arm64 repository verification passes **352 tests**, zero failed/skipped,
including all 54 credential cases. Locked restore, Release build, syntax and
generated-inclusive format checks pass. The managed credential command now also
collects raw XPlat coverage on Windows/macOS/Linux within its existing 150-second
deadline, for later source-bound platform accounting. Actionlint passes.

This does not establish combined coverage or an 80% module mutation result.
The draft PR remains blocked on final native measurements and independent QA.

At `f1c1b5c`, the lead audited fresh hosted TRX and artifact digests: all 54 cases
passed on Windows and macOS Intel, with zero failures/skips. Linux passed the
same count through core and explicit unsupported contracts, not native adapters.
The tested PR merge tree exactly matched that head's tree. Direct storage safety
passed; `qa-evidence` correctly refused the absent independent attestation.

The initial Cobertura reports combine branch outcomes per line, so their platform
percentages cannot be added to establish combined coverage. The credential-specific
runsettings now collect OpenCover with distinct branch offset/path identities,
including both native adapters and every credential type, without exclusions.
Local validation executed all 54 cases within the same 150-second deadline and
produced 106 distinct branch points. That verifies report granularity, not the
combined threshold. Production code and test identities are unchanged.
