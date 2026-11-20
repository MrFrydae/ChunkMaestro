# Windows authority test handoff

Implementation worker, PR #6, parent `40db08810bf038e79bb16112abfbbb3e84bb2331`,
base `2e4d072c84dc3e7ac8cb3a310e83051c8579237d`. Requirements Q14, Q149,
Q153, Q163–164 and Q190; storage authority/recovery and safety qualification
sections of the orchestration directive. Later owner authorization permits the
parent one bounded hosted Windows qualification run. This worker did not spend it.

## Scoped patch

Only the new `WindowsAuthorityQualificationTests.cs` and this handoff are owned.
Production, existing tests, project files, tools and workflows are unchanged.
Nine Windows test cases supplement `PlatformSecurityTests`,
`ControllerLockFailureTests` and `AuthorityMutationContractTests`:

- Four cases add actual SYSTEM or Administrators FullControl grants to an
  owner-owned directory or database. They inspect the real grant and owner,
  reopen retained fleet data, persist a new fleet, and check DACL preservation.
- Two cases distinguish actual inherited ANONYMOUS LOGON Allow and Deny rules.
  The runner token is asserted to exclude that principal. Inherited foreign Allow
  refuses scope and public opening, preserving bytes, entries and DACL. Deny
  permits retained-data reading and writing. Fixture restoration precedes an
  explicit reopen/write. No new account or token is created.
- Two cases permit foreign read-only ancestor access or an InheritOnly grant.
  Secure creation must protect every missing intermediate and leaf directory and
  the new file. Assertions inspect owner, DACL protection, owner FullControl and
  absence of inherited entries, then verify file contents and ancestor DACL.
- One case holds a real exclusive database handle. Binding refuses with
  InvalidLocation; bytes remain intact. Disposal permits rename/retry; successful
  binding prevents database/directory relocation; disposal leaves the caller's
  controller stream usable and releases the directory for relocation.

Existing suites already exercise null DACLs, wrong leaf owner, foreign explicit
leaf grants, dangerous live ancestor rights, links/dangling links and basic
identity/lock lifecycle. This patch does not repeat those as new evidence.
No genuine production defect was established during this source review.

## Required parent integration before running tests

The Windows class has an OS annotation for compiler analysis, **not** a runtime
skip. Do not run it on macOS. Add these blocks after the existing Compile items,
following the repository's platform-specific compile conventions.

In `tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj`:

```xml
<ItemGroup Condition="!$([MSBuild]::IsOSPlatform('Windows'))">
  <Compile Remove="WindowsAuthorityQualificationTests.cs" />
</ItemGroup>
```

In `tooling/quality/storage/MutationTests.csproj`:

```xml
<ItemGroup Condition="!$([MSBuild]::IsOSPlatform('Windows'))">
  <Compile Remove="../../../tests/ChunkMaestro.PersistenceTests/WindowsAuthorityQualificationTests.cs" />
</ItemGroup>
```

On Windows both existing wildcards include this file. Verify evaluated Compile
items in both projects after integration. The worker cannot commit those project
edits under its NEW-files-only ownership.

## Actual local validation

Host: Darwin arm64; Python 3.13.18; pinned SDK 10.0.404. No Windows OS or Windows
ACL/kernel execution was available. Commands from the checkout root:

```sh
export CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet
python3.13 tooling/build/build.py restore
"$CHUNKMAESTRO_DOTNET" build tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj --no-restore -c Release
"$CHUNKMAESTRO_DOTNET" restore tooling/quality/storage/MutationTests.csproj --locked-mode
"$CHUNKMAESTRO_DOTNET" build tooling/quality/storage/MutationTests.csproj --no-restore -c Release
"$CHUNKMAESTRO_DOTNET" format ChunkMaestro.slnx --verify-no-changes --no-restore --include tests/ChunkMaestro.PersistenceTests/WindowsAuthorityQualificationTests.cs
DirectoryBuildTargetsPath=/tmp/windows-authority-local.targets python3.13 tooling/build/build.py verify
```

Both builds compile the new Windows source on macOS and passed with zero warnings
or errors. Formatter verification of the included source passed. For root verify,
the external targets file imports the existing repository Directory.Build.targets
and removes the two relative Windows Compile paths on non-Windows. This models
exactly the snippets above without editing an existing repository file. Root
verify passed locked restore, Release build, syntax/format verification and
264 macOS tests (zero failures, zero skipped). The syntax check inspects the new
file too. A first pre-restore build found missing assets; locked restore resolved
that prerequisite before successful builds.

Local logs and the temporary import are copied under
`.artifacts/windows-authority/`. The TRX is under `.artifacts/tests/0/`.
Artifacts are local validation, not qualification receipts. No test/dylib/source
weakening, coverage exclusion, mutation suppression, or long mutation run occurred.

## Remaining evidence

All nine new Windows cases are **UNEXECUTED**. Compilation does not prove ACL
inheritance, native sharing, Windows token behavior or SQLite operation there.
The parent must integrate the compile exclusions and collect fresh exact-head
Windows and macOS coverage/mutation/native/full-volume/recovery evidence. No
historical kill credit transfers; timeouts do not count as actual Killed.
90% line, 85% branch and 80% actual Killed thresholds and original eligibility
remain binding. Collection is not qualification PASS. This handoff supplies no
independent QA verdict and cannot resolve qa-evidence by itself.

Rollback is removal of these two new files and the parent-owned compile snippets;
no production data, users, privileges or machine policy were changed.
