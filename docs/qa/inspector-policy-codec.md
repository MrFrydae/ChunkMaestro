# Issue 14 inspection-policy DATA codec

Implementation evidence only; independent final implementation QA and hosted CI remain required. This library enables no product operation. G1–G6 and ENODATA qualification remain unresolved.

Lead integration adds an explicit policy-suite step to the existing Linux, Windows and macOS managed jobs: solution membership alone does not execute tests in this workflow. Each step retains TRX and diagnostic coverage in the existing managed artifact, with the established bounded command supervisor and no automatic retry. This addition changes no existing assertion, test step, dependency, runner or acceptance gate. Hosted outcomes remain pending until the actual artifacts are inspected.

## Scope and provenance

Base: `3f8cddf08c60cc4e81835f74e683a27d4063fc42`. The approved corrected design is candidate `chunkmaestro-14-installed-policy-contract-3f8cddf-v2`, manifest SHA256 `adcd2d0cfd0bb4841560fe41e4e71d83564eb328df097e9cdaf0d7cbcd2a8959`, CONTRACT.md SHA256 `f48ff2b0e6cc0799c002dc11a68e688ec20a179861f7a1f8dd3cdbe53b0fcf72`. Independent corrected security design PASS manifest: `aacef97495d20809f101cae0cfc7889dc88e31043ccf88b8b073bab74244e87d`. That PASS is design-only. The original F1 FAIL remains preserved externally. Both supplied manifests and all 359 frozen source bindings were verified before edits; historical input/predecessor bindings were also verified.

Requirements: Q9, Q11–13, Q20–22, Q31, Q151, Q158, Q162, Q168, Q189–195 and the superseding owner testing policy dated 2026-11-23. Restricted authority and reviewed target bindings motivate the grammar; this slice only represents proposed claims. Testing scores are diagnostic, with no numeric gate.

`ChunkMaestro.PrivilegedHelper` is an ordinary BCL-only net10.0 library with no product project references or entry point. Only `ChunkMaestro.HelperPolicyTests` receives internal visibility. Desktop and Agent do not reference it. The test project uses existing central package versions; only the two new locks are added. Solution membership comes from actual `build.py sync`. Tests alone now trigger high-risk QA classification, preserving all existing gates.

## Contract

Internal synchronous `InstalledPolicyCodec.Decode(ReadOnlySpan<byte>, CancellationToken)` returns the closed `PolicyDecodeResult` hierarchy: sealed `ParsedInspectionPolicyData` or sealed `PolicyParseRefusal` containing one fixed enum code. Parsed construction is private; decoder implementation lives inside the parsed partial class to enforce this. Every scalar is a get-only string/unsigned value. Drop-ins are ordered immutable key/value pairs in a read-only wrapper around a private copied array. Temporary schema nodes never escape. There is no public policy API or raw-input retention.

Caller must keep the input stable and exclusively unmodified during copying. ReadOnlySpan does not guarantee that ownership or atomic acquisition. A private snapshot is made only for input lengths 1..16384. Later caller mutation cannot change data or revision. Concurrent mutation during copying violates this API contract.

Whole-input strict UTF-8 validation, including the suffix, precedes syntax validation; an initial BOM is forbidden. The first pass visits every first-value property-name/string token, including unknown fields and wrong-type values. Only token-kind-guarded GetString catches InvalidOperationException (ill-formed escaped UTF-16 -> MalformedJson). Read JsonException maps to MalformedJson. Valid surrogate pairs pass syntax and then fail the appropriate known ASCII field grammar; valid unknown keys remain UnknownField. Accepted containers start at depths 0..3; reader guard 5 exposes the first forbidden container at CurrentDepth 4, immediately returning DepthExceeded. After the first complete JSON value, a byte scan permits exactly SP/HT/LF/CR; every other suffix returns TrailingData without tokenizing a second value. Root must ultimately be an object. Comments, trailing commas and incomplete first values refuse.

The second pass uses an explicit bounded stack, not recursive traversal. Objects are closed, case-sensitive and ordinal, with every listed field required exactly once and any field order allowed. Duplicate names are compared after decoding escapes. Unknown/duplicate/type errors occur in encounter order; missing fields refuse at object close. Null always has WrongType. There are no defaults, coercions or extension buckets.

| Object | Exact required fields |
| --- | --- |
| root | schema, host, identities, instanceId, target, packageManifest |
| host | machineId, deploymentId |
| identities | agentUid, inspectorUid, gameUid, agentGid, inspectorGid, gameGid |
| target | unitName, gameRoot, fragment, dropIns |
| gameRoot | path, ownerUid, ownerGid, deviceMajor, deviceMinor, inode, mode |
| fragment, each drop-in, packageManifest | path, sha256 |

Numeric tokens use `0|[1-9][0-9]*`, at most 20 digits, parsed as unsigned integers without floating-point conversion. Fractions/exponents/negatives including -0 are InvalidNumberLiteral; syntactically illegal 01/+1 are MalformedJson. Overflow is NumberOutOfRange. Schema is ulong and must equal 1. UIDs and role GIDs are uint 1..4294967294; UIDs must be pairwise distinct, GIDs may match. ownerUid is uint and must be 0 or gameUid. ownerGid is 0..4294967294. Device major/minor accept full uint values. Inode is ulong 1..MaxValue. Mode is numeric permissions 0..511, with owner read/search set and group/other write clear. Mode restrictions produce InvalidBinding.

Machine ID is 32 lowercase hexadecimal digits, nonzero. Instance/deployment IDs are nonempty canonical lowercase D UUIDs. Digests are exactly 64 lowercase hex; zero digest is valid DATA. Unit is `[A-Za-z0-9][A-Za-z0-9_-]{0,62}\.service`, preserving case. Paths are exact ASCII absolute lexical Linux paths, 2..1024 bytes, 1..16 nonempty components of 1..255 `[A-Za-z0-9_.-]` characters, excluding `.`/`..`. No repeated/trailing slash, root-only path, expansion, normalization, resolution, NSS or filesystem lookup occurs.

Fragment basename must equal the unit name. There are 0..8 drop-ins; each basename has a nonempty stem plus `.conf`, and full paths must be unique and strictly ordinal ascending. All root/fragment/manifest/drop-in paths are pairwise distinct. Manifest path is exactly `/usr/lib/chunkmaestro/inspect/manifest.json`. Fixed cross-check order is schema support, UID collision, root owner/mode, fragment basename, manifest binding, path duplicates and drop-in ordering. A ninth item is CollectionLimit after the complete syntax pass. These comparisons do not prove any actual installation or permission.

After all validation, SHA256 hashes the entire private snapshot, including whitespace, ordering and escape spelling. Revision is lowercase hex content identity, not authentication or a monotonic version. No policyRevision/self-digest input is accepted. Cancellation is checked at entry, after copying, at every token in both passes, before hashing and immediately before returning the fully constructed result. Cancellation throws with the caller token. OOM and implementation defects propagate. There is no broad catch, fallback, previous-result reuse, cache, logging or event emission. Refusals contain no input, path or exception text.

Fixed refusal codes: EmptyInput, InputTooLarge, InvalidEncoding, MalformedJson, DepthExceeded, TrailingData, UnknownField, DuplicateField, MissingField, WrongType, InvalidNumberLiteral, NumberOutOfRange, UnsupportedSchema, InvalidIdentifier, InvalidDigest, InvalidPath, InvalidUnitName, IdentityCollision, InvalidBinding, DuplicateBinding, UnorderedDropIns, CollectionLimit. Phase/encounter order governs combined faults; the list is not a global priority table.

## Executed tests and diagnostic evidence

macOS 27.2.1 arm64, private pinned SDK 10.0.404/runtime 10.0.15. SDK version matched global.json. No install/upgrade or policy relaxation occurred. All results below concern synthetic offline data.

| Plan | Actual evidence |
| --- | --- |
| P01–P04 | Exact assertions for every output scalar, empty/eight drop-ins, every seed truncation, max-sized valid whitespace payload, oversize, malformed/root/tail/encoding cases; all 34 supplied vectors are embedded verbatim and execute from the checked-in corpus without `/tmp` access. |
| P05–P06 | Every schema field missing, duplicate/escaped duplicate, unknown/case variant, null and wrong primitive/container; drop-in fields tested separately. Depth and malformed-string ordering vectors execute. Container values inside a drop-in field correctly hit depth refusal before schema type checks. |
| P07–P12 | Each numeric field's invalid lexical forms, uint boundaries, exact >2^53/ulong.MaxValue inode; identifier/digest/unit/path lengths and grammar, reserved IDs/collisions/shared GIDs, fixed bindings, duplicate/unordered drop-ins, all modes 0..512 with an independent bit oracle. |
| P13–P15 | Independently precomputed seed SHA256 plus BCL byte-hash oracles, spelling/whitespace variants, immutable fields/collection, caller mutation, serial/parallel no reuse, pre-cancel and 128 bounded cancellation races. Production checkpoint inspection complements stress; no deterministic mid-checkpoint hook or unsafe OOM injection was added. |
| P16–P17 | Existing InspectRequest frame round-trip using parsed ID/revision only; exported types, BCL references, friend assembly, no setters/PInvoke and rejection of fake proof/trust fields. Source review confirms no filesystem/process/network/native calls in library. |
| P18 | Seed 14032026: 10000 arbitrary byte cases of length 0..16385, each decoded twice; 5285 deterministic per-byte structural mutations, each decoded twice. Seed 14019: 256 generated valid numeric-claim/field-order variants, then UID-collision mutations. Accepted outcomes use independent value/hash assertions. |
| P19 | Warmed isolated process: 1000 serial plus 1000 parallel decodes per small/max/deep/long case, allocation/CPU/full-GC retention deltas recorded below and in external TRX. No threshold asserted. |

Final policy suite: **211 passed, 0 failed/skipped**; existing Contracts suite: **18 passed, 0 failed/skipped**. Build-tooling tests: **23 passed** including test-only helper-path all-three-role escalation. Specification checker passed. Root Release build and syntax guard, root format including generated sources, and forced locked restore passed. New locks were reviewed; no existing lock or central dependency changed.

Diagnostic helper coverage: line rate **0.988**, branch rate **0.9905** (rounded), from retained Cobertura. Five targeted source mutations (size guard, decoded duplicate detection, UID comparison, snapshot digest input, mode-write guard) were each detected by focused tests; original source was restored and full focused tests/build/style/format rerun. This is a targeted sensitivity experiment, not an exhaustive Stryker score.

Isolated resource observations, without coverage instrumentation:

| Case | Bytes | Allocated bytes/call | Serial wall/CPU ms per 1000 | Full-GC serial/parallel retained delta bytes |
| --- | ---: | ---: | ---: | ---: |
| valid small | 755 | 14560.3 | 15.051 / 15.053 | 392 / 48312 |
| valid padded maximum | 16384 | 30184.3 | 34.425 / 34.404 | 616 / 680 |
| deep malformed | 16384 | 16528.3 | 1.069 / 1.073 | 616 / 704 |
| long malformed string | 16376 | 17728.3 | 6.572 / 6.583 | 616 / 44392 |

These are local process observations, including timer/process measurement overhead and thread-pool/JIT retention noise, not proof of zero retention, cross-platform performance or an SLA. Source inspection and immutable-output tests establish that raw input is not stored in results. Aggregate caller concurrency/memory admission remains future work; GC/stack erasure is not promised.

Initial failures are retained externally: BOM-check overload compile error; cancellation/culture analyzer violations; test oracle overlooking root-owner binding; coverage-injected BCL netstandard reference; compact initializer/import formatting; and expected WrongType overlooking forbidden drop-in container depth. No failing assertions were deleted; expectations were corrected against the frozen contract. All final runs are green.

## Limits, recovery and rollback

Broad `build.py verify` and `build.py test` are **NOT RUN**: their native-vault/authentication fixtures are outside this task's authorization. Safe alternatives were full solution compilation/style/format/restore plus explicitly selected policy and Contracts tests. Native window/auth/vault access, real host/SSH/systemd, Linux/Windows runtime execution, installer/E2E authority, hosted CI, aggregate concurrency qualification, SAST/SBOM/secret scan, exhaustive mutation tooling, deterministic internal cancellation injection and OOM injection are **NOT RUN**. There is no installed operation to exercise end-to-end; the full bytes-to-data and existing wire boundary are tested locally. Independent all-three-role final QA remains pending; this author supplies no independent verdict.

Refusal yields no data. Cancellation/resource/defect exceptions publish no result. A future caller must leave authority unavailable and avoid defaults/cached authority. Corrected bytes may be decoded afresh offline; parsing does not approve installation. There is no permission repair, automatic re-adoption or fallback to “latest.” G1–G6 require separately reviewed provenance/acquisition/qualification/maintenance/socket/ACL work, and ENODATA remains unqualified.

Rollback removes the new library/tests/docs, the two generated solution memberships and the narrow QA classifier addition through parent review. No migration, installed file, live listener, credential, host or game data is touched. Parent owns integration, signing, shipping and independent exact-head implementation QA. Complete scoped patch, source hashes, command logs, TRX, coverage, resource/fuzz/mutation outputs, initial failures, environment and read-only reopened manifest are in the external implementation handoff bundle.
