# Local fleet storage, contract v1 (issue #3)

This bounded storage slice addresses Q14, Q22, Q25, Q45, Q149, Q153 and Q163–164.
It is not completion of Q153, controller migration, Phase 1, or independent QA.

## Native application integration

Reference Application for `IFleetInventory` and Domain for immutable identity/metadata records.
The desktop composition root references Persistence and owns a single
`await using LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForCurrentUser(localStorageConfirmed: true), token)`.
Only pass true after confirming that the OS local-application-data directory is
local and not synchronized/shared. Do not expose this choice through remote input.
No arbitrary path overload is public. A separate internal fixture path exists only for tests.

Keep the session alive for the entire controller lifetime, including tray mode.
Dispose it on shutdown; cancellation/failure during initialization releases locks.
Do not initialize a second store for another window. ReadAsync returns all local fleets,
hosts and instances, including genuinely empty collections on first launch.
RetrievedAt is local cache retrieval time, RegisteredAt is registration time;
neither means successful host contact.

RegisterFleetAsync, RegisterHostAsync and RegisterInstanceAsync are explicit manual
metadata writes. Registration does not enroll, inspect or change a machine, claim
a process, execute a shell, create a listener or grant a remote capability.
Future adoption must perform its own reviewed transport/security work.
IDs are nonempty GUID wrappers and caller-created stable identities. Names are bounded,
non-control display text. Addresses accept IP literals or DNS hostnames, not URLs,
credentials, commands or filesystem paths. DNS matching is lowercase with trailing
dot removed; IP literals are normalized without lookup. Aliases resolving to the
same machine cannot yet be detected.

Service names are bounded syntactic metadata, not helper authorization.
The same fleet cannot register a canonical host address twice; different fleets
may describe the same address. Each host/service pair is unique. Endpoint conflicts
are conservatively host + edition/transport + port, across all bind addresses.
Java/TCP and Bedrock/UDP may share a numerical port. This may reject legitimate
multiple-interface configurations until bind-address semantics are designed.
It cannot detect ports held by unregistered processes. No network checks occur.
All endpoint insertion and instance creation commit atomically.

Observation supports Unknown, Fresh, Stale and Offline with observed/contact timestamps
and source. Every registration/reload currently returns Unknown with null evidence:
there is no observer or observation write API yet. AsCached converts Fresh to Stale,
preserving evidence timestamps. Offline requires future explicit connectivity evidence;
empty inventory or opening the app never fabricates an outage. No remote execution or
offline queue exists. The contract accepts no credential values or secret command fields.
Free-text display names are non-secret input; callers must not paste secrets into them.
OS credential references and keychain integration are intentionally not yet introduced.

## Initialization, locking and schema safety

The active DB is fixed to the user's OS LocalApplicationData/ChunkMaestro/Fleet/fleet.db.
The store rejects UNC paths, reparse points/symbolic links in existing ancestors or
contents, and files outside the owner scope. New Unix directories/files request 0700/0600;
existing modes are never changed. macOS checks effective UID ownership with Darwin stat,
and denies group/other mode access and any extended ACL. Windows checks the owner SID and rejects allow ACLs
except current user, SYSTEM and Administrators. New directories and explicitly created files
set the current user as owner and use a protected user-only DACL at creation.
No permission repair occurs. If scope is unacceptable, initialization fails.

Portable path validation does not prove network-volume locality, Windows redirected
profile locality, cloud synchronization exclusions,
hard-link identity, or resistance to a malicious same-user process swapping paths.
Do not claim those native checks are implemented. macOS and Windows are the product
targets; Linux store startup throws PlatformNotSupportedException before filesystem mutation.
Windows ACL/lock behavior is implemented but requires Windows execution by independent QA.
A copied DB on another desktop is not prevented by a local file lock; cross-device
controller ownership/trust handoff remains future work.

controller.lock is held with FileShare.None for the full session. migration.lock
is held during initialization. Lock files persist; never delete them to break a
running controller's authority. The OS releases handles on disposal or process death.
All callers must use this initialization boundary, not directly run EF update commands
on the active database. Within a session reads/writes/disposal are serialized.

Startup opens SQLite with pooling disabled, foreign keys on and a bounded busy timeout,
checks integrity, validates migration history against the compiled ordered prefix,
and rejects unknown/newer migrations or newer user_version. Unversioned files containing
application tables are rejected. It never calls EnsureCreated or attempts a downgrade.
The initial EF migration, designer and snapshot are committed. Down throws explicitly.
An abandoned EF __EFMigrationsLock row causes a prompt categorized recovery error:
it is never blindly cleared, and startup does not wait indefinitely for it.

Before any pending migration, SQLite's online BackupDatabase API creates a unique local
before-migration-<random>.db and checks it. It is not a raw file copy, so committed
WAL contents are included. See [Microsoft's backup API documentation](https://learn.microsoft.com/dotnet/standard/data/sqlite/backup)
and [EF SQLite lock behavior](https://learn.microsoft.com/ef/core/providers/sqlite/limitations).
Only after a completed backup does EF MigrateAsync execute. After migration, integrity
is checked again and WAL is enabled. Fresh initialization also backs up the empty SQLite
database before its first migration. Backups are local recovery points, not off-host protection.
No automatic pruning, quota management, encryption, export/import or restore is supplied.

## Failure and reviewed recovery

FleetStorageException supplies a stable StorageFailure category and sanitized startup
message, plus BackupPath when a completed recovery point exists. No store is returned
on failed initialization. Original database, sidecars and backups remain. A partially
created backup can remain after backup failure and is not advertised as completed.
Cancellation propagates cancellation, releasing locks; inspect the directory for
recovery points if cancellation happened after backup.

On corrupt/unsupported/newer schema, stop and preserve all files. Use the compatible
application or a reviewed compatible recovery point. On migration failure, preserve
the original DB and sidecars and the completed BackupPath. The failure test deliberately
leaves a partial schema change and demonstrates that restart rejects it rather than
pretending recovery succeeded. On an abandoned EF lock, stop every controller, preserve
the DB/sidecars, inspect migration history and integrity using a compatible version,
then choose reviewed recovery. No lock-row deletion or restore is automated here.

To restore, first dispose/stop every user of the directory. Preserve the failed directory
as evidence; never overwrite it blindly. Verify a selected backup in a separate local
owner-scoped recovery location with integrity/schema checks and the matching application.
A desktop recovery/import workflow must explicitly coordinate DB, OS credentials, trust
and controller ownership before adoption of that recovered copy. This slice supplies
no dangerous partial implementation of that workflow.

## Baseline integration and platform validation (2026-11-14 follow-up)

Rebased with signing onto main f7095c476ffa698ab4d02555f1a31429d9430c41.
The root solution now includes the four storage projects. CPVM pins EF 10.0.15,
SQLitePCLRaw.bundle_e_sqlite3 3.0.5, Test SDK 18.12.0 and VS runner 4.0.1
(xUnit 2.9.3). The dependency probe also directly references the bundle; its lock
was regenerated with the rest of the solution, not copied from another branch.

Use CHUNKMAESTRO_DOTNET to select the pinned SDK, then:

```sh
python3 tooling/build/build.py sync
python3 tooling/build/build.py update-locks
# After removing project bin/obj, force a cold locked restore:
dotnet restore ChunkMaestro.slnx --locked-mode --force --no-cache --packages .artifacts/cold-packages
python3 tooling/build/build.py verify
```

The legacy validate-standalone.sh now invokes the actual root verifier. It no longer
synthesizes different packages or weaker analyzer policy outside the repository.

Downstream native run 43561704499 (head 27a9162) is failure evidence, not acceptance:
Linux's 20 SQLite tests reached the unsupported OS check at StorageLocation.cs:110;
Windows failed the owner check at :86 after inherited directory creation. Seven
headless UI tests passed, which did not establish storage correctness.

The test project explicitly compiles StorageTests + PlatformSecurityTests on Windows
and macOS (25 cases), and UnsupportedPlatformTests on other build hosts (one contract
case). There are no runtime skips. Linux runs assertions that public construction,
internal preparation, file creation and store opening throw before creating anything.
It does not run or attest the Windows/macOS SQLite runtime suite. This preserves the
Q014 Windows/macOS controller boundary; the restricted Linux agent is a later component.

Local macOS 27.2 arm64 / SDK 10.0.404: normal root verifier passed with 25 tests,
zero failures/skips and zero build warnings. Tests use real SQLite and cover empty
initialization, persisted identity/protocols, invalid/duplicate registration, atomicity,
competing process + forced exit/reacquisition, migration/model consistency, newer
schema rejection, corruption preservation, interrupted migration and consistent backup,
uncheckpointed WAL backup, abandoned EF lock refusal, Unicode paths and provenance.
New security cases cover secure creation/reopening, unsafe existing directory/file
refusal, linked ancestor and dangling entry, and macOS extended ACL rejection.
Windows additionally rejects null DACLs and tests foreign Administrators ownership in a disposable fixture;
that negative test requires the elevated hosted Windows runner and fails if its fixture
cannot be established. Windows execution must be established from this PR's CI, never
inferred from macOS. Prior 19/20-test claims are superseded by these scoped results.

No signing settings, QA/secret gates, live hosts or other branches were changed.
Those earlier test results predate the authority/schema correction described below. Existing unsafe permissions are
never repaired; users must choose a separate secure location through reviewed recovery.

Remaining unknowns: native volume/sync detection, hard-link identity, malicious same-user
path swaps, macOS foreign-UID fixtures (no privilege escalation), Windows elevated native
SQLite sidecar ownership after abrupt crashes, full-disk/mid-migration chaos, 20-instance
load, coverage and mutation thresholds. Windows/macOS SQLite may create transient sidecars;
existing sidecars remain subject to the same strict owner/permission validation. No broad
ACL/owner exception is introduced for recovery. Preserve a rejected directory and its
sidecars/backup for reviewed recovery using the compatible app. Independent QA remains
lead-owned; neither green tests nor this document authorize merge.

Windows hosted run 43615601283 exercised the creation fix: 24/25 cases passed,
including new ACL/owner/link regressions. The remaining case expected UnsupportedSchema
but got InvalidLocation after the test opened its backup read-only. The fixture now
precreates the backup's WAL/SHM files through StorageLocation.CreateOrOpen before
that direct read-only SQLite inspection. Such connections can leave sidecars behind;
elevated Windows default ownership is inappropriate for owner-scoped fixture files.
This creates only absent fixture files with secure ownership, validates existing ones,
and changes no existing permissions or production acceptance. The final assertion
still requires UnsupportedSchema; no assertion was weakened to accept InvalidLocation.


## Authority, schema and dispatcher correction (SR-01/SR-02/FUNC-03/UXP-02/03)

Storage preparation validates every existing ancestor before creating files or directories.
macOS requires root/current-user ownership, no group/other write authority, and no ACL
allow entries on ancestors. Root-owned sticky directories (including `/private/tmp`)
are permitted because untrusted users cannot rename another owner's children there.
Deny-only ancestor ACLs are permitted; leaf directories/files retain strict current-user
ownership, private mode bits and no extended ACL entries. Regular files must have one link.
The native helper compiles with `xcrun clang` against the installed macOS SDK and is copied
with the persistence assembly; builds require Xcode command-line tools and target the
build host architecture. It uses SDK `struct stat`/ACL declarations, not managed offsets.

Windows ancestors require current-user or trusted system ownership (SYSTEM,
Administrators, TrustedInstaller), a non-null DACL, and no untrusted applicable allow
entries granting deletion of children, deletion, attribute writes, or security/ownership
changes. Create-child rights alone cannot replace a validated existing directory; new
components receive protected current-user ACLs and are checked immediately. Leaf owner
criteria remain unchanged. These checks conservatively reject some safe ACL configurations;
permissions are never repaired. Owner/admin/root and malicious arbitrary same-user
processes remain outside the isolation guarantee.

The session holds directory/database native identity handles through disposal and binds
the actual controller lock handle. Windows handles deny delete sharing; macOS compares
held device/inode identities against paths. Identity and permissions are checked across
acquisition and before/after inventory operations. SQLite uses one retained connection,
serialized by the session gate, rather than reopening a path for each read/write. A detected
rename/replacement fails closed with InvalidLocation. Dispose releases the original
connection and handles; it never opens the replacement database for recovery.

Initialization builds an independent in-memory database using the committed EF migrations
at the applied prefix and compares SQLite's stored DDL for tables, columns, keys, FK
constraints/actions, indexes, triggers, views, history and migration-lock structure.
Exact comparison intentionally refuses even benign manual SQL rewrites. Only SQLite's
reserved internal objects are excluded (their constraints are represented in table DDL).
Validation runs before migration and again after migration; unexpected objects and removed
uniqueness/FKs are refused without repair. Physical checks and `PRAGMA foreign_key_check`
are separate: any reference violation fails with CorruptDatabase at open, after migration,
and when verifying backups. A failed backup is not returned as a completed recovery point;
preserve its candidate file for investigation.

The supported prefixes remain explicit: empty database and committed InitialFleet.
No second migration or fabricated v0 was introduced. Populated version-to-version upgrade
and recovery acceptance remains unestablished until a real required schema change exists.
Existing interrupted-migration/backup tests exercise empty-to-initial migration, not that
missing acceptance case.

Open, read, write and disposal execute on background tasks; the per-session gate serializes
I/O. Public calls return tasks before SQLite's synchronous busy waits. Lock contention has
a bounded SQLite timeout and produces a sanitized StorageUnavailable with retry/recovery
guidance. Constraint violations remain InvalidRegistration; cancellation is preserved.
Nothing automatically replays failed writes. Dispose waits for in-flight operations.

Local correction evidence: SDK 10.0.404, macOS arm64, normal verifier passes 38 tests,
zero skips/failures/warnings; formatting and syntax policy remain enforced. Added regressions
cover no-change unsafe ancestry refusal, real user/sticky-temp paths, held directory/file/
lock replacement, missing tables/indexes, altered columns/FKs, unexpected triggers, orphan
rows, backup refusal, held writer call-return responsiveness, sanitized atomic denial,
cancellation and explicit retry. Existing valid reopen, actual migration and failed
migration backup tests pass. Independent reviewer security/UX repro sources were executed
against old and corrected implementations in fresh disposable fixtures. Their old security
harness labels any thrown exception HARNESS_ERROR: corrected expected refusals require
interpretation alongside the asserting repository regressions; that label is not an
acceptance pass. Windows/macOS hosted execution of these new tests and fresh independent
QA are required; local macOS is not Windows evidence.

Coverage >=90% line / >=85% branch / >=80% mutation, real full-disk/interrupted backup/
migration chaos, populated upgrade/recovery, and independent exact-head QA remain merge
blockers. The separate safety worker owns its tooling/workflow/test artifact integration.
No merge approval is asserted. Preserve original rejected files, sidecars and completed
backups; recover only into a separate secure local directory after review with a compatible
application. Old head is retained locally as `backup/pr6-9122a15`.

Native API references: [Apple stat/fstat/lstat](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/stat.2.html),
[Apple sticky-directory semantics](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/chmod.2.html),
installed macOS `acl_get_entry(3)`/`sys/acl.h`,
[Windows directory handles](https://learn.microsoft.com/en-us/windows/win32/fileio/obtaining-a-handle-to-a-directory),
[FILE_ID_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info),
and [CreateFile sharing](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew).

### Windows SQLite sidecar creation

Run 43618443442 passed macOS/Linux but failed 17 of 38 Windows cases at the
owner-SID check after SQLite activity. The retained connection exposes live
WAL/SHM files to the session's ongoing confinement checks. Windows default file
security derives from the creating token; an elevated token can default to an
administrator owner. Before opening SQLite and after enabling WAL, the store now
precreates absent WAL/SHM files using its explicit owner and protected ACL.
Backup connections use the same preparation. Existing files are validated and
never truncated, re-owned or granted broader access by this step.

The normal verifier passes all 41 cases on local macOS with zero skips and zero
build warnings/errors. New cases check live sidecar confinement across writes,
reads and reopen, and unsafe WAL/SHM refusal without changing bytes/permissions
or creating the main database. Actual Windows confirmation remains pending;
these results do not establish the outstanding safety or independent QA gates.
References: [Windows default file security](https://learn.microsoft.com/en-us/windows/win32/fileio/file-security-and-access-rights)
and [SQLite WAL lifecycle](https://www.sqlite.org/wal.html).
