# Private accepted-connection owner (N1)

This is UNQUALIFIED_FIXTURE_ONLY implementation, with no production caller,
executable, DSO export, managed import or authority conversion. It does not qualify
an authentic Agent. N2 native pre-CLR hosting and N3 installed launch/export,
manager and Domain correlation are absent. Self-contained distribution remains
required; a framework-dependent component-host tutorial is not a substitute.

The normative inputs are the sealed next-enforcement proposal at head
55f22c30e6a6e62aa16721636bb8eecbbb18969d (manifest
9990ec618e7e538ea005d46887aacb4ef6786cfc96252eb6ff5bf4f95d5cd9e8,
111 payloads), and the independent security DESIGN PASS at accepted head
de45c94d60c7f30ed4e71cc8a3c610f7c4b3c133 (manifest
dae3841225887a1c84c87fe4e8aec6530976e7be1dbb7fa9a8e56e6502b2251e,
124 payloads). These are DESIGN_ONLY, not implementation acceptance. The commits
are distinct with equal trees. Trace: Q9/20–22/31/151/162/168 restricted authority;
Q14/149/153/163–164 ownership/recovery; Q25/150/164 no ambiguous replay;
Q158/189/191/193/195 isolated native testing and later 2026-11-23 testing policy.
Coverage and weakened-control scores are diagnostic, without percentage gates.

## Private contract

The hidden create/attempt/measure/close/destroy functions return 1 on success, 0 on
refusal. They accept no descriptor, activation or environment-selected endpoint.
Create returns a resource-owner token and a distinct opaque lifetime token for
the same allocation. Opaque state holds directory/listener/accepted slots
initialized to -1 and a NULL candidate. Output arguments are mandatory and may not overlap each other, owner
storage or the input byte range. Invalid output combinations refuse before effects.
As with ordinary C APIs, supplied pointers must refer to valid writable typed
storage; address arithmetic cannot validate arbitrary pointers.

Create allocates bookkeeping before effects, validates an explicit length1..95
(no embedded NUL), requires canonical absolute directory spelling and euid-owned
mode0700 leaf, pins that directory and binds fixed `agent.sock`. The resulting
path fits Linux sun_path108. It does not chmod/chown/unlink existing objects.
The listener is AF_UNIX stream CLOEXEC|NONBLOCK, flags checked explicitly.
SO_RCVBUF4096 is a resource hint set before listen; Linux can double it. Backlog1
bounds the requested queue, not hostile-client availability or identity. No
credential options are needed: the unchanged candidate obtains SO_PEERPIDFD.

The owner consumes one accept4 attempt before syscall entry. EAGAIN/EINTR/ENOMEM
consume this generation. Readiness cannot refund it. It immediately claims every
successful returned integer, including0, before checking flags. The unchanged
candidate validates connected shape and retains its original pidfd. Only the
accept-derived retained FD reaches candidate acquisition. There is no raw FD or
candidate getter or user callback. Measurement invokes the unchanged candidate's
private duplicate and liveness checks without receiving data or ancillary rights.

An error-checking pthread mutex excludes synchronous operations. Reentry or
concurrent admission contention atomically revokes the generation and poisons the
outer completion. The separate lifetime token keeps storage and its mutex alive after resource
close. Caller joins ALL entrants and waiters before calling destroy with that
token. Quiescence is an explicit caller obligation; trylock cannot detect an
entrant paused outside the mutex. Concurrent destroy/free, stale pointers,
simultaneous close calls and untrusted shared descriptor-table mutation are
outside the contract. Close revokes before waiting for active work; it never
closes an FD to cancel a borrow. Same-thread reentrant close refuses and leaves
its token for the enclosing caller. Close consumes the resource token before resource release, but does not destroy
the mutex or free storage. Destroy is a separate quiescent-only operation after
close and joins; it consumes the lifetime token before freeing storage. A failed
mutex destruction retains the token, records uncertainty, and is not retried. Final operation admission follows syscall/borrow completion; a
revocation after that serialized admission cannot retroactively cancel publication.

Owner diagnostics have closed stages NONE..DESTROY, version1, sticky
first stage/errno, separate sticky first cleanup stage/errno, uncertain and
attempted bits, and a nested unchanged fixed32 candidate record. Errnos are
bounded1..4095 or0. Candidate close writes its FINAL record before candidate free;
owner closes cannot overwrite candidate primary, secondary or ownership flags.
Snapshot copying is synchronous, not atomic publication. An idempotent close on
a cleared token preserves the caller's previous snapshot. Missing diagnostics
refuses even close/destroy, leaving the applicable token intact. A valid
measurement output is cleared before live-owner admission, so a busy refusal
cannot leave a preceding snapshot in that output. Each known close token is consumed
before one close syscall, including EINTR. Uncertain candidate ownership remains
quarantined; no FD scan or guessed close repairs it. No close implies Empty,
retirement or physical/global closure.

Ordinary close never unlinks any pathname. Directory identity checks around bind
catch observed swaps, not every rename-and-revert race. There is no bindat; a
retained directory FD does not secure absolute bind. No fchdir/proc magic-link
shortcut is used. An isolated fixture owns its teardown; uncertain/unconfirmed
scratch remains for inspection. Production ancestry qualification is absent.

## Campaign and evidence

Run as a non-root user on actual disposable Linux, with a kernel supporting the
existing SO_PEERPIDFD/pidfs profile, a C11 compiler, pthreads, libsystemd.so.0,
Python3, coreutils timeout and existing candidate harness prerequisites:

```sh
sh tests/ChunkMaestro.InspectorNativeTests/run_agent_connection_owner.sh
```

Unsupported OS/root exit77 with NOTRUN. This is a required Linux CI execution,
not permission to treat exit77 as a successful native test. The runner records
source hashes, uname/id/compiler, raw logs and build products under
`.artifacts/ci/agent-connection-owner/run-*` (override CM_N1_ARTIFACTS explicitly).
It runs plain and ASan/UBSan deterministic and kernel binaries, compiles the
production source independently, and runs the unchanged candidate campaign.
GCC/gcov coverage is reported when available; absence remains NOTRUN. No installs,
auth, vault, VM/service or live-host action is performed.

N1-01: separately owned child created before listener/originals connects, writes
sentinel, remains live during accept/candidate/measure; MSG_PEEK confirms bytes.
N1-02: no adoption API; test-only candidate call asserts accepted-slot provenance.
A separate deliberate inherited-writer case observes connection-time peer PID
remaining the connector while its child writes. This expected limitation stays
UNQUALIFIED. It is not current-writer authentication or a no-alias theorem.
N1-03/09: regular-file/symlink/occupied endpoint preservation, directory replacement
at bind with explicit race limits, and post-accept replacement preservation.
N1-04/05: allocation/open/socket/flags/options/bind/listen/accept controls;
EINTR/EAGAIN/ENOMEM one-shot refusals; candidate NULL/unknown, dup/secondary/original
cleanup composition. The unchanged candidate campaign separately executes actual
candidate interposition for malformed SO_PEERPIDFD and duplicate/profile failures.
N1-06/08: reentry at accept/acquire/measure boundaries, live-owner concurrent
contention, revoke-before-drain, replay, missing outputs and cleared-token calls.
N1-07: fd0 directory/listener/accepted slots, immediate close/reuse on injected
EINTR and complete bounded local FD-set identity/flags restoration (4096 cap;
excess refuses). The unchanged candidate campaign also covers candidate fd0.

Outside-tree compiled weakened controls substitute the listener, retry accept,
ignore CLOEXEC checks, lose fd0, retry close, erase candidate secondary, publish
post-revoke, free storage at resource close, or unlink a replacement. Each must compile and then fail its intended
behavioral assertion. A compiler failure, unrelated abort or escaped control fails
the campaign. These controls are not a production mutation switch. All native
cases, sanitizers, controls and coverage are NOTRUN on the implementation Mac;
shell syntax, source/whitespace and input binding checks are narrower evidence.
The implementation author's checks are not independent QA.

## Integration, rollback and remaining gates

Exactly five new files belong to N1. Existing shipping exports11/imports10,
candidate fixed32, measurement fixed24, journal288, managed projects, inventories
and workflows remain unchanged. Accepted PR41 x64/ARM531 each and storage793 PASS
are historical baseline evidence only; they do not test N1. The lifetime regression pauses a contender after a busy trylock result, completes
resource close, verifies storage has not been freed, then resumes and joins the
contender before destroy. It observes ordering without reading freed storage.
The original five-file delivery and provider-stopped preflight remain preserved;
no completed independent verdict is inferred from that run.

Parent owns signed CI
wiring, current x64/ARM native execution, source qualification, new independent
implementation preflight and three new exact-head FINAL roles before merge.
No shared integration diff is necessary for standalone compilation.

Remove the five unshipped additions to roll back; no database/schema/live state
was changed. Failed fixture teardown preserves scratch and first failure. Unknown
ownership needs supervised disposable process closure, never a guessed FD scan,
reset of a durable record, session.lock unlink or production close/retry repair.

All eleven gaps/G1–G6, native UI/auth, installed Debian, ACL/ENODATA, protected
ancestry, pre-CLR/no-old-ring, authentic Agent/Worker, launch/export/noalias,
manager/Domain, watchdog, native check-send/cancellation, physical closure and
OpenPGP/cloud trust remain unresolved. No V2AcquiredAgent, Reserve/start/upload,
journal or package behavior is added.
