# Issue14 native session escrow boundary (implementation, not acceptance)

This slice implements `session_escrow.h`/`.c` only. It creates no executable,
privileged service, activation, worker, journal, incident unlock or product grant.
Owner-approved holder/escrow design is bound by manifest
`71835510517582b4e22dccac08737837d3aad929e4bef71fc966d781ac3c77c0`;
independent **design-only** PASS is bound by
`24401bfef3b302dab3db575c77d94c8f764abd3ceebf8e1f3834486591c25432`.
Original FAIL remains preserved in those inputs. Neither verdict is implementation
or native acceptance. Requirements: Q9/Q20–22/Q25/Q31/Q150–151/Q158/Q162/Q164/
Q168/Q174/Q182/Q189–195, with the later diagnostic-only metrics decision.

## ABI and ownership

`cm_escrow_bind` duplicates two already independently authenticated original
non-thread pidfds. It never opens a numeric PID. Worker is role 0, agent role 1.
Caller must serialize all operations and retain exclusive descriptor ownership
while duplicating. It must bind fixed invocation, initial namespace, boot, epoch,
start, cgroup and original acquisition provenance independently. This library
cannot distinguish an independently reopened same-task pidfd from the original
sole trusted acquisition; caller must never substitute one. No current-PID or
fdinfo text is used. Duplicate process identities, thread flags, ordinary files,
legacy anon-inode FS and unqualified pidfs metadata are refused. Duplicate
process rejection compares device/inode independently of status flags. Exact
per-role handback correlation still compares flags as well as device/inode;
matching a role's metadata cannot prove independence from the other role.

The handle owns two CLOEXEC duplicates. Borrowed originals remain caller-owned.
`metadata` returns device/inode/normalized F_GETFL in worker/agent order for the
protected journal and trusted manager adapter. Device is encoded Linux st_dev;
manager dump major/minor must be converted/compared correctly. Flags remove raw
Linux O_LARGEFILE (x64 octal 0100000, arm64 octal 0400000), as systemd does, including on 64-bit glibc.
`observe` returns readability and HUP separately; neither is a cleanup receipt.
Close consumes the handle even on error, closes each FD once and never retries
Linux close on EINTR. Errors use a fixed enum/string, with no paths/values or
native exception messages. First operational error sticks until close. Null or
invalid setup fails without a handle. Caller must pass empty output storage.

`upload` sends exactly one descriptor per role with distinct bounded
`cm-worker-<32 lowercase hex epoch>` / `cm-agent-<epoch>` names,
`FDSTORE=1` and `FDPOLL=0`, sender PID 0, unset-environment 0. It preserves both
owned descriptors. The single upload attempt is consumed before any call,
including an expired deadline. It never retries EINTR or a partial upload.
A successful barrier only reports preceding processing, including rejection.
There is deliberately no stored/ARMED/accepted result in this ABI.

Deadline is absolute CLOCK_BOOTTIME microseconds, equality is expired, maximum
remaining window 30 seconds. Remaining time is checked before each send, before
and after barrier. This is an observation budget, not a guarantee of bounded
kernel execution: public libsystemd notify may block, and the barrier timeout
uses libsystemd's clock semantics. Run off the dispatcher under a separately
qualified holder lease/watchdog; never credit timeout as physical cleanup.

`handback` is startup-only, once per process, same protected holder service.
After recognizing a canonical LISTEN_FDS count 0–16 it assumes ownership of that
entire inherited contiguous range, closing it on refusal. Invalid/unbounded
counts leave inherited ownership to startup quarantine. Exactly two entries,
canonical current LISTEN_PID and bounded names are required before allocating
through libsystemd. Both orders are allowed. Library return count, role names,
CLOEXEC, pidfs type, non-thread flags and both protected expected identities are
checked; duplicate roles/identities, wrong-role/type/stale/truncated/extra entries
refuse. sd_listen_fds_with_names uses unset=1; bounded-declaration refusal also
clears listen variables. No retry can close reused descriptor numbers. A returned
recovery handle cannot upload. No names/environment alone authenticate PID1 or
same-unit origin. Parent must exclude other inherited FDs and establish protected
loader and manager provenance before this call. Do not call from an agent.

## Public runtime dependency and provenance

No systemd headers, implementation, internal libsystemd-shared code or binaries
are vendored. Only three public prototypes are declared. Link to public
`libsystemd.so.0` using `-Wl,-l:libsystemd.so.0`. ELF symbol version references
require sd_pid_notify_with_fds@LIBSYSTEMD_219,
sd_listen_fds_with_names@LIBSYSTEMD_227 and sd_notify_barrier@LIBSYSTEMD_246.
Missing SONAME/version fails linking/loading; there is no fallback. Parent must
qualify actual Debian package/source/security origin, hashes, architecture,
ELF SONAME/NEEDED/version definitions, DSO loader ancestry/environment, and
transitive LGPL distribution/notices obligations before packaging. Symbol
presence does not establish a qualified systemd manager/kernel profile.

Primary ABI sources: [public systemd v257 prototypes](https://github.com/systemd/systemd/blob/v257/src/systemd/sd-daemon.h),
[versioned exports](https://github.com/systemd/systemd/blob/v257/src/libsystemd/libsystemd.sym),
[notify/listen implementation](https://github.com/systemd/systemd/blob/v257/src/libsystemd/sd-daemon/sd-daemon.c),
[manager dump normalization](https://github.com/systemd/systemd/blob/v257/src/core/dbus-service.c),
[Linux v6.12 pidfs](https://github.com/torvalds/linux/blob/v6.12/fs/pidfs.c),
[pidfd thread flag](https://github.com/torvalds/linux/blob/v6.12/include/uapi/linux/pidfd.h),
[pidfs magic](https://github.com/torvalds/linux/blob/v6.12/include/uapi/linux/magic.h).
Design bundles retain source snapshots and hashes; external handoff binds these
unchanged inputs by manifest rather than recursively copying old bundles.

## Tests and parent integration

Run as an ordinary user on ephemeral Ubuntu x64/arm64 with a qualified pidfs
kernel, C compiler, sanitizer runtime and public runtime library already present:

```sh
sh tests/ChunkMaestro.InspectorNativeTests/run_session_escrow.sh --controls
sh tests/ChunkMaestro.InspectorNativeTests/run_session_escrow.sh --kernel
# Default --all runs both; every selected suite runs plain and ASan+UBSan.
```

No dependency install or PID1 contact occurs. The kernel suite forks its own
unprivileged child and uses real pidfd_open to test retained original identity,
live/readable zombie/HUP after reap, borrowed-original closure and thread/type/
duplicate refusal. It is not a PID-reuse stress test or installed manager fixture.
Control objects interpose syscalls at compilation, never through a production
injection API. They test partial duplication, interrupted stat/fs/dup/poll/close,
legacy/thread/alias rejection, partial notification, barrier failure, deadline,
sticky first failure, bounded attempts, partial/extra/rejected handback,
wrong metadata, duplicate labels, wrong PID, truncation and reordered delivery.
Omit-close and incorrect-FDPOLL controls must fail their intended assertions;
compiler failure or arbitrary crash alone is not diagnostic credit. Production
object inspection excludes control symbols. The test parent installs a 15-second
alarm; forked children do not inherit it. Current child paths are finite, and CI
adds the existing 180-second supervisor, but qualified process-tree cleanup is
not established by this checkpoint. Scores/coverage are diagnostic only, never
acceptance thresholds. Native coverage is not measured.

Parent-owned unapplied integration requirements:

- Compile session_escrow.c as a separate Linux native object/shared ABI with the
  public SONAME link above; include its header. Existing run.sh/project/solution/
  package files remain unchanged. The ordinary inspector-metadata x64/arm64 CI
  jobs invoke the standalone script with --all through the credential-stripped
  supervisor (180 seconds), preserving existing native/managed checks and names.
  Any prerequisite refusal (including exit 77) fails that step; it is missing
  proof. No auth/vault test invocation is added. Raw command output, partial build
  objects, negative logs and ELF diagnostics are retained under the existing
  .artifacts/ci/inspector-metadata/session-escrow upload path even on failure.
  The existing source.sha256 globs include all five new native/test inputs;
  the sixth addition is this documentation, bound in the external source hashes.
- Add managed bindings with exact enum/layout, opaque owned lifetime and safe
  cleanup. This ABI is synchronous and not thread safe. No root entry mode here.
- Supply authenticated manager DumpFileDescriptorStore plus NFileDescriptorStore
  exact inventory (two distinct names, mode/device/inode/flags, no unknowns),
  actual main-process NotifyAccess provenance and sole writer discipline.
  Successful send/barrier alone MUST NOT arm or publish Bound.
- Keep durable single occupancy/one start attempt/no replay and protected BOUND ->
  upload -> readback -> ARMED -> publish ordering. Recovery is retire-only.
  Commit RETIRED before exact-name removal/barrier/empty readback/EMPTY; this
  slice does not expose manager removal or a fake cleanup proof.
- Retain no automatic activation/counter security credit, pre-CLR setup/enter/
  register io_uring denial and no old rings, TSYNC before input, exact authenticated
  credentials/rights and fault-invalidates-cleanup. No HUP-only or new-agent
  assertion can replace original cleanup/no-descendant/no-exfiltration proof.

G1–G6, ENODATA, policy-readable acquisition, protected ancestry, real PID1 handback,
manager reexec/GC/anchor/provenance, normal worker/holder/agent fault retirement,
whole-agent closure and independent QA remain unresolved. Routine healthy-host
fault recovery must not require reboot; uncertainty remains occupied/quarantined.
No incident unlock, permission repair or automatic host reboot is authorized.

## Local execution and rollback

Prepared checkout: `/Users/derek/.t3/worktrees/ChunkMaestro/t3-f04efec4`, branch
`t3/native-session-escrow`, head `9d8434be5297a43bfea6d6dcf6466a60d1b48da3`.
Requested integration branch is `feat/14-native-session-escrow`; no checkout or
branch was created. All six owned files are uncommitted additions. No C# changes.
Locked restore, Release build/style and generated-inclusive format verification
passed. HelperPolicy: 260 passed, 15 skipped, 0 failed; Contracts: 18 passed,
0 skipped, 0 failed. Initial build failed with 19 NETSDK1004 errors because
assets were absent; locked restore resolved this prerequisite. No auth/vault
tests were executed.
Darwin arm64, pinned SDK 10.0.404. Native runner returns 77 NOT RUN; Linux C
compilation/plain/sanitizer/kernel/control tests and diagnostic coverage are all
unexecuted locally. This is not full issue acceptance. External sealed handoff
contains exact commands, logs, hashes, managed baseline results and limitations.

Rollback before integration is removal of these six owned additions. No host,
service, database, journal or permissions changed. After integration, drain/retire
occupied sessions using authenticated original evidence before changing ABI or
package; never clear escrow/occupancy to make rollback appear successful.

## S1 correction (parent implementation; Linux verification pending)

The original artifact preflight found that comparing status flags in the
duplicate-process guard admitted same-task pidfds with different O_NONBLOCK
settings. That original FAIL remains unchanged. The corrected guards compare
process identity separately from exact role metadata. New deterministic bind
and handback cases cover differing flags for the same process, valid distinct
processes with differing flags, and refusal of per-role expected-flag mismatch.
The owned-resource kernel suite also opens a second nonblocking self pidfd.
Those Linux cases are written but NOT RUN on this Mac. Parent portable tests
compile only the extracted comparison functions; they establish no pidfd,
Linux ABI, manager or full native acceptance.

## Standalone CI checkpoint

Integration is uncommitted in the T3-prepared isolated checkout at
`e51cabb8363687720b275e14f242c539429210db`; the original author checkout above
remains untouched. No production DSO/project binding, package, managed P/Invoke,
installed loader or holder activation is added. The proposed build integration
patch remains unapplied. Linux compilation, ABI, controls, sanitizers and kernel
proof remain NOTRUN until actual hosted execution. Existing ephemeral-runner
compiler/runtime packages are prerequisites, never installed or assumed valid.
Rollback removes the six additions and the single standalone CI step; no live
state exists. Parent owns signed shipping, ordinary hosted CI and three fresh
FINAL independent QA verdicts before merge.
