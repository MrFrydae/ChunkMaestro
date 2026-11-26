# Private V2 recovery and consuming owner

Issue 14; authored parent `3ea45c4b1ae4e396f647f017bfff5d0f44c2ce87`.
Integrated on accepted PR39 base `29342f9e3e78f64625c24e8f9da52d2c25d78833`.
This is an internal, **UNQUALIFIED model**, with actual BCL temporary-file journal
storage. It has no production factory, caller, native implementation, qualification
conversion, V2-to-V1 adapter, schema/ABI change, or new dependency. V1 and the V2
codec, transitions, journal, and original tests remain unchanged.

Requirements: Q9/Q14/Q20–22/Q25/Q31/Q149–151/Q153/Q158/Q162–164/Q168/Q174/Q189–195,
the source's later testing policy, and directive sections 2.3/5.3/13/17. The sealed
recovery design (manifest `cbe57ba86c98afb7b751fa8e7e0623da895298971452fa9adf995c6a1bc2dfce`)
and independent security DESIGN review (manifest
`a94b02ff7c4c85e8459f3e11cb47927b72c1ec2aac2339498903bce4af291e1e`)
are design inputs only. Their reference assertions do not establish implementation
or native acceptance.

## Behavior

The sole consuming owner claims its journal instance before constructing recovery.
Its monitor serializes operations; caught nested entry poisons outer completion.
A fresh reservation requires inline prior-owner/process/FD and manager boundary
observation, then original Agent measurement with explicit presence1 metadata.
Bound requires independently supplied Worker correlation and native Worker0/Agent1
values matching the persisted Agent and acquisition profile. Storage Agent0/Worker1
is a separate enumeration. Full current keys include both six-field identities and
all six metadata fields. Store snapshots copy immutable entry scalars and retain
no caller-owned array/list aliases.

Full-record CAS, directory-sync seam, and exact journal readback precede each
phase-dependent effect: StartAttempted before start, Bound before upload, and
fresh exact-two inventory before Armed/EverArmed and publication. Attempt loss
never refunds start/upload/publication. Reopened nonempty states, including an
unused Occupied record, are retire-only. Agent-only state never fabricates Worker
or calls a two-role native boundary. Model authentication/qualification claims
cannot be converted to production authority.

The fixed absolute BOOTTIME deadline expires at equality. Pre/post port guards
prevent later effects after expiry, caught reentry, or storage ambiguity. Ordinary
concurrent cancellation linearizes when it acquires the monitor: it cannot revoke
an already-entered inline send. These checks do not establish native check/send
atomicity, watchdog containment, bounded physical cleanup, or real cancellation
latency. Ports must complete inline; no asynchronous completion permits exist.

Retirement requires common543 plus early32 (575) or EverArmed64/128/256 (991).
Each later real fault advances the fault revision, retains the first failure,
and erases proof. None retains the prior fault while clearing proof. Proof must
match the complete current key. Durable Retired precedes cleanup, which permits
only the exact current remaining acquired-role subset. Each observed name is
removed once; lost acknowledgement requires a fresh inventory/proof reconciliation.
A barrier and new exact-zero count/dump under stable holder/configuration precede
retained-metadata Empty. No blanket cleanup is available.

Empty commit remains inside the entered owner monitor. Finishing consumes close
before the sole witness-close port, and only successful close yields Drained.
Close failure poisons without retry, Empty retirement, reservation, or lock release.
A later Empty owner still requires independently qualified previous-owner and
manager closure. Trusted callers retaining the original journal reference can
explicitly dispose it; this model encapsulation limit is not hostile-process
exclusion. The journal remains held throughout normal owner closure.

Storage ambiguity stops later effects and commits on that instance. First operation
and subsequent cleanup/storage exceptions remain separately inspectable through
internal diagnostics. Explicit model refusals use stable categories; preserved
port and storage failures need sanitization at any future public boundary. Cleanup failure
cannot replace the first operation error or imply success.

## Executed model validation

macOS arm64; pinned SDK 10.0.404 at `/Users/derek/.dotnet/dotnet`, Python 3.13.
Locked repository restore, full Release build plus read-only syntax policy,
generated-inclusive format verification, and specification validation were run.
The session-filtered HelperPolicy suite and Contracts suite use the actual public
internal model boundary and BCL temporary files. Native/GUI/auth/vault and broad
build.py test/verify are deliberately excluded. Raw commands, exit codes, counts,
TRX and diagnostic coverage are retained in the accompanying sealed handoff.

Tests cover all21 key-field mismatches at arm/proof/remaining/empty boundaries;
all missing proof conjuncts; both architectures and flag asymmetries; changed Agent
flags after Reserve; inventory duplicates/unknowns/native tuples/predicates; array
mutation after snapshot; reopen at every nonempty phase; start/upload/publication
lost acknowledgements; Agent-only ambiguous-work denial; partial cleanup and lost
applied removal acknowledgement; barrier/count/nonempty/holder/configuration drift;
latest-fault freshness and None; overflow; all journal barrier points across seven
transitions; sync/readback ambiguity; first versus cleanup errors; deadline equality;
caught reentry at inline boundaries; finite send/close latches and close-once failure.
Effects assert complete actual journal bytes as well as phase before proceeding.

Disposable source controls compile before running intended behavioral assertions:
metadata-blind matching, omitted durable Armed persistence, and caught-reentry
poison removal. Compiler failures/timeouts/skips do not count as detected controls.
Coverage and controls are diagnostic; no coverage or mutation threshold is applied.
These controls are not loaded by the real checkout. Existing V1/V2 journal/codec
assertions are unchanged. Independent final QA, current CI and signed integration
remain parent-owned.

## Remaining limits and rollback

All eleven product gaps remain unresolved: early-exit NO-WORK; readable-only
unreaped exit; PID reuse; Worker OOM; holder OOM; ancestor/oomd policy; real-agent
descendant/exfiltration closure; manager reexec; anchor-removal sensitivity;
pre-CLR/TSYNC/no-old-ring; real product protocol. G1–G6, ENODATA/ACL visibility,
ancestry, installer/loader provenance, original Agent measurement and independent
Worker acquisition, authenticated manager inventory, complete whole-original
handback/closure, installed holder/watchdog, native cancellation/send exclusion
and physical cleanup remain blocked. The accepted private measurement primitive
is not authenticated acquisition or authority and is not composed into these ports.
No Linux architecture/native/installed acceptance, real disk-full,
interruption/power-loss, native descriptor-close fault or desktop performance is
claimed. Injected barriers and model flags establish narrower behavior only.

Before operational use, rollback reverts the six private additions through reviewed code. Operational V2
state would require compatible qualified drain or quarantine: never reset/delete,
re-own, downgrade, restore old proof, promote temps, blanket-clean, replay, or
prescribe reboot. Q174 incident unlocking remains a separate owner-confirmed flow.
