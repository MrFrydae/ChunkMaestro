# Linux ACL self-context diagnostic collector — issue 14 remains OPEN

This bounded first implementation observes only the collector's own process.
It does not establish the future inspector's context or qualify any deployment.
Current product `NoDataUnqualified` / `Unavailable` behavior is unchanged.
The collector is separate diagnostic tooling, not a C# agent or helper API.

Traceability: Q20–22/Q31 constrain authority and adoption; Q151/Q158/Q162
constrain the future agent, disposable Debian evidence and packaging;
Q168 scopes authority; Q189–195 and the later 2026-11-23 owner testing policy
require meaningful failure evidence, honest environmental limits and independent
QA. No implementation acceptance or issue closure follows from these unit tests.

## Entry boundary and future execution

Python >=3.11, matching the existing Debian fixture's minimum; standard library
only, no dependencies or new license. For later separately reviewed/authorized
Linux fixture execution, the sole command shape is:

```text
python3 tooling/inspector-qualification/collector.py collect RUN_ID
```

RUN_ID is 1–32 ASCII characters, first alphanumeric, then alphanumeric/underscore/
hyphen. It is a supplied correlation label, never trusted identity or authority.
No arguments/default invocation, unsupported platform/version, extra flags and
malformed run IDs refuse with exit 2 before collector file I/O. Python interpreter
startup/imports are outside that claim. There are no PID, path, command, profile,
trust, qualification, authentication or output destination arguments. No subprocess,
ACL creation/deletion, permissions repair, mount, namespace transition or network
operation exists. This work did not execute the collector against Linux.

A later coordinator must bind the reviewed source/tree and private output to its
fixture ledger and supervise/reap its own collector process. The five-second
monotonic interval is cooperative between reads, not a hard deadline for blocking
open/read/readlink/fsync. There is no dangerous asynchronous FD close. Output I/O
and interpreter startup are outside that interval. A coordinator timeout or kill
means incomplete/unknown; do not relax permissions to obtain observations.

## Fixed acquisitions and sanitization

`SOURCES` in collector.py is the complete allowlist, sampled twice:

| Fixed source | Retained value |
| --- | --- |
| `/proc/sys/kernel/random/boot_id` | Strict UUID |
| `/proc/sys/kernel/osrelease` | Bounded release token; no binary authentication claim |
| `/proc/self/status` | Uid/Gid four-tuples (real/effective/saved/filesystem), Groups, five capability masks, NoNewPrivs, Seccomp and Seccomp_filters |
| `/proc/self/stat` | Own PID and start ticks only; process name discarded |
| `/proc/self/ns/user`, `/proc/self/ns/mnt` | Numeric namespace handles only, via readlink |
| `/proc/self/uid_map`, `/proc/self/gid_map` | Strict nonoverlapping numeric mapping triples |
| `/proc/self/mountinfo` | Mount/parent IDs, device numbers, enumerated filesystem class and selected flags |
| `/sys/kernel/security/lsm` | Bounded, unique LSM name list |
| `/proc/self/attr/current` | Label bytes hash only; label semantics UNKNOWN |

No general environment, arbitrary proc traversal, command line, journals,
credentials, game data, private fixture seed or caller-chosen files are read.
Raw status/stat/mountinfo/subject bytes are not written. Mount roots, targets,
device sources, optional fields and arbitrary superblock options are discarded;
mount flags retain only ro/rw/nosuid/nodev/noexec/noacl. Filesystem names outside
the enumerated diagnostic classes become OTHER. Omitted flags do not establish
ACL enablement or idmap absence. Even the LSM list is an observation, not proof
of full active policy or hook semantics. Namespace handles/maps do not prove an
initial namespace. Kernel release does not bind running code/config/packages.

Each acquisition reads at most 65,537 bytes (65,536 accepted plus one oversize
sentinel); 1,048,576 total charged bytes, 22 finite acquisitions, 128 mount rows,
16 mapping rows, 256 groups, 8,192 bytes per record and 262,144 serialized report
bytes are ceilings, not product capacity or SLAs. An oversize snapshot is rejected
rather than truncated into a valid result. Partial raw bytes from failed reads
are not available and their byte count/hash remain unknown (0/null). Once the
budget is exhausted, subsequent reads are refused. These bounds may conservatively
reject otherwise legitimate large hosts.

Each acquisition reserves its entire allowed byte amount before I/O. A normal
return releases the unused allowance; an exception retains the reservation
because partial native reads may already have consumed unreported bytes.
Failed reads therefore cannot reset the total budget. Namespace readlink calls
require a full per-source reservation before starting, since Python readlink
does not accept a caller-sized buffer. These are the fixed Linux self namespace
handles, within the current proc trust domain, not arbitrary symlink requests.

The reader continues across short reads until EOF or the byte ceiling, with a
maximum of 64 read calls per source. A valid prefix is never accepted merely
because one native read returned fewer bytes than requested. Fragment-cap
exhaustion stays incomplete/unknown. Blocking-call supervision remains a future
coordinator responsibility, including these bounded read loops.

## Output schema and completion

Schema `chunkmaestro.self-context.v1` always contains:

- `runtimeQualification="UNRESOLVED"`, `productPromotion=false`,
  `targetInspectorContext="NOT ESTABLISHED"`.
- Supplied run identity, source file SHA256/class, UNKNOWN source closure/revision,
  UTC/monotonic collection interval, SELF_CONTEXT or OFFLINE_FIXTURE domain.
- `selfIdentity`, `namespaceBinding`, drift list and the fixed G1–G6 gap map.
- Two ordered sweeps of each allowlisted evidence key with run ID, sweep,
  fixed source/API, UTC and monotonic interval, identity/namespace references,
  UNKNOWN source revision, status/errno, returned byte count, raw-byte SHA256,
  sanitized-value SHA256 and parsed value. Raw retention is NOT RETAINED.

Each evidence item also records `bytesKnown` and its charged `readBudgetBytes`.
The report's `readBudgetBytes` is the conservative charged/reserved upper bound;
`readBytesKnown` sums transfers with known returned sizes. `readBytes` is an
exact observed total only when every attempted transfer size is known, otherwise
it is null. A failed partial transfer's bytes=0/hash=null is an unknown-data
sentinel paired with bytesKnown=false, not a claim that it consumed zero bytes.

Observation class is `observed` only for actual reader output, `supplied` for
internal offline fixtures, `unknown` for unsuccessful measurements. There is no
inferred qualification or report-import/product-consumption path. Unknown status
fields are discarded after structural/duplicate checks; injected qualified or
promotion claims never affect the fixed qualification fields. Missing required
fields, malformed/duplicate/overlong values, unsupported attributes, denied or
missing files, timeout, interruption and I/O errors remain incomplete/unknown.
No error is interpreted as ACL absence. A matching sample does not exclude ABA
or malicious kernel/root/interposition; this is not attestation.

Mount records must contain exactly one of ro/rw. Both or neither are malformed;
legitimate read-only and writable records remain supported diagnostics.

`collectionCompletion=COMPLETE` means all selected diagnostic acquisitions parsed
and matched, including own PID; it says nothing about the remaining proofs.
Raw status/stat hashes may differ for unrelated dynamic fields; their selected
credentials/identity must match. Other raw sample changes conservatively invalidate
collection, including mount topology, boot identity, maps, LSM and subject hashes.

The CLI creates a new random directory under fixed `/tmp`, mode 0700, never a
caller path. Files are 0600 with exclusive creation. It writes an INCOMPLETE
marker first, then report.partial, fsync, exclusive hard-link publication to
report.json, then receipt.partial and receipt.json last. Existing final files
cannot be overwritten. Receipt binds report SHA256; both must be reopened and
verified by the coordinator. The INCOMPLETE marker intentionally remains: without
a valid receipt, all content is incomplete. Partial files alone are never evidence
of completion. No directory fsync/power-loss durability guarantee is made;
receipt/hash mismatch after interruption is unknown. Exit 0 means complete
collection with receipt, 1 means incomplete collection/output, 2 means refusal.
There is no qualified output or success path into the product.

The final receipt hard link is the publication boundary. Its fsynced source
`receipt.partial` remains alongside it so that no fallible cleanup follows that
commit. A partial file without the final receipt is insufficient. Cancellation
after completed publication can stop the caller without retroactively changing
the already recorded collection; the coordinator still verifies the receipt and
report, and runtime qualification always remains UNRESOLVED.

## Remaining proof gaps and validation

All are UNKNOWN/blocking regardless of collection completion:

| Gap | Remaining work |
| --- | --- |
| G1 | Exact patched binary/modules/generated configuration/package/source closure |
| G2 | Every protected object's statx/mount/idmap and initial namespace proof |
| G3 | Full LSM policy/domain/labels, loader and complete seccomp semantics |
| G4 | Protected installer/effective launch and maintenance invalidation lifecycle |
| G5 | Real same-inspector-context ACL C0–C6 corpus and denial/drift controls |
| G6 | Whole helper/socket/bus/proc/backend and deadline/native desktop proofs |

Offline verification (no synthetic Linux-native PASS):

```text
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tooling/inspector-qualification -p 'test_*.py' -v
```

Tests cover parsers, bounded random malformed inputs, denied/missing/unsupported
and interrupted I/O, cap exhaustion, late timeout, wrong PID/start identity,
namespace/mount/credential drift, ignored qualification claims, early refusal
without host reads, fixed reader paths and output disk/short-write/interruption
failure. The CLI integration case mocks Linux and all context reads; it supplies
no Linux execution evidence. No environmental skip counts as pass. Coverage is
diagnostic only. Existing product proof remains unchanged and was not rerun.

Real hosted Linux and disposable Debian execution, source/launch provenance,
independent implementation QA and full helper acceptance remain NOT RUN here.
Fixture/CI/classifier integration belongs to the parent; this slice proposes no
shared-file changes. No fixture activation is authorized by this document.
Rollback removes only these new owned source files and this run's disposable
output after evidence retention; nothing is deployed and product authority is
unchanged. A later coordinator must preserve partial output and reap only its
ledger-owned processes, never unrelated hosts/processes or fixture bases.

Lead integration retained the original 22 passing offline cases and added two
short-read regressions, both of which failed against the delivered reader. The
corrected 24-case suite passed under Python 3.13 on macOS with zero failures/skips.
These are file-stream fixtures and offline parser/entrypoint tests, not Linux
collection or Debian qualification. Original worker evidence and its Python
3.15 execution remain separately attributed and unchanged.

The initial independent artifact preflight found SEC-14-01 (partial-read failures
bypassed the total budget) and SEC-14-02 (contradictory ro/rw flags could complete).
Its original FAIL and all 11 sealed artifacts remain under
`/var/folders/8p/klwq9s9n3jg2hm6pqx99hc3w0000gn/T/chunkmaestro-14-security-preflight-w0umndv2/`.
The new lead regressions failed before correction. The corrected 27-test suite
passed with zero failures/skips, and the lead re-executed the original unchanged
12 reviewer controls successfully in a fresh owned evidence directory. That
reexecution is not a new independent verdict. No Linux collection or native
qualification ran; fresh independent review remains required.

## Linux CI integration

This slice is stacked on signed owner-fixture commit
`cb95c3a64873c92ea884d73c490a68d71dc81917`; that parent makes qualification-tool
changes automatically high-risk for three-role QA. The Ubuntu CI contracts job
now supervises this suite for at most 30 seconds and uploads the real Linux
self-context integration evidence as `inspector-context-linux`.

The Linux-only test invokes the actual collector entrypoint and fixed reader,
with no reader/platform mocks. It checks receipt/report/source hashes, private
output, real process identity and observed boot/process/namespace acquisitions.
Other diagnostic acquisitions may remain unavailable or drifted. The test
accepts an honestly reported INCOMPLETE collection only with a valid receipt,
matching exit status and unchanged UNRESOLVED/productPromotion=false boundaries;
it never turns unavailable measurements into observations or qualification.
It retains only the invocation's fixed sanitized files and source copy. No
installer, VM, ACL mutation, privilege change or authentication runs.

Local macOS validation has 27 passing tests and one explicit Linux-only skip.
The original 12 reviewer controls also pass when reexecuted by the lead. The
native integration has not run locally; hosted Linux execution remains pending.
Its future receipt proves only these diagnostic behaviors, not Debian 13,
LSM/filter semantics, source/binary attestation or the remaining G1–G6 profile.
