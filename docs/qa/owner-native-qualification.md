# Bounded owner native qualification fixture

Issue12; frozen accepted main `0df7245e52e910d12be00c4155c53ee03be6f03e`.
Bridge PR21 is an INTERNAL checkpoint after all three independent QA roles and
18 CI checks. Actual native authentication remains **NOT AUTHORIZED**. This
fixture does not accept issue12 or provide public owner authority.

Traceability: Q14/145/149/153 preserve native direct services and local authority;
Q163–169 separate OS identity from CorePermission, key possession and Keychain;
Q174 requires revocation; Q181/189–195 and the 2026-11-23 testing decision require
independent QA and truthful evidence. Coverage is diagnostic, with no score gate.
See [the production ABI and bridge limits](macos-owner-verification-bridge.md).

## Safe local execution

From the repository root, using existing Python and Apple compiler only:

```sh
python3 -m unittest discover -s tooling/owner-qualification -p 'test_*.py' -v
python3 tooling/owner-qualification/run.py --no-prompt --reviewed-head REVIEWED_40_CHARACTER_SHA --desktop-confirmed-unlocked
```

Replace the uppercase placeholder only with the exact reviewed fixture commit.
The operator must confirm that the local desktop is currently unlocked before
supplying the desktop flag; that confirmation is not an owner identity proof or
authentication authorization. Do not automate or infer that confirmation from a
missing dictionary key. The runner binds HEAD and every fixture/production source
to that commit, then compiles a private byte snapshot. A later live source edit
cannot change compiler input. A changed or uncommitted fixture refuses.

No arguments, unknown arguments, combined modes, missing confirmations and any
present GITHUB_ACTIONS variable refuse before children or build directories.
A non-root local macOS console/login session with matching UID is required before
compiler launch. The public CGSession dictionary is point-in-time metadata and
does not attest lock state. No undocumented screen-lock key, private lock API,
unlock, settings change or
Screen Recording request is performed. The host repeats session checks before
AppKit startup and requires an actual owned visible nonminiaturized NSWindow with
a positive window number. If prerequisites fail, UNAVAILABLE/NOT RUN replaces PASS.
Do not run on an uncertain/locked session; metadata cannot attest owner identity.

The runner uses one unique external temp directory, including isolated child HOME
and TMPDIR. Its fixed compiler flags link the exact unmodified production
`Native/mac-owner.m` and header, with frozen SHA256 checks and no test defines,
provider, mock or replacement symbol. Only Foundation, AppKit, LocalAuthentication
and CoreGraphics are linked. The inherited credential/tool injection environment
is replaced by a five-key allowlist. Build deadline is 60 seconds; native no-prompt
deadline is 20 seconds. Combined stdout/stderr limit is 1 MiB per command. Each
owned process group is killed and leader reaped even when the leader exits first;
failure to prove group disappearance fails the command. Build directories and logs
are retained for review, never automatically deleted. No shared scratch/live data,
Credentials, Persistence, owner binding or VM is used. No dependency is installed.

The no-prompt host creates one disposable visible owned window. It checks ABI2,
foreign pointer bits, stale generation, off-main refusal, actual positive capture,
single-use binding, no-interaction availability probe, cancel/release and removed
ingress. A fresh request captures the same window, whose real close must revoke
it. Main-queue fences follow production's queued observer cleanup; these establish
queue progress, not internal observer-count or ARC-deallocation measurement.
No evaluation entry is called by this path. The no-prompt label has no Verify
button. Probe availability is not authentication. NSWindow visibility metadata is
**not independently observed native screen evidence**. No screenshot is requested
or inferred from it; physical evidence is NOT RUN unless separately collected
using an already permitted native surface. Do not request capture permissions.

## Later owner participation — not run or authorized by this patch

The parent first reviews this exact patch, integrates risk classification outside
this worker's ownership, and obtains fresh independent functional,
security/reliability and UX/accessibility/performance review. No real prompt may
run before those reviews and a separate concrete owner permission request.

A future request should name the reviewed source/artifact hashes, machine, single
scenario (success, OS cancel, app Cancel, close-before/after-start or pending
cleanup), expected single prompt, limits and retained sanitized evidence. Ask for
one run of that scenario; approval never authorizes repeats or the other scenarios.
Only after that approval, the owner may manually launch the runner with the
explicit `--interactive` mode. This command is intentionally omitted from safe
copy/paste instructions. The owner must then deliberately click the clearly labeled
Verify button once. Launch itself never evaluates; no autostart, retry, automation
click or dialog approval is permitted. Every process consumes one attempt even if
capture/admission fails. Subsequent scenarios require separately reviewed approval
and a fresh process. Exiting does not permit silent re-prompting.

Verify creates a fresh ABI2 request and captures the current real owned window on
main. Its sole evaluation entry transfers immutable request/generation/window,
nonce and cookie binding. The callback validates those fields, the known raw
result range and phase0-before-phase1 with matching result. It emits only fixed
event names, statuses and the raw numeric diagnostic category, never nonce,
session bytes, authentication data or OS error descriptions. Successful raw policy
outcome remains diagnostic: no CorePermission, key possession, Keychain unlock,
public success boolean or owner authority exists here.

Cancel irreversibly revokes, including before Verify: it consumes the prepared
attempt and disables Verify, so a later click cannot create a request. Closing
releases ingress and reports pending/unknown
if no phase1 acknowledgment arrived. Failed, mismatched or silent delivery retains
unknown ownership, never claims clean drain and never enables replacement. The
process has a 180-second runner deadline; timeout/process exit is not safe cookie
drain. Phase1 certifies cookie drain only, not complete native ARC deallocation.
Real success, OS cancel, close, cleanup, focus, keyboard and screen-reader behavior
remain later manual qualifications. No Windows or installer evidence is implied.

## Evidence and review handoff

Each runner invocation prints its unique external bundle path. `report.json`
records preflight, mode, OS/runtime, exact argv/environment, timings, exits and
limits. Per-command raw logs are bounded and sanitized by the host's fixed
protocol; compiler errors contain source diagnostics only. `source-manifest.json`
binds host, runner, tests, documentation and production sources;
`artifact-manifest.json` hashes retained files (excluding itself). Original first
failures remain in their original bundles; reruns create fresh bundles.

Review the owned patch and hashes against frozen main, then run the offline suite
and safe native command independently. Inspect every host assertion, stdout/stderr,
exit and group reaping; a process launch or lack of prompt alone proves nothing.
No .NET files/projects are changed, so managed product acceptance is not supplied.
Parent owns full-repository checks and all-role QA; implementer checks are not QA
verdicts. Rollback removes only `tooling/owner-qualification/**` and this document.
Preserve existing signing configuration and all original external evidence.

## Implementation observations on this machine

Implementer evidence only, macOS Darwin 27.2 arm64. The safe native run observed
15 passing assertions plus a passing completion marker, real owned window capture and real close revocation, with
two main-queue cleanup fences. Evaluation was not called. The offline suite has
11 tests, including teardown after an exited leader leaves a descendant, output
limits, timeout log retention, launch/compile failures, missing-observation refusal
and pre-child mode/hosted guards. Interactive tests, authentication, independent
screen observation, accessibility, Windows and final independent QA are NOT RUN.

Handoff directory: `/private/tmp/chunkmaestro-owner-fixture-handoff-mfBzmZgs/`.
Its final-native-bound stdout points to the final unique native bundle. The original
session-key lookup refusal is retained in
`/var/folders/8p/klwq9s9n3jg2hm6pqx99hc3w0000gn/T/chunkmaestro-owner-qualification-ryuo4pj8/`;
the original compiler failure (unsupported `explicit_bzero`, replaced by a volatile
byte wipe) is retained in the neighboring `chunkmaestro-owner-qualification-du0__ktp/`.
Neither is overwritten by corrected runs.

Repository `build.py build` and `build.py format` were attempted with the pinned
private .NET SDK, without restore. Build failed NETSDK1004 for missing assets;
formatter failed unresolved references with the same unrestored graph. Raw logs
are in the handoff. No package installation/restore or managed test was performed.
These failures block any claim of a passing full-repository check. No C# or shared
build policy changed. Parent must restore under its own authorization and complete
required checks and independent QA before considering any manual prompt request.

The actual compiled host also refused missing arguments, unknown arguments and
GITHUB_ACTIONS-present no-prompt launch with exit 64 before AppKit startup. Each
process group was confirmed reaped. These checks never invoked interactive mode.

## Lead integration corrections

The original four-file delivery and 55 artifact hashes were verified and remain
preserved. Its 15 real no-prompt assertions are implementation evidence for that
earlier fixture, not a claim that the corrected runner was executed interactively.
The original parent-HEAD equality check would refuse the fixture once committed.
The runner now requires the explicit reviewed fixture SHA, verifies each source
against its Git blob, and compiles a private snapshot of those exact bytes. Native
production hashes remain pinned to the accepted bridge. Both modes also require
explicit operator confirmation of an unlocked desktop; no undocumented lock key
is read or absence interpreted as unlocked. These flags do not authorize an OS
prompt or qualify a session/principal.

Command interruption now reaps the owned group before recording failure. A
teardown error is recorded separately and cannot erase the original exit/output
or become success. Fresh offline validation passed 15 tests, including SIGTERM,
cleanup-error preservation, changed-source refusal and snapshot stability after a
live edit. The updated native host compiled with warnings as errors. Fresh real
window execution of the corrected fixture, independent all-role QA and any
interactive qualification remain pending. Parent CI/risk integration is separate.
Logs and source bindings are under `.artifacts/owner-fixture-integration/`.

## Independent preflight corrections

The original preliminary FAIL at local head
`b5644f1a575262a2bdd0a6b8e657ade5166e89e6` is preserved under
`/private/tmp/chunkmaestro-security-preflight-c85f8f0a-2930-4b57-b6f6-805eba2e4bb7/`
with its reproduction-only addendum. SR-01 reproduced SIGTERM during cleanup
discarding the primary command record. SR-02 found that Cancel before Verify
left the prepared attempt available. Both new regression cases failed before
the correction; their original log is retained separately.

During cleanup and record writing, SIGTERM/SIGINT are now recorded without
interrupting preservation of the first result. Handler restoration follows record
writing. A cleanup signal/error cannot become success, even if the child exited0.
Cancel now consumes the prepared attempt before native effects and disables its
button. A compiled no-window Objective-C action probe invokes Cancel then Verify,
asserting no request/evaluation is created; it starts neither AppKit nor OS policy.
This is handler-state proof, not physical UI or authentication evidence.
Fresh offline validation passed18 tests on macOS, including both real-signal
cleanup timings and the no-window native action case. Independent correction
recheck and actual native/authentication qualification remain pending. Original
FAIL evidence and older successful checks remain attributed to their exact code.

The subsequent artifact recheck found SR-03: a signal after JSON serialization
could fail the returned result while the saved command record still looked
successful. That original FAIL and its reproductions remain unchanged under
`/private/tmp/issue12-sr-correction-ayvryboz/`. The new write-window regression
failed in all four signal/child-exit combinations before this correction.

Command records now publish complete JSON through an atomic rename. A signal
received during serialization, writing or initial publication is sticky and is
included in the final saved and returned failure snapshot. Repeated cancellation
cannot change that snapshot during its final rewrite. A new signal after a
successful command's publication interrupts the enclosing caller instead of
retroactively changing the completed command. Such an interruption does not
produce a successful enclosing qualification result. Atomic publication is not a
claim of persistence across power loss.

Fresh offline validation passed 23 tests, including serialization, write-window,
rename, repeated-signal and post-publication interruption controls. The prepared
Cancel/Verify no-window probe remains included. This is an unsigned artifact
candidate based on the same local parent; signing availability and a new
independent artifact recheck are pending. It is ineligible for final QA
aggregation. No window, authentication prompt or production authority was used.

The v2 artifact recheck retained two material findings: SR-03-R (a failed
cancellation rewrite left the earlier success record published) and SR-04 (a
signal interrupted restoration of the process-global handlers). Its original
FAIL and execution-provenance addendum remain unchanged under
`/private/tmp/issue12-security-v2-86o3vb9l/` and the adjacent addendum directory.
Both new regression methods failed against v2, across six subcases.

Publication now starts with an explicitly incomplete command record containing
the primary child outcome. Only a subsequent successful atomic publication marks
the evidence complete. The success predicate requires this explicit complete
state; missing, unknown or incomplete evidence cannot pass. Failed final writing
or rename therefore leaves incomplete evidence and the original output/exit,
and propagates failure to the caller. Cancellation after snapshot freeze aborts
the caller; it cannot mutate the frozen snapshot during publication.

Handler restoration defers cancellation and temporarily blocks SIGTERM/SIGINT
until both prior handlers are restored. Newly pending signals are consumed and
propagated afterward; the original signal mask is restored. An existing exception
continues propagating rather than being replaced by deferred cancellation.
Fresh offline validation passed 27 tests, zero failures/skips. This includes final
write/rename failures, real signals at handler restoration, explicit incomplete
evidence refusal and original-mask preservation. Two initial test-double failures
from the new complete-state contract are retained with the corrected test run.
The candidate remains unsigned and subject to a new independent artifact review;
native/authentication qualification and final all-role QA remain unexecuted.

The v3 recheck found SR-04-R: a second blocked signal arriving after the pending
snapshot could dispatch the restored default SIGINT handler during unmasking.
That replaced a propagating publication OSError with KeyboardInterrupt, although
the original child evidence survived and no success was reported. The original
308-artifact FAIL bundle remains under `/private/tmp/issue12-security-v3-d7v1xr_j/`.

The runner now captures the active publication exception before restoring
handlers/mask. If normal cancellation dispatch at mask restoration raises while
that error is propagating, the publication error remains primary and receives
a diagnostic note. Without an existing publication error, cancellation still
propagates. Signals delivered after restoration belong to the caller's original
handlers; this is not immunity to later cancellation or process termination.
The new real mixed-signal regression failed all six serialization/write/rename
and child-exit subcases before correction. Fresh offline validation passed 28
tests, zero failures/skips, including exact handler/mask restoration and retained
incomplete evidence. The unsigned candidate still requires independent recheck;
no window or authentication was executed.

The v4 recheck found SR-04-R2: ambient `sys.exception()` may refer to an error
already handled by the caller. Invoking command() inside that caller's except
block could therefore suppress restoration cancellation when this invocation
had no publication error. Its original 397-artifact FAIL remains unchanged under
`/private/tmp/issue12-security-v4-3ww4y67_/`.

Publication failure is now tracked explicitly by this invocation's own try/except
block. No ambient exception-state query is used. Without a failure from that
block, cancellation propagates even when the caller is handling another error,
and that caller error is not annotated or modified. The new regression failed
all six single/mixed-signal caller-exception subcases before correction. The full
29-case offline suite passed with zero failures/skips, including the previous
publication-error preservation cases. This remains an unsigned artifact awaiting
independent recheck, with no actual native window or authentication execution.

## Main-branch and CI integration

The v5 unsigned source artifact passed independent security preflight with 29
offline tests and 76 additional controls. That report is retained under
`/private/tmp/issue12-security-v5-dt_1l8mz/`; it is not an exact-commit final QA
aggregate. Integration starts from accepted main
`82e7c27fc7aa7579efbaff6bc2ff834437db451a`, with the same runner, host and test
bytes. Production bridge hashes remain pinned to the accepted PR21 code.

The macOS managed CI job runs only the offline unittest suite, supervised for at
most 60 seconds and retaining its log. It does not invoke the graphical fixture
entrypoint. Its no-window NSObject Cancel/Verify probe compiles with the existing
Apple frameworks and never initializes NSApplication or evaluates OS policy.
The hosted marker is explicitly supplied after the standard CI environment
filter, so accidentally invoking the guarded fixture still refuses hosted use.
Both qualification-tool paths automatically require all three independent QA
roles. Hosted execution, signing, exact-head final QA and real native
qualification remain outstanding; no prompt is authorized by this CI wiring.
