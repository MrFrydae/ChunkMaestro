# Leaf permission contracts — issue 3 / PR 6

Implementation test evidence only; not independent QA or merge approval.
Source Q14/Q149/Q153/Q163–164/Q189–195 and directive §13.1 apply.
The 90% line / 85% branch / 80% mutation thresholds are unchanged.

Parent: `3bc99c26edc3448b3ffdc474ea953bedc92dddf9`.
Branch: `test/3-leaf-permission-contracts`. The delivery commit contains this
record and narrow additions to `AuthorityMutationContractTests.cs` only.
Test SHA256: `bd653c48bc2194d9e9534ae44c048e20d10b50cc30a6837e832384177d4be92f`.
Production source, migrations, model, packages, locks, configuration and native
code are unchanged. All previous 15 authority cases remain intact.

## Behavior exercised

Twelve cases cover owned synthetic leaf files and directories independently.
On macOS each individual GroupRead, GroupWrite, GroupExecute, OtherRead,
OtherWrite and OtherExecute bit is added. On Windows the corresponding cases
add distinct Everyone allow rights: ReadData, WriteData, AppendData, ExecuteFile,
WriteExtendedAttributes and ChangePermissions. Windows execution is still needed.

Each case initializes a populated real store, closes it, snapshots database
bytes, directory entries and permissions, and broadens only its fixture leaf.
It requires sanitized InvalidLocation refusal from direct internal CheckScope
and public OpenAsync, plus CreateOrOpen for files. Refusal preserves content,
entries and unsafe permissions. A finally block restores the exact fixture
mode/ACL; explicit reopening retains the original fleet and permits a new write.
ACL comparisons assert preservation of the fixture, not a prescribed DACL layout.
Ancestor read/traversal permissions are intentionally unaffected.

## Executed validation

macOS 27.2.1 arm64; private SDK 10.0.404/runtime 10.0.15 at
`/Users/derek/.local/share/chunkmaestro/dotnet/dotnet`.
Artifacts: `/private/tmp/pr6-leaf-validation-oqddntf_`.

Every .NET/third-party invocation used `replay_ci.child_environment`'s explicit
noncredential allowlist, absolute SDK/DOTNET_ROOT/PATH, IPv6 disabled, MSBuild
node reuse disabled and telemetry opted out. No credential values were read or
printed. No HTML opened, dashboard, private feed, installation, privilege or
settings changes, live host, world or user data was used.

Commands and results after the final test edit:

- Initial affected `dotnet test ... --no-restore` stopped at NETSDK1004 because
  this checkout had no assets file; no tests executed. No retry without a cause.
- `python3 tooling/build/build.py restore`: locked restore PASS.
- `dotnet test tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj -c Release --no-restore --filter FullyQualifiedName~AuthorityMutationContractTests --logger trx --results-directory <artifacts>/affected`:
  **27 passed, 0 failed, 0 skipped** (15 prior + 12 new).
- `python3 tooling/build/build.py verify`: locked restore, Release build,
  syntax style and generated-inclusive format verification PASS; build has
  **0 warnings, 0 errors**; **170 tests passed, 0 failed, 0 skipped**.
  Full log `verify.log`; copied TRX `repository-verify.trx`.

The parent's 35 storage Python results are unchanged earlier evidence, not a new
execution here. No new Windows, Intel, coverage or full-module measurement exists.

## One focused private diagnostic

The actual local compiler (15 files), archive and original archive tree, sole
factory deletion, 869 post-build source files, 370 dependencies/outputs, SDK
executable and current replay.py builder hashes were reopened and matched to
`/private/tmp/pr6-3bc99c2-measure-a2x304v_/private/provenance.json` before use.
Private advisories in `/tmp/pr6-private-tool-advisory-review.md` remain in effect;
no warnings were suppressed. This is the private upstream build with only
IgnoreBlockMutantFilter registration removed, not an official release.

Exact external drivers: `<artifacts>/diagnostic-driver.py` and
`diagnostic-parser.py`. `diagnostic/commands.jsonl` retains complete commands,
working directories, exits, durations and log hashes. Each command deadline was
1,000 seconds. Following locked restore, clean/nonincremental build and full
baseline, the single mutation invocation was:

```text
dotnet <verified-private-Stryker.CLI.dll> --config-file <repo>/tooling/quality/storage/replay/persistence.json --mutate **/StorageLocation.cs --output <artifacts>/diagnostic/private --log-to-file
```

Discovery directory: `<repo>/tooling/quality/storage`. This is diagnostic selection
only; it does not change any gate exclusion. Exit **2** (upstream threshold
failure), **112.7 seconds**, no command timeout. No identical retry or full-module
rerun occurred. Clean/nonincremental restoration and full tests followed:
**170 baseline and 170 restoration passed, 0 failed/skipped; exact identities equal;
all tracked inputs unchanged** (`diagnostic/restoration.json`).

The complete raw `diagnostic/private-patched.json` is retained, including all
other-source filtered rows. It contains **697 mutant rows**: 67 Killed,
17 Survived, 113 NoCoverage, 26 CompileError and 474 Ignored. StorageLocation's
199 rows contain 67 Killed, 17 Survived, 113 NoCoverage and 2 CompileError.
All 67 Killed rows have killedBy IDs resolving to actual report test definitions.
No timeout receives kill credit; no final score is inferred from this selection.

The repository's strict catalogue parser stopped on two migration entries whose
upstream source field is `File ignored by mutate filter`, each with zero mutant
rows. Raw JSON was preserved unchanged. The external parser checks this exact
sentinel and zero rows, retains those entries separately in
`empty-filtered-files.json`, and validates actual source bytes, UTF16 spans,
source/span/report hashes and identities for every reported mutant row.
Stryker's console counts 800 instrumentation candidates, but the selected JSON
omits the 103 migration mutants; no missing rows or outcomes were invented.

`diagnostic/historical-comparison.json` preserves all 43 historical StorageLocation
rows, original IDs, statuses, replacements, source hashes and spans alongside
fresh diagnostic rows. **Zero newly killed historical survivors**. In particular,
IDs **661–667 remain Survived**, with recorded coverage by the new cases
(`diagnostic/new-case-coverage.json`). Direct CheckScope still calls native cm_scope,
whose leaf check independently rejects S_IRWXG/S_IRWXO; this masks removal of the
C# permission guard even through the internal contract. The passing tests expose
no product defect. No production weakening, equivalence waiver or exclusion is
proposed; the redundant guards and unresolved Windows cases remain reported.

The parent's full official/private reports and historical Windows observations
remain earlier evidence. This focused result does not establish current combined
qualification. Independent role verdicts and current Windows qualification remain
lead-owned and unresolved. No GitHub checks/reviews were queried or changed.

Rollback: revert this signed test/documentation commit; no product data changed.

Artifact integrity (SHA256):

- Raw private JSON: `084cb5cf87fd9ce69be92c5f902937f4cefa94bce018df3ec9d70c4500e67804`.
- Historical comparison: `44e64dbcb8ee77d5e6bfe580e5f1122ccc1676bca21a774b3dce249044883e30`.
- Repository verify log: `9ac2e108216924f323c425f667394f4109d6fc54de1b0f8fc41aa86328b05b63`.

`<artifacts>/artifact-sha256.json` inventories the remaining evidence files.
