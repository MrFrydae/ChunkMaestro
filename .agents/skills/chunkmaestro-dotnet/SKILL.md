---
name: "chunkmaestro-dotnet"
description: "Build, format, test or diagnose ChunkMaestro C# and native build inputs using the pinned SDK and repository validation commands."
---

# Validate .NET changes

Read [AGENTS.md](../../../AGENTS.md), [C# conventions](../../../DOTNET.md) and
[development commands](../../../docs/operations/development.md). Resolve the SDK
from `global.json`, dependencies from `Directory.Packages.props` and project locks,
and commands from `tooling/build/build.py`; do not copy version numbers into plans.

Confirm the selected `dotnet` resolves the exact SDK pin. Set
`CHUNKMAESTRO_DOTNET` to a verified private installation when PATH resolves another
SDK. Leave other projects' SDKs and shell profiles alone. If installation is
necessary, use official artifacts and verified provenance under a user-owned
location. An SDK mismatch is not permission to relax the pin or update packages.

Use explicit C# types and multiline Allman constructs as required by AGENTS,
including tests and checked-in generated output. `dotnet format` alone is not
the generated-source policy: the repository also runs its read-only syntax check.
Do not disable IDE0008, nullable, warnings-as-errors or analyzers to make a build
green. Read custom project targets for native toolchain/architecture prerequisites;
managed coverage does not measure native C code.

From the target checkout, choose the relevant existing commands:

```sh
python3 tooling/build/build.py verify
python3 -m unittest discover -s tooling/build -p 'test_*.py' -v
python3 tooling/specification/check.py
```

`verify` owns locked restore, Release build, syntax/format verification and test
discovery. During iteration, run the smallest affected suite; after C# changes,
complete the repository build/format checks. Do not describe an absent suite's
explicit NOT RUN result as passing product tests. Report executed/failed/skipped
counts and distinguish Linux refusal contracts from Windows/macOS runtime tests.

After intentional project/package changes, use `sync` or `update-locks` only as
needed, inspect generated deltas, then perform a forced locked restore with the
selected SDK. Incremental assets can conceal a stale probe/project lock. Do not
regenerate locks just to conceal an unexplained dependency change.

Record checkout/head, OS/architecture, SDK, commands, outcomes and artifact paths.
Reuse unchanged evidence; a different OS, native architecture, package graph or
test fixture is a different proof. Guidance-only changes use the agent validator
and affected tooling checks; they do not establish native product acceptance.
