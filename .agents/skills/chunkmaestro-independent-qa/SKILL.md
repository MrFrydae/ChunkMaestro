---
name: "chunkmaestro-independent-qa"
description: "Perform or aggregate independent exact-head functional, security and UX/performance QA for ChunkMaestro pull requests."
---

# Produce an independent verdict

Read [AGENTS.md](../../../AGENTS.md), the affected source answers and sections
2.3/13/17 of the [directive](../../../docs/product/orchestration-directive.md).
Resolve live base/head, complete diff, intended behavior, original findings and
available evidence. Review the exact requested head in an isolated checkout.

Regular PRs require functional and security/reliability verdicts. High-risk changes
require all three roles, including UX/accessibility/performance. Determine risk
from the directive and `tooling/build/qa_ci.py`; labels cannot downgrade actual
risk. Self-review is useful preparation and never an independent verdict. A
reviewer must not approve code they implemented, including a later fix they made.

The assigned role reviews the bounded change, runs meaningful public-boundary and
failure/recovery cases, and reports material defects with path/line, trigger,
actual/expected result and reproducible evidence. Reuse unchanged baseline checks;
do not repeat entire unrelated suites as a ritual. Native, platform, safety and
behavioral obligations are not waived by a green unit suite or bounded PR.
Apply the [owner testing policy](../../../docs/product/requirements-source.md#owner-testing-policy--2026-11-23):
retain coverage reporting, use coverage/mutation scores diagnostically, and base
acceptance on meaningful behavior and security evidence. Document environmental
limits and practical alternative verification; a skipped test is not a pass.

Keep review checkouts read-only unless implementation is separately assigned.
Use disposable external fixtures without production data. Do not mutate live
hosts, repo settings or source during a review. Feedback publication follows the
task's existing authorization. If a fix is required, return the defect to the
implementation owner; every new delegated review/recheck is a new task.

Create the original report described in [evidence fields](references/evidence.md).
PASS means no unresolved material finding or applicable acceptance blocker in the
declared scope. FAIL reports retain their findings; unexecuted cases stay explicit.
Keep original reports immutable and distinguish later verdicts by exact SHA.

For aggregation, the lead reads completed reviewer threads/reports, verifies
separate sessions/roles and implementation independence, and checks every report's
head against the live PR. Preserve original reviewer labels with verified session
provenance. Validate with `tooling/build/qa_evidence.py`, adding `--high-risk` when
applicable. The validator does not authenticate reviewers or replace this check.

Publish authorized human feedback and the lead aggregate through the existing
`qa_ci.py` marker/account contract. Confirm the actual CI artifact contains the
current head and verdicts; same-named artifacts from older attempts can differ.
Agent sessions sharing a GitHub account are not distinct human approving reviewers.
A completed automated review or passing report-format check is not full merge QA.
