# Native storage qualification (issue #3; Q153/Q164/Q190–191)

Run sequentially, after the normal repository verifier:

```sh
CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet \
DOTNET_SYSTEM_NET_DISABLEIPV6=1 \
python3 tooling/quality/storage/native.py /absolute/new/output-directory
```

The output directory must not already exist. This runner compiles the exact
`src/ChunkMaestro.Persistence/Native/storage.c` into isolated dylibs. It never
writes the production source or installed/test dylibs. No dotnet build runs
inside native collection. Use one collection/build at a time per worktree.
Fixtures use TemporaryDirectory; chmod/ACL changes affect only those fixtures.
There are no ownership changes, mounts, network requests or privileged commands.
Commands have a 60-second per-process bound; the checked-in mutation catalogue
bounds the number of compiler/test processes. Output is retained on failure.

`native.json` schema version 2 is the machine contract for the lead-owned gate:

- `head`, `clean`, `platform`, `architecture`, `source_sha256`, `runner_sha256`
  and `resources` bind evidence to checkout and harness. Regenerate after commit.
- `runtime` points to real filesystem assertions; `metrics` and
  `coverage.json` contain **only real filesystem** LLVM coverage of the exact C
  file. `uncovered_branches` retains raw LLVM branch coordinates and true/false
  counts. Do not interpret a compound expression as a single branch.
- `controlled_failure_coverage` and `combined-coverage.json` separately report
  real cases plus ten simulated API/metadata cases. `faults.h` redirects only
  the instrumented C translation unit to test wrappers; it does not modify the
  source. These cover foreign/root owners and ACL lookup/tag/enumeration/free
  failures. They do **not** establish those OS conditions occurred naturally.
- `mutation` records every catalogue entry, original/replacement/site, mutated
  source SHA256, status and failing assertion names. Mutated C sources and
  dylibs are retained. `commands` retains full compiler/test argv, return codes,
  timeouts and logs. Killed means a completed JSON assertion report with at
  least one failed invariant; crashes and infrastructure errors are not kills.
  Uncovered means a surviving site has no baseline LLVM region execution.
  Score is killed / **all entries**, including uncovered, compile errors and
  timeouts. Survivors are never ignored or assigned equivalent waivers.
- `qualification` reports separate real filesystem, controlled failure and
  mutation threshold booleans. Top-level `status` remains FAIL if real filesystem
  coverage is below 90% line / 85% branch, even if controlled failure coverage
  meets those targets. Mutation requires >=80%; execution errors/timeouts fail.
- `preservation` verifies source/dylib hashes after execution; original source
  and any pre-existing test dylib are copied into the output before compilation.

This is a bounded, deliberately selected safety mutation catalogue, not exhaustive
mutation of every C token. Some checks overlap in the current implementation;
all survivors remain visible. The real suite establishes mode, ACL allow/deny,
symlink/hardlink refusal, descriptor nofollow/cloexec, rename/replacement identity,
missing/closed/invalid handles, special files and cross-device pipe identity.
No Windows, Intel macOS, managed migration, backup or independent QA conclusion
can be drawn from this native arm64 evidence.
