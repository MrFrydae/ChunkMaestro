# Specification verification and evidence contract

Issue: [#1](https://github.com/MrFrydae/ChunkMaestro/issues/1). Input baseline: signed source-only `ab631d4`. Product implementation status remains **not-implemented for all Q001–Q205**, with no product evidence. This document is an implementer's verification report, not independent QA approval. The lead owns review and merge; no repository protections were changed.

## Reproduce offline

From repository root, using Python 3.9+ standard library only:

```sh
python3 tooling/specification/check.py
python3 -m unittest discover -s tooling/specification -p 'test_*.py' -v
```

After intentionally editing mapping/acceptance fields in [requirements-data.json](../product/requirements-data.json), run `python3 tooling/specification/check.py --render`, inspect the Markdown diff and rerun both commands. The [checker](../../tooling/specification/check.py) uses source/answer SHA-256 digests to prevent accidental source/matrix drift. Never regenerate input hashes to conceal an unreviewed source change. The [matrix header](../../tooling/specification/matrix-header.txt) and JSON are the renderer inputs; checked-in Markdown must exactly match output.

The checker requires the ordered exact set of 205 source IDs and matrix IDs; one nonempty answer each; matching question, line and answer digests; component/checkpoint/scope/unique acceptance ID; bootstrap unimplemented status with empty evidence; deterministic rendered rows; twelve decision records; thirteen proposed boundary ADRs with alternatives, consequences, failure/recovery and tests; and existing relative Markdown targets/fragments across owned docs. It does not fetch external links, run the app, interpret every acceptance clause, prove OS privilege enforcement or verify upstream version currency. Source input links are not rewritten: the original questionnaire attachment link in the immutable source refers to an unavailable attachment and is outside the owned-document link sweep. No new document depends on that attachment. Existing upstream reference links are provenance rather than implementation proof.

Negative tests mutate temporary copies, not source/workspace: missing/duplicate row, source tamper, incorrect source line, acceptance omission, false product evidence, invalid scope, stale rendering, broken local file/fragment, missing decision and missing ADR section must each fail. Source/matrix validation is automated; semantic completeness still requires independent functional and security review against all 205 answers.

## Execution record

Environment for this baseline: Darwin arm64, Python 3.9.6. Executed `python3 tooling/specification/check.py --render`: PASS for 205 source answers/rows/cases, source and answer digests, bootstrap status/evidence, local links, twelve decisions and thirteen ADRs. Executed the unittest command above: PASS, 13 tests, including deterministic rendering and twelve rejection scenarios. Final read-only validation is recorded in the PR body and implementation handoff. These results verify specification tooling only. No .NET/Gradle/native/VM/provider/installer/security-enforcement tests exist at source bootstrap or were executed by this docs task. No coverage/mutation percentage or production behavior is claimed. Review verdicts and required checks are pending lead-owned independent review.

Later explicit owner policy requires explicit C# types everywhere, including tests and checked-in generated code; see the [policy overlay](../architecture/README.md#later-owner-policy-explicit-c-types). Enforcement/AGENTS.md and source conversions remain lead-owned outside this PR; no C# compliance result is claimed.

## Product evidence needed by later slices

Each row starts with no evidence. Future implementation must deliberately evolve the bootstrap-only checker schema/status rules in a reviewed PR before recording implemented status. Required evidence object should include commit, source IDs and acceptance subcases, exact test command, fixture/version/OS, sanitized output or durable artifact/check URL, pass/fail and limitations, failure/recovery observations, independent review disposition and merge reference. A plan, source answer, screenshot/mock, signed commit or specification checker pass alone is never product implementation evidence.

Expand each `AC-Qnnn` clause into positive, denial and failure/recovery tests where applicable. All clauses must pass; unsupported/unavailable scenarios are tested outcomes, not skipped green assertions. Use xUnit/Avalonia headless for .NET contracts/viewmodels and real Windows/macOS for UI/keychain/lifecycle/packages; JUnit plus pinned real game fixtures for bridges; disposable Debian 13 with actual systemd and distinct principals for helper/enrollment/schedules/recovery; documented provider fixtures followed by separately labeled live evidence. Never use the owner's worlds for destructive failure injection.

Cross-cutting mandatory cases: wrong/replayed enrollment token and trust; wrong service/caller/path/JVM input; symlink/TOCTOU/archive escape; dropped ACK before/after effects; restart crash recovery and real five-minute warnings; failed save pauses without force-kill; desktop-off approved job vs host-missed occurrence; DST/overlap/revision locks; changed plan/remote drift/full disk/partial fleet failure; missing NAS/incomplete transfer/lost key/corrupt archive/selective restore; WAL backup/interrupted migration/newer schema; absent bridge/addon/old version/Folia wrong-thread/edition collision; unsupported metric/control truth; native accessibility/themes/tray/sleep/installer updates; extension OS access/egress denial and provider VM/game privilege separation. Each boundary's ADR defines targeted cases and recovery expectations.

For safety-critical modules publish ≥90% line, ≥85% branch and ≥80% mutation where applicable with Stryker.NET and meaningful failure assertions. Native/VM/provider evidence is separately labeled from contract fakes; absent runner or credentials is pending, never pass. Independent QA verdicts and GitHub review identities are distinct under [OD-07](../product/open-decisions.md#od-07-independent-qa-and-repository-enforcement). This specification PR needs functional/security review; implementation high-risk slices need all three roles. Stable release is additionally blocked on license approval, signing and complete product evidence.

Recovery for this PR: lead can apply a signed revert of owned documentation/specification tooling; no schema, runtime or host changes need reversal. Unknowns are limited explicitly in the [decision register](../product/open-decisions.md) and proposed [ADRs](../architecture/README.md), not resolved by checker success.
