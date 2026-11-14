# Project agent skills

The owner requested reusable skills adapted from SchoolCal. The source checkout
uses `.agents/skills/`, not `.agent/`. ChunkMaestro uses the same canonical
repo-local directory, with task routing in [AGENTS.md](../../AGENTS.md) and Codex
display/invocation metadata in each skill's `agents/openai.yaml`. Load only the
applicable entrypoint and its relevant references. No global skill installation,
symlink, copied credentials or other assistant configuration is required here.

## Adaptation

The SchoolCal skill inventory was inspected at source commit
`bbce83a5c3069faa03106fc7166c406c49f2b97c`. Procedures were rewritten for this
repository; this is not a wholesale copy or a claim of SchoolCal product support.

| ChunkMaestro skill | Source patterns selected | Changes for this project |
| --- | --- | --- |
| `chunkmaestro-implement-slice` | `schoolcal-implement-request`, `self-review-branch` | Source-question traceability, native boundaries, bounded ownership and actual failure/recovery evidence. |
| `chunkmaestro-dotnet` | `schoolcal-dotnet-sdk`, commit validation | Current SDK/CPVM/lock sources and repository commands; explicit types, generated-source checks and native architecture distinctions. |
| `chunkmaestro-persistence` | `schoolcal-create-migration`, review guidance | EF/SQLite and native controller authority, WAL/schema/FK integrity and measured safety qualification. No SQL Server/Postgres generator or blanket prohibition on schema-corruption tests. |
| `chunkmaestro-native-desktop` | `schoolcal-pr-screenshots`, review guidance | Actual Avalonia/native behavior, truthful capability states, accessibility and evidence limitations. No DOM/browser harness standing in for the app. |
| `chunkmaestro-independent-qa` | `schoolcal-review-pr`, self-review separation | Two/three independent roles, exact-head provenance and real CI artifact contract. |
| `chunkmaestro-ship-pr` | `schoolcal-commit`, `schoolcal-create-pr` | Configured signing, scoped staging, stack linking and existing merge authority. |
| `chunkmaestro-pr-feedback` | `schoolcal-pr-feedback-loop` and waiting references | Stale-event triage and supported callbacks. No copied shell waiter, build-suppression policy or arbitrary repair ceiling. |
| `chunkmaestro-dependency-review` | `schoolcal-review-dependency-pr` | Primary upstream evidence, NuGet/native/Java usage and supported architecture proofs. |
| `chunkmaestro-worktrees` | `schoolcal-worktree`, `worktree-build-cleanup` | Runtime-owned child isolation, safe restacks and explicit cleanup scope. No copied local settings, pnpm setup or assumed inactivity. |

School-specific SIS/calendar, HubSpot, Shortcut, production database, Azure,
Kubernetes, browser rollout and marketing-image workflows were not imported.
Composed workflows retain the user's current authorization; a skill does not
create a new approval reset or confer permission for external actions. The product
source and orchestration directive remain authoritative and unchanged.

The owner's C# follow-up also selected SchoolCal's `.editorconfig` conventions
and `DOTNET.md` patterns. ChunkMaestro's [C# guide](../../DOTNET.md) and formatter
retain compatible naming, spacing, modifiers and expression preferences while
preserving the stricter explicit-type/multiline policy. Source warning suppression,
generated-code exemptions, compact statements, CRLF preservation, MSTest-only
testing and application-specific logger/database/timezone dependencies were not
copied. Nullable design and existing analyzers are not weakened.

## Maintain and verify

Give each skill a matching directory/name, discriminating description, concise
`SKILL.md`, useful references only, and `agents/openai.yaml`. Use quoted scalar
YAML values for the small metadata format checked by this repo. Keep UI default
prompts tied to the actual `$skill-name`; automatic selection is enabled unless
the owner requests otherwise. Update AGENTS routing when adding/removing skills.

```sh
python3 tooling/agents/check.py
python3 -m unittest discover -s tooling/agents -p 'test_*.py' -v
```

The check validates package metadata, repository-local links, routing and unsafe
resource paths. It does not certify workflow judgment, execution permissions or
product behavior. Use realistic independent forward tests for substantial/risky
workflow changes, with disposable artifacts and no external mutation. Independent
QA/merge requirements still come from the directive.

Read SDKs, package versions, build targets, available tools and CI/risk rules from
their current sources. Do not turn a past commit, test count or tool availability
into a permanent skill rule. The separate Agent Skills CI workflow runs structural
checks and regression fixtures. Removing the packages/routing/tooling through a
reviewed signed revert is the recovery path; there is no runtime data migration.
