# Issue14 internal policy session journal

Implementation slice only; no product admission, native acceptance or independent QA.
Owner approval of the holder/escrow architecture supersedes the design reports'
old pending-approval language. Q9/Q20–22/Q31/Q151/Q162/Q168 constrain authority;
Q25/Q150/Q164 require durable correlation without replay; Q174 incident unlock
remains separate. Q158/Q189–195 and the 2026-11-23 override govern evidence:
coverage is diagnostic. Q182 approval is already supplied for this bounded work.

## Storage and ordering

The internal journal has one 288-byte version-1 record with SHA-256 corruption
check, exact length, reserved-byte/enum/Boolean/correlation checks, boot/epoch,
monotonic revision, original agent and worker invocation/pidfs/start/namespace/
domain labels, deterministic role+epoch escrow names, attempted-start consumption,
ever-armed marker, first failure, latest fault revision and retirement proof
revision/conjunction summary. SHA-256 is NOT authentication. Domain GUIDs are
adapter correlation labels, not an assertion that a cgroup has that native ID.
The future adapter must bind labels to exact actual manager/cgroup identities.

`session.lock` is never unlinked. FileShare.None nonblocking exclusion is for
cooperating processes. It does not qualify protected ancestry, root ownership,
symlink safety or an adversarial filesystem. All calls on one recovery instance
must be serialized by its future holder. No product adapter exists here. A
caller must pass an already-qualified fixed directory; do not accept user paths.
New-install initialization requires an internal install adapter, is explicit,
and refuses existing state. Ordinary missing/corrupt/newer/oversize reads poison
the live journal and cannot create empty state. No Boolean trust factory exists.

Each commit compares the exact current record and revision, writes a unique
same-directory temporary, flushes file data to disk, atomically renames, requests
directory synchronization through IPolicyJournalDurability, and reads back exact
bytes. No next action returns before this sequence finishes. A failure poisons
the instance, returns no action, and leaves temp files for diagnosis. Never use a
temp file as recovery input. After rename, an error is ambiguous even if new
bytes can be read: no further action on that live instance. Explicit reopen and
qualified reconciliation are mandatory. FileStream.Flush(true) alone does NOT
commit rename/directory metadata. Do not delete the journal as a clearing step.

The integrated default adapter requires Linux x64/arm64 and calls exactly libc
`open`, `fsync` and `close`. Directory open is read-only with O_DIRECTORY,
O_CLOEXEC and O_NOFOLLOW; a final symlink refuses. Every error refuses, including
close failure; close is never retried. Parent ancestry and actual filesystem
crash durability remain unqualified. macOS/Windows refuse before creating a
journal. The existing native-surface security assertions retain their snapshot
checks and add an exact internal type/library/three-entry-point allowlist with
restricted search flags. The shared native loader recognizes only the existing
snapshot shim and this exact Linux libc name. No package/project/lock change is
required. Native Linux integration tests exercise real temp-directory sync,
missing/file/symlink targets and initialization after the shared loader is active;
they remain NOT RUN on macOS, where only platform refusal is executed.

## Recovery boundary

Fresh explicit reservation persists Occupied. The original live reservation
alone consumes StartAttempted before returning one unqualified start action.
Reopen never returns start or gate actions, including unused Occupied. BOUND
records both originals before escrow. Exactly two role-distinct entries must
match original identity, epoch name, non-thread pidfd/CLOEXEC/no-poll metadata.
Invalid/partial escrow confirmation durably enters Retiring; send/barrier success
alone is not accepted. ARMED persists before returning one gate action. A crash
at ARMED always takes the strong proof path, even before publication. The holder
must apply actual cancellation/deadline/admission serialization before acting;
this storage boundary supplies no clock or authentication and performs no
manager, worker, publication, stop/kill or escrow effects.

Retiring precedes cleanup. Pre-ARMED proof requires gate revoked, sole launch job
terminal, no pending jobs, protected empty domain, all owned and original-agent
references closed, and trusted startup closure excluding input/rights/children/
old async resources. Post-ARMED additionally requires original worker HUP,
same-generation terminal state and all-thread descendant/async closure. These
are typed INTERNAL UNQUALIFIED adapter inputs, not worker/agent Boolean claims.
Agent closure requires independently qualified live cooperation or original
agent death plus descendant/exfiltration closure; a new agent cannot attest it.

Retired persists proof before returning exact-name removal action. Reopen here
is cleanup-only. Partial remaining inventory must contain only exact originals;
unknown entries refuse, and exact empty readback is required before Empty.
Fault observations must call Retire: it preserves first failure, advances fault
revision and invalidates earlier proof even after Retired. Fresh proof at the
new revision is necessary. Empty retains prior correlation/proof; a new epoch
must differ. The qualified adapter must allocate fresh unpredictable epochs for
new authenticated requests, never accept caller-selected/replayed epochs.
Cross-boot reconciliation is deliberately unavailable, including Empty from an
old boot: no boot-ID text alone clears occupancy.

## Verification and limits

External sealed handoff holds exact commands, exits, TRX, raw logs, diagnostic
coverage, source/runtime hashes, complete new-file patch and unapplied native
diff. The owned tests use real temporary files, flush/rename/readback, lock
contention, bounded corruption and newer schemas, stale evidence, both retirement
conjunctions, cancellation, partial escrow/removal, and 28 barrier-by-transition
interruption schedules. Write interruption occurs after the first half of the
temporary payload. Injected IOException models disk-full/I/O refusal; no real
disk was filled. Reopen is a process-lifetime simulation, not power-loss proof.
Test-only directory synchronization is intentionally unqualified. Lock contention
also runs a separately launched, method-filtered .NET test process against the held
temp journal, then confirms the unchanged record and successful reopen after release.
Linux native platform qualification remains NOT RUN locally. No native Linux fsync or real holder/systemd,
kill -9, x64/arm64 or power-loss campaign was executed on this macOS checkout.

Preserve no activation/counter security, before-CLR setup/enter/register io_uring
denial and no inherited rings, TSYNC before input, exact credentials/rights,
original-generation retirement, no replay and cleanup invalidation. Existing
arbiter/native/config fixtures are unchanged. G1–G6, ENODATA, policy-readable
acquisition and protected ancestry remain unresolved. Storage cannot establish
reaping, authentication, qualified escrow or routine-fault recovery by itself.
No automatic reboot, generic root runner, incident unlock or permission repair.

Rollback before integration removes only the six new owned files; no deployed
state exists here. Once a future adapter writes a journal, preserve it and all
escrow on rollback; never downgrade unsupported schema, reset occupancy or
initialize over missing/corrupt operational state. Parent owns native adapter,
installation, holder integration, qualified boot reconciliation and independent
exact-head QA. Ordinary faults on a qualified healthy profile should retire
locally; kernel/manager/storage uncertainty may quarantine. Full issue acceptance
remains unclaimed.
