# Protected credentials — issue 11 implementation handoff

Parent: `5274a128ce7df34529ee32cf47d5dd23da168990`. Uncommitted implementation,
restricted to the Credentials project, CredentialsTests project and this document.
Source205 Q5–6, Q14, Q145, Q163, Q165–168, Q175, Q189–193 and the later owner
brief govern. This is a trusted-core foundation, not completed controller identity,
OS reauthentication, desktop integration or global product acceptance. The issue12
research Vault contract was read; its crypto/profile/schema proposals are not implemented.

## Contract and recovery

`CredentialScope` consists of Domain `FleetId`, a nonempty target GUID identifying
one host/server/integration/controller, and a canonical lowercase ASCII letter,
digit or hyphen purpose of 1–64 characters. Uppercase is refused before reservation
or any native call; it is never silently folded. Windows `CREDENTIALW.TargetName`
is case-insensitive, so allowing two case spellings would alias scopes and give
their create-only mutexes different names. Use stable protocol purposes, not display text.
`CredentialReference` adds a nonempty opaque GUID. Both are nonsecret metadata.
Names are exactly `ChunkMaestro.credentials.v1/{fleet:N}/{target:N}/{purpose}/{id:N}`.
The macOS service is exactly `ChunkMaestro.credentials.v1`, account is the full name.
There is no store enumeration or arbitrary external target API in the public contract.

`CredentialStore.Reserve(scope)` is pure: it validates and generates a reference,
without opening a store or persisting anything. Persist that entire locator in Pending
metadata **before** `CreateAsync(scope, reference, bytes, token)`. The latter returns
the same reference only after native success. It never chooses a replacement locator,
updates an existing item, retries creation or regenerates missing material. Issue12
must retain Pending on uncertainty and use `UseAsync` at the same locator to reconcile;
Conflict alone proves existence, not that the stored bytes match the pending identity.
Payload version, identity binding and possession validation belong to that caller.
No SQLite transaction, reference table, CA or cross-store atomicity is supplied here.

Create accepts 1–2560 bytes, the Windows generic credential bound. This deliberately
bounds the shared byte contract; it is not an invented macOS OS limit. Use gets an
exact reference and a synchronous `SecretConsumer(ReadOnlySpan<byte>)`; it does not
return secret data. Delete operates on that same validated reference and reports
Absent on a missing item. Wrong fleet, target, purpose or empty reference is rejected
before the adapter is invoked. Reservation/reference possession is not authorization:
trusted callers must enforce owner/session policy, and persist/validate their scope.
No presentation project references this assembly in this patch.

`CredentialException.Failure` provides Absent, Denied, Locked, Unavailable,
Unsupported, WrongScope and Conflict without native input/status/secret text or inner
exceptions. `Completion` is NotStarted for validation/preflight failures, NotCreated
for create Conflict, and conservatively Unknown for a native create error. Native
success returns normally. If macOS deletion succeeds but interaction-state cleanup
fails, completion is Unknown, never NotStarted. Reconcile the same exact locator;
do not automatically repeat a destructive operation. Unknown does not mean absent
or rolled back. Caller
callback exceptions propagate, so trusted callbacks must also keep their exception
messages and logging secret-free. Callback failures do not mutate the store.

Cancellation is observed before copying input, while waiting for the per-store gate,
and immediately before dispatching native work. Once dispatched, the native call is
not cancellable: await it to completion even if the token is canceled; successful
create still returns the same reserved locator. No post-effect OperationCanceledException
or compensation deletion conceals success. A process crash or transport/API failure
can still leave an unknown native outcome. Process-kill/power-loss durability and
issue12 Pending/Ready recovery are not qualified by these tests.

## Native implementation and trust boundary

macOS uses Security.framework `SecKeychainCopyDefault` to retain one selected file
Keychain, `SecItemAdd`, `SecItemCopyMatching`, `SecItemDelete`, and
`SecKeychainGetStatus`. Add sets `kSecUseKeychain`; read/delete set a one-element
`kSecMatchSearchList`. The item is a generic password; native duplicate rejection
provides atomic create-only behavior, also across processes. No synchronizable or
data-protection-Keychain option, access-group/entitlement changes, ACL grants,
certificate/trust operations, unlock attempts, network operations or global search-list
updates are performed. This file-Keychain choice does not claim device-bound,
hardware-backed, nonexportable or non-roaming protection against external Keychain
migration/backup. File Keychain APIs remain available but deprecated; signed app
packaging and future Apple compatibility need lead qualification.

Queries set both `kSecUseAuthenticationUIFail` and the legacy
`kSecUseNoAuthenticationUI`. Real locked-fixture testing demonstrated these alone
were insufficient on this host: a read blocked in Security.framework. The adapter
therefore serializes its own native operations under a static gate, saves the
process-local interaction flag with `SecKeychainGetUserInteractionAllowed`, disables
it with `SecKeychainSetUserInteractionAllowed(false)`, and restores the original flag
in finally. It checks the selected Keychain's unlock-status bit first so a known
locked Keychain reports Locked rather than the OS's ambiguous authentication denial.
A race can still return Denied after the precheck. This is a **process-level state
constraint**: integrate all other Keychain callers with this coordination, or isolate
this component in a suitable dedicated process. The gate cannot coordinate arbitrary
third-party in-process Keychain callers. User security policy/settings are not changed.
The interrupted-test fixtures were deleted by exact path using SecKeychainOpen/Delete;
no user credential item was inspected or removed.

Windows uses Advapi32 `CredReadW`, `CredWriteW`, `CredDeleteW`, `CredFree`,
`CREDENTIALW`, `CRED_TYPE_GENERIC=1`, `CRED_PERSIST_LOCAL_MACHINE=2`, flags 0.
Persistence is for the current OS user on this computer across logons; it is not
machine-wide access or enterprise roaming. Before write, a SHA-256 of the exact
reference name identifies a named `Mutex` using .NET `NamedWaitHandleOptions` with
CurrentUserOnly=true and CurrentSessionOnly=false. Cooperating processes therefore
serialize the existence check and write across sessions. An abandoned mutex still
requires the check. Existing items yield Conflict; failed checks never proceed to
write. CredWrite itself replaces existing data: the no-overwrite guarantee requires
all cooperating writers to use this lease and exclusive app namespace. It cannot
prevent malicious same-user writers from bypassing the mutex. Windows execution,
mutex behavior/permissions, cross-session behavior and true OS-denial/logon-unavailable
proof remain pending; injected mappings are not native Windows acceptance.

The component is a trusted in-process byte vault. A callback can copy bytes and
same-user/admin compromise can defeat its intended boundary. It is not an OS-admin
security boundary, nonexportable-key provider, mlock facility or general signing oracle.
Core authorization must precede use; the scope is not a MAC over untrusted metadata.
There is no plaintext filesystem fallback, logging, CLI secret handling or secret
export API. Test child arguments contain only synthetic fixture paths and nonsecret
reference components; synthetic values are compiled fixtures, not CLI arguments.

## Lifetime and concurrency

Each store serializes native calls and its callback. Native work runs on a worker
thread. Dispose waits for in-flight work, releases retained native resources exactly
once, and rejects later operations. The semaphore remains allocated to permit racing
waiters to observe disposal safely. Callback reentry into store operations/disposal
is rejected in the flowing execution context to avoid deadlock; callbacks must not
suppress that context and synchronously reenter. Keep callbacks bounded.

Creation copies caller memory and clears its owned array in finally, including
adapter failure and cancellation while queued. The caller must clear its original
input and must not mutate it during the initial copy. Read arrays are cleared after
callback return or exception. Windows blob buffers, including collision checks, are
cleared before CredFree. macOS writes wrap pinned caller-owned bytes with
CFDataCreateWithBytesNoCopy and kCFAllocatorNull; the wrapper is released before the
owned bytes are cleared. Reads copy OS-returned CFData then CFRelease it; immutable
CFData and internal OS/provider/GC copies have no guaranteed explicit erasure here.
Every CF dictionary, string, array, data wrapper and retained Keychain reference is
released along normal paths. Perfect erasure or crash-time cleanup is not claimed.

## Evidence and remaining gates

Run `python3 tooling/build/build.py verify` for the integrated solution's locked
restore, build, syntax, formatting and discovered test suites. The existing managed
Windows, macOS Intel and Linux CI jobs now run the credential suite with a 150-second
deadline and retain TRX/hang diagnostics. Linux proves explicit unsupported behavior;
it does not certify either native adapter.

The original worker delivery is retained outside tracked source under
`.artifacts/credentials-11-delivery/original-evidence/`, with its original source
hashes and report. It includes argv, exits, raw logs, final TRX counters,
environment and source/header/research hashes. Earlier logs and TRX include two
aborted native runs and a real Locked-versus-Denied assertion failure. Early iteration
logs were captured before the final manifest workflow: their source snapshots are
not frozen and they are history, not evidence for final source acceptance.

The original worker's normal verification stopped at solution/project-glob mismatch
because shared solution integration was lead-owned. The lead has now added both
projects to the solution. That historical failure remains in the original delivery.
The worker ran direct locked restores, Release builds, repository syntax and
owned-source formatting separately. Earlier
unrestricted formatter checks also reported external package/generated obj formatting;
those diagnostics remain preserved, without altering dependencies or generated
intermediates. No SDK/package/policy update or warning suppression was used.

Real macOS evidence covers fixture create/use/reopen, two-reference preservation,
wrong purpose, exact deletion/missing, collision preservation, locked refusal,
separate-process competition at a reserved locator, loser/restart retry and winner
preservation. Fixtures use SecKeychainCreate(prompt=false), compare search-list
snapshots with CFEqual without reading names, and SecKeychainDelete/CFRelease plus
removal of only their unique temporary directory. The tests never open normal user
items. Windows tests use only random exact names and clean up their known references.
No Windows, Linux or Intel execution is claimed. Linux tests explicitly assert the
production Unsupported constructor result, rather than reporting a skipped native pass.
On other platforms the macOS-only test executes a clearly named status mapping contract.
Injected tests cover buffer clearing, callback exceptions/reentry, early and queued
cancellation, in-flight disposal, post-dispatch cancellation returning the reserved
reference, ambiguous native error completion, and sanitized OS status mappings.

Lead still owns solution/CI discovery, meaningful safety-module verification,
diagnostic coverage reporting, additional fault/kill-boundary testing,
Windows/macOS Intel/installer/native UI evidence and all three independent exact-head
QA roles. The [2026-11-23 owner policy](../product/requirements-source.md#owner-testing-policy--2026-11-23)
replaces numeric coverage/mutation gates, including native credential mutation,
with meaningful direct safety evidence. No retired storage mutation/manual receipt workflow is revived. No
independent verdict, complete Q166, production deployment or global acceptance is claimed.

Rollback before integration is deletion of the three newly owned patch paths after
review. After integration use a signed reviewed revert; retain real credential items
and Pending metadata for explicit recovery. Never bulk delete by prefix. Only exact
new synthetic fixture references/Keychains may be removed by these tests. No commit,
push, GitHub write, PR/watch, CI dispatch or signing prompt was performed.

Official provenance: [Apple Keychain implementations](https://developer.apple.com/documentation/technotes/tn3137-on-mac-keychains),
[Apple SecItemAdd](https://developer.apple.com/documentation/security/secitemadd(_:_:)),
[Apple no-interaction flag](https://developer.apple.com/documentation/security/seckeychainsetuserinteractionallowed(_:)),
[Microsoft CREDENTIALW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw),
[CredWriteW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew),
[CredReadW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw),
[CredDeleteW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-creddeletew),
[CredFree](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credfree),
[Windows object namespaces](https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces).
Local SDK SecItem.h, SecKeychain.h and SecBase.h verified declarations/constants;
.NET SDK reference XML verified NamedWaitHandleOptions and Mutex overloads.

Final local validation (macOS arm64, SDK 10.0.404): **16 executed, 16 passed,
0 failed, 0 skipped**. Four cases exercise actual native Keychain behavior,
including separate child processes; twelve are injected/core contract cases.
Forced locked project restore, root locked restore, root and new-project Release
builds, repository StyleCheck, owned/generated-inclusive formatting and source205
specification checks exit 0. The normal verifier exits 1 at the unmodified solution
discovery boundary. This single known integration block is not a verifier pass.
No coverage percentage or mutation score was measured.

### Lead integration checkpoint

The lead verified all 76 original deliverable hashes, preserved the original
delivery, and integrated only source, tests, project locks and this document.
Machine-specific validation scripts and raw evidence are not shipped as tests.
The new case-alias regression failed twice against the original implementation
and passes after canonical-purpose validation. Formatting corrections were also
needed when both projects joined the full solution; the original failed checks
remain retained.

On macOS arm64, integrated `build.py verify` passes **316 tests**: 28 desktop,
18 credential and 270 persistence cases, with zero failures/skips. Locked restore,
Release build, syntax and generated-inclusive formatting pass. The 46 CI-tooling
and 21 build-tooling tests, specification check and actionlint pass. Separate
macOS-only credential coverage is **74.21% line / 60.37% branch**, including the
unexecuted Windows adapter. This is a gap measurement, not combined-platform
qualification. Windows/Intel execution, further failure-path coverage, mutation
qualification and independent QA remain required before merge.

### Native failure corrections

The independent security preflight at `f1c1b5c` reported SEC-01, REL-02 and REL-03.
Its original FAIL and all 507 evidence hashes remain preserved. The lead reproduced
the first two through the actual assembly and an internal native-resource boundary:
the old read retained both synthetic bytes after restoration failure, and the old
delete reported NotStarted after real deletion. Both regression failures are kept.

Read now retains responsibility for its allocated managed buffer until interaction
restoration completes; any exception before return clears it. A post-delete cleanup
failure reports Unknown. Every dictionary/string/search-list/data-wrapper allocation
is checked before use, acquisitions enter cleanup protection before the next
allocation, and only nonzero owned references are released.

Seven macOS-specific cases prove both restoration failures and null dictionary,
account, service, search-list and data-wrapper refusals. They use fresh owned
Keychains with injected allocation/interaction results and observe that all tracked
native handles are released. The fault interaction implementation does not alter
the actual process flag; these are controlled fault proofs, not natural OS errors
or actual memory exhaustion. The default resource implementation still calls the
real framework APIs. No public test hook or new public capability is exposed.

These cases compile only on macOS; Windows/Linux do not report them as successful
native tests. Local macOS arm64 full verification passes **359 tests**, including
61 credential cases, zero failures/skips; locked restore, Release build, syntax
and generated-inclusive formatting pass. Fresh final-head Windows/Intel execution,
coverage/mutation qualification and independent correction review remain pending.
The Windows read's initial structure marshaling is now inside the existing
guaranteed-free region too; native Windows failure injection remains unexecuted.
