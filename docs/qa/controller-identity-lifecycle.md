# Internal controller identity lifecycle — issue #12 composition

Scope: Q14/Q149/Q153/Q163–169/Q177–195; frozen parent
`a081e692d4d067f009af76d4a39178814004dab4`, intended integration branch
`feat/12-controller-identity-lifecycle`, PR18 stacked on PR16. Implementation
checkout is T3-managed `t3/controller-identity-lifecycle`. Lead integrates atop
`effd6fd032fa0cea3817fa7c3e8587656f8d081c`; this patch does not edit the lead's
metadata migration test, metadata documentation or standalone volume lock.
This is an uncommitted internal composition, not full issue #12 acceptance.

## Contract and authority

`LocalFleetStore.CreateRootIdentityAsync` is internal trusted-core work. It
creates ephemeral P-256 material, reserves a random scoped native reference,
commits immutable Pending metadata, then calls create-only native storage. Any
Pending refusal, including a post-commit exception, returns before native write.
No overwrite, compensating delete, automatic mutation retry or replacement exists.
Failures preserve the original database, certificate, attempt and locator.

The native item is reopened through `CredentialStore.UseAsync`; the exact
versioned envelope, full current certificate profile, certificate/SPKI hashes and
independent fresh challenge possession are checked before conditional Ready.
Resume receives all four expected IDs, requires the exact durable locator and
only reads existing native material. Absent, denied, invalid, expired and wrong
scope items refuse publication. An uncertain native mutation must be reconciled
by explicit resume, never by another Create. Resume of an already Ready row
revalidates native storage without replaying the publication UPDATE.

Ready remains public inventory, not key authority. Each authoritative reopen
reads native storage afresh, validates current time/profile and proves possession.
The returned locator is nonsecret; no signer, private-key DTO, issuance, listener,
enrollment or trust-store operation is exposed. Real OS reauthentication and
native owner UI are still required before making this reachable from public
application flows. Internal methods are a trusted assembly boundary, not a
sandbox against malicious code running as the OS user.

## Encoding and memory ownership

The envelope is at most 2560 bytes: 144-byte fixed header plus canonical PKCS8.
Offsets: magic `CMROOT01` at 0; little-endian profile version at 8; .NET GUID byte
encodings for fleet/controller/attempt/credential at 12/28/44/60; exact SHA256
certificate/SPKI at 76/108; little-endian PKCS8 length at 140. The profile version
implies the fixed `controller-root-v1` purpose; metadata validation enforces it.
There are no optional fields, padding or trailing bytes. PKCS8 is fully consumed,
re-exported for canonical equality, and independently bound to certificate
possession. Unsupported private curve input is sanitized as cryptographic failure.

RootMaterial owns and clears its exported PKCS8 in a synchronous borrowed-span
callback, including callback exceptions. Envelope encoding clears its scratch
buffer in finally. The composition owns and clears its returned envelope around
native Create, even when cancellation or native failure occurs. CredentialStore
owns and clears its write copy and native read buffer; RootMaterial clears the
canonical import copy and disposes the ephemeral/reopened key. Public certificate
and hashes need no secret erasure. Callbacks may neither retain nor log borrowed
secret spans. The caller owns CredentialStore/TimeProvider lifetime and must keep
them alive until the returned task completes. This does not claim natural OOM,
runtime-internal/native allocator, swap or crash-dump erasure proof.

## Lifetime, scheduling and failure semantics

All composed work begins on Task.Run, including SQLite, crypto and clock calls.
The operation lease serializes lifecycle calls; lock order is operation lease,
then existing DB gate. DB gate is released before metadata method reentry or
native callbacks. Disposal acquires the operation lease before the DB gate and
therefore cannot release retained connection/path/controller authority during a
native effect. Internal clock/native callbacks must not recursively dispose or
reenter the owning store. No caller callback is part of this API.

Session identity is checked before/after native boundaries, including native
exceptions. Cancellation is checked at safe boundaries; cancellation after native
Create does not imply rollback. Certificate expiration and backwards clock
movement across native validation refuse publication/reopen. Conditional Ready
and post-commit identity/cancellation refusal can leave an unknown caller outcome;
reopen the exact attempt and inspect durable state. No success is inferred from
an exception. Existing SQLite public metadata methods preserve their conditional,
copy and recovery contracts.

## Executed evidence and limits

Local environment: macOS 27.2.1 arm64, pinned SDK 10.0.404/runtime 10.0.15,
Python `/opt/homebrew/bin/python3.13`; SDK executable
`/Users/derek/.local/share/chunkmaestro/dotnet/dotnet`.

The 23 lifecycle cases use public LocalFleetStore open/register/inventory behavior,
real SQLite and internal trusted composition with disposable explicitly owned
macOS Keychains. The fixture faithfully copies the existing credential fixture:
explicit keychain handle for all native calls, search-list equality around
creation, delete only the owned fixture keychain. It never selects the user's
default Keychain, modifies trust, or edits the search list.

Cases include reconstruct/reopen, native deletion after Ready, absent Pending,
matching resume, wrong key/attempt/locator/scope, malformed trailing envelope,
expired certificate, ambiguous Create-after-effect, cancellation, disposal race,
duplicate and concurrent initialization, publication statement rollback,
Ready replay reconciliation, denied native read/create, backwards/current clock
changes and write/read buffer cleanup. The envelope test rejects each single-byte
change and every truncation and checks borrowed export failure/disposal behavior.
These are parser negative tests, not a mutation-testing campaign.

Three tests actually kill a child process at narrow test-adapter phase barriers:
after durable Pending immediately before native Create, after successful native
Create before its result is acknowledged, and after Ready completion before caller
acknowledgment. Reopening proves the exact attempt survives and resume never
creates a replacement. These are deterministic process-death boundaries, not
random death inside native/SQLite commit instructions. Publication failure uses an
actual SQLite ABORT trigger (statement rollback); Ready-before-ack death proves
successful-publication reconciliation. No injected unknown SQLite commit, natural
OOM, real OS authorization prompt or native owner UI proof is claimed.

Commands and complete logs/TRX/source hashes are retained under
`.artifacts/controller-identity-lifecycle/`. Run affected suites with the pinned
SDK using Release, locked restore and the repository verifier. Root verification
also runs generated-inclusive format and syntax policy checks. The storage plan
adds all 23 cases to both platforms and updates both fixed-total tests, preserving
all existing cases/gates. No package graph/version or lock changed; project edits
only add test friends and platform test compilation routing.

Initial failures are retained: missing worktree NuGet assets before locked
restore; copied fixture indentation rejected by format; fixture double-dispose
caused a macOS Security crash and was fixed by giving CredentialStore sole adapter
ownership; xUnit rejected blocking Result access; malformed private curve initially
escaped as PlatformNotSupportedException and now fails cryptographically.

Windows native execution, Linux refusal execution, safety coverage thresholds,
independent functional/security/UX verdicts and lead's integrated-head checks are
still required. Linux uses the existing unsupported-storage suite and refuses
native credential construction; native lifecycle fixtures are excluded there.
No hosted dispatch, production access, mutation campaign, commit, push or PR
mutation was performed. Full issue12 and owner-reauthentication acceptance remain
blocked; no independent approval is supplied by this implementation agent.

## Threats, rollback and recovery

Binding prevents accidental cross-fleet/controller/attempt/ref/key substitution;
full profile validation prevents accepting a plausible but wrong certificate.
Native ACL/authentication guarantees remain those of the OS credential provider.
A privileged OS attacker can alter process memory/storage; this is not hardware
attestation or OS-user reauthentication. A matching item can disappear after a
successful check, so the nonsecret result grants no durable signing capability.

Rollback removes the code patch without changing schema or deleting metadata or
native items. Preserve Pending/Ready records, SQLite/sidecars/backups and exact
native locators. After an uncertain operation explicitly resume that same locator;
if absent/denied/invalid/expired, stop for reviewed recovery. Never reset metadata,
auto-delete an orphan, overwrite native material or issue a fresh identity as
recovery. Controller migration, rekey/revocation, credential cleanup, backup-key
handoff and UI recovery workflows remain separate scoped work.

## Final validation receipt

All commands ran from the frozen checkout with `CHUNKMAESTRO_DOTNET` set to the
SDK executable above:

```sh
/opt/homebrew/bin/python3.13 tooling/build/build.py restore
/opt/homebrew/bin/python3.13 tooling/build/build.py verify
/opt/homebrew/bin/python3.13 -m unittest discover -s tooling/build -p 'test_*.py' -v
/opt/homebrew/bin/python3.13 -m unittest discover -s tooling/ci -p 'test_*.py' -v
/opt/homebrew/bin/python3.13 tooling/specification/check.py
git diff --check
```

Final root verify passed locked restore, Release build (zero warnings/errors),
syntax policy, generated-inclusive format and 475 tests with zero failures/skips:
28 native-shell/headless, 34 root profile/envelope, 61 credential, 352 persistence
(including 23 new lifecycle cases). Build tooling: 21 passed. CI tooling: 46
passed, including exact inventory totals of Darwin 352 / Windows 361 / combined
713. The Windows total is a plan-contract assertion, not Windows execution.
Specification checker and diff whitespace check passed. CI tooling's printed
synthetic timeout/retained-fixture messages belong to mocked failure-path tests;
they are not real disk-full execution. No full-disk lifecycle claim is made.

The earlier 19-case lifecycle run and three isolated process-kill cases passed;
those logs remain alongside the final 23-case and root receipts. Failures before
the final pass are preserved rather than overwritten. Source SHA256 manifest,
artifact SHA256 manifest and complete applyable dirty patch are generated after
this receipt. Their paths are `source.sha256`, `artifacts.sha256` and
`lifecycle.patch` within the evidence directory. Hash manifests are evidence
identifiers, not independent QA attestations.

## Lead integration and fixture ownership

The original 15 source and 34 artifact hashes were verified before integration
on top of `effd6fd`; the backup-handle and standalone volume-lock corrections
are preserved. Lead review found that Windows fixture disposal did not remove
the synthetic native items. The fixture now tracks exact names before its own
native creates, including uncertain completion, and records the exact retained
locator from a killed worker's test database. Cleanup deletes only those names,
tolerates already-absent items and reports other failures. It never enumerates
the user's vault. macOS continues to delete only its explicitly owned Keychain.
Fixture disposal is idempotent; native adapter ownership stays with CredentialStore.

All 23 integrated lifecycle cases pass on macOS arm64 after this fixture correction.
Windows cleanup execution and final platform/coverage/independent evidence remain
pending. The internal lifecycle is not yet reachable through an owner-reauthenticated
native application flow and does not complete issue #12.

After signed restacking onto the credential boundary contracts, final integrated
macOS arm64 verification passes **488 tests**: desktop 28, profile/envelope 34,
credentials 74, persistence 352, zero failures/skips. Locked restore, Release
build, syntax/style, formatting and all 46 CI tooling tests pass. Inventory totals
remain Darwin 352 / Windows 361 / combined 713. New Windows lifecycle and fixture
cleanup execution, current coverage and independent acceptance remain pending.
