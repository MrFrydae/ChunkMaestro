# Repository instructions

## C# type declarations

Use explicit C# types. Do not use `var`, including local variables, `foreach`, `using` / `await using`, `out` declarations, and deconstruction. Use named types instead of anonymous types where an explicit declaration is needed. This rule applies to production code, tests, and checked-in generated C#; normalize generated output before committing.

The owner explicitly requested this policy. Keep IDE0008 enabled as an error and all three `csharp_style_var_*` preferences set to `false`. Do not suppress or weaken the rule. Run the repository formatting/build checks after changes.

## Multiline C# braces

Every C# brace-delimited construct must span multiple lines, including empty blocks, methods, control flow, lambdas, property/accessor lists and object/collection/anonymous initializers. Use Allman braces on their own lines. Do not preserve single-line blocks or multiple statements on a line. String interpolation delimiters and braces inside strings/comments are not code blocks. Apply this policy to tests and checked-in generated code too. Keep the formatter settings and syntax-aware style check enabled.

## Product and review boundaries

Read `docs/product/requirements-source.md` and `docs/product/orchestration-directive.md` before implementation. Source answers and later owner decisions govern. Do not substitute a web app for the native desktop. Never claim unexecuted tests as passing or implement successful placeholders. Preserve configured commit signing. Implementation merges require the directive's independent QA gates.

## Project skills

Repo-local skills live in `.agents/skills/`. When a task matches a skill below,
read its `SKILL.md`; load linked references only when relevant. An explicitly
named skill also applies. Skills support the task's existing scope and authority;
they do not override owner decisions, runtime instructions or the rules above.

| Task | Skill |
| --- | --- |
| Scoped feature or defect implementation | [chunkmaestro-implement-slice](.agents/skills/chunkmaestro-implement-slice/SKILL.md) |
| SDK, C#, build, style and test validation | [chunkmaestro-dotnet](.agents/skills/chunkmaestro-dotnet/SKILL.md) |
| SQLite, migrations, authority and recovery | [chunkmaestro-persistence](.agents/skills/chunkmaestro-persistence/SKILL.md) |
| Avalonia, native interactions and visual/accessibility evidence | [chunkmaestro-native-desktop](.agents/skills/chunkmaestro-native-desktop/SKILL.md) |
| Independent role verdicts and exact-head aggregation | [chunkmaestro-independent-qa](.agents/skills/chunkmaestro-independent-qa/SKILL.md) |
| Signed commits, PR creation/refresh and stack integration | [chunkmaestro-ship-pr](.agents/skills/chunkmaestro-ship-pr/SKILL.md) |
| Review feedback, CI notifications and event-driven waiting | [chunkmaestro-pr-feedback](.agents/skills/chunkmaestro-pr-feedback/SKILL.md) |
| NuGet, SDK, native, Java and Action dependency review | [chunkmaestro-dependency-review](.agents/skills/chunkmaestro-dependency-review/SKILL.md) |
| Task isolation, restacking and requested cleanup | [chunkmaestro-worktrees](.agents/skills/chunkmaestro-worktrees/SKILL.md) |

Keep shared policy here and task procedures in the owning skill. Keep mutable SDK,
package, check and tool facts in their executable sources. When changing skills,
routing or metadata, run `python3 tooling/agents/check.py` and
`python3 -m unittest discover -s tooling/agents -p 'test_*.py' -v`.
See [adaptation and maintenance](docs/operations/agent-skills.md).
