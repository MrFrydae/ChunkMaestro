# C# development conventions

Applies to production code, tests and checked-in generated C# across ChunkMaestro.
These conventions adapt SchoolCal's C# style to this product. The owner's rules in
[AGENTS.md](AGENTS.md) take precedence; [.editorconfig](.editorconfig) owns machine
formatting preferences, and the build/formatter/syntax checks enforce their scope.

## Types, layout and naming

- Use explicit types, including declarations in loops, resource lifetimes,
  deconstruction and `out` arguments. Use named types when a local value needs a
  declaration; do not introduce anonymous locals that force inferred typing.
- Use file-scoped namespaces and keep imports outside them. Follow the source
  convention of one import group without forcing System imports to the front.
- Use four spaces, UTF-8, LF and a final newline. Allman braces stand alone;
  every brace-delimited construct spans multiple lines, including empty blocks,
  properties/accessors and initializers. Use one statement per line. Braces in
  strings/interpolation/comments are not blocks.
- Use PascalCase for types and non-field members; interface names start with `I`.
  Follow the configured modifier order and accessibility conventions. Keep existing
  field conventions unless the affected design requires a change.
- Prefer language type keywords, readonly fields/structs when appropriate,
  concise null/coalescing/pattern expressions and collection expressions where
  they preserve clear types. Expression-bodied accessors/properties are allowed;
  prefer block bodies for methods, constructors, operators and local functions.
  Braced lambdas and initializers still follow the multiline rule.
- Preserve explicit typing and layout in generator output before committing.
  Generated headers are not an exemption. Do not weaken IDE0008, analyzers,
  nullable or warnings-as-errors to quiet a style or compiler failure.

The copied formatter preferences cover parentheses, spacing, imports, indentation,
modifier order, expression forms and naming suggestions. Compact-block/statement
preferences were overridden, and source generated-code exemptions and warning
suppression were excluded. Naming suggestions are not a claim that every naming
rule is a build error; explicit types and multiline syntax have separate checks.
The normal verifier includes the formatter: a standalone `dotnet build` does not
diagnose every IDE style rule. File-scoped namespace violations are also checked
through that formatter gate ([IDE0161](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/style-rules/ide0160-ide0161)).

## Data and API design

Use nullability to express actual absence. Prefer empty collections when empty is
a valid non-null state. Use constructors or `required` for real initialization
invariants; do not make valid domain data optional or insert meaningless defaults
merely to silence warnings. Keep warning fixes within the task's affected scope.

Prefer `DateTimeOffset` for instants and `TimeSpan` for durations; persist instants
unambiguously and distinguish display/schedule timezone decisions. Do not import
another project's timezone library or resolve the owner's pending DST policy as
a formatting change.

For new JSON paths use `System.Text.Json` and existing compatible contract helpers.
This does not replace versioned Protobuf/gRPC contracts or authorize rewriting
existing serializers. Preserve protocol and supported-version compatibility.

Use structured named log fields when the selected logger supports them. Keep
secrets, raw commands/SQL, sensitive paths and input values out of diagnostics.
Public failures use stable sanitized categories and truthful recovery guidance.
No external logging service or source-project logger is implied by this guidance.

## Async, queries and performance

An async API name does not prove it yields. Keep blocking I/O off the native
dispatcher; pass cancellation where meaningful, dispose resources and supervise
background work. Do not add no-op discard assignments to pretend a parameter or
task was handled. Do not hide obsolete-call warnings by deprecating a valid caller.
Replace an obsolete API or document/review the actual compatibility requirement.

Filter, order and page in the database/provider query when applicable. Use LINQ
when it reads well, and address costly shapes: nested repeated scans, redundant
enumeration and per-item materialization. Build a lookup/set once for repeated
joins and materialize once when repeated evaluation is costly. Do not impose
SchoolCal's school-scale assumptions or SQL Server bulk APIs on local SQLite.
Measure changed hot paths at a stated realistic workload; avoid invented budgets.

## Tests and validation

Use the repository's selected xUnit projects and existing test infrastructure,
not SchoolCal's MSTest-only policy. Arrange/act/assert and descriptive PascalCase
names should express an observable invariant. Prefer public boundaries and real
SQLite/protocol/native behavior where required; test doubles have a narrower scope.
Schema damage and permission refusal tests are legitimate safety cases. Do not
substitute migration-text matching for actual upgrade/recovery behavior.

Read [development commands](docs/operations/development.md) and use
[chunkmaestro-dotnet](.agents/skills/chunkmaestro-dotnet/SKILL.md) for the pinned
SDK, locked restore, normal build, generated-inclusive formatting and syntax
checks. Keep local analyzers enabled. After C# or formatter-policy changes run
the relevant repository build/format and policy regressions. Report absent,
skipped, platform-specific and native acceptance accurately. Source safety
thresholds and independent QA are unchanged.
