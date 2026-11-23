# Persisted schema contracts — issue #3 / PR #6

Implementation evidence only; no independent QA or merge approval. Source Q149,
Q153, Q163–164 and Q189–195; existing fleet contract also defines canonical targets
and distinct Java/TCP and Bedrock/UDP reservations. No product, migration, package,
SDK, lock, workflow, tooling or existing test files changed.

Parent: `516438ab87c5fd502660caeab20a8376f5caa4a1`.
Signed test commit: `cf6e1e1d7522863a5875daa3b0bd211ec723dbc2`.
Branch: `test/3-persisted-schema-contracts`.
Host: Darwin 27.2.0 arm64 (macOS); SDK 10.0.404, runtime 10.0.15.

Thirty new real SQLite cases seed through LocalFleetStore public registration in
unique owner-scoped ForTests directories. Fourteen NULL cases cover required
metadata/relationships, three canonical uniqueness cases cover host/service and
same-host same-transport ports across different bind addresses, four missing-parent
cases cover every stored FK, three parent-delete cases refuse inventory loss, and
four cases refuse invalid editions/ports. Two positive boundary cases retain ports
1 and 65535 with both TCP and UDP after reopen. Connections explicitly enable and
check foreign_keys=1. Every denied write asserts the SQLite constraint category,
compares every stored value (including hidden endpoint HostId), then reopens the
public store and compares identities, metadata, relationships and endpoints.
No source/DDL matching or model annotation assertions are used.

## Validation

Commands from the checkout, with CHUNKMAESTRO_DOTNET set to
`/Users/derek/.local/share/chunkmaestro/dotnet/dotnet`,
DOTNET_SYSTEM_NET_DISABLEIPV6=1 and MSBUILDDISABLENODEREUSE=1:

```sh
/Users/derek/.local/share/chunkmaestro/dotnet/dotnet test tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj -c Release --filter FullyQualifiedName~DatabaseConstraintContractTests --logger trx --results-directory /tmp/pr6-schema-contracts/focused
python3 tooling/build/build.py verify
```

Focused: 30 passed, zero failed/skipped. Root verify: exit 0, locked restore,
Release build, syntax checks, generated-inclusive formatting and 131 passed,
zero failed/skipped. Logs: `/tmp/pr6-schema-contracts-focused.log` and
`/tmp/pr6-schema-contracts-verify.log`. Final restoration below also passed 131.
The test commit signature was verified against the configured public signing key
using a disposable allowed-signers file and a per-command git configuration;
persistent signing/trust settings were unchanged.

## Focused private diagnostic, not qualification

Executed `python3 /tmp/pr6-schema-contracts-diagnostic.py` on the clean signed test
commit. The script verifies the private build's source/compiler/dependency/archive/
patch hashes against `/tmp/pr6-replay-private-lead-v2/provenance.json`, freezes
workspace/config hashes and records its explicit noncredential constant environment
allowlist in `diagnostic/binding.json`. It inherits no environment values. Access
remained within the conditions in `/tmp/pr6-private-tool-advisory-review.md`;
existing advisories remain unresolved, with no dependency or threshold changes.
Only InitialFleet was selected. JSON reporting only; no local HTML opened or
executed, dashboard reporting, credentials, live hosts or user data.

Production InitialFleet SHA256:
`6ec74386301dbd5ed289b61fc4da6e1765dc7560292489cbb796c836b376811f`.
Its bytes match the historical 4f263fc report exactly. Private result: **76 killed,
17 survived**, no timeout/compile-error/no-coverage entries. Tool exit 0 is only
this diagnostic selection's result, never a full-module gate pass. Baseline and
restoration each passed 131 with zero failures/skips; all tracked hashes and clean
status were unchanged afterward. Exact executed commands follow:

```text
/Users/derek/.local/share/chunkmaestro/dotnet/dotnet restore /Users/derek/.t3/worktrees/ChunkMaestro/t3-de1d2a82/tooling/quality/storage/MutationTests.csproj --locked-mode
# exit 0; 0.676s; log /tmp/pr6-schema-contracts/diagnostic/restore-cf65acb9e1eb4a849d90c4d25242f9a4.log
/Users/derek/.local/share/chunkmaestro/dotnet/dotnet test /Users/derek/.t3/worktrees/ChunkMaestro/t3-de1d2a82/tooling/quality/storage/MutationTests.csproj -c Release --no-restore --logger trx --results-directory /tmp/pr6-schema-contracts/diagnostic/baseline
# exit 0; 7.441s; log /tmp/pr6-schema-contracts/diagnostic/baseline-75af1f8c30bd413c918e3beb63f5789d.log
/Users/derek/.local/share/chunkmaestro/dotnet/dotnet /private/tmp/pr6-replay-private-lead-v2/stryker-net-6e77a3451bac4793e9c839c3ff4055c1b30ca3af/src/Stryker.CLI/Stryker.CLI/bin/Release/Stryker.CLI.dll --config-file /tmp/pr6-schema-contracts/diagnostic/config.json --output /tmp/pr6-schema-contracts/diagnostic/mutation --log-to-file
# exit 0; 202.264s; log /tmp/pr6-schema-contracts/diagnostic/private-3a9030953eb247eba831ccc64c4449e4.log
/Users/derek/.local/share/chunkmaestro/dotnet/dotnet clean /Users/derek/.t3/worktrees/ChunkMaestro/t3-de1d2a82/tooling/quality/storage/MutationTests.csproj -c Release
# exit 0; 0.536s; log /tmp/pr6-schema-contracts/diagnostic/restored-clean-a732583026774ed69c2d4f25771ef434.log
/Users/derek/.local/share/chunkmaestro/dotnet/dotnet test /Users/derek/.t3/worktrees/ChunkMaestro/t3-de1d2a82/tooling/quality/storage/MutationTests.csproj -c Release --no-restore --logger trx --results-directory /tmp/pr6-schema-contracts/diagnostic/restored
# exit 0; 6.362s; log /tmp/pr6-schema-contracts/diagnostic/restored-10f4bd97b26143968454d5d1605b4435.log
```

All 16 historical InitialFleet survivors remained survivors: IDs 472–483, 485,
489, 493 and 496 (the exact enumeration and source/span/mutator/replacement match
are retained in `diagnostic/comparison.json`). They alter the original endpoint
column types/nullability, PK/FKs and uniqueness. The subsequent endpoint migration
replaces those definitions, so the current public API/reopen contracts do not
observe their removal from the earlier migration. **No new kill credit or
equivalence waiver is claimed.** The seventeenth survivor is ID507, removing the
Down refusal message (historically NoCoverage); the all-version downgrade tests
still require refusal, rather than specific wording. It remains reported too.
The lead's 15 remaining model survivors were not rerun or waived here.

Artifacts are under `/tmp/pr6-schema-contracts`; `artifact-sha256.json` records
command logs, configs, bindings, comparisons, reports, TRX and script hashes.
Historical `/tmp/pr6-product-mutation-4f263fc-delivery.json` is diagnostic context,
not acceptance evidence. No production defect was confirmed in this assignment.

## Remaining limits

No fresh full-module coverage/mutation qualification, Windows/Intel execution,
native UI/performance or independent role verdicts were collected. All 90/85/80
thresholds, native evidence and independent QA gates remain required. The lead
owns aggregation and hosted integration. No push, PR comment, merge or watch was
performed; hosted checks/reviews were not queried. Revert the test-only commit to
remove these contracts; no database recovery or production rollback is involved.

PR https://github.com/MrFrydae/ChunkMaestro/pull/6; local checks pass as above,
hosted checks/reviews unassessed by this implementation worker.
