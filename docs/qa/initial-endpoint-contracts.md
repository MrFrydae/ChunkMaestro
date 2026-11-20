# InitialFleet endpoint contracts — issue 3 / PR 6

Implementation test work only. Requirements Q149, Q153, Q163–164 and Q189–195;
90% line / 85% branch / 80% mutation thresholds remain unchanged. No production,
migration, model, lock, package, configuration or existing test files changed.

The new InitialEndpointConstraintTests uses supported EF IMigrator targeting
20261114034833_InitialFleet in native-prepared, owner-scoped synthetic temporary
StorageLocation fixtures. Real SQLite connections disable pooling and enable and
check foreign keys. Nine cases reject null in each endpoint field, absent host or
instance references, duplicate identity with a different valid host (isolating the
primary key), and duplicate host/edition/port with different instance/address
(isolating port reservation). Java and Bedrock may share a numeric port.

Each case compares every seeded row after the actual rejected write, then opens
and reads through public LocalFleetStore upgrade. It recovers the actual compatible
pre-upgrade backup into a separately prepared fixture, repeats the InitialFleet
write/refusal and row preservation check, then upgrades/reads that copy. The original
backup bytes stay unchanged. No fabricated predecessor, downgrade or later port /
edition CHECK requirement is used. No DDL/message/source/reflection assertions.

Environment: macOS arm64; private pinned SDK 10.0.404 at
/Users/derek/.local/share/chunkmaestro/dotnet/dotnet. Parent
3bc99c26edc3448b3ffdc474ea953bedc92dddf9; signed test commit
ed72431ce32ee7cc791260373fd552de02ddd2d4. SSH signature cryptographically verified
with ssh-keygen check-novalidate; the machine has no configured Git allowed-signers
trust file, so identity trust verification was not asserted or configured.

Every .NET invocation uses replay_ci.child_environment's explicit noncredential
allowlist with absolute SDK/DOTNET_ROOT/PATH, IPv6 disabled, reusable MSBuild nodes
disabled and telemetry opted out. Complete commands, exit codes, elapsed seconds,
logs and TRX remain at /private/tmp/pr6-initial-endpoint-evidence.

Validation after final test formatting:

- Initial no-restore invocation failed NETSDK1004 before compilation because this
  checkout had no assets. Official locked solution restore then passed.
- Filtered suite: 9 passed, 0 failed, 0 skipped, including both prefix/backup paths.
- python3 tooling/build/build.py verify: exit 0; locked restore, Release build
  (0 warnings/errors), syntax/style and generated-inclusive format verification
  passed; complete suite 167 passed, 0 failed, 0 skipped.

Commands are recorded in affected.json (failed first invocation), restore.json,
affected-restored.json, format.json and verify.json. The external run.py wrapper
applies the allowlist and a finite 1000-second deadline; it contains no credentials.

No Windows or Intel execution, full current-head module qualification, line/branch
measurement or independent QA is claimed. No pushes, PR changes, monitoring, live
hosts/worlds/user data or privileged/settings changes were performed. Any remaining
mutants retain their original identities/statuses without timeout kill credit,
equivalence waiver, score exclusion or altered source.

## Bounded diagnostic and limits

One private targeted command ran against signed test head ed72431, using the
external diagnostic.py and diagnostic-config.json retained in the evidence directory.
Selection was `**/Migrations/*_InitialFleet.cs`; discovery used the existing
MutationTests.csproj and all existing tests plus the nine owned cases. This is
diagnostic selection only, never a gate exclusion. The actual command, cwd,
1000-second deadline, elapsed 133.83 seconds and exit 0 are in
`diagnostic/commands.jsonl`. No tool rebuild, full-module rerun or identical retry
was performed. Strict validation failed after the successful tool invocation;
no mutation command was repeated to work around that failure.

Before execution, reopened local compiler files (15), pinned archive, sole factory
patch, full source inventory (869), dependencies/outputs (370), SDK executable,
builder and build log matched supplied provenance. Advisory warnings remain
retained in the original supplied private build; none were suppressed. Reporter
HTML was never opened. Baseline and restoration each passed 167 tests, 0 failed,
0 skipped, with the same exact identities and unchanged tracked inputs.

The strict existing complete_report validator refused source binding because the
unselected EnforceEndpointInvariants file has embedded text `File ignored by mutate
filter`, zero reported mutants, and different source bytes. The command log says
800 created / 681 filtered / 26 CompileError / 93 tested, whereas the final raw
report contains 790 rows (671 Ignored / 26 CompileError / 87 Killed / 6 Survived).
The ten parent identities from that unselected migration are absent. The raw report
is preserved unchanged; `diagnostic/result.json` remains **UNRESOLVED**. No parser,
config, production source or report was repaired and no gate credit is granted.

A separate diagnostic reader byte-matched all mutant-bearing sources and the owned
test source. Its 790 identities match the parent report exactly; the missing ten
parent rows remain separately recorded in `missing-parent-rows.json` with original
IDs, source hashes, spans and statuses. `source-matched-observations.json` preserves
all present rows, including filtered other-source rows. Historical comparisons keep
both original historical and parent rows in `historical-comparison.json`; no old
status was overwritten. This analysis is explicitly separate from the refused
strict collection result.

Of the fourteen historical InitialFleet survivors, eight have actual source-matched
Killed observations attributed through upstream killedBy to the new tests:
473, 475, 477, 479, 481 (required fields), 485/489 (foreign keys), 493 (port reservation).
Six remain observed Survived: 472/474/476/478/480 (column type-string mutations) and
507 (downgrade refusal guidance string). No equivalence waiver is asserted; no
implementation-message or DDL assertions were added to kill them. These observations
receive **no qualification credit** because strict complete-report binding failed.
There is no timeout kill credit or final-score claim. No genuine product defect
was observed within the tested contracts.

Artifacts: `diagnostic/private/reports/mutation-report.json` (original full upstream
output), `diagnostic/result.json` (UNRESOLVED), `diagnostic/observations-summary.json`,
`diagnostic/own-test-attribution.json`, both full-suite TRX directories and command
logs. The raw report SHA256 is
`95795a09c7a74c9377c7445edee3709ba81663ba6d681db9994fb9e99a3dd7a8`;
private provenance SHA256 is
`b01f5c09603b20807b567d9d3de6239f93fd1a8f00b69f0acdf80825ea5a6cdf`;
SDK executable SHA256 is
`b27f0111c0759100f50577f4f8169ee004d3800d35fb8293a0cc7ac408bef628`.
`evidence-sha256.json` in the external evidence directory inventories retained files.

The final documentation commit adds only this evidence narrative; all executed
C# evidence remains bound to the unchanged test/source bytes at ed72431. Historical
Windows observations are preserved only as history, never combined into current
qualification. Live PR checks and reviews were not queried, changed or watched.
