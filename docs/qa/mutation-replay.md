# Private Stryker block execution experiment (issue 3, Q190)

Q190 and directive §13.1 remain **90% line / 85% branch / 80% mutation**.
This implementation collects supplemental evidence in CI. Collection never passes
qualification, changes mutation exclusions or dependency versions, or grants QA credit.
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
The production run path has since been exercised on the frozen head recorded below.

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

The initial pilot did not attempt product mutation execution. Historical research enumerates 773
entries, including 162 filtered entries, but its embedded sources/head differ
from parent `09c273c`; those positions/outcomes must not qualify this branch.
A complete current-head production run on each required OS/architecture is still
needed, including any unresolved entries and existing line/branch shortfalls.
Cost is one private build, two full Stryker runs and two clean full-suite
baselines per environment. Fixture seconds do not estimate product cost.

Actual macOS arm64 product collection at frozen head
`4f263fcc4ff607b329f13c53aa85ff387f1a31bb` completed in 26.2 minutes;
coverage, baseline and restoration each passed 101 tests without skips.
Official and private runs retained their threshold-failure exits. The fixed
original denominator was 765 (791 entries minus 26 original compile errors).
There were 407 attributed kills after supplementation: **53.20%**, below 80%.
Persistence coverage was **84.29% line / 61.03% branch**, also below its gates.
These measurements are historical implementation evidence, not qualification of
a later head, Windows execution or an independent QA verdict. Original reports,
commands and limitations remain at `/tmp/pr6-product-mutation-4f263fc-delivery.json`
and `/private/tmp/pr6-product-mutation-4f263fc-7vfe08mi`.

## Lead integration prerequisites

The private source builder requires Python 3.12+ for tar's data-filter extraction;
normal repository tooling retains its existing Python baseline. The production
runner can bind a separate in-workspace `--mutation-directory`. For Persistence,
use `tooling/quality/storage` with its MutationTests project, not the repository
root containing standalone resource fixtures. This changes discovery location,
not selected mutators or source exclusions. The recorded binding includes it.
The tracked persistence.json matches the existing qualification settings.

Example production invocation (long-running; serialize all native/build outputs):

```sh
python3 tooling/quality/storage/replay.py run \
  --root . --output /tmp/new-storage-replay \
  --provenance /tmp/new-private-build/provenance.json \
  --official /Users/derek/.nuget/packages/dotnet-stryker/5.0.0/tools/net10.0/any/Stryker.CLI.dll \
  --config tooling/quality/storage/replay/persistence.json \
  --test-project tooling/quality/storage/MutationTests.csproj \
  --mutation-directory tooling/quality/storage --timeout 7200
```

The worker's private-v2 build predates the final builder-hash field. Its fixture
proof remains historical implementation evidence, but current production work
must use a fresh private build with the integrated builder hash recorded. Upstream
warnings and dependency provenance require review; fixture success does not
silently approve their use or establish general product qualification.

Lead replay check: the first alternate-directory fixture attempt was UNRESOLVED
because its test-project paths still assumed the old directory. Correcting the
external fixture's explicit project paths produced /tmp/pr6-replay-lead-pilot-v2:
4/4 baseline and restoration, official 6 Killed / 4 Ignored, private 10 Killed with
killedBy attribution, all inputs preserved. Fresh private build provenance is
/tmp/pr6-replay-private-lead-v2/provenance.json. Its builder hash and the final
pilot runner hash match the integrated replay.py bytes. The failed attempt is
retained without credit. Production config now explicitly selects
MutationTests.csproj; ordinary qualification uses the same explicit selection.
These remain fixture/tool integration results, not product mutation execution.

## Trusted collection boundary

The focused advisory analysis permits bounded collection using this verified
build and trusted synthetic tests, with local JSON/HTML reports left unopened and
no cloud reporters, credentials, private feeds or live-host data. Advisory warnings
remain recorded. Actual dynamic assembly loads remain unmeasured; shipped library
identity alone is not a runtime-load claim. Review evidence is retained at
/tmp/pr6-private-tool-advisory-review.md and .json.

Production collectors must launch from an explicit noncredential environment
allowlist; replay.py's inherited environment is not itself a credential boundary.
MutationTests excludes both volume resources and standalone replay fixture sources
from default compilation, then links only the actual persistence suite. The four
fixture-shape tests are mechanism proof and must not inflate the product baseline.


## Hosted adapter and portable artifact contract (issue 3)

`replay_ci.py` runs serially after the existing official coverage and three-module
collection in each Windows/macOS measurement job. Both jobs explicitly check out
`pull_request.head.sha` (dispatch uses `github.sha`); the replay binding independently
records that actual clean Git head. There are no privileged event handlers, added
permissions, secrets, action-pin changes or automatic retries. Full-volume and
macOS native measurements still execute after collection failures and retain their
outputs, but cancellation stops subsequent work.

Private execution requires repository `MrFrydae/ChunkMaestro`, actor and event
sender numeric ID **38875853**, and either a same-repository PR authored by that
owner with the checked-out head SHA or an owner dispatch matching `GITHUB_SHA`.
Forks, other authors/actors, stale heads, unsupported events/platforms and dirty
trees fail **UNRESOLVED**, never a skipped proof. This is execution trust, not an
independent QA verdict. The workflow's ordinary read-only PR sandbox remains.

Before private build/restore/tool execution, the adapter replaces the inherited
environment with `ENV_KEYS`, an explicit list of machine paths, locale, Windows
runtime/architecture keys and the selected SDK location. It forces telemetry
opt-out, no reusable MSBuild nodes and IPv6 disablement. It excludes GitHub/auth,
cloud, SSH, private-feed variables, process startup hooks and arbitrary SDK/NuGet
options. Only allowlisted key **names** are recorded; credential values are never
inspected or printed. Hosted ephemeral HOME/profile directories must contain no
private feeds or credentials. This adapter does not authorize execution against a
credential-bearing workstation or live data. Local JSON/HTML remain unopened.

The existing builder downloads the hash-pinned upstream archive into a unique
job-owned system temporary directory, checks the original factory and sole patch,
builds from unchanged upstream locks and verifies the entire source delta.
The adapter additionally refuses any generated reporterJS other than SHA256
`751fb010242b0b44e32d84fe7fe0b9ff1da182823b94f59f5c52b001fcfc163b`.
Advisory warnings remain in the build log; the reviewed conditional-use rationale
is `/tmp/pr6-private-tool-advisory-review.md`. No advisory is suppressed or claimed
fixed. The official DLL comes from the ordinary restored tool package and version
in `.config/dotnet-tools.json`; no separately installed tool replaces it.

The uploaded **existing platform folder** now contains:

- Existing `manifest.json`, coverage, Domain/Application/Persistence official
  mutation reports, volume and native evidence, without relocation.
- `replay/`: byte-for-byte runner `result.json`, `binding.json`, commands, original
  official/private-patched output trees and report copies, both catalogues, ledger,
  baselines/restoration and every runner log.
- `replay-private/`: entire byte-preserved upstream build tree (including bin/obj),
  archive, sole patch, provenance and build logs. This preserves all referenced
  private build outputs; dependency advisories remain visible in those logs.
- `replay-collection.json`: adapter status, trusted context/head, SDK, OS/architecture,
  elapsed seconds, noncredential environment key names, original temporary root
  and SHA256 maps of **all** relocated files, including bin/obj. Failed partial
  builds/runs are copied too. Original temporary data is retained on failure.

The lead must validate adapter status **and** raw runner status/binding, preserved
inputs/restoration, exact current head/source/runner/config/SDK/private provenance,
all referenced hashes and logs, report completeness, ledger identity and the
existing official/native/volume evidence. Relocation never edits any raw JSON or
log. An absolute path below `original_temporary_root/replay` resolves portably as
`<platform>/replay/<suffix>`; similarly for `replay-private`. Absolute command/cwd,
checkout and compiler paths are execution provenance, not portable gate paths.
Validators must reject traversal/unknown roots and check `relocated` hashes before
using those mappings. Do not require original temporary paths to exist after
upload. Do not grant aggregation credit from this wrapper alone or relabel raw
Ignored rows.

The private builder's dependency/output inventory and the runner's baseline
binary inventories originally used the host's native relative separator. The
validator accepts Windows separators in those specifically identified inventories
by normalizing them in memory before safe relative-path validation. Absolute,
drive-qualified, UNC, traversal and colliding normalized names remain rejected.
Raw evidence bytes/hashes are not rewritten; uploaded artifact-member inventories
still require their original POSIX names. Tests cover both valid Windows paths
and the retained refusal boundaries.

`replay_gate.py`, called by the existing aggregate gate, verifies those relocated
inventories, the pinned archive's original contents and sole source delta, built
dependencies, SDK/runner/configuration/input bindings, real full-suite TRX and
restored assembly hashes. It reconstructs both source-bound catalogues and the
append-only ledger, requires identical unambiguous mutation identities, and binds
actual Killed observations to test definitions from the preserved test sources.
It reads artifacts without executing them. Compiler paths remain recorded host
provenance; the Linux aggregator does not claim to reopen another runner's SDK.
Collection trust comes from the owner-only workflow, not from hashes alone.

Only validated supplemental Killed outcomes join the ordinary Windows/macOS
union. Original compile-error eligibility is retained, while new compile errors,
timeouts, missing cases, stale inputs and unresolved identities receive no credit.
Supplemental reports must match the ordinary Persistence catalogue. Native C,
full-volume, populated upgrade/interruption and the unchanged 90/85/80 checks
still apply. Independent exact-head role verdicts remain a separate merge gate.

Official collection now distinguishes complete evidence from score acceptance.
Each fresh uniquely located report must contain terminal, source-bound
mutants; Pending/unknown, missing/ambiguous reports, source mismatch and
all nonzero codes except actual **2** are unresolved. Code 2 additionally requires
a computed official Stryker score below its unchanged 80% break threshold; code 0
must agree with that score. Upstream counts Killed plus Timeout as detected and
excludes Ignored/CompileError for this exit-code consistency check only; the
aggregate still grants kill credit only to Killed. An empty Application report
with its complete frozen interface-source inventory and exit 0 is measured N/A,
never inferred 100%. Other empty, all-ignored or unscored reports are unresolved.
Complete code-2 reports retain the
actual exit code and `collected-threshold-fail` record. `COLLECTED` alone returns
collection success; the unchanged 90/85/80 aggregate acceptance remains separate.

GitHub documents a [six-hour hosted-job execution limit](https://docs.github.com/en/actions/reference/limits).
The job is bounded at 350 minutes; official collection at 105 minutes (Windows
Persistence commands 5400 seconds, other official commands 2400 seconds), and
private replay at 210 minutes (Windows mutation commands 5400 seconds, macOS
mutation commands 3600 seconds; upstream build 600 seconds). These are finite collection
budgets, not measured completion promises. Commands record wall time and timeout;
exhaustion fails unresolved with available partial artifacts, never a hidden
retry or successful substitute. Remaining job time accommodates volume/native
work and uploads; an overall job cancellation can still prevent complete upload.

The command runner prints START/END records and a WAIT heartbeat every 30 seconds
with elapsed time and log byte count. A heartbeat proves the runner is waiting;
it does not claim that mutation execution is making useful progress. Failures and
timeouts also print a bounded diagnostic log tail. Full logs remain unchanged.
When output grows, a heartbeat includes its latest 512 bytes with terminal color
escapes removed from the displayed copy. Both ordinary and replay configurations
enable Stryker's Dots reporter (actual per-mutant outcomes), while ordinary runs
also retain debug/testhost logs via `--log-to-file`, as replay already does.
These diagnostics distinguish completed mutants from a silent testhost without
claiming that a runner heartbeat alone proves mutant completion. The Windows
run at `9cda0e3` passed all 133 runtime tests and full-volume recovery but its
Persistence mutation command hit the unchanged 2400-second deadline without a
final report. No qualification or mutation kill credit was granted to that run.
The next Windows diagnostic at `eba7f3c` retained actual Stryker test-session
events: distinct mutants continued completing through the command deadline.
It was not a single stalled testhost. Its incomplete run still receives no gate
credit. The matched official/private configurations now use four mutation workers
and the Windows budgets above; this changes execution capacity, not source,
mutators, source exclusions, tests, denominator, kill rules or score thresholds.
The larger finite budget is based on observed work, not an assumption of success.
Timeout termination has bounded taskkill/exit waits; an unconfirmed exit remains
an explicit unresolved timeout, never a mutation kill. Baseline commands have a
separate maximum of 600 seconds, including restoration.

Private replay requires successful ordinary collection (a complete threshold-fail
report is still collection success). A failed/timed-out official command no longer
launches another long replay. Volume/native/diagnostic upload and aggregation use
`!cancelled()` so ordinary failure preserves evidence while cancellation stops
work. Per-PR concurrency cancels superseded runs. These controls do not relax any
score, omit a required acceptance result or turn cancellation into qualification.

Local adapter validation on macOS arm64 used disposable parser/path/subprocess
fixtures, including an actual host temporary path with spaces, source mismatch,
threshold-code mismatch, environment stripping and byte-preserving failure
artifacts. Windows path execution and complete hosted private/product mutation
runs have **not** been performed by this implementation task. Historical macOS
mutation outputs are diagnostic only. Full current-head Windows/macOS execution,
aggregate qualification and independent product QA remain required.
## Windows private replay runtime correction

The Windows collection at `083344305855fdb9e2e08038748d00f20d864a3c`
[failed at its command deadline](https://github.com/MrFrydae/ChunkMaestro/actions/runs/43715235691/job/132815295510).
Its initial collection completed, and its replay official pass finished in
4,308.8 seconds. The private pass was terminated at 5,401.8 seconds by its
5,400-second bound. The retained debug log contains 534 completed mutant sessions
and further test activity through the termination time. This establishes active
work at the cutoff, not a deadlock or a completed mutation score. Baseline and
restoration each passed all 185 Windows tests; tracked inputs were preserved.

The immutable Windows artifact is ID `14171990695`, SHA-256
`d2d0f975baa2530d60bac28be54862b941e95413cccfb7a65d5383b2056a4bcf`.
The failed aggregate correctly refused the incomplete replay. Its partial
mutation metrics do not establish combined-platform qualification.

The private Windows command now has a bounded 7,200-second allowance. Its
official replay remains bounded to 5,400 seconds; macOS command limits and the
350-minute whole-job limit remain unchanged. The containing replay step allows
225 minutes for both commands, baseline/restoration and provenance preparation.
This adjustment follows observed ongoing execution; no safety threshold, mutant
denominator, timeout kill-credit rule or acceptance check changes.

Heartbeats also show a bounded, ANSI-stripped tail from Stryker's main debug log
when it changes. This makes mutant activity visible when console output is
buffered. Raw logs are preserved byte for byte, and progress sampling grants no
qualification credit. The regression exercises progress output, bounded output
size, original-byte preservation and unchanged command deadlines.

Local validation: 35 storage-tooling regressions and 15 build-tooling regressions
passed. Fresh hosted collection and independent QA remain required.
