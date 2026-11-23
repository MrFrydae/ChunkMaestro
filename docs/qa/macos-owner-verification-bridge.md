# macOS internal policy evaluation bridge

Issue12 remains OPEN. Implementation parent is local signed
`99d43be26034ab00e1645cf33e2133164407baef`, whose direct accepted-main parent is
`a504b3b049e1b2826267a47220b507d313ec8d33`. The isolated checkout started clean.
The commit contains an SSH signature; local verification lacks an allowed-signers
file. Signing configuration was preserved. No commits or external writes were made.

This continuation adds actual INTERNAL LocalAuthentication evaluation/callback
plumbing. No real authentication prompt was invoked. The public
IOwnerIdentitySession and OwnerFleetSession remain unchanged and denial-only.
There is no production caller, provider injection, public success boolean, token,
credential access, owner binding, SQL admission or Desktop integration.
A raw policy observation is never owner qualification, possession or CorePermission.
OS success does not unlock the selected file Keychain.

Q14/145/149/153 retain native direct services and local storage authority;
Q163–169 separate OS identity, reauthentication and core permission; Q174 requires
safe cancellation. Q181/189–195 and the later 2026-11-23 policy retain independent
QA and meaningful security evidence with diagnostic coverage. Qualified session
monitoring remains necessary before enabling public owner authority. Diagnostic
session GUIDs and matching UID snapshots cannot establish that monitoring.
No private lock APIs or principal/session qualification are implemented here.

## Inputs and provenance

Original bundles remain unchanged:

- `/private/tmp/chunkmaestro-owner-ui-design-93976830-20261123-203637/`
- `/private/tmp/chunkmaestro-owner-ui-review-93976830-20261123-205546/`
- `/private/tmp/chunkmaestro-owner-ui-ordering-correction-93976830/`
- `/private/tmp/chunkmaestro-owner-ui-correction-review-93976830-20261123-independent/`
- `/private/tmp/chunkmaestro-owner-verification-a504b3b-20261123/`

All 54 design/review hashes and 53 original partial-delivery hashes were checked.
Historical AUTH-D01 FAIL remains immutable; corrected design PASS is design-only.
The original partial's 537 tests/320 native assertions do not attest this source.
Only installed public Foundation, LocalAuthentication and AppKit frameworks are
linked with ARC/blocks. No NuGet/package/SDK/solution/workflow change was made.
Apple API contracts are in the original retained official snapshots and installed
LAContext.h/LAError.h. `evaluatePolicy` has no NSWindow attachment API; this bridge
makes no sheet-attachment or system-authentication attestation claim.

## C ABI v2 and internal call sequence

`Native/mac-owner.h` is the authoritative protocol. ABI uint32, statuses/results
int32, request/generation uint64, window uintptr, sizes size_t, nonce bytes uint8
match managed uint/int/ulong/nuint/byte. There are no returned error strings,
NSError pointers or caller-derived ObjC receivers. Wrong versions, lengths, zero
scopes, unknown policy errors and foreign windows refuse. Accessible non-null
buffer memory remains a trusted in-process C caller obligation.

Create copies exactly 32 nonce bytes and 16 diagnostic session bytes, requires a
nonzero generation, allocates a distinct LAContext, sets Touch ID reuse to zero
and interactionNotAllowed YES. IDs increase without reuse; overflow refuses.
Managed random nonce bytes are zeroed after create; callback binding retains a
separate copy until drain. Probe creates its own no-interaction LAContext, so it
cannot race evaluation by mutating the evaluated context. Create/Probe/Prepare
cannot reach evaluate. Prepare remains refusal-only.

Status values 0–6 retain ContextCreated/Available/Unavailable/Unsupported/
InvalidInput/Canceled/Failure. New 7 means callback ownership transferred and
queued; 8 means a real window object was captured, never permission. Create uses
ABI2, and tests intentionally reject ABI3. Raw observations are 0 PolicySatisfied,
1 Denied, 2 Canceled, 3 Unavailable, 4 InvalidWindow, 5 Unknown. Only an OS reply
with success YES and no error maps to raw PolicySatisfied in the production build.
Unknown domains/codes map Unknown. No canEvaluate call runs inside an OS reply.

Future trusted/manual composition must first call CaptureWindowExplicitly on
AppKit's actual main thread with a current owned visible, nonminiaturized NSWindow
and matching generation. Native code compares integer bits against NSApp.windows
before messaging any window; it retains that actual object and arms will-close
revocation. Capture is single-use and cannot rebind. Retention prevents address
reuse after capture. Before initial capture, pointer provenance still belongs to
the trusted UI caller; no raw address can establish its own historical identity.
Absent/foreign/closed windows refuse. Off-main capture returns Unsupported.

Only then may EvaluateExplicitlyAsync transfer a callback to the separate explicit
native entry. Evaluation dispatches asynchronously to AppKit, validates the same
retained owned visible window, and reserves OS start under the request state gate.
No managed/native gate surrounds OS queries, evaluate, invalidate, dispatch,
external callbacks or waits. There is no dispatch_sync, Task.Wait or Task.Result.
The main AppKit run loop must be running; a console test runner is insufficient.
Other OSes return Unsupported before loading this Mac library.

## State, cancellation and ownership

Queued admission installs immutable request/generation/window/nonce/cookie binding
and reserves the process-wide active slot. Only one evaluation may be active;
a stalled callback or failed terminal delivery blocks replacement evaluation.
The request cannot evaluate twice, including after successful terminal drain.
Managed repeated Evaluate calls return the same task, never restart native work.
An invalid managed entry before admission returns Unknown without queueing.

Revoke sets irreversible canceled state under the gate before calling invalidate
outside it. If revoke wins queued start, no evaluate occurs. If start wins, that
one OS invocation may follow invalidate; no absence-of-prompt guarantee is made
for cancellation after start admission. Every later success is suppressed if
revoke wins terminal observation ordering. Window close uses the same revocation;
hidden/removed/window mismatch at reply maps InvalidWindow. Changing the supplied
generation refuses; a future generation requires a new request, never mutating an
old request's generation. No OS lock/switch/power coverage is inferred from this.

Lookup obtains a strong ARC reference before releasing the registry gate. Release
removes ingress then revokes/invalidate outside it. Active callback ownership is
separate from ingress, so lookup/release/cancel races cannot free active context.
Queued blocks and OS reply blocks retain native state. Window observer removal
runs on main; a captured request released before evaluation queues observer cleanup.

Native terminal-once state clears delivery ingress before invoking managed code.
Phase0 validates exact identity and stores the sanitized terminal result. Returning
1 acknowledges receipt only. Phase1 signals safe cookie drain while the process-wide
busy barrier remains held; managed frees its GCHandle exactly once, zeroes the retained
nonce, and completes its TaskCompletionSource with asynchronous continuations.
Only a successful phase1 acknowledgment removes the native busy record. Reentrant
or concurrent replacement admission during either callback refuses; failed phase1
delivery keeps the barrier held without a remove-and-reinsert gap.
Late/duplicate OS replies see native terminal state and cannot access the freed
cookie. Phase mismatch/identity mismatch/managed callback failure returns 0;
failed terminal/drain delivery retains native state and the managed GCHandle as
unknown, with no retry, timeout-cleanup or replacement evaluation.
Native closure references may outlive cookie drain; their terminal guard prevents
any subsequent callback or OS start. Drain certifies safe cookie release, not a
physical guarantee that every ARC reference has already deallocated.

Dispose revokes and releases ingress idempotently without awaiting an OS reply.
DisposeAsync additionally awaits the shared evaluation drain task; no artificial
timeout marks cleanup complete. A stalled OS callback keeps disposal pending and
resources owned until real drain or process exit. The finalizer performs ingress
release/revocation; the completion GCHandle roots a separate state object, not
OwnerContext, allowing that fallback to run. No competing replacement may be
started to bypass an unknown active slot. Finalizer timing and real UI disposal
responsiveness still require platform qualification.

## Executed checks and scope of evidence

Evidence: `/private/tmp/chunkmaestro-owner-evaluation-99d43be-20261123/`.
`commands.json` records argument arrays, exit codes and timings; `environment.json`
records paths/runtime/toolchain; source/artifact hashes bind the uncommitted patch.
Set CHUNKMAESTRO_DOTNET to `/Users/derek/.local/share/chunkmaestro/dotnet/dotnet`,
DOTNET_ROOT to its parent, MSBUILDDISABLENODEREUSE=1 and
DOTNET_CLI_USE_MSBUILD_SERVER=0. Python is `/opt/homebrew/bin/python3.13`.

Final checks: locked restore, Release build with zero warnings/errors, syntax and
generated-inclusive formatter verification, 542 repository tests (12 bridge),
21 build-tooling and 50 CI-tooling tests, all 205 specification checks passed.
The ordinary existing disposable vault fixtures ran; no new vault operations were
introduced. Native real no-prompt harness: 325 assertions including the retained
100 allocation/probe/cancel/release races and real invalid-window capture refusal.
Separate callback simulation: 72 assertions. Both passed ASan. x86_64 dylib
compiled with warnings as errors; Intel execution was not performed.
Managed coverage is retained diagnostically; native source is not measured by it.

The synthetic binary includes the SAME production native registry, callback,
window-observer, admission/cancel and ownership code, replacing only owned-window
lookup and the OS evaluate invocation. It has no real evaluatePolicy call or
public fake export and is not shipped in the production dylib. Synthetic success
is never actual OS verification. Tests cover cancel-before-dispatch, dispatch wins,
release while callback survives, late/duplicate reply, stale generation, absent
window, close/hide after start, failed terminal delivery, blocked replacement and
phase-separated drain. Managed simulation covers identity/nonce mismatch, unknown
result, revoke, retained cookie through terminal receipt, exact release on drain,
and idempotent pre-transfer refusal. Native capture of a real positive NSWindow,
real prompt behavior, actual callback timing, managed DisposeAsync while a real
AppKit callback is pending, finalizer timing, principal/session monitoring,
accessibility/focus, Windows/Linux execution and installers remain unexecuted.
The native harness is the practical run-loop alternative to console-runner UI tests.

Initial checks failed CA1816 (explicit SuppressFinalize in DisposeAsync), CS0051
(internal enum in public test signature), and the multiline syntax guard for a
compact Cookie accessor. All were fixed without suppression; initial logs remain.
No independent implementation QA verdict is supplied by this implementer.

## Manual qualification: explicit later owner opt-in only — NOT RUN

After three independent implementation reviews of these exact source hashes,
prepare an isolated native AppKit test host with one ordinary owned visible window
and a running main loop. It must not reference Credentials, Persistence or public
owner use cases. Require a deliberate explicit opt-in before calling evaluation.
Allocate ABI2, capture that host's actual NSWindow on main with matching generation,
then queue cm_owner_evaluate_explicit with the versioned two-phase callback.
Record phase0 sanitized category and phase1 safe drain separately; never log nonce,
OS error descriptions or user authentication data. Do not reuse a request.

With separate freshly created contexts, review and explicitly authorize each
manual scenario: OS success, user cancellation, available password/biometric paths,
window close before/after start, app cancellation, duplicate UI intent, and close
while disposal awaits. Confirm no UI from default Create/Probe/Prepare and no
Keychain/core/storage effect after raw OS success. Qualify native focus, keyboard,
screen reader and event coverage on actual supported machines. Unqualified lock,
switch and suspend monitoring continue to block public owner authority. A stalled
callback must remain unknown/pending; exiting is not a successful drain receipt.
No such host or interactive run was executed in this delivery.

Rollback restores only this task's three owned paths to the frozen local parent,
removing its new files. Parent project/solution registration stays intact. No
schema, keychain, host, account, settings or durable data migration needs reversal.
The immutable handoff includes an owned patch and hashes; independent QA, later
manual opt-in, Contracts integration and public/core authority remain parent work.

## Lead correction: drain acknowledgment ordering

At local completion head `06809a9dd3079c3b64651423e41bbf2c83e9a3f4`, a fresh
no-prompt callback probe reproduced replacement admission during phase1, before
drain acknowledgment. The original failure is retained under
`.artifacts/owner-native-integration/drain-ordering/`. The correction holds the
native busy record through both acknowledgments. Checked-in regressions attempt
replacement from inside phase1 on another synthetic window and prove that a
refused phase1 keeps later replacement blocked. Original phase0 failure, duplicate,
window-close and cancellation assertions remain. Both harness modes passed ASan:
93 callback assertions and 21 refused-drain assertions. These simulated callback
checks never invoke OS evaluation and do not establish native qualification.
Fresh root verification passed 543 tests (13 bridge), zero failures/skips, with
Release build, syntax/style and formatting checks. This graph contains the native
bridge and accepted projects, separately from the unmerged Contracts branch.
Independent correction review remains pending; these results are separate from
the implementer's immutable original evidence.
