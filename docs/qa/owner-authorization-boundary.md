# Owner authorization boundary — issue 12, denial slice

Worker parent: `93976830e996cac835d42b5c5353de5635026065`.
Lead integration base: `0b4fe626684616d31f52b6e30640ab57c90ad9b3`.
Branch: `feat/12-owner-boundary-integration`.
This is not issue 12 completion, native qualification or independent QA approval.

## Authority and API

Q14/149/153 retain direct local services and a single retained SQLite store.
Q163–175, especially Q166/167/174, require core owner checks and safe cancellation.
Q181/189–195 and the later 2026-11-23 owner policy retain meaningful tests and
security assertions; coverage/mutation scores remain diagnostic, not gates.

The original design FAIL AUTH-D01 remains unchanged. The independent corrected
PASS applies only to design. Inputs were verified against all 300 source bindings
and 22 original design, 16 FAIL, four correction and 12 PASS artifacts. Receipts,
full source/patch hashes and command outcomes are in the external implementation
bundle `/private/tmp/chunkmaestro-owner-boundary-93976830-implementation/`.

`IOwnerIdentitySession` exposes observation plus explicit Initialize, RecoverExact
and VerifyExisting intents. Its result enum contains only Unsupported and Refused.
No production result authorizes creation, key use or publication. The observation
reports fleet membership and unavailable capability; it deliberately does not
assess identity metadata, OS owner binding or native possession. Revision and
record values are nonsecret freshness hints, never permission. Copying, constructing
or changing a record cannot grant anything. Only the latest returned object from
this session is recognized for the Unsupported category; other observations refuse.
Freshness here concerns this session's observation sequence, not a durable row digest.

`OwnerFleetSession` is sealed and borrows an existing `LocalFleetStore`. The caller
owns that store, disposes this owner session first, then disposes the store. Closing
immediately rejects ingress and drains inventory reads; it does not acquire the
identity-operation lease, dispose/reopen the borrowed store or recursively enter a
lease. Previously observed capability can remain Unsupported if the caller closes
the borrowed store prematurely; a subsequent observation refuses. Both are denials.
Normal store opening, migration, locking and inventory reads are storage setup,
not OS authentication and not identity effects of a refused use case.

The session never constructs CredentialStore (including its default-Keychain
constructor), invokes a lifecycle method, prompts, binds, writes identity metadata,
adds tables/events, or publishes Ready. No provider, principal assertion, grant,
private bytes or signer enters its public contract. Existing internal trusted
Create/Resume/Reopen are unchanged. No Desktop friend or public test hook was added.
Native-call absence follows the new boundary's complete call graph: denial methods
only inspect session state; observation calls existing ReadAsync. SQLite fixtures
prove actual durable preservation. These are not instrumented OS-vault call counts
and do not claim interactive/native acceptance.

## Internal ordering proof

`OwnerAuthorizationRequest` is internal, accessible to the existing trusted
PersistenceTests friend only. It is disconnected from production effects. Its
fixed initialization plan is Begin → Create → Use → Ready, not a generic capability
protocol. Immutable exact fleet/controller/attempt/credential/revision and a single
request instance scope each admission. Verification is consumed once. An internal
opaque admission object is returned only to its winning worker. Completion checks
reference identity against the one in-flight reservation; matching stage/scope
alone cannot fabricate, transfer or replay it. No public permission token or grant
exists. Duplicate/invalid/out-of-order inputs irreversibly terminalize.

Revoke and admission share one private mutex. Static Stopwatch observations occur
inside admission; there is no injected clock callback. No SQL, native effect,
external callback, user code, wait or UI work occurs under that mutex. Revocation
before admission denies it. Admission before revocation permits only that exact
in-flight completion; Finish acknowledges drain, not authorization. Failed/unknown
completion terminalizes; no later stage advances to admission after revoke/expiry.
Actual workers must eventually admit after their final gate/transaction reread;
this primitive neither supplies nor proves those future integration hooks.

Barrier tests exercise both winner orders, queued revoke, concurrent duplicate
admission, replay, scope changes, invalid observations, deadline after verification,
late/duplicate verification, failed/unknown finish and single-use plan completion.
The caller-check negative control intentionally demonstrates an unsafe synthetic
effect after revoke while worker admission refuses. Deadline testing waits for an
actual monotonic deadline by bounded spinning outside the mutex, without sleeps.
These are private in-memory proofs, not positive SQLite/native ordering proof.

## Evidence and remaining work

Real public-boundary tests preserve schema and every SQLite table value, including
Pending and Ready identity DER/hashes/locators/publication timestamps. They exercise
all three intents, absent/unknown/wrong fleet, stale/copied observations, cancellation,
closed owner session, disposed borrowed store and retained inventory after owner
disposal. They create in-memory disposable certificates, never native vault items.

The exact storage inventory adds 10 boundary cases and 30 ordering cases on each
supported storage OS: Darwin 352 → 392; Windows 361 → 401; aggregate 713 → 793.
The existing Linux Remove*Tests block explicitly re-includes only pure ordering
contracts alongside its existing unsupported-platform contract. Real SQLite tests
remain Windows/macOS only. No baseline assertion or accepted test was removed.

Local macOS arm64 evidence and exact initial errors/final outcomes live in the
external REPORT.md/report.json. Locked restore, Release compilation, syntax and
formatter verification ran with the pinned private SDK. Full root `verify` and
all-tests were not executed by the worker because existing credential/lifecycle
suites perform native vault fixture operations forbidden in that delegated task.
Their tests remain intact. Safe persistence, root-identity and
headless regression execution and diagnostic coverage are recorded separately.
Windows, Linux execution, native OS reauthentication, UI interaction/accessibility,
installer qualification, mutation, current CI and final independent three-role QA
remain unexecuted or lead-owned. No successful owner worker, binding transaction,
audit durability or native provider is implemented. Those require separately
reviewed integration and qualification before positive capability can be enabled.

Lead integration added opaque completion ownership: a fabricated same-stage
reservation, a reservation from another matching request, and a completed prior
stage's reservation cannot finish the current effect. Three additional tests
exercise those cases. The original worker delivery and design reports remain
unchanged. On the integration branch, full root `verify` passed **530 tests**:
392 persistence, 74 credentials, 36 profile/envelope and 28 headless, zero failures
or skips. This includes existing owned disposable native-vault fixtures; it does
not execute native owner authentication or enable a positive use case. Locked
restore, Release build, syntax and formatter checks passed. Exact commands, TRX,
hashes and limits are retained under `.artifacts/owner-boundary-integration/`.
Current Windows/Linux CI and independent implementation QA remain outstanding.

## Rollback

Remove the five new C# files and this document; revert only the owned test-project
include and storage inventory/count changes. No schema, existing identity metadata,
vault entry or production setting needs rollback. Keep external evidence and all
historical design/FAIL/PASS bundles. Do not delete or downgrade existing stores.
