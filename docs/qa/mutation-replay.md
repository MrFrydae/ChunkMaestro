# Private Stryker block execution experiment (issue 3, Q190)

Q190 and directive §13.1 remain **90% line / 85% branch / 80% mutation**.
This implementation collects supplemental evidence; it does not change or pass
`qualify.py`, CI, mutation exclusions, manifests, dependency versions or QA gates.
Independent final Functional, Security/Reliability and UX/Performance roles are
still required for storage qualification. This is implementation evidence only.

The preferred mechanism builds upstream Stryker.NET 5.0.0 at
[6e77a3451bac4793e9c839c3ff4055c1b30ca3af](https://github.com/stryker-mutator/stryker-net/tree/6e77a3451bac4793e9c839c3ff4055c1b30ca3af).
The **only source edit** deletes `new IgnoreBlockMutantFilter(),` from
`MutantFilterFactory`. The builder checks both the archive and original factory
SHA256 before making the exact one-line deletion. It preserves upstream ending
returns, out initialization, mutators, compilation, test execution and other
filters. There are no handwritten C# repair substitutions. This private build is
**ChunkMaestro-private-stryker5-no-block-filter**, not an official release/tool.
The installed official tool and pinned repository manifest remain untouched.

The official codeload archive SHA256 is
`80702658bac0989e9416e43239ee9d0ca971cea79628bed2b7b1917edd07b269`;
original factory SHA256 is
`203c899e3f5bdd8b0f263b02559ab8f2d7c20754e6bd7c501ac2a1c6b360d3e7`;
patched factory SHA256 is
`ba884d6b60901af8743e3942f12a9237ef8f61c1874029dadc05789858156f5f`.
The archive was previously researched against the installed package's repository
commit and the official annotated release tag. A hash mismatch refuses the build.

## Reproduction

Use an unused, user-owned external temporary path for every build and attempt.
Source trees and built third-party binaries stay outside the repository. No tool
installation, SDK bootstrap or profile changes are performed. Dependencies restore
from upstream's unchanged locks. The upstream build generates its normal HTML
reporter JavaScript through LibraryManager; that resource's bytes are recorded
separately in `source_after_build`, along with all binary/assets/lock hashes.
The build currently emits upstream dependency/security warnings; see its retained
log. No package update or warning suppression is applied. This build has not been
approved as a generally distributed dependency.

```sh
export CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet
export DOTNET_SYSTEM_NET_DISABLEIPV6=1
export MSBUILDDISABLENODEREUSE=1
python3 tooling/quality/storage/replay.py build-private /tmp/my-private-stryker
```

`--archive PATH` may reuse the same official archive; its SHA256 is still checked.
All filesystem paths used in MSBuild calls are canonicalized: mixing macOS `/var`
and `/private/var` (or `/tmp` and `/private/tmp`) can produce a restored graph
without its project references. Such a graph is not successful parity evidence.

Copy `tooling/quality/storage/replay/fixture` to a fresh external directory
(excluding any `bin`/`obj` directories). The fixture includes its own exact package
locks and SDK pin. Then, using the actual installed official entry DLL:

```sh
python3 tooling/quality/storage/replay.py run --fixture \
  --root /tmp/my-fixture --output /tmp/my-fixture-attempt \
  --provenance /tmp/my-private-stryker/provenance.json \
  --official /Users/derek/.nuget/packages/dotnet-stryker/5.0.0/tools/net10.0/any/Stryker.CLI.dll \
  --config /tmp/my-fixture/config.json \
  --test-project /tmp/my-fixture/Tests/Tests.csproj
```

Do not build resource fixture projects in place: they are disposable standalone
inputs, not projects in the product solution. They intentionally have their own
package declarations. Production runs omit `--fixture`, require a clean workspace,
and collect a **fresh official report first**. Use the exact clean target head,
actual test project and reviewed config under that workspace. The runner offers no
historical-report import. The lead must serialize repository/native build outputs;
the exclusive lock prevents other invocations of this runner, not arbitrary tools.
The production run path has not been exercised against the product suite here.

## Machine artifact contract (schema 1)

- `provenance.json`: distinct private identity; source commit/archive URL/hash;
  every original/patched source hash; exact patch bytes/hash; complete compiler
  file hashes and `dotnet --info`; build command/exit/timing/log; every resulting
  binary, dependency assets and lock hash. Existing upstream files/locks must not
  change during build. The one expected generated reporter resource is recorded.
- `binding.json`: category (`fixture-only` or `production-experiment`), exact head
  (null only for fixture), OS/architecture, all tracked workspace input hashes (all fixture inputs in fixture mode), including
  production/tests/native/projects/configs/locks, config and official tool file
  hashes, private provenance hash, SDK identity and unchanged thresholds.
- `commands.jsonl`: append-only, fsynced commands with cwd, exit code, measured
  wall time, timeout flag and uniquely named complete log plus its SHA256. POSIX
  timeout kills the process group; Windows invokes `taskkill /T /F`. Timeout is an
  unresolved infrastructure outcome, never a kill. Windows termination behavior
  has not been executed here. Artifacts/attempt directories are never reused.
- `baseline` / `restored`: real full-suite TRX after clean/nonincremental builds;
  exact class/method/display identities and all-Passed counters must match, with
  nonzero executed cases and no skips. Binary manifests accompany both. Failed
  baseline/compile means no mutation testing or stale-DLL testing. Following a
  mutation-command failure, restoration still cleans, builds and tests if the
  initial baseline was valid. Failed restoration invalidates collection.
- `official.json` and `private-patched.json`: byte copies of actual Stryker reports;
  original reporter outputs/logs remain present. Official report hash is checked
  at completion. Mutant test results remain upstream Stryker JSON `killedBy` and
  trace/testhost events; no per-mutant TRX is invented. Baselines have actual TRX.
- `catalogue.json`: **every** official report entry, preserving duplicates, raw
  status/reason/id, exact replacement, source hash, UTF16/end-exclusive span,
  original span hash and report hash. Identity includes relative case-preserved
  path, source hash, location, mutator and replacement. BOM/newline byte mismatch,
  traversal, mapped `#line` positions or invalid UTF16 boundaries refuse parsing.
  CRLF, LF and CR coordinates are supported. No source is literally rewritten;
  preservation of every source byte proves outside-span preservation too.
- `ledger.jsonl`: initially one `NOT_RUN` row per official catalogue entry, written
  before the patched run; later appends supplemental observations. Original
  Ignored statuses are retained. Missing/ambiguous duplicate identities remain
  unresolved, never deduplicated. CompileError, Ignored and Timeout from the
  patched run are unresolved, receive no kill credit and cannot reduce the
  **fixed original applicable denominator**. A Killed observation requires
  upstream `killedBy`; it is supplemental evidence for lead/QA review, not a gate
  verdict. New patched-only entries remain in `patched-catalogue.json` for audit.
- `result.json`: `COLLECTED` or `UNRESOLVED`, original counts/fixed denominator,
  baseline identities/restoration, input preservation and all artifact hashes.
  Exit code 2 is pinned upstream `BreakThresholdViolated`: collection retains the
  threshold failure and its completed report without relaxing the threshold.
  `COLLECTED` means evidence collection completed, not that thresholds passed or
  every originally filtered entry was executable. It must never be interpreted
  as a product or independent QA pass. Aggregation remains lead-owned.

## Executed disposable pilot

macOS arm64, private existing SDK 10.0.404; no SDK installation. Actual artifacts:
`/tmp/pr6-replay-private-v2` (source build/provenance) and
`/tmp/pr6-replay-pilot-v7` (final runner fixture attempt), with standalone inputs
`/tmp/pr6-replay-fixture-v7`. The exact commands and timing are in commands.jsonl.
Both baseline and restoration executed **4 passed, 0 failed/skipped**. Official
Stryker generated **10 total: 6 Killed, 4 Ignored**. Private patched Stryker
executed **10 total: 10 Killed, 0 Ignored/CompileError/Timeout**. All four formerly
filtered blocks (void, nonvoid, out and iterator) have actual failing test results
and `killedBy` mappings. Testhost traces show `Assert.Equal`, `Assert.True` and
`Assert.Single` failures. Sources and dependency locks were preserved exactly.
This proves these fixtures use upstream instrumentation without literal-replay
repairs; it does not establish production or universal instrumentation parity.

Early attempts are retained as failures: alias-path private build failed
compilation; an unsupported file logging config was refused before mutation;
a fixture attempt detected a changed project-reference lock and was unresolved.
None received qualification credit. Synthetic Python tests exercise parser,
refusal, duplicate accounting, missing test attribution and timeout contracts;
they are explicitly **not runtime acceptance**.

No product mutation execution was attempted. Historical research enumerates 773
entries, including 162 filtered entries, but its embedded sources/head differ
from parent `09c273c`; those positions/outcomes must not qualify this branch.
A complete serial production run on each required OS/architecture is still
needed, including any unresolved entries and existing line/branch shortfalls.
Cost is one private build, two full Stryker runs and two clean full-suite
baselines per environment. Fixture seconds do not estimate product cost.
