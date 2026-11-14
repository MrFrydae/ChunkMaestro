# QA evidence contract

Use the current `tooling/build/qa_evidence.py` and `qa_ci.py` as the executable
contract. Keep the original reviewer report separate from the lead aggregate.

Each original report records:

- Exact 40-character head and base; role: `functional`, `security-reliability` or
  `ux-accessibility-performance`; unique reviewer/session identity and verdict.
- Commands, working scope, observed pass/fail/skipped counts and log/artifact paths.
- OS/architecture, selected SDK/runtime and relevant tools/versions.
- Evidence supporting the conclusion, concrete findings and their disposition.
- Limitations, assumptions, unexecuted cases and applicable acceptance blockers.

The aggregate names implementers and includes the required independent reviews.
Each review needs `reviewer`, `role`, `verdict`, `commands`, `environment`,
`evidence`, `limitations` and `findings`; keep per-review head/base as well.
Only an empty findings list is appropriate when no findings exist. A known issue
does not disappear because it was omitted from the aggregate.

The lead attests actual delegate provenance, exact-head identity and independence.
Do not generate or relabel a review to satisfy a missing role. Do not aggregate
old PASSs onto a new head or turn a FAIL into PASS after an unreviewed fix.
Historical reports may be published as historical; they cannot approve current
code. A new original report is required for an independently rechecked correction.

Inspect the downloaded CI verdict JSON as well as the check result. Reports from
one GitHub identity establish agent-role QA, not additional human approvals.
