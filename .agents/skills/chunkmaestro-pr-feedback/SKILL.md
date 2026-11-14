---
name: "chunkmaestro-pr-feedback"
description: "Triage ChunkMaestro PR comments and check notifications, fix actionable failures and distinguish stale events from current merge evidence."
---

# Handle PR feedback without losing state

Read [AGENTS.md](../../../AGENTS.md), relevant source requirements, the live PR
head/base/state and the specific notified comment/job. Reuse verified session
context. A completed review can contain findings; read the threads, not just its
summary badge. An old failed job can coexist with a newer successful attempt.

Record the event SHA, current SHA, run/attempt, finding identity and prior response.
Inspect whether the cause still exists in current code. Deduplicate by cause and
evidence, not notification count. Do not repeat a repair, push, review request or
comment merely because an already-handled event arrives again. A stale event does
not excuse a new current-head finding in an edited summary comment.

Reproduce material defects narrowly and apply authorized in-scope fixes. Run the
affected proof and relevant build/format checks once after the final fix. Fix
small findings directly; reserve a separate implementation task for substantial
work with explicit ownership. A delegated re-review is always a new task. Old
reports remain historical; an implementation self-check does not close their QA.

Classify CI failures from logs. Missing independent QA is a real merge block,
even when other checks pass. Do not weaken checks, add build-suppression labels,
manufacture attestations or call an unexplained failure a flake. A rerun is useful
after a concrete correction, evidence publication or demonstrated infrastructure
failure; repeated identical retries are not investigation.

Use [signed shipping](../chunkmaestro-ship-pr/SKILL.md) for changed code and
[independent QA](../chunkmaestro-independent-qa/SKILL.md) for final verdicts.
Continue authorized work while gates remain; do not invent a fixed repair budget
that cancels the user's larger objective. Ask only about a real unresolved
decision or a next action outside the existing scope.

For pending work, read [event-driven waiting](references/waiting.md). On final
readiness, verify live expected head, actual required checks and artifact contents,
review findings, conflicts and independent acceptance. Report current evidence,
fixes and remaining blockers concisely; a watch wake is news, not merge approval.
