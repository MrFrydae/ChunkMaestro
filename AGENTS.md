# Repository instructions

## C# type declarations

Use explicit C# types. Do not use `var`, including local variables, `foreach`, `using` / `await using`, `out` declarations, and deconstruction. Use named types instead of anonymous types where an explicit declaration is needed. This rule applies to production code, tests, and checked-in generated C#; normalize generated output before committing.

The owner explicitly requested this policy. Keep IDE0008 enabled as an error and all three `csharp_style_var_*` preferences set to `false`. Do not suppress or weaken the rule. Run the repository formatting/build checks after changes.

## Multiline C# braces

Every C# brace-delimited construct must span multiple lines, including empty blocks, methods, control flow, lambdas, property/accessor lists and object/collection/anonymous initializers. Use Allman braces on their own lines. Do not preserve single-line blocks or multiple statements on a line. String interpolation delimiters and braces inside strings/comments are not code blocks. Apply this policy to tests and checked-in generated code too. Keep the formatter settings and syntax-aware style check enabled.

## Product and review boundaries

Read `docs/product/requirements-source.md` and `docs/product/orchestration-directive.md` before implementation. Source answers and later owner decisions govern. Do not substitute a web app for the native desktop. Never claim unexecuted tests as passing or implement successful placeholders. Preserve configured commit signing. Implementation merges require the directive's independent QA gates.
