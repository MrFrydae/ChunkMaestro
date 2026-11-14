# Local fleet storage, contract v1 (issue #3)

This bounded storage slice addresses Q14, Q22, Q25, Q45, Q149, Q153 and Q163–164.
It is not completion of Q153, controller migration, Phase 1, or independent QA.

## Native application integration

Reference Application for `IFleetInventory` and Domain for immutable identity/metadata records.
The desktop composition root references Persistence and owns a single
`await using var store = await LocalFleetStore.OpenAsync(StorageLocation.ForCurrentUser(localStorageConfirmed: true), token)`.
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
and denies group/other mode access. Windows checks the owner SID and rejects allow ACLs
except current user, SYSTEM and Administrators; it inherits existing user-directory ACLs.
No permission repair occurs. If scope is unacceptable, initialization fails.

Portable path validation does not prove network-volume locality, Windows redirected
profile locality, macOS extended ACL confinement, cloud synchronization exclusions,
hard-link identity, or resistance to a malicious same-user process swapping paths.
Do not claim those native checks are implemented. macOS and Windows are the product
targets; Linux store startup deliberately rejects unsupported owner semantics.
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

## Integration dependencies and validation

Add these projects to the root solution:

- src/ChunkMaestro.Domain/ChunkMaestro.Domain.csproj
- src/ChunkMaestro.Application/ChunkMaestro.Application.csproj (references Domain)
- src/ChunkMaestro.Persistence/ChunkMaestro.Persistence.csproj (references Domain/Application)
- tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj (references Persistence)

Required central PackageVersion entries used in validation:

| Package | Version |
| --- | --- |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.15 |
| Microsoft.EntityFrameworkCore.Design | 10.0.15 |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 |
| Microsoft.NET.Test.Sdk | 17.14.1 |
| xunit | 2.9.3 |
| xunit.runner.visualstudio | 3.1.5 |

Design and test runner are PrivateAssets=all. The explicit SQLite bundle overrides
the old native SQLite transitive dependency. Early EF 10.0.0 validation produced
NuGet vulnerability warnings; it is not the delivery dependency set.
Commit the included project package lock files with these versions; lead integration
must regenerate locks if the central baseline differs. Target net10.0, nullable and
implicit usings enabled. Central props/solution/package files were not edited.

The self-contained validation command is:

```sh
bash tests/ChunkMaestro.PersistenceTests/validate-standalone.sh
```

It copies only owned projects into a new temporary directory, creates standalone
central package/build props and solution outside the checkout, retains evidence,
and runs these exact checks there:

```sh
dotnet test StorageValidation.slnx --nologo --logger 'trx;LogFileName=storage.trx'
dotnet format StorageValidation.slnx --verify-no-changes --no-restore
```

Set CHUNKMAESTRO_DOTNET to a .NET 10 SDK executable on another machine.
Tests use actual temporary SQLite DBs, not mocks: empty initialization,
persistent identities/reload, TCP versus UDP, invalid and duplicate targets,
atomicity, competing process and forced exit/reacquisition, newer history/version,
corruption preservation, failed migration with recoverable backup, online WAL
backup, abandoned migration lock, symlink refusal and cached-state provenance.
Failure injection uses an internal migration executor seam; only the public path
uses EF MigrateAsync. Initial schema creation is exercised by every ordinary test.

Implementation evidence: macOS 27.2.1 arm64; SDK 10.0.404, runtime .NET 10;
19 tests passed, zero skipped; standalone test and format checks passed. Windows runtime, full-disk/kill-during-migration chaos,
malicious filesystem races, 20-instance load, line/branch coverage and mutation
thresholds have not been verified. These remain independent QA/integration gates.

## Later owner policy: explicit C# types

All checked-in C# now uses explicit types, including tests and migration source. Root `.editorconfig` rejects `var` through IDE0008; standalone validation copies the policy and enables style checks during compilation. Existing namespace/braces formatting was normalized to the shared baseline. After conversion, standalone warning-as-error compilation, all 19 SQLite tests (zero failures/skips), and formatting passed. This does not constitute independent QA or native platform acceptance.

Later owner policy: all C# brace-delimited constructs now span multiple lines (including properties, empty blocks and initializers); interpolation delimiters are unchanged. Formatter and syntax-aware policy checks passed. The 19 real SQLite tests and standalone build/format passed after this formatting change. Repository-wide CI enforcement is supplied by the dependent engineering baseline PR.
