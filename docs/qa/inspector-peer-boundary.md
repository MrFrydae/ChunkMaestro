# Internal Linux peer facts boundary — issue 14 remains OPEN

Source: Q9/Q11–13/Q20–22/Q31/Q151/Q158/Q162/Q168/Q189–195 and the
2026-11-23 owner testing override (scores diagnostic, meaningful safety evidence
required). This is the next native foundation on accepted main
`534e00fac119df24fc6baf08715a123957f5aa22`. Corrected inspector design PASS
resolves the original FD design contradiction only at design level; the original
FAIL remains preserved. No G1–G6 qualification, ACL visibility, helper acceptance
or global product acceptance follows from this primitive.

`peer.h` is internal native source, with a hidden query symbol and native
`pid_t`/`uid_t`/`gid_t` fields. It is not a managed ABI. `cm_peer_query` returns
Facts, Unavailable or Invalid with raw internal errno (zero for semantic/shape
refusal), and zeroes writable output on every failure. NULL output is Invalid.
Only Facts permits use of the fields. Caller must exclusively own/serialize the
supplied FD for the full call, with no concurrent close/reuse/cancellation by
close. The primitive neither retains nor closes it. No heap allocation, payload
read/write, socket creation/connect, filesystem query/mutation or authorization
setter exists. The synchronous syscalls have no claimed hard deadline; a future
coordinator must bound/supervise work without violating descriptor ownership.

Checks require SOCK_STREAM, non-listening state, bounded local/peer AF_UNIX
addresses, connection and exact SO_PEERCRED length with positive PID and mapped
UID/GID. Unnamed/abstract connections can yield facts; they do not prove a
pathname or systemd launch. pid=0 (possible namespace invisibility), negative PID
and UID/GID sentinel -1 refuse conservatively. UID 0 is ordinary credential data,
not automatic acceptance or refusal. Other namespace mappings/overflow IDs may
look ordinary: facts cannot qualify namespaces. No namespace/privilege changes
are made by tests. Same-UID processes share identity, inherited endpoints retain
connect/listen/socketpair-time facts, UID reuse and hostile root remain policy limits.
These are not current identity, executable attestation or helper authentication.
There is no expected UID parameter or decision. SO_PEERCRED must precede payload
access when a future caller composes this primitive; no integration exists here.

## Reproducible checks

Run `sh tests/ChunkMaestro.InspectorNativeTests/run.sh` as an ordinary Linux user.
The existing metadata kernel/control tests, required sanitizers and complete-FD
leak negative remain unchanged, followed by plain and ASan/UBSan peer builds.
Compiler/sanitizer failures are fatal. Existing CI's stable context/artifact and
180-second outer supervision are unchanged; the parent owns fresh Linux CI.
macOS exit 77 means NOT RUN, including all kernel and sanitizer cases.

The peer kernel suite uses an AF_UNIX socketpair and an owned private temporary
pathname listener/client with a forked child. It checks exact PID/UID/GID,
inherited endpoint credentials, queued sentinels unconsumed by queries and FD
usability. It rejects negative/closed/file/listening/unconnected/datagram/
seqpacket/TCP descriptors, repeats queries 1000 times and compares complete
`/proc/self/fd` descriptor-number snapshots before/after, excluding only the
scanner's own FD. This detects leaks, not every possible resource misuse; no
retained production resource is created. Fixtures use only current identities.
Ordinary listener facts cannot stand in for the future real systemd listener
proof. No wrong-UID denial/authentication claim is made.

A separately compiled object remaps only three query syscalls, using a test-only
header. Each query stage receives EINTR/EACCES/ENOPROTOOPT, short/oversized lengths;
credential controls include zero/negative PID and invalid UID/GID. Assertions
check exact outcome/errno, deterministic failure fields and no retry/fallback.
These doubles prove control flow, not kernel identity. A disposable copy with
the stream guard removed must fail the refusal assertion. This is one diagnostic
sensitivity check, not a mutation score/campaign. A separate production-object
import allowlist permits only getsockopt/getsockname/getpeername, memset, errno
access and the compiler's exact stack-protector failure helper. Stack protection
is explicitly enabled for the inspected object. Payload/network creation/exec/
ioctl/mutation and test imports remain excluded; a compiled `recv` object must
be rejected by the same allowlist without executing that object.

## First Linux execution and test-header correction

At signed head `73219a708752990f49f3402eb862e333c6c633a1`, Linux CI run
`45313662951` passed all four existing metadata variants, its descriptor-leak
control and the plain peer kernel fixture. The injected peer object then failed
strict compilation: command-line function remapping renamed glibc's GNU
transparent-union socket declarations before the pointer-based test declarations.
Original log and artifact `14884007719` remain retained. No passing injected or
sanitized peer execution is claimed from that run.

The test-only header now loads the real system declarations first and remaps
calls only under `CM_PEER_INTERPOSE`, used solely for the injected production
object. The separate controls translation unit retains real libc calls. No
production C source, assertion, strict warning or sanitizer option is removed.
See [glibc socket declarations](https://raw.githubusercontent.com/bminor/glibc/release/2.39/master/socket/sys/socket.h).
The same runner's metadata production object included `__stack_chk_fail`;
the peer import audit explicitly accounts for that compiler safety helper rather
than disabling stack protection. Fresh Linux execution remains required.

## Delivery and remaining work

Local macOS shell/static/syntax evidence and command outcomes are in the external
delivery report. No Linux execution, ASan/UBSan result, real Debian/systemd proof,
Windows native run, independent implementation QA or CI PASS is asserted here.
Parent must run fresh Linux CI and all three independent QA roles against the
integrated exact head. Existing Contracts, metadata APIs and ENODATA semantics
remain unchanged. No other path is required for this bounded primitive; no
unapplied integration change is proposed. Future callers/policy/coordinator,
helper/client wiring, root/systemd/namespace qualification, framing and request
parsing are separate reviewed work. Rollback removes these peer source/tests
and the appended harness section; there is no deployed state to undo.
