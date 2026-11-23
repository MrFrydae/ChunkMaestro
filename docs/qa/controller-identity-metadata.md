# Controller identity metadata preparation

Issue 12 metadata slice, parent `544c6b748714a8a4049dee150f9d9f2a5926daae`,
branch `feat/12-identity-metadata`. This is an uncommitted implementation delivery,
not full identity acceptance. Source: Q14, Q149, Q153, Q163–168, Q171–175,
Q181, Q189–195 and the orchestration directive. The original research at
`/private/tmp/chunkmaestro-issue12-research-5274a128-20261123/REPORT.md` was read
and its SHA256 matched
`22b8cd94af06bcfd26a75b959ac1dc49a4850a12c222c16aedc090246e5915fa`.
The current strict profile is reused unchanged; chain success never substitutes
for its exact self-signature/profile validation.

## Durable contract

Migration `20261123174014_ControllerIdentityMetadata` adds one optional row per
existing fleet, with restrictive FK and independent unique controller, attempt
and reserved credential IDs. GUIDs use the existing uppercase D database encoding;
profile SAN GUIDs keep their strict lowercase encoding. Fixed purpose is
`controller-root-v1`, profile 1. The row reconstructs the exact pure
`CredentialReference(CredentialScope(FleetId, ControllerId, Purpose), CredentialId)`;
it stores no native name, path, envelope or private material.

States are Pending=0 and Ready=1. Public DER is bounded to 4096 bytes; certificate
and SPKI SHA256 bindings are exact 32-byte blobs. UTC instants are representable
integer Unix milliseconds. Pending has no publication; Ready requires publication
at or after creation. Schema checks enforce shapes and supported values. Logical
validation additionally parses canonical nonempty GUIDs, validates state/time,
and calls the strict RootProfile at the recorded creation instant, including
self-signature and exact public hashes/scope. Expiration today preserves inventory;
authority/use must separately deny current expired or not-yet-valid identities.

The three existing migrations and Designer files are unchanged. The snapshot and
one generated forward migration are updated; Down throws. Generated C# is normalized
to explicit types, Allman braces and file-scoped namespaces. Generated SQL was
inspected: transactional new-table/index/history additions, restrictive fleet FK,
no existing-table rebuild or data/key generation. All genuine historical prefixes
remain validated against their own migrated in-memory reference before upgrade.
Existing pre-upgrade backup and native session/path authority behavior are retained.

## Internal boundary and recovery

`BeginIdentityMetadataAsync`, `ReadIdentityMetadataAsync` and
`PublishIdentityMetadataAsync` are internal members of the same sealed partial
LocalFleetStore. They share the retained connection, semaphore, disposed check,
background worker and native identity verification before/after I/O. No second
production connection, nested gate acquisition, native credential call, public
Initialize success, owner-authentication claim or network service is introduced.
Byte arrays are copied before queuing mutations and when returning read results.
Failures use sanitized categories without raw provider exception inner details.

Begin accepts validated Pending only and inserts once. Existing Pending or Ready
always conflicts; nothing is overwritten. The caller MUST durably commit Pending
before requesting future native creation. Conditional publication matches the
exact fleet/controller/attempt/reserved reference/profile/purpose/DER/hashes and
creation instant with State=Pending and PublishedAt=NULL. Every replay conflicts,
including the same publication request; PublishedAt never changes after Ready.
There is no generic update, delete, reset or automatic mutation retry API.

**Metadata publication is not private-key proof.** A future composed trusted
service MUST reopen the exact native reserved item, validate the full RootProfile
at current use time and separately prove possession, then invoke this internal
conditional transition. No public forged proof boolean or signing API is supplied.
Granting future composition friend access and owner authorization remain reviewed
integration work. Tests construct synthetic public certificate fixtures using
short-lived in-memory keys; no private key is exported or persisted.

Cancellation before a queued operation has no mutation. Cancellation or failure
after SQLite has committed can leave a durable row despite a failed response;
post-I/O native verification failure likewise cannot promise rollback. Reopen and
reconcile the same attempt. Never proceed to native creation on an uncertain Begin
outcome, compensate by deleting a key, or repeat initialization. A Pending attempt
and opaque reference survive restart as possible orphan/recovery information.
A database-only backup restores metadata, never proves or recreates any key.
Refused databases and prior completed backups are preserved; no auto-repair,
downgrade, reset, vault scan or native cleanup is implemented.

## Evidence and remaining gates

Raw evidence, original failures, SQL, logs, TRX, coverage and input/artifact hashes
are retained outside source in `/private/tmp/chunkmaestro-metadata-evidence/`.
See its `REPORT.md` and `manifest.json` for final counts and exact commands.
The SDK is pinned 10.0.404; execution is local macOS arm64. Locked graph changes
add only project references to ControllerIdentity and Credentials and their
existing Domain dependency, propagated to persistence/desktop/test locks. No
package versions, package hashes or central package definitions change.

Tests use real disposable SQLite files: genuine populated predecessor upgrade,
Pending/reopen/Ready, duplicate initialization, altered scope/reference/payload,
concurrency, statement rollback, cancellation/disposal, malformed logical values,
missing/altered constraints, exact backup/recovery state and expired inventory.
The trigger-abort fault proves SQLite statement rollback, not process crash,
cross-store atomicity, power-loss durability or natural OS denial. Existing
storage process-interruption tests execute in the repository suite; no new native
key/crash acceptance follows from them.

CI inventory has an ownership conflict: discovery and aggregation read
`tooling/ci/storage-plan.json` directly; the permitted `storage.py` edit cannot
alone update all consumers. The exact unapplied required diff is
`/private/tmp/chunkmaestro-metadata-evidence/required-storage-plan.patch`.
It adds the new class and both platform counts, and updates the Python regression's
fixed total. These paths are outside assigned ownership and were not edited.
Direct safety CI correctly refuses the unclassified class until the lead applies
that diff. Local tests are not a substitute for that required gate.

Full issue 12, all-role review and native acceptance remain BLOCKED. Storage
requirements stay >=90% line and >=85% branch with combined platform evidence;
local coverage alone has not met the branch gate. New crypto mutation remains
pending separately; retired STORAGE mutation/manual receipts remain retired.
Windows/macOS Intel, composed native exact-key reopening/possession, actual native
denial, real cross-store process-kill recovery, secret-canary scanning of composed
private material and independent functional/security/UX verdicts are not supplied.
No production data, real OS credential store, trust configuration, commit, push,
GitHub mutation, workflow dispatch or watch was used. Reviewed recovery must keep
the upgraded database and completed backups; older applications must refuse this
schema rather than downgrade it. Removing this patch is only safe before using its
new schema; deployed rollback requires compatible reviewed recovery.

Final local delivery verification passed **408 tests** (28 desktop, 33 profile,
18 credential, 329 persistence; zero failures/skips). Of the persistence cases,
59 are new metadata cases. Forced locked restore and root verify passed;
Release compilation reports zero warnings/errors, syntax/format checks pass.
The final persistence coverage rerun passed all 329 cases. Unique-source coverage
using the CI point aggregation is 94.57% line / 75.36% branch for persistence on
this Mac, 98.25% / 95.00% for the new session boundary and 100% / 96.67% for the
new row validator. Whole-module branch acceptance still requires remaining
platform evidence; no exclusions or thresholds were changed. The proposed
unapplied CI inventory was validated against actual retained Darwin TRX with
329 cases; Windows's proposed 338-case inventory remains unexecuted.

## Lead integration checkpoint

The lead verified all 17 delivered source hashes and 57 artifact hashes, preserved
the original delivery and independent artifact FAIL, and restacked the profile
checkpoint onto the corrected credential foundation. The sole CI merge conflict
was resolved by keeping both the detailed credential coverage command and the
separate profile test command. The original signed profile checkpoint remains
reachable from a recovery ref.

The required inventory changes are now applied: all 59 metadata cases belong to
integrity on each supported platform, with totals Darwin 329 / Windows 338.
Independent review additionally found that `test_gate.py` still expected the old
combined 549 total; it now requires 667. All **46 CI tooling tests pass**, including
that regression. This resolves the known integration mismatch without removing
any original case, weakening thresholds or changing the direct storage method.

Integrated macOS arm64 full verification passes **451 tests**: desktop 28, profile
33, credentials 61 and persistence 329, zero failed/skipped. Forced locked restore,
Release build, syntax and generated-inclusive formatting pass; specification and
actionlint pass. Historical migrations and profile implementation bytes are
unchanged. Windows/Intel execution of the metadata cases, current-head independent
review and composed native lifecycle remain pending. A Ready metadata row still
does not establish private-key possession or grant authority.

## Hosted integration corrections

Run `45239108716` at `a081e69` exposed two integration defects. Both disk-full
jobs refused locked restore with NU1004: their standalone project lock lacked
the new ControllerIdentity/Credentials project dependencies. The lock is now
regenerated for that project. Existing NuGet versions, content hashes and package
dependency records are unchanged; forced locked restore succeeds. The original
failure was reproduced locally before this correction.

Windows integrity executed the populated prior-schema upgrade and backup checks,
then failed fixture deletion because the backup verification connection was
pooled. That connection now disables pooling and is disposed before a new
exclusive backup-file open proves release. The backup compatibility and populated
row assertions remain required. No global pool clearing or cleanup suppression
was added.

Correction verification on macOS arm64: 451 repository tests, 46 CI tooling tests,
and the genuine 64 MiB disk-full/recovery case pass with no failed or skipped
cases. Repository build, syntax and formatting pass with zero warnings/errors.
These are working-patch checks, not frozen-head platform qualification. Hosted
Windows verification and final independent acceptance remain pending. Original
hosted logs, digest-verified failure artifacts, local before/after restore logs,
TRX and correction evidence are retained under
`.artifacts/qa/pr18-a081e69-notification/`. The aggregate storage and QA failures
correctly reflected the failed prerequisite jobs; neither gate was weakened.
