---
name: "chunkmaestro-implement-slice"
description: "Implement a bounded ChunkMaestro feature or defect fix with requirements traceability, real failure behavior and reviewable evidence."
---

# Implement a product slice

Read [AGENTS.md](../../../AGENTS.md), the affected answers in the
[requirements source](../../../docs/product/requirements-source.md), and the
[orchestration directive](../../../docs/product/orchestration-directive.md).
Later owner decisions govern. Read the complete source when assigning architecture
or cross-cutting scope; a matrix is an index, not a replacement for the answers.

Resolve the behavior, issue, source-question IDs, dependencies, acceptance cases,
ownership, rollback and current authorization before editing. Reuse the existing
issue/worktree for the same task. A small fix need not become an unrelated project.
An unresolved decision blocks its dependent behavior, not all independent work.

Use a branch shaped like `feat/<issue>-<scope>`, `fix/<issue>-<scope>`, or the
appropriate docs/test/chore variant. Preserve unrelated local work. Use
[worktree guidance](../chunkmaestro-worktrees/SKILL.md) when isolation is needed.
Delegate only when authorized and useful; retain the directive's four-agent total
ceiling, approved model/effort and independent ownership. A task brief includes
source IDs, files/contracts owned, dependencies, failure/recovery criteria,
permitted resources, delivery branch and required evidence.

Implement observable behavior through the actual boundary: native desktop to local
services, restricted agent/provider capability, migrated SQLite or real protocol.
Unavailable capability remains unavailable. Cached/unknown state is explicit;
there is no successful placeholder, blind disruptive replay or web substitute.
Carry existing authorization into composed skills; selecting a skill does not
grant permission to message others, mutate a live host, merge or release.

Use [C# validation](../chunkmaestro-dotnet/SKILL.md),
[persistence](../chunkmaestro-persistence/SKILL.md),
[native desktop](../chunkmaestro-native-desktop/SKILL.md) and
[dependency review](../chunkmaestro-dependency-review/SKILL.md) only as relevant.
Prove the changed behavior and its important denial/recovery paths. A defect
regression should fail before the fix where practical. Reuse passing evidence
while its inputs remain unchanged; expand checks for a concrete remaining risk.

Inspect the final diff and validation gaps before
[shipping](../chunkmaestro-ship-pr/SKILL.md). Self-review is not independent QA.
Use [independent QA](../chunkmaestro-independent-qa/SKILL.md) for the merge gates.
Report what works, exact commands/results and head, assumptions, unexecuted cases,
recovery and the next blocked dependency. Follow the directive's definition of
done; a ready patch, merged slice and completed milestone are different states.
