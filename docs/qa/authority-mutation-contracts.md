# Authority mutation contracts — issue 3 implementation evidence

This is implementation test work for PR https://github.com/MrFrydae/ChunkMaestro/pull/6,
not independent QA or current-head safety qualification. Source Q149, Q153,
Q163–164 and Q189–195 and directive §13 apply. Gates remain 90% line,
85% branch and 80% actual mutation kills; timeouts never earn kill credit.

Base: `4cd923c7059d1a3a1c789ce0cfd81e957b0a13f9`.
Branch: `test/3-authority-mutation-contracts`. The signed delivery commit contains
only this document and `AuthorityMutationContractTests.cs`.
Both owned runtime sources remain byte-identical to published `ba8b1b9`.
No SDK, dependency, lock, production, existing-test or policy changes were made.

## Behavioral scope

Existing StorageTests and PlatformSecurityTests were assessed first. They already
exercise unconfirmed consent, public unsafe-entry refusal, leaf permission
expansion, live replacement, sidecars during SQLite use and unsafe ancestors at
startup. New cases add these distinct boundaries:

- Resolve the confirmed production default path through ForCurrentUser, checking
  the OS local-data root and fixed store components. Never prepare or open that
  real user directory. The existing consent-refusal test remains intact.
- Reject a relative path whose absolute target is disposable and owner-scoped,
  without creating a database or destination.
- Refuse missing native identities; allow explicit creation and retry.
- Exercise failed BindFiles with missing database, followed by disposal, directory
  rename and retry. Refuse a disposed caller controller stream.
- Prove directory/file capture handles release on disposal while the caller-owned
  controller stream remains writable. On macOS, removal of the bound pathname
  refuses verification, and restoration of the original inode permits retry.
- Refuse unsafe scope during identity construction and files made unsafe between
  directory capture and binding, before returning authority to the caller.
- Refuse an owner-created hard link on macOS and a null file DACL on Windows,
  preserving contents and allowing explicit retry after fixture restoration.
- Expand only an owned synthetic ancestor's authority during a live session;
  refuse read and registration, preserve prior inventory, then restore and retry
  the previously rejected identity. macOS exercises group/other write; Windows
  enumerates six distinct dangerous rights.
- Exercise sidecar preflight directly: preserve nonempty existing WAL/SHM bytes,
  create only absent peers on Windows, and validate all existing peers before any
  creation. macOS preflight leaves creation to SQLite. Unsafe synthetic sidecars
  remain untouched; restore only fixture changes before explicit retry.

Internal friend APIs are used for actual identity lifetime and preflight refusal,
not reflection or private fields. Failures require a categorized invalid location,
nonempty authority-related guidance, no sensitive path and no claimed backup.
Fixtures use unique owner-scoped temp paths. Permission changes restore captured
modes or access descriptors in finally. No foreign ownership, real ancestor,
privilege, live host, world, secret or user data changes are involved.

## Executed validation

macOS 27.2.1 (26B131), arm64; SDK 10.0.404, runtime 10.0.15.
Selected host: `/Users/derek/.local/share/chunkmaestro/dotnet/dotnet`.
Restore/build/test/format commands ran with the explicit noncredential environment
from `replay_ci.child_environment`, plus CHUNKMAESTRO_DOTNET set to that host.
It forces DOTNET_SYSTEM_NET_DISABLEIPV6=1 and MSBUILDDISABLENODEREUSE=1.
Credential values were not inspected or printed. No private tool was executed.

Final commands (the dotnet executable below is the selected host):

```sh
dotnet format whitespace ChunkMaestro.slnx --no-restore --include tests/ChunkMaestro.PersistenceTests/AuthorityMutationContractTests.cs
dotnet test tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj -c Release --no-restore --filter FullyQualifiedName~AuthorityMutationContractTests --logger 'trx;LogFileName=authority.trx' --results-directory /tmp/pr6-authority-contracts
python3 tooling/build/build.py verify
```

Final focused suite: **15 passed, 0 failed, 0 skipped**. Repository verify:
**148 passed, 0 failed, 0 skipped**, with locked restore, Release build,
read-only syntax guard and generated-inclusive formatter verification all passing.
An initial compilation failed CA1416 on deferred restoration lambdas; explicit
platform guards corrected those call sites without suppressions. Its failure log
is retained. Final tests ran after the last C# change and formatting.

Artifacts: `/tmp/pr6-authority-contracts/manifest.json` records source and artifact
SHA256 values, OS/architecture, base and results. Important artifact hashes:

| Artifact | SHA256 |
| --- | --- |
| authority.trx | `3cecc4d65378f79a3b161eefa3777a0d6f517cc29de66698085624330240dc9e` |
| repository.trx | `479c19dd842fdccfc7f981f12974fbfa30a28cd8771a46cb4c77ad97ff9717a3` |
| verify.log | `48c2b40fd7905d833454ba519d22db28bd196d1fca4dcf77ccd3b0982fbf4e81` |
| historical-owned-survivors.json | `28c37860b5d23a33f9f4f8d3fe8d3bd0ba833bde70fddc743ece45e37983425b` |

Runtime source SHA256:
StorageLocation `275915e2b5f675e7181fca2aafa935129c8fc8cac2946dd97af9607a0da13166`;
StorageIdentity `c5f516ce53b34ed99604eb38a8bcc4f9c0391121f6e0bd3870e61ec720b3c174`.

## Unresolved qualification and platform limits

No mutation diagnostic was run. No additional kills, coverage improvement or
current-head qualification is claimed. The artifact preserves all **78** supplied
owned-source survivor rows (57 StorageLocation, 21 StorageIdentity), including
original identities and all four platform/tool outcomes. Every one remains
unresolved for this delivery pending actual current-head measurement. No tests or
mutants were removed and no equivalence waiver was introduced.

The lead's immutable ba8 compatibility evidence (699/739 lines, 244/276 branches,
553/774 strict actual kills, 67 additional kills needed) is historical only. It is
not imported as fresh evidence. The 90/85/80 thresholds and full matched-platform
qualification remain lead-owned.

Windows runtime execution, Windows ACL restoration and handle cleanup, macOS
Intel execution, empty/unavailable native default roots, foreign ownership and
trusted-system principal variations remain unexecuted here. UNC access is not
attempted. The suite does not alter process/user settings to manufacture default
root failures. Linux excludes these runtime tests and runs the repository's
unsupported-platform contract; that would not qualify these cases. No observed
production defect required an ownership handoff. Final independent role verdicts
and hosted checks for the delivery head are not established by this work.
