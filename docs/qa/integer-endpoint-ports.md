# Integer endpoint port correction — local implementation evidence

Source: Q14/Q149/Q153/Q163–164; storage/testing directive.
Finding: https://github.com/MrFrydae/ChunkMaestro/pull/6#discussion_r4603013813
Parent: `7fc110dfd86e8ef210e03d87cfac47bc887a5849`.
This is implementation evidence, not an independent QA verdict.

## Reproduction and resulting behavior

On the unchanged parent production code, two disposable real-SQLite/public-API
reproductions passed: InitialFleet and EnforceEndpointInvariants each stored
`real:25565.5`; inventory materialized port 25565 and registration of a second
instance at integer 25565 succeeded. The final refusal/upgrade expectations
failed four cases against that implementation (zero skips). Temporary repro
source and TRX are retained externally; no deliberately vulnerable test remains
in the suite.

Five new cases pass after correction: both populated historical versions refuse
fractional startup twice before upgrade/backup or usable-store return, with
identical main database bytes, raw rows/types/values, migration history and an
existing completed backup. A separately copied fixture receives an explicit
integer correction, upgrades and refuses duplicate registration without adding
an instance; originals remain identical. Both historical versions preserve
1/25565/65535 through upgrade and compatible backup recovery. Direct fractional
UPDATE and INSERT fail with SQLITE_CONSTRAINT_CHECK (275) without inventory
changes. Whole REAL and numeric-text inputs are verified as stored integers,
not mislabeled as corrupt. The fifth case injects an exception after the new
migration's DROP, verifies an active transaction and enabled foreign keys,
then proves row/schema/history rollback and absence of the temporary table.
This injected failure is narrower than actual process death.

## Migration review and integrity

Existing EF Design and dotnet-ef 10.0.15 generated the new migration, Designer and
snapshot through FleetDesignFactory. The scaffold changed only CK_Endpoints_Port;
the generated default rebuild was replaced with the established transactional
leaf-table SQL pattern and refused Down. Generated C# was normalized to explicit
types, Allman braces and file-scoped namespaces. Model pending-change checks
pass. Generated forward SQL has one BEGIN/COMMIT around CREATE/copy/DROP/rename,
unique-index recreation and history insertion. Edition CHECK, PK, restrictive
FKs and indexes remain intact; no foreign-key disable or suppressed transaction.
Endpoints must remain a leaf with no incoming FKs.

Both committed migrations and Designers are byte-identical to the parent.
SHA256:

| Source | SHA256 |
| --- | --- |
| InitialFleet.cs | `6ec74386301dbd5ed289b61fc4da6e1765dc7560292489cbb796c836b376811f` |
| InitialFleet.Designer.cs | `8e4a563a768ea02a924ec2d440661aeee827a695fed95ac6cfcf544b3617d342` |
| EnforceEndpointInvariants.cs | `95e6511a938109949804fb06ef888729efff361d610a0597c19b9c3190487c63` |
| EnforceEndpointInvariants.Designer.cs | `cc236416dd65c62e90f1aa10dce489d45ec4f7f1b329fec1a3802c798f2491b4` |

VerifyIdentitiesAsync is unchanged for parent integration. No SDK, dependencies,
analyzers, nullable/error policy, CI, shared gates or qualification tooling changed.

## Commands and evidence

Host: Darwin 27.2 arm64; private SDK 10.0.404, .NET runtime 10.0.15.
DOTNET below means `/Users/derek/.local/share/chunkmaestro/dotnet/dotnet`.
Evidence directory: `/private/tmp/pr6-integer-port-evidence` (local, not uploaded).

- `DOTNET test tests/ChunkMaestro.PersistenceTests -c Release --filter FullyQualifiedName~IntegerEndpointPortTests --logger 'trx;LogFileName=pre.trx' --results-directory /private/tmp/pr6-integer-port-evidence`: pre-fix 2 repro passes, 4 expected regression failures, 0 skips.
- `DOTNET /private/tmp/chunkmaestro-endpoint-ef-tools/.store/dotnet-ef/10.0.15/dotnet-ef/10.0.15/tools/net8.0/any/dotnet-ef.dll migrations add EnforceIntegerEndpointPorts --project src/ChunkMaestro.Persistence --configuration Release` with DOTNET_ROOT set to the private SDK directory: generation passed.
- Same EF entry point, `migrations script 20261115020704_EnforceEndpointInvariants --project src/ChunkMaestro.Persistence --configuration Release --output /private/tmp/pr6-integer-port-evidence/forward.sql`: passed.
- Focused IntegerEndpointPort/PopulatedUpgrade/InitialEndpointConstraint/DatabaseConstraintContract/SchemaReferenceContract run: 52 passed, 0 failed/skipped before the fifth new rollback case was added.
- `DOTNET format whitespace ChunkMaestro.slnx --no-restore --include-generated`, then full formatter on generated/new files: normalized output. Two intermediate verifier runs caught generated import ordering; corrected with no weakened rules.
- `CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet python3 tooling/build/build.py verify`: final 227 passed, 0 failed/skipped; locked restore, Release build (0 warnings/errors), syntax and generated-inclusive formatter passed.
- `python3 tooling/specification/check.py`: passed.
- `git diff --check`: passed.

Logs include pre.log/pre.trx, affected.log/affected.trx, generated-migration.cs,
generate.log, forward.sql, verify.log and verify-final.log (intermediate format
failures), verify-pass.log (success), dotnet-info.txt and unchanged-migrations.sha256.
Final full-suite TRX, source/runtime/evidence hash manifest and exact signed local
delivery commit are recorded in the external machine report.

## Recovery and remaining blockers

Follow the explicit separate-copy recovery procedure in fleet-storage.md.
Out-of-range integer migration-failure semantics remain covered by existing
regressions. No automatic repair, downgrade or restore is introduced.

No push, PR mutation, workflow trigger, watch, pilot, full mutation, volume or
native qualification campaign was performed. The prior pilot remains stopped.
90% line / 85% branch / 80% mutation, Windows/macOS/native/volume/recovery
qualification and all three independent roles remain merge blockers. Local
macOS implementation tests are not Windows execution, full native acceptance or
independent review. The parent must run combined verification after integration
of its separate identity-encoding correction and obtain exact-head reviews.
