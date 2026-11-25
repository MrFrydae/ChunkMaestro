# Issue 14 managed session escrow bridge

This private implementation binds `session_escrow.h` at frozen parent
023a59620672a647fee7d244a629c5f250d1d9b1. It supplies no executable, holder,
service composition, authentication, journal adapter, manager readback, activation
or product grant. No existing product code calls the bridge. Requirements are
Q9/Q20–22/Q25/Q31/Q150–151/Q158/Q162/Q164/Q168/Q174/Q182/Q189–195,
including the later diagnostic-only metrics decision. Architecture authority and
qualification gaps remain those in `policy-session-native-escrow.md` and its
referenced owner-approved design bundles.

`InspectorNativeLibrary` installs the sole assembly import resolver. It retains
the existing Linux-only snapshot path and safe-directory libc handling, adding
only the fixed base-directory name `libchunkmaestro_session_escrow_v1.so`.
Unknown names and other OSes refuse; there is no environment override, installer,
policy search or arbitrary library path. Fixed names alone do not establish
protected loader ancestry or qualify transitive libsystemd dependencies.
Production DSO packaging remains parent-owned and absent. The test project alone
builds the real C shared library on Linux with the public libsystemd SONAME.

The bridge imports the six real bind/metadata/upload/handback/observe/close
functions. Native identity layout is explicit: 24-byte stride, device at 0,
inode at 8, flags at 16; observation is two 32-bit integers. Role 0 is worker,
role 1 agent. Strict epoch validation precedes bind: exactly 32 lowercase ASCII
hex characters plus a separately supplied NUL. Both borrowed SafeHandles remain
DangerousAddRef-held throughout duplication, including exceptions. No numeric PID
acquisition exists in production. The owner retains only an opaque SafeHandle;
there is no raw descriptor getter.

Operations serialize on the owner lock, including close. Close drains an active
synchronous call, consumes once even on failure, and never retries. SafeHandle
finalization is fallback cleanup only. Cancellation is checked after acquiring
the operation lock and before the native effect. It cannot abort a running
native call or prove cleanup. In-flight native blocking still requires the
separately qualified holder/watchdog budget; this class creates no background
thread or dispatcher composition.

All results are unqualified. Metadata rejects zero inode, duplicate process
identities independently of flags, and flags outside normalized O_RDWR plus
O_NONBLOCK. Observation accepts only integer 0/1 and never interprets HUP as
retirement. Status values outside 0–7 refuse. Native refusals, malformed output
and uncertain exceptions poison subsequent operations. Upload consumes its attempt
before deadline validation/native execution; only pre-effect cancellation leaves
it unconsumed. Native CLOCK_BOOTTIME validation enforces the remaining 30-second
window. Success returns `SendAndBarrierOnly`, never stored/ARMED/trusted.
Handback is guarded once per process with no reset/refund, even on validation or
load failure; the native guard remains authoritative across managed load contexts.
A recovered owner is retire-only and cannot upload. Expected identity labels,
names and environment are never authentication.

Callers still owe protected durable generation occupancy before bind, original
acquisition provenance, prework gating, authenticated exact manager inventory,
sole writer discipline, protected journal ordering and retire-only reconciliation.
Constructing another owner does not refund a generation. No conversion to
PolicyFacts, journal ARMED, cleanup proof or old-work resumption is implemented.
G1–G6, ENODATA, protected ancestry, whole-agent closure and real manager/kernel
qualification remain unresolved and parent-owned.

Portable tests inject an explicitly internal managed ABI dispatch seam. They
exercise handle pinning, cleanup on uncertain output, refusal/poisoning, exact
layout, cancellation, single upload, serialized close/drain and once-only
retire-only handback. They do not interpose Linux syscalls and are not native
proof on macOS. Linux-only tests use the real library as an ordinary user with
pidfds for the test process and its owned child: retained metadata, borrowed
closure, live/dead observations, duplicate-process and ordinary-file refusal.
Those tests never call upload, notify, store, handback or service operations.
A missing pidfs/kernel/library prerequisite fails instead of becoming success.
No production C/header or existing native harness was changed.

Local Darwin arm64 validation uses SDK 10.0.404. Locked restore, Release solution
build and syntax check pass; generated-inclusive formatting is verified. Focused
bridge cases in the final full run: 26 passed, 2 Linux-only skipped, 0 failed.
Full HelperPolicy: 313 passed, 17 skipped, 1 failed. Exact commands/logs are
retained in the external sealed handoff. Its existing
import inventory rejects any new P/Invoke outside snapshot/journal, so that test
remains failing in this scoped patch. Parent must review/apply the separate
unapplied integration diff which adds exact six-entry escrow assertions without
weakening existing snapshot/journal checks. No existing test was edited.
Linux ABI execution, C compilation, sanitizer tests and independent QA are NOT RUN
here. Passing managed tests do not qualify the holder or product.

Rollback before integration removes the additions and restores the snapshot
resolver/test project changes. No live state exists. After future composition,
retire/drain occupied sessions using authenticated originals before changing
ABI/packages; do not clear occupancy or retry an ambiguous effect for rollback.
