# Issue 14 offline inspection contracts

Foundation on accepted main `a504b3b049e1b2826267a47220b507d313ec8d33`,
integration branch `feat/14-inspection-contracts-integration`. No helper, provider, socket listener, Linux
execution, enrollment, audit writer or product inspection capability is added.
Encoding an Observed value is data serialization, never source attestation or
permission. Existing desktop capabilities remain unavailable. No issue closure or
product acceptance is claimed.

Trace: Q9/11–13/20–22/25/31/45/151/158/162/168/189–195;
ADR0004/0005/0010 and the threat model. All 205 source answers and the later
2026-11-23 testing policy govern; coverage/mutation are diagnostic. Independent
implementation QA remains required, with three roles for this boundary.

## Provenance and scope

Authoritative external design: `/tmp/chunkmaestro-14-design-0b4fe62/`
(REPORT, CONTRACT-DETAILS, PROOF-PLAN, design.json), frozen at
`0b4fe626684616d31f52b6e30640ab57c90ad9b3`. Preserve that provenance rather than
rebinding it to main. The original independent review
`/tmp/chunkmaestro-14-independent-review-404e8216-012a-4d22-a8a3-aac01794e25b/`
remains FAIL for F1. The correction
`/tmp/chunkmaestro-14-fd-correction-0b4fe62/` and new independent review
`/tmp/chunkmaestro-14-correction-independent-review-b4b22a36-4afa-4979-a5be-df262f0ecd77/`
resolve F1 at design level only. Original design/FAIL/correction/PASS manifests
contain 25/13/5/25 entries; 309 original frozen source bindings are retained.
Implementation evidence and input audit: `/tmp/chunkmaestro-14-contracts-a504b3b/`.
Accepted main also contains the separate owner-denial module; it is unchanged.

Owned files are exclusively `src/ChunkMaestro.Contracts/**`,
`tests/ChunkMaestro.ContractsTests/**` and this document. Contracts targets the
pinned net10.0 SDK and framework System.Text.Json with no package dependencies.
Tests consume existing central xUnit v3, test SDK, VS adapter and collector pins;
new project locks are intentional. No shared package, workflow, persistence,
credential or UI change belongs to the worker patch. Lead integration adds sorted
solution membership and automatic high-risk QA classification for Contracts and
OwnerVerification source paths.

## Public data and schema

`InspectRequest` and `InspectResponse` are sealed immutable classes; constructors
validate all variable values. Fixed version/action/source are initialized get-only
properties. There are no default IDs, latest-revision selection, grants, claimed
UIDs, execution APIs, arbitrary unit/path/JVM/shell/environment inputs, or query
delegates. `InspectCodec` encodes framed bytes, decodes bounded payloads and reads
one framed message from a caller-owned Stream. DTOs have no OS methods.

Request required fields only: `version` integer literal 1; `action` exact `inspect`;
`requestId` and `instanceId` nonempty lowercase D GUIDs; `policyRevision` exactly
64 lowercase hexadecimal characters. Response required fields only: `version`,
`requestId`, `instanceId`, `policyRevision`, `helperEpoch` (same GUID grammar),
`outcome`, `reason`, `observedAt`, `durationMs`, `serviceState`, `source`,
`managerPresence`. Request action is not repeated in the response.

Version/correlation/revision must exactly match the outstanding typed request.
Source is exactly `debian-systemd-system-bus`. Duration is integer literal 0..3000.
UTC timestamp has the separate canonical wire grammar
`yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'` (seven fractional digits, Gregorian valid date,
no offset alternative). Typed DateTimeOffset requires offset zero; wall clock is
not a freshness clock. GUID and hash syntax validates data, not randomness or
policy provenance. Property order is unrestricted on decode; encoder order is
stable. Enum strings use the exact PascalCase names below; numeric/unknown/case
variants refuse. No forward-compatible unknown-field acceptance is implied.

Both payloads refuse missing/unknown/duplicate decoded property names (including
escaped-equivalent names), null/wrong types, comments, trailing commas, depth >4,
invalid UTF-8/BOM, integer overflow, fractional/exponent integers and invalid
identifiers. JsonDocument is bounded before parsing; explicit ordinal field
tracking supplies duplicate rejection. Nested structures cannot satisfy any
scalar field. The finite schema is deliberately flat.

## Complete combination table

All combinations outside this table refuse in both typed construction and wire
decode. Invalid enums also refuse. Non-Observed always has Unknown serviceState.
`NotFound` is manager diagnostic presence only, never a service state.

| Outcome | Reason | Service state | Manager presence |
| --- | --- | --- | --- |
| Observed | None | Active, Inactive, Failed, Transitional | Present |
| Denied | PolicyDenied | Unknown | Unresolved |
| Unknown | TargetDrift | Unknown | Present, NotFound, Unresolved |
| Unknown | NotLoaded | Unknown | Present |
| Unknown | UnsupportedState | Unknown | Present |
| Unknown | ConcurrentChange | Unknown | Unresolved |
| Unavailable | DependencyUnavailable, PermissionDenied, Timeout, Busy | Unknown | Unresolved |

Removed/missing binding requires TargetDrift even when manager absence is proven;
manager NotFound cannot establish a fresh usable binding. NotLoaded is Unknown,
not Inactive. `active` maps to Active; `inactive` to Inactive; `failed` to Failed;
activating/deactivating/reloading/refreshing to Transitional; all others to Unknown.
Active describes PID1 service state only, including oneshot services; no Minecraft,
JVM, Java TCP or Bedrock UDP health is implied.

Reasons carry no arbitrary exception text. Future authenticated handling may emit
a correlated denial only after identity and correlation are validated. Malformed
or unauthenticated callers must close without policy/target data. Codec exceptions
are local failures (ArgumentException/InvalidDataException/JsonException/InvalidOperationException and strict
encoding errors); do not serialize exception messages as protocol responses.

## Framing, resources and recovery

Four-byte unsigned big-endian payload length, then exact UTF-8 JSON bytes:
request 1..1024, response 1..4096. Zero/oversize/uint-max refuses after the four-byte
header and before payload allocation/read. ReadExactlyAsync supports fragmentation.
After payload, a one-byte read must return EOF before any DTO is returned. Extra
byte, second frame and truncated header/payload refuse. Maximum read buffer is the
message cap. There is no dispatch callback or executable path in the codec.

Read methods link caller cancellation to a one-second framing deadline, including
EOF. No detached timeout race, background query or replacement task is started;
all reads are awaited and the linked timer is disposed. Cancellation and I/O faults
propagate. Caller owns the stream on success and failure, must close it after a
failed read, and must perform write-half shutdown after sending an encoded request.
Never reuse a failed connection or drain attacker-controlled bytes indefinitely.
The codec neither disposes nor retries the stream. It has no write method because
a generic Stream cannot express socket half-close.

An arbitrary Stream may ignore cancellation or block; the timer does not prove a
hard stop. Future transport must bound admitted connections/work and safely close
and drain its owned operations before replacement. This codec has no socket or
SCM_RIGHTS receiver: ancillary FD rejection/close-on-exec/disposal is future native
work. Native worker drain, setup/hash and sd-bus allocation/call limits, total 3s
query budget, 250ms remaining-call limits, rate limits and concurrency bounds
remain unimplemented and unproven.

Internal `PolicyFacts`/`InspectionDecisions` are pure fixtures, exposed only to the
owned test friend. They check nonzero pairwise-distinct numeric identities,
exact peer/instance/revision, qualified identity, target binding and input stability;
denial precedes drift. They neither load root policy nor authenticate Linux peers.
A caller-provided qualified flag conveys no actual authority.
`ObservationWindow` models one outstanding read with caller-supplied nonnegative,
normalized local monotonic milliseconds. Negative starts refuse before elapsed
subtraction, so overflow cannot turn an invalid interval into a fresh result.
It rejects mismatches, duplicate/late (>3s) responses and
known changed helper epoch. Failed observation cannot become fresh. Age <=15s
from request issuance is conservative; normal completed one-shot close preserves
freshness, unexpected mid-query failure invalidates it. Revision/process restart
and detected epoch changes require invalidation; undetected restart cannot be
known instantly. No retries, scheduling, queues or durable exactly-once claims.

Recovery is a fresh authorized read after independent administrative restoration
and revalidation; never permission widening, auto-adoption or service repair.

## Evidence and remaining work

Tests cover golden request bytes/endian framing, response roundtrip, every byte
truncation of both messages, every uniform fragment size, oversize-before-read,
strict malformed inputs, exhaustive enum combination matrix (typed and wire),
synthetic identity/denial precedence and observation invalidation, deterministic
EOF cancellation and fault ownership. Fuzz seeds 14, 205, 4048216 each exercise
1000 bounded random/mutated request payloads with a per-thread allocation bound.
These are managed behavior proofs only. No native or kernel claims follow.

Exact commands, environment, counts, initial failures, diagnostic coverage,
source and artifact SHA-256 inventory and machine handoff are retained in the
external implementation evidence directory. Mutation execution is NOT RUN; no
mutation score or diagnostic metric substitutes for behavior/security assertions.

For local full validation only, the original solution was backed up and existing
`build.py sync` temporarily added Contracts and ContractsTests. The original
solution must be restored before handoff. Its resulting project-glob guard is
expected to fail until parent integration. This protocol foundation is integrated
separately from the unmerged native verification bridge; its solution contains the
contract pair and already accepted projects. The worker's temporary graph result
remains historical. Lead integration adds a monotonic overflow regression that
rejects the worker's original calculation and two high-risk path checks. Fresh
integration results are retained under `.artifacts/inspection-contracts-integration/`.
Fresh lead verification passed 548 tests with zero failures/skips, 50 CI-tooling
tests, 21 build-tooling tests and 18 agent-tooling tests. Locked restore, Release
build, syntax/style, formatting and specification checks passed. Original failing
overflow proof and the prior risk-classification result are retained separately.
Actual CI and new exact-head three-role QA remain required.

All D01–D12 remain NOT RUN: real Linux peer credentials/systemd socket activation,
FD/ACL/proc namespace, actual bus owner/signature/state reads, native build/ABI,
resource/worker limits, denial/drift/race/recovery and unrelated-resource proofs.
Readable metadata FDs must replace the flawed O_PATH/fgetxattr composition;
ENODATA needs qualified backend/LSM visibility, never error-as-absence. Pathname
socket ACLs need the corrected retained readable-parent/proc-self procedure and
namespace/lifetime proof. Broker root credentials do not prove the named PID1
owner; fixed Hello/GetNameOwner/GetConnectionUnixUser/Manager.UnitPath exchanges
and reply sender checks remain future obligations. Launch/package/loader trust,
root-owned policy/install and audit/enrollment/owner UI are unimplemented.
No VM/SSH/APT/sudo/install, grants, listeners, production data or trust changes
were made. The worker delivery used no GitHub writes, commits, pushes or
subdelegates; lead publication and independent review are separate integration work.

Rollback: remove only this patch's two new project trees and this document;
parent then regenerates its combined solution and locks as appropriate. No
storage migration, runtime deployment, permissions or data rollback is needed.
