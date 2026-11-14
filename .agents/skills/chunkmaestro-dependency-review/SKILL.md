---
name: "chunkmaestro-dependency-review"
description: "Assess ChunkMaestro SDK, NuGet, native library, Java bridge or CI dependency changes against actual usage, platform support and provenance."
---

# Review a dependency change

Read [AGENTS.md](../../../AGENTS.md) and the affected project/build inputs. Review
the entire version range using primary upstream releases, migration/security notes
and actual package metadata. Separate verified facts from inference. A patch
number or a green dependency probe alone does not establish compatibility.

Classify runtime, native, generator/build, test and CI changes. Find actual imports,
APIs/configuration and shipped assets. Map material upstream changes to affected
usage or a bounded not-used search; investigate transitive and platform changes,
not just the direct version declaration. Check supported OS/architecture/runtime
and Java/server-family capabilities when relevant.

Read SDK pins, central versions, lock content hashes, package sources and Action
commit pins. Investigate unexpected lock deltas, install/build hooks, native
binaries and licenses. An unresolved product license is not permission to choose
one or publish a release. Do not remove an advisory workaround without evidence
that resolution now covers the affected runtime dependency.

Use [C# validation](../chunkmaestro-dotnet/SKILL.md) for intentional lock updates
and cold locked restore. Exercise affected behavior: SQLite WAL/recovery and
native ownership, Avalonia runtime/packaging, or bridge/server APIs as applicable.
A macOS arm64 run is not Intel proof; managed tests do not cover a native shim or
validate every platform binary. Never infer universal Folia/Paper/Fabric support.

Attribute failed checks to the changed usage using logs. Missing, stale, skipped
or suppressed runs are gaps. A flake needs evidence. Reuse applicable exact-head
CI, supplement only the missing proof, and report compatibility, blockers and
remaining platform/security evidence with source/run links.

This review does not itself authorize edits, comments, approval or merge. Carry
the caller's existing authorization into any requested follow-up. All normal
[independent QA](../chunkmaestro-independent-qa/SKILL.md) gates still apply.
