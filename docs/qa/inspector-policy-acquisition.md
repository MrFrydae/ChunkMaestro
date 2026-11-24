# Retained policy DATA acquisition foundation

This slice implements an internal Linux retained-readable snapshot and an internal
`RetainedPolicyAcquisition.RetainedPolicyDataUnqualified` lease. It enables no real
helper admission. There is no product consumer, fixed-path readable acquisition,
installer, service, transport, PolicyFacts adapter or public authority API.

The reviewed contract is the sealed issue14 acquisition design (manifest
`fd034546376ae9f9578ac5797ad1210e15849e8b1a96d603d02352bf402a8dd2`, CONTRACT
`39809c3a30c1c68790c8124acd28b22d8565fcaca36d1779868d7d589fe4e70e`). The independent
security DESIGN verdict is not implementation QA. Requirement trace: Q9/Q11–13,
Q20–22/Q31, Q151/Q158/Q162/Q168/Q189–195 and the 2026-11-23 owner testing policy.

## Implemented boundary

Native `take` consumes two distinct exclusive readable CLOEXEC descriptors via
slots, including ordinary refusal, and publishes only a complete opaque snapshot.
Invalid/overlapping slot storage is rejected before transfer. Duplicate numbers
close once. Destruction nulls its slot before closing and never retries close.
A caller retains ownership when managed platform/cancellation preconditions or
native-library resolution prevent entry into native `take`.

The leaf must already be readable and regular, have one link and 1..16384 bytes,
and share its observed device with its retained directory. Flags are inspected,
never changed. `policy.json` identity is reopened only with constrained openat2
and O_PATH; it is never opened readable by this implementation. No proc fallback,
attribute enumeration, arbitrary target, repair, execution or network operation
exists. ACL queries use only two fixed names, fixed buffers and two sweeps on the
readable descriptors. Every ENODATA remains unqualified; other errors refuse.

Two initial bounded pread passes require exact bytes/EOF, with full metadata,
ACL and path checkpoints. Subsequent operations compare to the original snapshot;
observed failure is sticky. Short reads advance actual offsets. EINTR consumes a
finite 32768-call budget per pass, including EOF; this is not an elapsed deadline.
Matching samples cannot exclude privileged ABA or prove atomic snapshot isolation.
The 128-byte facts ABI explicitly encodes LE integers, not a native struct.

Managed acquisition allocates its invalid SafeHandle before native `take` can
consume descriptors, then adopts the returned pointer without another allocation
inside the cleanup scope. This closes an allocation-failure descriptor leak found
during lead integration; genuine OOM was not induced on the owner machine.
It keeps a SafeHandle reference through facts, necessary root
numeric filtering, same-handle copy, unchanged pure Decode, exact-array SHA256
consistency and final native check. Cancellation is checked around each call and
publication. Nothing partially publishes or falls back to cached DATA. The lease
constructor is private; it has no parsed-DATA extraction or combination factory.
Lease access and Check/Dispose share one gate; Dispose waits for an active Check.
SafeHandle finalization is cleanup, not a deadline or semantic qualification.

Only tests build the shim, in the helper-policy test output. Resolution uses that
fixed absolute output path, with an explicit Linux guard; no environment, policy
or platform search is accepted. This is not protected packaging/load provenance.
The production object is separately compiled/audited without test interposition.
The broad assembly P/Invoke assertion now confines imports to the private adapter
and its generated nested types and exact ABI library/entrypoint prefix; all pure
codec/result types remain native-free. Existing no-public, no-new-friend, immutable
DATA, trust-field refusal and product-reference assertions remain enabled.

## Local evidence and limitations

Author environment: macOS arm64, pinned private .NET 10.0.404. Linux native tests,
x64/arm64 ABI execution, sanitizer and real ACL/drift/fault/sensitivity runs are
**NOT RUN** here. Native harness exits 77 on this host; that is not PASS. Only the
portable public C declaration header can be strictly compiled here. The external
author handoff contains actual build/test/format logs, first failures, TRX,
coverage and diagnostic managed mutation records. It supplies no independent PASS.

T1–T5 native tests are separate kernel/control builds: bounded byte/stat oracles,
flag/type/size/hardlink/symlink/FIFO/socket refusal, O_DIRECT capability and DAC denial, full FD sets including a high
FD, sentinel preservation, sticky drift, direct file access and parent access/default ACL encoding, deterministic
pipe-barrier entry replacement/truncation/growth/chmod/link/overwrite, every reached
stat/ACL/open failure occurrence, short reads/EINTR/exhaustion/EOF/oversized returns,
allocation/close faults, exact open flags and bounded ACL names. Guard-bypass
sensitivity asserts the intended failure, not merely abnormal termination. Native
line/branch coverage is diagnostic. Existing metadata/peer audits are unchanged.

T6–T7 include independently assembled ABI vectors, malformed lengths/fields/status,
real current-user Linux raw bridge/shape refusal, and a separately compiled test
shim with explicitly synthetic UID0 facts. Synthetic facts exercise handoff,
malformed bridge refusal, cancellation barriers in take/facts/copy/final check and
Check versus Dispose serialization. They establish no real root qualification.
No test switch exists in the production object/entrypoint. Linux-only cases are
explicitly skipped on macOS; unsupported-platform refusal itself executes.

Lead integration adds a literal independent 128-byte native vector, including
high-bit IDs, signed seconds, nanoseconds and reserved bytes; aliased output/input
and partial-overlap slot refusal; and inode/mode/UID/time drift at every reachable
stat checkpoint. These tests are authored but not yet executed on Linux.

Remaining limitations: regular leaves normally reject default ACL creation, so
positive default-ACL observations on leaves use the fixed-name injected controls;
actual parent access/default and leaf access ACL cases require a supporting
backend. Real LSM controls, privileged ABA and arbitrary hostile-stage combinations
remain outside this unqualified foundation. The existing finalizer, parallel
acquisition and full FD-set stress tests do not prove those properties. No
architecture proof or native acceptance is inferred from source inspection or
managed byte vectors.

Lead artifact preflight found a refusal-table mismatch introduced during
integration: two separately exercised modes had been appended to the 15-row
refusal list without expected results. They are now kept in their dedicated
vector/drift tests, and a C11 static assertion binds the refusal/expected counts.
A portable extracted-table ASan/UBSan check passed on macOS; the historical 17/15
mismatch fails the new static assertion. This is not Linux harness execution or
independent closure. The original preflight FAIL is preserved; final review is
still required after actual Linux execution.

## First hosted architecture run and audit correction

At `4b27617335b6acc39de26c7aac55b1da188a8f58`, workflow 45410416405
compiled and ran all four policy kernel/control variants on Linux x64, including
the literal ABI vectors and checkpoint controls. It then stopped at the production
import audit: the non-optimized object references `gnu_dev_major` and
`gnu_dev_minor`. On arm64, metadata/peer kernel and sanitizer runs completed, but
the peer audit stopped on `__stack_chk_guard` before the policy variants.
Later sensitivity, gcov and focused managed steps in those jobs did not execute.
These are partial results, not architecture acceptance.

The audits now name exactly those glibc device-extraction and compiler canary
symbols. glibc's [sysmacros declarations](https://github.com/bminor/glibc/blob/master/misc/sys/sysmacros.h)
map `major`/`minor` to `gnu_dev_major`/`gnu_dev_minor`;
[GCC 13 AArch64 options](https://gcc.gnu.org/onlinedocs/gcc-13.3.0/gcc/AArch64-Options.html)
describe the global stack canary. Stack protection remains enabled. No wildcard
or blanket helper exemption exists. Both peer and policy audits require a
separately compiled `recv` payload control to be rejected. Raw policy imports and
negative-control outputs are retained. Portable matcher tests reject network,
execution, test-control and lookalike symbol names; those tests do not establish
Linux compilation or native execution of the correction. Corrected hosted results
and all final roles remain pending. Original failed artifacts and decoded logs
are retained in the lead evidence bundle.

## ARM VSTest post-processing limitation and alternate verification

At `8878d452ffb5fa876ddec5f1ca0845e7e81f4f66`, workflow 45411018184's
x64 inspector job passed completely. The arm64 native kernel/control, sanitizer,
ABI, import, negative-sensitivity and gcov stages passed. The arm64 managed TRX
records 251 executed/passed, zero failures/skips; VSTest then failed its artifact
post-processing directory verification with errno 22. This is a failed job and
an unresolved VSTest/platform limitation, not complete CI acceptance or evidence
that the directory was safely validated. Its original artifact is retained.

Arm64 now executes the same freshly built pinned xUnit v3 assembly directly,
with theory pre-enumeration, `-failSkips`, raw xUnit XML, TRX and CTRF results.
A separate discovery pass is matched to execution by exact escaped display names
and method/case multiplicities;
duplicate discovery IDs, duplicate execution names, nonpassing outcomes, empty
runs and inconsistent summaries refuse acceptance. This does not claim stable
cross-run xUnit IDs or byte-identical display names across report formats.
The outer existing process supervisor still bounds and reaps the worker.
No test source, assertion or test filter changes; no SDK/package upgrade or
VSTest security-disable flag is used. The x64 job retains the original VSTest
coverage collector; arm64 direct-run managed coverage is NOT measured. The prior
arm64 coverage remains historical. Native coverage remains separately measured
on each architecture. Scores are diagnostic. Corrected ARM execution and final
independent QA remain pending; this alternative does not repair upstream VSTest.

## Parent Linux commands

Prerequisites: disposable ordinary-user Ubuntu 24.04 x64 **and** arm64 runners;
exact global.json SDK already provided, C11 compiler, Linux openat2 UAPI headers,
ASan/UBSan and matching gcov, accessible procfs FD enumeration and xattr-capable
private /tmp and an FD limit above 1024. Do not use root, install packages or repair permissions to pass.
No production credentials, VM/SSH/auth/vault/window activation is needed.
The CI inspector job now uses ordinary-user `ubuntu-24.04` and
`ubuntu-24.04-arm` runners, preserving the existing x64 check/artifact name and
adding a distinct arm64 check/artifact. Both execute the native kernel/control,
ASan/UBSan, negative sensitivity and ABI vector tests, followed by the focused
managed bridge suite. Each child command has at most a 180-second supervisor;
the job has a 12-minute cap and no automatic retry. The QA job depends on the
complete inspector matrix, so one failed architecture blocks acceptance.
Source/compiler/architecture/shim hashes and raw TRX, logs and diagnostic coverage
are uploaded, including failures. Existing direct safety checks remain intact.
Actual hosted results and independent final QA are still pending at this commit.

For separate x64 reproduction from the checkout with the patch applied (use the
explicit direct-runner workflow commands on arm64 for the limitation above):

```sh
set -eu
export CHUNKMAESTRO_DOTNET=/absolute/path/to/exact-pinned/dotnet
mkdir -p .artifacts/policy-acquisition
uname -a > .artifacts/policy-acquisition/environment.txt
"$CHUNKMAESTRO_DOTNET" --info >> .artifacts/policy-acquisition/environment.txt
python3 tooling/build/build.py restore > .artifacts/policy-acquisition/restore.log 2>&1
python3 tooling/build/build.py build > .artifacts/policy-acquisition/build.log 2>&1
python3 tooling/build/build.py format > .artifacts/policy-acquisition/format.log 2>&1
sh tests/ChunkMaestro.InspectorNativeTests/run.sh > .artifacts/policy-acquisition/native.log 2>&1
"$CHUNKMAESTRO_DOTNET" test tests/ChunkMaestro.HelperPolicyTests/ChunkMaestro.HelperPolicyTests.csproj --no-build --no-restore -c Release --logger trx --results-directory .artifacts/policy-acquisition/managed --collect 'XPlat Code Coverage' --blame-hang-timeout 2m > .artifacts/policy-acquisition/managed.log 2>&1
"$CHUNKMAESTRO_DOTNET" test tests/ChunkMaestro.ContractsTests/ChunkMaestro.ContractsTests.csproj --no-build --no-restore -c Release --logger trx --results-directory .artifacts/policy-acquisition/contracts > .artifacts/policy-acquisition/contracts.log 2>&1
python3 tooling/specification/check.py > .artifacts/policy-acquisition/specification.log 2>&1
```

Preserve first failure logs before retrying. Upload both `.artifacts/policy-acquisition`
and `.artifacts/policy-snapshot` (native control, sensitivity and gcov reports),
exact patch/source hashes, built shim hashes, compiler versions and architecture.
A testhost timeout is a failure/unresolved worker, never a cancellation PASS or
mutation kill. Run no broad `build.py test`/`verify` on this Mac: those discover
unauthorized native credential/vault/owner-evaluation fixtures. Use the safe
project-specific alternatives above. Any native compile failure is a real first
failure to repair, never a waived prerequisite.

## Remaining admission blockers and rollback

Initial safe readable acquisition, protected ancestry, per-object namespace/mount/
idmap, G1–G6 source/LSM/ACL/package/launch qualification, maintenance epochs and
bounded worker completion remain blocked. Numeric UID0 is a necessary filter only.
Future integration must stop admission, disable activation/restarts, drain and
dispose before changes, then requalify/new epoch. Interrupted maintenance remains
disabled. This foundation grants no real helper admission.

Rollback removes the owned new primitive, adapter, tests and narrow wiring through
a reviewed patch. There is no migration, deployed state or permission change to
reverse. The external handoff preserves the complete patch for restoration. All
three FINAL independent roles and actual hosted architecture evidence remain
parent-owned before merge; this author cannot supply them.
