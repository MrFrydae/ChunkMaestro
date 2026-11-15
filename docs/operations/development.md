# Development baseline

Implements the engineering baseline for Q145, Q154–155 and Q177–194, issue #4. This branch owns build/configuration and contributor tooling. It does not implement or certify product behavior.

## Prerequisites and SDK

Use native Windows/macOS development and a separate disposable Debian 13 VM for systemd tests. No Docker or browser app is needed. Install Python 3.9+ and **.NET SDK 10.0.404**. `global.json` selects that exact SDK, disables roll-forward and excludes previews. A system .NET 11 SDK cannot satisfy it. SDK selection and project target framework are separate controls ([Microsoft documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)); owned managed projects target `net10.0` (OS-specific net10.0 TFMs permitted).

This implementation installed a private official macOS arm64 SDK at `/Users/derek/.local/share/chunkmaestro/dotnet`. For this machine:

```sh
export PATH="/Users/derek/.local/share/chunkmaestro/dotnet:$PATH"
export DOTNET_ROOT="/Users/derek/.local/share/chunkmaestro/dotnet"
dotnet --info
```

Alternatively set `CHUNKMAESTRO_DOTNET` to the absolute `dotnet` executable for the root Python commands. This does not change the SDK pin. On Windows use `python` in place of `python3` below. No global install or permissions change is required.

Install provenance: fresh official [10.0 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json) identified SDK 10.0.404 / runtime 10.0.15. The downloaded [osx-arm64 archive](https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.404/dotnet-sdk-10.0.404-osx-arm64.tar.gz) was independently SHA-512 hashed and compared with that metadata before extraction:

```text
1c9a13f625d0c08f08ef5a53597bababe3d2473527e98db2199210940c69dd5fc599a39a4d7c30ebf297ca2334a81b2f64b9bf05ff99585de9af4a5d85215e96
```

Other platforms must select their own official RID/archive and matching metadata hash. Do not reuse this arm64 hash for another archive. Review security patches and update the SDK pin intentionally.

## Root commands

Run from the repository root:

```sh
python3 tooling/build/build.py sync
python3 tooling/build/build.py restore
python3 tooling/build/build.py build
python3 tooling/build/build.py format
python3 tooling/build/build.py test
python3 tooling/build/build.py verify
python3 tooling/build/build.py run
# Or explicitly select an existing native executable:
python3 tooling/build/build.py run --project src/ChunkMaestro.Agent/ChunkMaestro.Agent.csproj
python3 -m unittest discover -s tooling/build -p 'test_*.py' -v
```

`sync` writes the sorted `.slnx` from `src/**/*.csproj`, `tests/**/*.csproj`, and `tooling/build/**/*.csproj`, excluding `bin`/`obj`. Commit the regenerated solution when integrating another slice. All other commands reject stale solution membership. The lead integrates projects from isolated workers; this branch does not write their projects.

`restore` requires every project's committed lockfile and uses `--locked-mode`. `build` builds Release with no restore; run restore first. `format` verifies formatting without modifying files or restoring. `test` selects evaluated `IsTestProject=true` projects, runs Release with no build/restore, requires fresh TRX output and rejects zero executed tests. Build first. A skipped-only suite is rejected. It does not certify how meaningful a nonempty suite is.

`verify` runs locked restore, Release build, format and discovered tests. During the source bootstrap it explicitly prints **NOT RUN** for absent product test projects; it still validates the dependency/build probe. Explicit `test` exits nonzero when no test projects exist. `run` defaults to the desktop project and fails honestly until that native executable exists. It never starts a mock UI. The probe under `tooling/build/Validation` compiles real dependency types and asserts inherited build policy; it is not a product or test suite and is not packaged.

## Dependency integration

NuGet sources are cleared and mapped exclusively to HTTPS nuget.org. No private feeds/credentials are needed. `Directory.Packages.props` centralizes versions and disables project `VersionOverride`. [NuGet locked restore](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies) checks the committed dependency graph and package content hashes. Commit `packages.lock.json` beside **every** project, including libraries. To add/update packages intentionally:

```sh
python3 tooling/build/build.py sync
python3 tooling/build/build.py update-locks
python3 tooling/build/build.py verify
```

`update-locks` is local maintenance only; CI never regenerates locks. Review lock diffs, advisories, upstream provenance and licenses before committing. Existing locks must resolve on all three CI platforms. Projects with runtime identifiers or OS-conditional references must include the required target graphs in their committed locks and validate them across the matrix; never bypass locked restore for a platform.

Verified NuGet defaults (exact versions, restored in the build probe): Avalonia/Desktop/Fluent/Headless.XUnit **12.1.6**, EF Core SQLite/Design **10.0.15**, xUnit v3 **3.2.2**, VS adapter **4.0.1**, Microsoft.NET.Test.Sdk **18.12.0**. Avalonia Headless.XUnit 12.1.6 references xunit.v3.extensibility.core 3.2.2; use xUnit v3 and `OutputType=Exe` for new compatible suites. xUnit v2 **2.9.3** is available centrally for isolated non-Avalonia suites that require it; do not combine both frameworks in one test project. Keep runner/SDK/EF Design references `PrivateAssets=all`. The baseline does not force UI packages into product libraries: only explicit references consume central versions.

Nullable references, C# 14, deterministic builds, recommended .NET 10 analyzers, code-style enforcement and warnings-as-errors apply to owned projects. CI enables deterministic source paths. Use documented narrow suppressions if an analyzer is inapplicable; do not globally disable warnings. SDK includes `dotnet format`. The local tool manifest now pins Stryker.NET for measured storage safety qualification; it does not replace normal verification. Java bridge toolchain/Gradle ownership belongs to its later slice.

## CI and independent QA evidence

`CI / managed (ubuntu-24.04)`, `CI / managed (windows-2025)`, and `CI / managed (macos-15)` build the actual solution available at each head, check formatting, and run tests when present. The lead must inspect the emitted GitHub check context names before making them required. They do not yet prove native desktop packaging or Linux agent behavior when those projects are absent. No installer, release or deployment is configured. Managed test results, redacted secret-scan results and independent QA verdicts are uploaded as CI artifacts.

Actions pins were verified with `git ls-remote` against upstream tags: checkout v5.0.0 `08c6903cd8c0fde910a37f88322edcfb5dd907a8`, setup-dotnet v5.0.0 `d4c94342e560b34958eacfc5d055d21461ed1c5d`, setup-python v6.0.0 `e797f83bcb11b83ae66e0230d6156d7c80228e7c`. Jobs use disposable hosted runners, read-only contents permission, no persisted checkout token and no production/signing secrets. They use `pull_request`, never `pull_request_target`. Update pins through reviewed PRs. Required status-check policy remains lead-owned.

`python3 tooling/build/qa_evidence.py FILE HEAD_SHA [--high-risk]` checks a reviewer-supplied JSON report. Contract: top-level `head` (exact 40-character commit SHA), `implementers` (nonempty list of agent identities), and `reviews` (list). Each review contains `role`, `reviewer`, `verdict`, `commands`, `environment`, `evidence`, `limitations`, `findings`; the last five fields must contain actual evidence or an explicit description of none/not-run. Roles are `functional`, `security-reliability`, `ux-accessibility-performance`. Functional and security are required; high risk adds UX. Reviewers must be unique, differ from implementers, and report PASS before this contract accepts the report.

This checker does not create verdicts, authenticate identities, retrieve reviews, or enforce GitHub approvals. Reports must be supplied by independently assigned reviewers, attached to PR feedback or lead-managed artifacts, and verified by the lead for provenance and exact head. It is deliberately not a green “QA approval” job in author-controlled CI. No approving evidence is included in this branch. The CI publication and acceptance mechanism is described below.

## Evidence, gaps and rollback

Local implementation validation: macOS 27.2 arm64, SDK 10.0.404, runtime 10.0.15. Locked restore, Release build (zero warnings/errors) and format verification passed against the build probe. Python tooling regression checks exercise failure cases. There were no product test projects, so **no .NET product tests ran**. Real Windows/macOS native interaction, installers, Debian/systemd, coverage and mutation tests remain unexecuted. Matrix results must be read on the PR; local macOS success does not assert Windows/Linux success.

Dependency auditing runs during restore for all packages and warnings fail builds. This baseline does not claim completed SBOM/license approval, dedicated SAST, exhaustive secret detection, mutation/coverage enforcement or native CI acceptance. Those need concrete implementation and evidence as product slices arrive. Never put secrets in files/logs/PRs. Public stable release is blocked pending license, signing and full QA decisions.

Rollback is a normal reviewed revert of these build/configuration files. It changes no database, host, permissions or production deployment. Preserve signed commits and do not force-push protected main. Lockfile and solution integration must be rerun after any revert that changes project membership or dependency versions.

## Explicit C# types — owner decision

`var` is prohibited in production, test and checked-in generated C#. `.editorconfig` configures all three var preferences false with IDE0008 as an error; the existing build style enforcement makes violations fail builds. `AGENTS.md` carries the policy into future agent tasks. A temporary built-in-type `var` fixture was compiled and failed with IDE0008, then removed.

Functional QA found test discovery evaluated Debug while execution used Release. Discovery now explicitly evaluates Release. The regression asserts the matching MSBuild configuration; independent re-review remains required. The QA publication and secret-scan review findings are addressed by the mechanisms below; independent exact-head re-review remains required.

## Blocking independent QA publication

`CI / qa-evidence` reads PR comments via a read-only GitHub token. It accepts only the latest `<!-- chunkmaestro-qa-v1 -->` attestation posted by the verified lead account (GitHub numeric id 38875853). That comment must contain exactly one JSON code block matching the `qa_evidence.py` aggregate contract and the PR's current head SHA. The lead must first inspect the independently assigned delegates' actual reports and provenance, then attach their unaltered verdicts and sanitized evidence. Never convert a FAIL to PASS or use author-supplied fake verdicts. This is **lead-attested agent evidence**, not independently authenticated human approvals. One GitHub identity cannot supply multiple human approving reviews. The lead remains responsible for provenance, risk classification, acceptance and the final merge.

The gate requires functional and security/reliability roles; high-risk paths and the `high-risk` label require UX/accessibility/performance too. File paths automatically escalate enrollment, secrets, schedules, privileged helper, agent, transport, persistence, deployment, backups and packaging changes. A label cannot downgrade that classification. Unrecognized high-risk changes must be escalated by the lead with the label before review. The lead must maintain the risk patterns as modules evolve. Missing/malformed, stale, non-PASS and self-review reports fail closed. CI uploads `.artifacts/qa/verdict.json` even for blocked results and includes the verdict in its check summary. The workflow does not execute comment text or have permission to approve, merge, modify files or publish statuses. Future branch changes can alter workflow code: the lead must inspect that code and the exact required checks before merge; this is not a sandbox against a malicious repository admin.

Reports added after CI runs require a CI rerun on the same head (`gh run rerun RUN_ID --failed`), not a source commit that would invalidate the reviewed SHA. No `issue_comment` privileged handler or `pull_request_target` execution is used. Once the actual job contexts exist, the lead must require `qa-evidence` and `secret-scan` along with the three managed checks. These checks must all pass at the current head before merge. There is no bootstrap self-approval exception for this PR.

## Secret scanning

Install Gitleaks **8.30.1**, then run `python3 tooling/build/build.py secret-scan`. It fails if the executable/version is missing, either scan fails, or a tracked symlink would escape the scan boundary. It scans local Git history (`--all`) and tracked working files only. Inline allow-comments do not suppress findings. Repository `.gitleaksignore` files are rejected before the scanner runs, including valid fingerprint exclusions and malformed entries; the checkout is not modified. Raw scanner stdout/stderr are captured and withheld; only the wrapper status and redacted JSON reports are published. Default Gitleaks rules are explicitly loaded, and results/logs are redacted. No user-local ignored credential files, caches or generated report directories are copied or uploaded. This scan detects common secret patterns; it cannot prove that every secret or sensitive noncredential datum is absent. Use synthetic nonfunctional credentials for negative fixtures, never real tokens.

CI downloads the Linux x64 archive from the official Gitleaks v8.30.1 release and verifies SHA256 `551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb` before extraction. This digest was matched to the official release checksum manifest. The upload-artifact v4.6.2 pin `ea165f8d65b6e75b540449e92b4886f43607fa02` was matched to its official upstream tag. Installation or scan failure blocks the check. `.artifacts/security` contains the redacted reports. Never post an actual secret as review evidence. SBOM, license approval, dedicated SAST and product safety coverage/mutation gates remain separate work and are not claimed complete.

Validation after review fixes (local macOS arm64, SDK 10.0.404): 14 build/QA contract regression tests passed; real Release-only deliberately failing xUnit suite was executed and rejected by `verify` (also with `CI=true`); real synthetic secret fixtures were rejected by both history and tracked-file scans and redacted in JSON; clean repository scan passed; locked restore/Release build/format passed. Live PR QA fetch with no exact-head attestation correctly failed and wrote a machine-readable blocked result. These results are implementation validation, not independent approval. Fresh independent review of the final SHA and hosted evidence are still required.

The first hosted Windows regression run exposed CRLF translation in temporary C# fixture generation. The fixture now writes UTF-8 bytes with LF explicitly, preserving the repository style policy on every host. The failing run's uploaded log established that the suite compiled cleanly but `dotnet format` stopped before test execution; no test pass was claimed. Hosted Windows confirmation of the corrected fixture remains required.

## Multiline C# braces — later owner decision

Every C# brace-delimited construct must be multiline, including empty blocks, lambdas, property accessor lists and initializers. Use Allman braces. String interpolation delimiters and braces inside comments/strings are excluded because they are not code blocks. `.editorconfig` disables single-line block/statement preservation and configures brace/member line breaks. `StyleCheck` uses the Roslyn syntax parser from the pinned SDK, adds no NuGet dependency, and enforces explicit types, Allman/multiline braces and one statement per line in owned C#, including checked-in generated source. Generated headers and `.g.cs` filenames do not bypass this check. Formatter verification explicitly includes generated source too. `build.py build` and `verify` run it after compilation; CI also exercises real negative fixtures.

Expand new/generated brace constructs into multiline form, run `dotnet format whitespace ChunkMaestro.slnx --no-restore --include-generated`, then rerun repository verification. The syntax checker is read-only and its fixtures verify that it leaves source untouched while recognizing interpolation/comments. The initial automatic rewrite needed manual cleanup around initializer punctuation, so no source-rewriting feature is shipped. Normal `dotnet format` alone does not enforce every empty block or auto-property case; use the repository checks.

Fresh QA at `86b9b6d` found generated-source style bypasses and Gitleaks implicit-ignore/diagnostic leaks; that head is not approved. Regression fixtures now run full `verify` on generated `var`, declaration-line braces, multiple statements, empty blocks and compact properties (all rejected), then valid generated interpolation/comment source (accepted without rewriting). The scanner fixture covers a deleted historical token plus a valid fingerprint exclusion, an invalid secret-shaped ignore entry, inline allowance and stdout/stderr/JSON redaction. Ignore files block before scanner invocation; raw scanner diagnostics never flow into CI logs. The reports remain subject to independent review on the resulting exact head.

Final local regression run for these fixes: 15 Python build/QA tests passed; full generated-source negative/positive verification fixtures passed with `CI=true`; the real Release-only failure was still executed/rejected; scanner fingerprint/invalid-ignore/inline-allowance/redaction regressions passed; normal root locked restore/Release build/format passed. Disposable .NET fixture processes disable reusable MSBuild nodes after a transient worker exit was observed during concurrent local fixture runs. Owned generated files are explicitly included in formatting verification; SDK-generated `bin`/`obj` intermediates are excluded from that owned-source list. This is implementation evidence, not independent approval or product/native acceptance.
