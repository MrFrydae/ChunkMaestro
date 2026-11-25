# Private original measurement — issue14

This is an unqualified, stateless snapshot primitive with no shipping caller. It
implements only the reviewed pre-Reserve measurement boundary. It does not acquire
an original, authenticate its owner, establish liveness, reserve a generation,
start a Worker, publish policy, or implement V2 journal composition.

Requirements: Q9/Q20–22/Q25/Q31/Q150–151/Q158/Q162/Q164/Q168/Q174/Q189–195;
directive 2.3/5.3/13/17. Coverage is diagnostic under the later owner testing
policy. The sealed design and independent SECURITY DESIGN PASS establish design
review only; three new implementation FINAL verdicts remain parent-owned.

`cm_escrow_measure_original_v1` adds one fixed 24-byte LP64 result with device,
inode, flags at offsets 0/8/16 and architecture/normalization/source/reserved at
20/21/22/23. Supported native profiles are Linux x64 and arm64. Source1 is a claim
under protected-holder preconditions, never acquisition authentication. Only the
compiled architecture's raw O_LARGEFILE is removed; remaining flags must be exactly
2 or 0x802. Null output and negative inputs cause no syscalls. The borrowed
original is never closed or mutated. The temporary CLOEXEC duplicate is closed
once, including on errors; first failure wins and any refusal leaves all output
bytes zero. Output is published only after successful duplicate close.

The private managed boundary pins the SafeHandle across extraction, dispatch and
validation. It validates the actual process architecture, layout and all profile
fields, copies immutable facts, releases in finally and sanitizes exceptions.
Pre-call cancellation dispatches zero times. Dispatch always drains. A final
cancellation observation after SafeHandle release refuses a usable result,
including cancellation requested during release. That check is the completion
boundary: later cancellation cannot revoke copied facts and remains the consuming
owner's responsibility. This supplies no atomic admission/send guarantee. Missing old-DSO entry points
refuse without a fallback. The existing fixed resolver and loader qualification
requirements remain. No native callback table or persistent measurement handle
is introduced. Fake ABI implementations exist only in tests.

A future consuming owner must provide non-reentrant exclusion, original retention,
alias/mutation/close controls, generation poison and atomic durable transitions.
The static operation cannot enforce that protocol or repair a poisoned generation.
A retained dead pidfd may measure successfully. Endpoint equality cannot detect
change-and-revert. At later Bound, Agent flags2 versus0x802 drift must refuse in
both directions; that comparison and Worker0/Agent1 integration are not wired here.
Existing journals, records, recovery and coordinators remain untouched.

## Validation and remaining gates

Local macOS arm64 evidence uses SDK10.0.404, locked restore, Release solution build,
generated-inclusive repository formatter, syntax guard, 205-source specification
check, selected helper contracts, TRX and diagnostic managed coverage. The external
handoff records exact commands, exits, input/source hashes and first failures.
Extracted actual C functions with inert modeled Linux types/syscalls were compiled
plain and ASan/UBSan under both profile constants. Compiled behavioral mutants
exercise omitted close, borrowed close, retry, early publication and weak flags.
A separate compiled managed control detects omitted AddRef. These controls provide
compiler/model evidence only, not Linux ABI, kernel, native sanitizer or installed
package proof.

The Linux runner exits77 on macOS. New isolated Linux tests cover ordered faults,
zero output, first-error precedence, exact close counts, main/thread-leader and
nonleader refusal, dead retention, ordinary descriptors and stable descriptor
sets. Both real x64 and arm64 plain/ASan/UBSan runs remain NOTRUN locally. Managed
real-ABI tests remain explicit skips. Parent must run those jobs and inspect exact
ELF exports, binary hashes, transitive libsystemd versions and protected loader
lineage. The original handoff retains its unapplied export-inventory proposal.
The lead correction includes a workflow guard that inspects all externally
defined dynamic symbols and rejects every non-strong-text type before comparing
the exact eight names. Inert workflow tests reject extra strong, weak, IFUNC and
object exports; they do not prove actual Linux ELF contents. Existing risk
prefixes classify all implementation paths as high risk.
No package/project/dependency changes are part of this patch.

All eleven product gaps, G1–G6, ENODATA, protected ancestry, pre-CLR/TSYNC/old-ring
exclusion, whole-Agent/all-descendant closure, callback/free and cancellation-send
atomicity, authenticated manager and qualified acquisition remain unresolved.
There is no product/runtime/issue14/global merge acceptance. Current CI/bot/merge
status is lead-owned; local model evidence makes no native acceptance claim.

Rollback before integration is removal of this uncommitted owned patch. There is
no persisted measurement state or operational generation to migrate. A future
operational generation requires qualified drain/quarantine, never journal reset,
rollback of durable start consumption, PID reconstruction or replay.
