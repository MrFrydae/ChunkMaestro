# Private V2 journal storage — unqualified

This issue14 subslice adds only a bounded file reader, serialized CAS writer and
claim-transition guard. Parent is frozen at
`d9f574f98fcc515d70c481c7ae6b5b1beedb47a6` for the original implementation;
integration base is accepted PR37 main
`b09c844603056f98ecf59d5f0ce6de44ae41e63d`, with an identical tree.
Integration, current CI and three new independent FINAL verdicts belong to the
parent. There is no production caller,
factory, recovery/coordinator V2, receipt, native adapter or activation.

Requirements: Q9/Q14/Q20–22/Q25/Q31/Q149–151/Q153/Q158/Q162–164/Q168/Q174/Q189–195,
the later diagnostic-only testing policy, directive 2.3/5.3/13/17. Governing design
manifest `27dd835330591d31d17c3bcd65dd21087123d03a11900825c9b452a613d2e555`
and independent DESIGN PASS manifest
`fdb3e0fa477995dd229ba436a6a41a6fc73c1b33ee62ff042c14c6f09ba24209` govern;
codec packet manifest is
`b560ddc88c38527bf3b83ffbfff4a6364adc41affe2990dd719ecf1acadae037`.
The external handoff records complete input/source verification and patch hashes.

## Storage and refusal contract

`PolicySessionJournalV2Unqualified` opens the existing cooperative `session.lock`
with exclusive sharing and never unlinks it. A serialized explicit lifetime owns
all reads, writes and disposal. Callback reentry, including attempted disposal,
poisons even when the callback catches the refusal. Concurrent disposal waits
for the current operation. Dispose closes the exclusion once, marking disposed
before close; an ambiguous close is never retried. Journal disposal proves no
native witness closure or prior-owner death.

Reads allocate exactly 288 bytes only after checking length, require EOF and
unchanged final length, and use the existing V2 codec. All V1 phases, including
pristine/retained Empty, future versions, corruption, missing and changed-length
files refuse. Existing files are never initialized or converted. Initialization
requires the existing abstract install adapter; absence is not authority. Read
errors cannot be hidden by `File.Exists`. No Boolean trust factory is introduced.

Commit compares the entire expected immutable record (including every native
metadata field), checks revision with checked addition and validates both record
shapes and the transition before creating a temporary. Stale/invalid requests
poison this implementation's lifetime and perform no replacement. Writing uses
unique same-directory CreateNew, first 144 bytes, Write barrier, remaining bytes
(including native metadata), Flush barrier, Flush(true), file close, Rename
barrier, atomic File.Move replacement, DirectorySync barrier, existing directory
fsync hook, then exact decoded-record readback. Readback does not replace fsync.
Any exception poisons; original, lock and interrupted temporary files are preserved.
No temporary is ever selected as recovery input. After rename, visible new state
can coexist with unknown durability. Reopen returns only serialized claims and
supplies no start, publication, replay or recovery action.

The existing Linux durability implementation refuses unsupported platforms on
write, with no new imports. Path ancestry, ownership and hostile replacement are
not authenticated. Same-length hostile rewrite and recomputed checksum are not
detectable provenance. This is cooperative exclusion only.

## Narrow serialized transitions

Within a generation boot, epoch, Agent identity, acquisition profile and Agent
flags cannot change. Worker absence becomes a complete distinct Worker with flags
only at StartAttempted -> Bound; the Agent remains identical. Afterwards Worker
identity and all metadata persist through Retiring, Retired and retained Empty.
AttemptConsumed and EverArmed follow start/arm and cannot be reset within the
generation. Ordinary progression is Occupied -> StartAttempted -> Bound -> Armed;
any non-Empty phase may enter Retiring, including repeated faults and invalidation
of an existing Retired proof. Retiring -> Retired requires a current-revision proof
claim and the codec's full conjunction. Retired -> Empty retains the proof and all
original metadata. FirstFailure survives; a later fault stamps the new revision
and invalidates proof. A new proof must follow the latest fault. These checks
validate claims, never authenticate observations.

Only Empty -> Occupied can replace generation metadata, using a distinct nonzero
epoch and complete Agent-only claims. A future qualified owner must enforce
prior-owner closure, finishing exclusion, current boot/architecture, installation,
manager/store and acquisition authority before calling this storage model. The
journal cannot supply those facts. In particular committing Empty and disposing
the journal do not solve close-once local witnesses or permit owner reuse. No
flags/defaults are synthesized and no inventory is used to reconstruct originals.
No cross-boot or within-generation profile conversion is permitted.

## Evidence boundaries and rollback

Tests exercise actual temporary files and the internal storage methods. Fake
TestDurability/ModelDurability deliberately omit directory fsync; their barrier
and close-failure injections are model evidence only, not real disk-full,
power-loss, filesystem crash or native close qualification. File Flush(true) is
executed, but directory fsync is not. The production durability test commits
pristine Empty and Agent-only Occupied records with the existing directory-fsync
hook on Linux, and requires unsupported-platform refusal before state/temp
creation elsewhere. Only the refusal branch has executed locally on macOS,
without native calls; the Linux branch requires hosted x64/ARM64 evidence. Its
original Linux skip was removed because the pinned ARM64 runner rejects skipped
cases. Original handoff evidence is unchanged. Native Linux/Windows/kernel
qualification is NOT RUN locally. Linux-only baseline cases remain skipped on
macOS. Coverage is retained
diagnostically, with no numeric gate. Compiled external weakened controls cover
missing transition checks, metadata-blind CAS and legacy coercion; they do not
modify or weaken baseline tests. Exact commands, first failures, counts, TRX,
coverage and source hashes are in the sealed external handoff.

All eleven product gaps and G1–G6 remain, including ENODATA, original Agent
measurement, ancestry, pre-CLR acquisition, whole-owner closure, manager
qualification and cancellation-to-send serialization. No operational migration,
production success, independent FINAL, merge or full-issue acceptance is claimed.

Rollback before integration is removal of these four new files. If qualified
future code ever creates operational V2 state, retain quarantine or drain using
compatible qualified code; never downgrade, reset, restore an older record,
unlink the lock, promote a temporary or replay an ambiguous operation.
