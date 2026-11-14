---
name: "chunkmaestro-ship-pr"
description: "Prepare signed scoped commits and create or refresh ChunkMaestro pull requests, preserving stack dependencies and independent merge gates."
---

# Ship a reviewable change

Read [AGENTS.md](../../../AGENTS.md), the repository PR template and the
[directive](../../../docs/product/orchestration-directive.md). Carry the actual
task's authorization forward. A commit-only request ends with a local commit;
skill selection does not independently authorize a push, PR, merge or release.
Do not ask again for an in-scope action already authorized in the conversation.

Inspect branch, complete diff, index and unrelated changes. Verify the issue and
base, including every unmerged stack layer. Stage only intended paths/hunks.
Prepare a concrete result before seeking any genuinely missing final approval.

Run the appropriate source generators/formatters before final verification.
Review their output and use [C# validation](../chunkmaestro-dotnet/SKILL.md) for
build inputs. Skill/guidance changes use `tooling/agents/check.py` and its tests.
Reuse checks whose scoped inputs have not changed; final checks are non-mutating.

Preserve configured signing; use signed commits and signed rebases. Do not disable
signing or replace its configuration to get past a local trust-file problem.
Verify staged scope and diff whitespace. Use a conventional title explaining the
resulting behavior. After a necessary restack, retain a recovery ref and use an
exact force-with-lease against the observed old head; never force a moving branch.

For an authorized push/create/takeover in T3, the next tool call links the full
PR URL(s), including every stack layer. Existing links persist. Outside T3, use
the available host integration and report the PR URL without claiming T3 linkage.
Prefer structured API bodies or a literal body file over shell-interpolated text.

Write the PR for a reviewer without conversation context: concrete behavior,
source IDs, issue/dependencies, exact validation, failure/recovery and remaining
gaps. Rewrite obsolete scope rather than accumulating contradictory "final" heads.
Attach native evidence only when actually executed; keep incomplete UI work draft.

Merge only within existing authority after the live expected head, all applicable
checks, independent QA, findings and acceptance criteria are satisfied. A green
CI run with missing product evidence is insufficient. Major architecture/scope,
privilege, license and public-release decisions remain owner-controlled. For
pending CI/reviews, follow [PR feedback](../chunkmaestro-pr-feedback/SKILL.md).
