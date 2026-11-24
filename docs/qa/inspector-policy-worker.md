# Issue 14 pure policy-worker contracts

Frozen parent: `16eb7ca51eda4f69eb9cb4824991f19bbf2edb38`.
This is an internal, offline protocol/lifecycle model, under the existing owner
master-orchestration authority. It has no runtime consumer or authority adapter.
The current RetainedPolicyAcquisition, native codec/ABI, Contracts, project graph,
locks, dependencies, analyzer rules and signing configuration are unchanged.

Design inputs, preserved unchanged:

- `chunkmaestro-issue14-containment-design-16eb7ca-a9w1_9xq`:
  manifest SHA256 `e25de875b44693317a5d5fbbe9196016e997d537a6a6a44e72cce3e9808494d1`;
  399 payloads, including 384 frozen source bindings.
- `issue14-security-design-review-16eb7ca-rb1n_il6`:
  manifest SHA256 `66eb7b7183bca38869c70fd49d64b50fb1386dea13a123f12c49170720ba0042`;
  seven payloads. Its DESIGN PASS grants no implementation or runtime permission.

SD-1 is addressed by using only legitimate requirement IDs in traceability below.
SD-2 is addressed by this explicit matrix and `PublishOrdered` transition.

## Wire contract

The datagram is exactly 48, 80 or 208 bytes. Integer fields are unsigned little
endian: `CMPW` at 0; u16 version 1 at 4; u16 opcode at 6; u32 total length at 8;
u32 reserved zero at 12; opaque 16-byte session at 16; u64 sequence at 32;
u32 status at 40; u32 payload length at 44. Exact lengths precede interpretation;
no length from input controls allocation. A fixed 208-byte stack snapshot is
validated before constructing a bounded immutable frame. The input transport
must supply a stable datagram during copying; concurrent mutation during the
copy is not a verified transport. Subsequent caller-array changes cannot affect
frames or receipts.

| Opcode | Direction | Allowed phase | Session / sequence | Total / payload | Symbolic rights |
|---|---|---|---|---|---|
| HELLO 1 | worker to coordinator | Starting, once before START | zero / 0 | 48 / 0 | 0 |
| START 2 | coordinator to worker | Starting after HELLO, once | nonzero new / 1 | 48 / 0 | exactly 2 |
| ACQUIRED 3 | worker to coordinator | Acquiring | exact original / 1 | 208 / 160 | 0 |
| CHECK 4 | coordinator to worker | ReadyUnqualified | exact original / next, no wrap | 48 / 0 | 0 |
| CHECKED 5 | worker to coordinator | Checking | exact original / outstanding | 80 / 32 | 0 |
| REFUSED 6 | worker to coordinator | Acquiring or Checking only | exact original / outstanding | 48 / 0 | 0 |
| RELEASE 7 | coordinator to worker | ReadyUnqualified | exact original / next, no wrap | 48 / 0 | 0 |

Status is zero except REFUSED, which permits exactly 1..8 (ShapeOrAbi,
NativeUnavailable, Drift, AclPresent, Incomplete, Limit, ParseRefusal,
InternalFailure). The arbiter retains only the sanitized NativeRefusal category.
Unknown bits/opcodes/status, malformed facts, wrong direction, zero session outside
HELLO, wrong sequence/correlation, trailing/truncated input and ancillary anomalies
refuse. Envelope validation is separate from phase/correlation validation.
No child receipt proves retirement; there is no release acknowledgment.

ACQUIRED contains exact 128-byte facts followed by a 32-byte revision. Facts
validation preserves DecodeFacts ABI semantics (version, size, reserved, byte
count 1..16384, device equality, directory/file kinds, nonzero inode/link counts,
leaf link count one, bounded nanoseconds and 16-bit modes), without initializing
the native interop class or resolver. CHECKED must match the immutable original
revision. Receipts expose defensive copies of facts/revision only: no DATA,
PolicyFacts, Observed, policy factory or admission. Revisions are consistency
measurements, never authenticity. Symbolic rights count, unexpected-ancillary and
truncation flags neither own nor close descriptors nor verify SCM_RIGHTS.
Production transport verification is NOT IMPLEMENTED.

## Ordered lifecycle and recovery

One arbiter models one lifetime: Idle, Starting, Acquiring, ReadyUnqualified,
Checking, Retiring, Quarantined, Retired. AcceptSymbolicOwnership starts fixed
internal test budgets: operation 3000, lease 30000, retirement observation 1000
logical units. These are model timestamps, not a product SLA or trusted runtime
clock. Zero time is valid; negative time, regression and arithmetic overflow
refuse. Equality expires; handshake/progress never resets a deadline. Check's
new deadline is capped by the original lease. Idle expiry is checked through
TimeAdvanced and every history access. No caller budget can extend a lease.

All events and accesses acquire the same lock. PublishOrdered parses, samples
logical time, verifies cancellation/state/session/outstanding sequence/revision,
and commits publication in that single ordering. There is no separate CanPublish
API. CancellationRequested is its own event, independent of CancellationCallback;
a delayed callback cannot permit publication after the request event. Concurrent
overlap is linearized by lock acquisition. A request ordered after publication
invalidates future accesses while preserving the historical receipt. This does
NOT prove actual CancellationToken request timing, OS scheduling or atomic runtime
admission: the external runtime bridge remains unimplemented.

ReleaseRequested during an operation revokes locally without queuing RELEASE or
closing anything. Wire RELEASE is allowed only while ready. Check/release races,
duplicate responses, revision drift, malformed input, worker exit and unknown
events retire without cached fallback. First failure and first cleanup failure
are separate sticky enums. Retiring expiry or incomplete RetirementObserved
quarantines. LeaderReaped, DomainEmpty and CleanupFinished are separate internal
symbolic events; only their conjunction plus RetirementObserved models Retired.
Quarantine requires separately modeled external retirement, never a child packet.
There is no reset/retry/replacement even after Retired within the same object;
an eventual external owner must enforce retirement before constructing another.
This narrower one-lifetime API avoids implying a global runtime supervisor.
No cross-object/global exclusion or crash reconciliation is implemented.

Recovery for this patch is removal of its six newly added files. There is no
migration, package, host, privilege or runtime state to roll back. No native I/O,
worker, executable, launcher, filesystem/network/privilege operation, timer,
process/thread supervisor, FD right, pidfd, cgroup, seccomp, P/Invoke, clock API,
GUI/authentication fixture or grant is added. All D1/D2/D3 decisions remain open;
G1–G6, ENODATA, readable acquisition and protected ancestry remain unresolved.
The root-window question remains UNANSWERED; its fixture was not run.

## Requirements traceability

| Source | Constraint retained |
|---|---|
| Q9 | Reviewed adoption/provisioning only; this model performs neither |
| Q11–13 | Recorded Debian/systemd/Java inventory is not runtime proof |
| Q20–22 | Restricted scoped identity, manual adoption, no new privileges |
| Q31 | No arbitrary commands, unit edits or launcher authority |
| Q151 | C# helper-library contracts only; no new executable role |
| Q158 | Native/disposable Debian integration remains unexecuted |
| Q162 | Packaging and protected helper replacement remain future obligations |
| Q168 | Correlated receipts confer no host/server capability |
| Q189 | Existing xUnit projects, independent wire fixtures and behavioral schedules |
| Q190 and owner 2026-11-23 | Coverage/mutation are diagnostic; preserve assertions, no percentage gate |
| Q191 | Bounded malformed-input and lifecycle fault schedules; native chaos remains unexecuted |
| Q192 | Authorized pure local tools only; no additional service or host access |
| Q193 | No successful runtime placeholders; unsupported boundaries explicit |
| Q194 | Exact commands, first failures and limits preserved in handoff evidence |
| Q195 | No live host/VM used; no production data touched |

## Validation scope

The focused HelperPolicy suite retains the original 236 passing macOS pure tests
and 15 Linux-only skips, adding pure PolicyWorker tests. Contracts remains
unchanged. Independent hand-built vectors cover opcodes/directions/status/session,
sequence, lengths 0..209, header corruption and uint overflow, ancillary flags,
facts ABI and deep copying. Full parser-to-arbiter schedules cover phase matrices,
request-before-publication with delayed callback, cutoff equality, idle expiry,
lease-capped checks, release/check order, clock regression/overflow, duplicate
completion, refusal/crash, cleanup faults and quarantine/no replacement.

Run only focused HelperPolicy/Contracts tests here. Repository build/format/syntax,
specification and tooling checks do not execute broad test discovery. Windows and
Linux execution, real native/transport/retirement/security/desktop acceptance,
P3–P10 and all independent final QA remain NOT RUN/parent-owned. This document
and green pure tests do not grant issue 14 acceptance or merge eligibility.

Local result (macOS 27.2.1 arm64, pinned SDK 10.0.404 / runtime 10.0.15):
HelperPolicy **258 passed, 15 skipped, 0 failed** (22 new pure test cases);
Contracts **18 passed, 0 skipped, 0 failed**; tooling/build **23 passed**.
Locked restore, root build plus syntax guard, generated-inclusive format and
specification checks passed. Broad build.py verify/test was never invoked.

Commands used: `python3 tooling/build/build.py restore`, `build`, `format` with
`CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet`;
`python3 tooling/specification/check.py`;
`python3 -m unittest discover -s tooling/build -p 'test_*.py' -v`;
pinned `dotnet test tests/ChunkMaestro.HelperPolicyTests --no-restore --no-build
-c Release --logger trx --collect:'XPlat Code Coverage'` and
`dotnet test tests/ChunkMaestro.ContractsTests --no-restore --no-build -c Release
--logger trx`. Exact logger/result-directory arguments and execution logs are
retained in the external handoff bundle, including the bounded validation scripts.

First failures retained: the initial build syntax guard and formatter rejected
compact new properties/array initializers and import order; corrected in owned
files. An initial Contracts command named the nonexistent ProtocolContractTests
project and failed before execution; corrected to ContractsTests. First deadline
mutant was detected; the first cancellation mutant failed compilation (an unused
field), so it was repaired in a new disposable copy and then behaviorally detected.
No original design/review or historical failure evidence was modified.

Two targeted negative controls in external disposable source copies deliberately
weaken deadline equality and cancellation-request admission. Each runs only
PolicyWorkerArbiterTests with a 180-second command bound and locked restore;
each is detected by failing behavioral assertions. These are diagnostic controls,
not a mutation score, native campaign or independent QA. Production guards stay
intact. Final validation commands use a 240-second bound per command.

Diagnostic coverlet results for the new types (line / branch):

- PolicyWorkerArbiter: 99.22% / 96.37%.
- PolicyWorkerFrame: 92.30% / 100.00%.
- PolicyWorkerProtocol: 100.00% / 96.07%.
- PolicyWorkerReceipt: 100.00% / 100.00%.

Coverage excludes native proof and is not a merge gate.

## Integration fixture scheduling correction

The initial Windows authority partition at `9ddc67c` recorded 111 passing cases
and one failure: `BarrierOrdersBothAdmissionAndRevocationWinners(2, true)`
timed out at its ten-second barrier. The record does not establish why that
hosted worker missed the rendezvous. No permission or authority assertion failed.
The original job log and raw TRX remain retained as failure evidence.

An isolated process using the unchanged request implementation and a deliberately
occupied one-thread pool reproduced the scheduling dependency: the `Task.Run`
worker missed the barrier, while a dedicated worker completed the same
admission/revocation assertions. This is a controlled scheduling counterexample,
not a reconstruction of the hosted runner's exact state.

The four blocking barrier-worker sites in `OwnerAuthorizationOrderingTests` now
use dedicated long-running tasks with the default scheduler. Barrier phases,
the ten-second timeout, all authority assertions and the production request code
are unchanged. No retry or timeout increase is used. This adds one existing test
file to the six-file model patch; rollback also restores that fixture file.
Windows execution of this correction remains pending until hosted CI proves it.
