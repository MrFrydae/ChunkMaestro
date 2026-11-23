# macOS owner verification bridge — blocked partial implementation

Issue12 remains OPEN. This uncommitted implementation is based on accepted denial
checkpoint PR19, frozen parent `a504b3b049e1b2826267a47220b507d313ec8d33`, on
`feat/12-macos-owner-verification`. The initial checkout was clean and isolated.
This is implementation evidence, not an independent verdict or native owner
acceptance. No authentication prompt was invoked.

The deliverable implements real no-prompt LAContext allocation, policy availability,
invalidation, request ownership and refusal-only preparation. **Evaluation,
NativeVerified, terminal callback handling and qualified OS session monitoring are
absent and blocked.** Availability cannot establish an eligible owner session.
A caller-supplied session GUID is only a diagnostic scope, never OS session proof.
No principal is accepted as verified. Every preparation refuses; no identity use
case references this assembly. Public IOwnerIdentitySession and PR19's private
admission object remain unchanged. No credential/persistence/Desktop reference,
owner binding, SQL/worker hook, UI, audit, Windows adapter or fallback is added.

This partial boundary follows Q14/145/149/153 (direct local service and retained
storage authority), Q163–169 (OS owner, scoped secrets, separate core permission),
Q174 (safe cancellation), Q181/189–195 and the 2026-11-23 owner testing policy.
Coverage is diagnostic. Q114–148/191 native interaction and accessibility are not
qualified by no-prompt tests. Optional application passphrase remains separate.
OS policy availability or eventual LA success does not unlock Keychain.

## Design and provenance

Authoritative external inputs, retained unchanged:

- `/private/tmp/chunkmaestro-owner-ui-design-93976830-20261123-203637/`
- `/private/tmp/chunkmaestro-owner-ui-review-93976830-20261123-205546/`
- `/private/tmp/chunkmaestro-owner-ui-ordering-correction-93976830/`
- `/private/tmp/chunkmaestro-owner-ui-correction-review-93976830-20261123-independent/`

All 54 supplied artifact hashes verified. Historical source bindings remain at
`93976830e996cac835d42b5c5353de5635026065`; they are not current-parent proof.
Original AUTH-D01 FAIL remains historical; corrected design PASS does not approve
this implementation. The corrected per-effect admission contract is not replaced
by preflight. The installed LAContext.h matches the retained header byte for byte.

Only public installed Apple SDK frameworks Foundation, LocalAuthentication and
AppKit are linked, plus their system/runtime dependencies. Owned Objective-C source
uses ARC/blocks and actual `xcrun clang`; no downloaded native binary or third-party
source is included. Primary API provenance is Apple's retained official
[preflight documentation](https://developer.apple.com/documentation/localauthentication/lacontext/canevaluatepolicy(_:error:)),
[invalidate documentation](https://developer.apple.com/documentation/localauthentication/lacontext/invalidate()),
and [reuse documentation](https://developer.apple.com/documentation/localauthentication/lacontext/touchidauthenticationallowablereuseduration),
plus SDK LAContext.h. `canEvaluatePolicy` is never called from an OS reply.
No new NuGet versions: production lock is empty; test packages and transitive
hashes match the existing CredentialsTests graph. Central pins/global.json unchanged.
Release packaging, minimum supported OS, signing/notarization and license approval
are not established by linking installed system frameworks.

## ABI and lifetime contract

`Native/mac-owner.h` defines C ABI v1. Fixed uint32 ABI, int32 statuses, uint64
request/generation, uintptr window and size_t buffer lengths match managed uint,
32-bit enum, ulong and nuint. Buffers are borrowed only during create and copied
natively; nonce is 32 random bytes per managed request, diagnostic session is 16
bytes, generation must be nonzero. Zero/missing scope, wrong length/version and
missing output pointer refuse before context allocation. Non-null C buffers must
point to the declared accessible length; this trusted in-process C ABI cannot
validate arbitrary buffer memory. Window integers have a stronger contract: they
are never converted to ObjC receivers or dereferenced.

Statuses: 0 ContextCreated, 1 Available (preflight only), 2 Unavailable,
3 Unsupported, 4 InvalidInput, 5 Canceled, 6 Failure. There is no verified/success
permission status. Creation means allocation only. Each request creates a distinct
LAContext, sets Touch ID reuse to zero and `interactionNotAllowed=YES`. The latter
is defense in depth; there is no evaluate call. A process-local monotonic uint64
registry identifier is never recycled; exhaustion refuses. Handles are not permits
or security capabilities against hostile same-process code.

The registry owns a strong ARC reference. Lookup takes another strong reference
under the registry mutex, then releases that mutex before request operations.
Per-request synchronization serializes probe and cancellation. Release first removes
ingress to the registry, then marks the retained object canceled before invalidating.
An operation that already owns a reference can finish, but no authentication effect
exists. Repeated cancel stays canceled; repeated native release returns InvalidInput.
Managed disposal is idempotent, revokes before release and serializes with probe;
a finalizer provides fallback cleanup. Strong references protect native raw-ABI
lookup/release races. No raw ObjC pointer, callback, GCHandle or native context
crosses to consumers. There is no callback to drain and no callback-drain proof.
Future evaluation must introduce separately reviewed retained callback ownership,
terminal-once guards, nonce/session/generation/principal revalidation, revocation
before late completion and stalled-drain behavior. Existing disposal is not proof
of those future properties.

Preparation rejects missing window, stale generation and canceled/released request.
On a worker thread it returns Unsupported before touching NSApp. On the main thread
it compares the supplied integer to actual NSApp window addresses before messaging
any window; absent/foreign handles refuse. Even an owned window returns Unsupported
because monitoring/principal qualification is absent. LAContext has no NSWindow
attachment API. No attached-sheet claim is made. No dispatcher wait, `.Wait`,
`.Result` or synchronous dispatch exists. This partial bridge does not marshal to
AppKit: callers off the UI thread are refused. A future async production composition
must safely marshal UI validation without holding the managed gate or blocking UI.
Synchronous diagnostic probe/cancel may take OS time; no UI responsiveness guarantee
or hard native completion deadline is asserted. If native invalidation stalls,
its object remains owned; there is no detached cleanup success claim.

## Executed validation and initial errors

Local macOS arm64, .NET SDK 10.0.404, private executable
`/Users/derek/.local/share/chunkmaestro/dotnet/dotnet`, Python
`/opt/homebrew/bin/python3.13`. Set CHUNKMAESTRO_DOTNET and DOTNET_ROOT to that
executable and its parent directory. Final verifier also set
`MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0`.

- `python tooling/build/build.py sync`: sorted solution adds only two project rows.
- Scoped `dotnet restore tests/ChunkMaestro.OwnerVerificationTests/ChunkMaestro.OwnerVerificationTests.csproj --force-evaluate`: new locks only, inspected.
- `dotnet restore ChunkMaestro.slnx --locked-mode --force`: PASS.
- `python tooling/build/build.py verify`: final PASS; locked restore, Release build,
  zero warnings/errors, syntax and generated-inclusive format; 537 tests passed,
  zero failed/skipped (28 headless, 36 identity, 74 credentials, 7 bridge, 392 storage).
  Unchanged repository disposable vault fixtures ran under this command.
- `python -m unittest discover -s tooling/build -p 'test_*.py' -v`: 21 PASS.
- `python -m unittest discover -s tooling/ci -p 'test_*.py' -v`: 50 PASS.
  Printed FAIL/timeout/image-retention records are expected mocked negative controls,
  not actual host-volume operations.
- `python tooling/specification/check.py`: PASS, all 205 source/matrix cases.
- Bridge `dotnet test ... --no-build --no-restore -c Release --collect:'XPlat Code Coverage'`:
  final diagnostic managed coverage 45/49 lines, 23/28 branches, retained externally;
  native code is not measured by this collector; no score gate.
- Actual clang ARC/block compilation uses `-Wall -Wextra -Werror`. Raw native
  harness: 320 assertions, including 100 lookup/probe/cancel/release races, PASS
  under AddressSanitizer. macOS x86_64 dylib compilation PASS; execution untested.

Initial full verify rejected a compact test array initializer at line60 via syntax
guard. Its collection-expression replacement caused CS0121 (xUnit overload ambiguity);
an explicitly typed local fixed that. One intervening parallel build reported
14 MSB4166 worker-node exit errors; its suggested temporary diagnostic directory
was already absent. Cause is unproven; disabling node reuse allowed subsequent
builds. All original logs retained. No analyzer/rule/package weakening occurred.

Managed tests assert real allocation/preflight, missing scope refusal, cancel-before-
start, repeated cancel/dispose, managed cancel/dispose races, missing/stale windows,
worker-thread refusal and the subprocess native harness. Native tests cover ABI,
nonce/session lengths/zero values, arbitrary window bits on the actual main thread,
distinct handles, canceled/released replay and raw ARC lifetime races. They are
no-prompt tests, not synthetic or actual OS authentication proof.

## Blockers, handoff and rollback

The full requested verification bridge remains BLOCKED/PARTIAL: no evaluate path,
OS principal/UUID/session acquisition, qualified lock/switch/power/window observers,
actual owned-window positive validation, async dispatcher integration, callback
failure controls or terminal-once state machine. No actual nativeVerified execution,
manual prompt, keyboard/accessibility, focus/modal, Intel runtime, Windows/Linux
execution, installer or current hosted CI evidence is claimed. Cross-platform code
returns Unsupported before native loading; actual Windows/Linux execution awaits CI.
MacFact tests explicitly skip there, never substitute successful placeholders.

Lead must assign three independent implementation QA roles on the delivered source
hashes/current review head, run current CI, and qualify monitoring/async/lifetime
contracts before adding evaluation. A physical macOS prompt requires later concrete
owner approval after the result is reviewed. Positive Core integration needs the
separate per-effect SQL/credential-worker admission and binding design. No UI or
public owner denial change is authorized by this partial bridge. No delegates,
commits, pushes, GitHub writes, listeners, VM or host-setting operations were used.

Evidence bundle:
`/private/tmp/chunkmaestro-owner-verification-a504b3b-20261123/` contains machine
handoff, exact commands/environment/results, initial failures, TRX, diagnostic
coverage, dependency/input verification, source/artifact SHA256 inventories and
owned patch. Hashes identify bytes, not independent approval. Rollback: remove the
two new project trees and this document, then run build.py sync to remove their
solution rows. There is no durable state/schema/keychain migration to undo.
