# Disposable systemd session recovery protocol fixture

Issue14 implementer patch; **no native case executed, no product qualification**.
Owner-approved holder/escrow architecture is the input, not this fixture's verdict.
Source requirements: Q9/Q20–22/Q25/Q31/Q150–151/Q158/Q162/Q164/Q168/Q174/Q182/
Q189–195; diagnostic metrics follow the later testing policy. G1–G6, ENODATA,
policy-readable acquisition and protected ancestry for the product remain open.

The new `tooling/policy-session-fixture/` tree contains real Linux Python pidfd and
public libsystemd notification calls, manager dump/count readback, static synthetic
services and one fixed campaign. It is test infrastructure. Its journal, Python
startup and synthetic agent are not shipping helper implementations or evidence
for real caller authentication, credentials/rights, pre-CLR resource closure,
all-thread TSYNC, original-agent descendants/FD exfiltration or incident unlock.
The synthetic processes never receive policy input. Worker units deny all three
io_uring calls from exec, but inherited rings and real managed startup remain
unqualified. No product admission path calls this fixture.

Startup diagnostics use at most eight distinct, nonce/source-bound `CM14-BOOT`
labels: cloud-init bootcmd, provision entry, template binding, authorization write,
guard completion, guest return, provision failure or guest timeout. They contain
no raw console, exception messages, package output or credentials. The host
retains only these fixed labels in the existing summary; malformed, repeated,
unbound or truncated frames refuse. They are diagnostic observations, never case
evidence or authority; a failure/timeout label additionally blocks candidacy.
Both ordered reports, all twelve raw cases, the exit
record, strict SSH and successful VM cleanup remain necessary. The 240-second
campaign and 160-second guest caps are unchanged. No startup label is a product
or installed-helper qualification claim.

## Files and fixed graph

`common.py` owns refusal, identity/dump comparison, notification bindings and bounded
commands. `process.py` implements fixed holder/worker/agent modes. `guest.py` runs
one campaign; `provision.py` is the fixed cloud-final entry. `prepare.py` constructs
the offline payload. `parent-integration.diff` is **unapplied** and changes only
cloud-init payload preparation and QEMU firmware bindings in the existing harness.
It adds no SSH command permission, but its old unconditional smoke override is
incorrect and MUST NOT be applied. Parent must implement a separate explicit campaign
and bounded raw artifact extraction for exits 1/2/3/signals, preserving ordinary smoke.
No INCOMPLETE/exit2 result may become whole-product PASS.
The parent must review/integrate this diff and CI artifact extraction separately;
no workflow, shared build, project or dependency file was edited.

PID1 starts sibling `cm14-holder.service`, `cm14-worker.service` and
`cm14-agent.service`; only the holder is root. Worker and agent use distinct fixed
DynamicUser identities. Each service has its own cgroup, task/FD/memory limits,
`Restart=no`, and no installation/activation edge. No common fixture slice places
the holder in the worker's memory OOM domain. Root remains privileged; fixed code
constrains its operations. `cm14-reference.target` has only `After=cm14-holder.service`,
retaining the loaded holder without pulling it or the worker into a start transaction.
It has no process. No Minecraft units, sudo policy, sockets or product configuration
are installed. Namespace collisions, effective fragment/drop-in drift and an
already occupied fixture refuse; no permission repair occurs.

Holder uses `NotifyAccess=main`, `FileDescriptorStoreMax=2`, preserve=yes and
`FDPOLL=0`. It opens original non-thread pidfds itself after fixed manager identity,
namespace and cgroup checks. It consumes its sole worker-start attempt before
StartUnit, stores worker and synthetic-agent FDs separately, barriers, compares
exact dump tuples plus NFileDescriptorStore, then journals ARMED. Notify send or
barrier alone cannot arm. Same-unit explicit recovery accepts only original named
FD identities, never starts the worker, and is cleanup-only. Pre-ARMED failure
refuses as `prearmed-no-work-unqualified`; it does not improvise a no-work proof.
Retirement requires both original HUP observations and terminal synthetic domains.
RETIRED precedes removal; partial removal and repeated exact-empty cleanup are
exercised. This conjunction is deliberately limited to the no-input synthetic
processes. Counters are diagnostic controls, never a product authority predicate.

## Positive disposable identity and prerequisites

There is no execution Boolean or caller-supplied command/unit. Before service
mutation, require Linux/root, systemd PID1, QEMU DMI, QEMU fw_cfg accelerator=tcg,
a fresh 128-bit boot fixture nonce, reviewed source digest, exact pinned image,
and a firmware hash of the root-owned authorization template. Cloud-final stamps
the current kernel boot ID into a root-only record; guard checks that ID on every
holder invocation. The firmware hash covers the canonical template excluding
boot_id (which is unavailable before boot); all other fields are externally bound.
Source paths and ancestry must be root-owned, non-writable by others, without
symlinks. Templates and payloads come from reviewed local bytes, never downloaded
executable code. The host harness's verified image provenance and forced TCG
configuration remain necessary: firmware strings are not hardware attestation
against a malicious host or root. This grants no authority on real Minecraft hosts.

The existing official amd64 image is `20261103-2651`; its SHA512 and metadata hash
remain those in `tooling/debian-fixture/image.json`. Official metadata fetched
2026-11-24 matched the existing metadata SHA512 exactly. `baseline.json` records:

- systemd and libsystemd0 `257.13-1~deb13u1`;
- linux-image-6.12.115+deb13.1-cloud-amd64 `6.12.115-2`, release
  `6.12.115+deb13.1-cloud-amd64`;
- python3 `3.13.5-1`, python3.13 `3.13.5-2+deb13u5`.

These are image metadata facts, not observed installed binaries. The guard compares
installed package/kernel values before use. Required tools are Python3.13 with
os.pidfd_open and ctypes, systemctl, busctl with JSON support, dpkg-query, public
libsystemd.so.0 with sd_pid_notify_with_fds, sd_notify_barrier and
sd_listen_fds_with_names, systemd's DumpFileDescriptorStore and NFileDescriptorStore,
QEMU firmware sysfs, unified cgroup v2 and genuine distinct Linux pidfs identities.
Actual symbols/API availability and runtime hashes must be observed in the guest;
absence or mismatch is UNAVAILABLE/FAIL, never a passing score. No gcc, headers,
libsystemd-dev, pkg-config or extra package is needed. gcc and libsystemd-dev are
absent from this image metadata; gcc-14-base is a runtime package, not a compiler.
systemd-oomd is also absent in metadata; this does not prove ancestor/global OOM
safety. Arm64 has no image pin or qualification in this patch.

Primary provenance: [Debian image metadata](https://cloud.debian.org/images/cloud/trixie/20261103-2651/debian-13-genericcloud-amd64-20261103-2651.json),
[systemd v257 sd_notify](https://www.freedesktop.org/software/systemd/man/257/sd_notify.html),
[sd_listen_fds](https://www.freedesktop.org/software/systemd/man/257/sd_listen_fds.html),
[FD-store implementation](https://github.com/systemd/systemd/blob/v257/src/core/service.c),
[dump ABI](https://github.com/systemd/systemd/blob/v257/src/core/dbus-service.c),
[Linux v6.12 pidfs](https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/fs/pidfs.c?h=v6.12).
The approved input research binds exact snapshots; this patch vendors no upstream
implementation. Public libsystemd is LGPL-2.1-or-later, dynamically loaded, not
internal libsystemd-shared. Later distribution needs exact Debian patches,
transitive notices/SBOM and license review; no product license is selected here.

## Campaign evidence and limits

Twelve raw cases: two-entry ARMED; full-store rejection despite send/barrier success;
wrong-main sender rejection; alive holder crash/handback; worker plus original
synthetic-agent death/HUP; HUP FD retention; inactive-holder anchor retention across
manager reload/GC opportunity; dead same-unit handback; unchanged worker start
counter/invocation; partial removal then holder crash; partial handback cleanup;
repeated empty cleanup. The inactive-GC case verifies loaded/store state after
reload, not an exhaustive manager GC/reexec proof. Every case requires real manager
output; offline tests never fill campaign PASS entries. Safety failure stops the
campaign, remaining cases stay NOT RUN. No automatic retries or fresh generation
within the campaign. Repeated bounded observations are not start retries.

Explicit gaps remain NOT RUN even if all twelve cases later pass: early-exit
NO-WORK closure, readable-only unreaped exit, PID reuse, worker/holder OOM,
ancestor/oomd policy qualification, real-agent descendant/exfil closure,
manager reexec, anchor-removal sensitivity, pre-CLR/TSYNC/no-old-ring proof and real
product protocol. Agent original death in this campaign is a synthetic pidfd case,
not the real agent cleanup proof. Normal healthy-host crash recovery is the goal;
manager/kernel/journal uncertainty quarantines without automatic reboot or replay.

Per command: five seconds, 64 KiB combined output in bounded file, 64 FDs, no core,
initial process group kill and leader wait attempted even when leader exits.
A descendant that calls setsid can escape this group: command containment is explicitly
UNKNOWN, and this is never product/service cleanup PASS. Production run_raw enforces
exact fixed argv and accepted exit codes; _run_process is an internal test mechanism,
not an authorized guest command interface. The parent MUST destroy and reap the outer
disposable VM after every campaign, including apparently successful protocol cases. Total guest command
budget is 150 seconds from import (includes cleanup), campaign observation budget
100 seconds, holder lease 75 seconds, unit runtime maxima 80/90 seconds. Limits are
fixture containment choices, not product performance targets. Root provision waits
at most 160 seconds; existing parent harness total/output/QEMU kill/reap limits
remain in force. Services have max 8/2 tasks and 128/64 MiB; actual OOM behavior is
not inferred from those values. No automatic limit inflation. Kernel unkillable
states cannot have guaranteed physical cleanup time; parent must destroy disposable
VM on uncertainty. The proposed integration is historical and UNAPPLIED. Fixed provision exports bounded
campaign.json through nonce-bound serial evidence with its unchanged INCOMPLETE result. A readiness marker alone
cannot qualify it. Parent must retain that evidence as CI artifacts.

The campaign records package bindings, kernel text, Python/libsystemd binary hashes,
manager version, exact dump tuples, start/invocation controls and OOM ancestry
observations. It preserves primary and cleanup failure independently. Cleanup stops
all owned service cgroups before removing units. Failed campaigns retain anchor,
units and journal/store for bounded guest evidence extraction and VM destruction;
they never clean ambiguous escrow. Successful protocol cleanup removes transient
units but retains the report/journal. It never makes the occupied fixture reusable.
Rollback is parent destruction of that disposable overlay/VM after extraction;
no production recovery or incident unlock is implied.

## Local validation

Safe offline command (no services, QEMU, root or network needed):

```sh
/opt/homebrew/bin/python3.13 -B -m unittest discover -s tooling/policy-session-fixture -p 'test_*.py' -v
```

Parse Python through ast.parse without pycache; run git diff --check and inspect
the complete untracked patch. No C# changes: .NET build/format and root build.py
verify/test are NOT RUN and not applicable to this owned Python/unit/document patch.
All twelve VM cases and eleven explicit gaps are NOT RUN on macOS. Exit codes:
1 campaign/cleanup failure, 2 completed protocol campaign with qualification gaps,
3 prerequisite UNAVAILABLE. No exit zero or blanket successful qualification summary.
Parent owns integration, real guest execution and independent exact-head QA.


## Correction bundle: SR-A1 through SR-A6

This candidate supersedes the original owned implementation bytes; the original
independent FAIL remains unchanged and is bound by its original root manifest.
Offline success below is implementation evidence, not independent QA.

State acquisition is exclusive mkdir followed by O_NOFOLLOW directory open, UID/mode
and device/inode pinning. Campaign publication uses that retained directory FD with
relative nofollow/exclusive temporary writes; occupied directories, dangling symlinks,
existing reports and swapped paths are never adopted or cleaned. Unknown evidence
remains in place for the parent. Journal/result/control publication opens protected
state without following its final component. All write progress is checked until full;
zero/negative/oversized counts fail. Temp fsync and one close precede rename; failures
before publication preserve old bytes and the temporary diagnostic file. Rename or
directory-fsync/close ambiguity preserves current bytes, never blind rollback or retry.
Injected faults are contract evidence, not actual disk-full/native proof.

Preparation now requires prepare(cloud_config, nonce, manifest_path, manifest_sha256).
The manifest must be outside the payload, externally bound to the reviewed exact Git
commit by trusted parent orchestration. Schema is {"schema":1,"commit":"<40 hex>",
"files":{"<relative path>":"<sha256>",...}}. Membership must equal common.SOURCE_FILES
exactly, including tests and the historical unapplied diff. The constructor rejects
extra/ignored files, unknown directories, symlinks, special files and byte mismatches;
then checks each payload byte against git show COMMIT:tooling/policy-session-fixture/PATH.
This uncommitted candidate cannot prepare as reviewed source. The parent must produce
the external manifest from the eventual independently reviewed signed commit and bind
its digest from that review/CI authority, not compute a new current-tree digest and
call it reviewed. No Boolean bypass exists. Tests inject an explicit synthetic manifest
and a mocked commit reader for construction only; production refuses those nonexistent
commit bytes. No actual commits/signing changes were made here.

The external manifest is uploaded as /run/cm14-source-manifest.json, outside the
payload to avoid self-hash cycles. Firmware source-sha256 and the authorization template
source_sha256 both mean SHA256 of those exact manifest bytes. Protected provision and
guard validate the same membership/bytes against that bound manifest, nonce, pinned
image, boot and template hash. The existing firmware option names remain unchanged.
Parent wiring must pass the two new prepare arguments; the historical integration diff
lacks them and is intentionally unusable. Do not bypass the refusal for offline tests.

Primary failure categories stay sanitized and separate from bounded cleanup/publication
categories; no raw command stdout/stderr is published on failure. Exit7 plus failed wait
still reports command-exit and leader-reap-unknown separately. Report publication is
best effort, with sanitized stdout fallback that retains the first cause. Cleanup still
attempts the initial group kill and leader wait. Process-group cleanup does not prove
all descendant death, Linux parentage or PID1 reaping on this macOS test host.

Cancellation catches BaseException at the campaign boundary. First SIGINT/SIGTERM
records cancellation before raising; repeated handler calls retain first cause without
repeated interruption of bounded finalization. A checkpoint publication occurs before
unit/anchor removal. Successful removal requires explicit all-12-case campaign completion
and verified RETIRED plus exact empty-store readback, with no primary or cleanup failure.
Cancellation before completion or during cleanup/publication blocks removal and retains
uncertain escrow/anchor/units. A cancellation after an already proven retirement cannot
undo previous removal; fallback evidence states failure/unknown rather than success.
All handler tests call pure handlers directly, without host signals or service effects.
The partial-removal-crash PASS is now recorded only after actual kill/restart and verified
one-entry handback; partial removal alone receives no crash proof credit.

Rollback remains bounded artifact extraction followed by destruction/reaping of the
outer disposable VM/overlay. Never rerun an occupied campaign, delete journal.new as a
repair, restore an ambiguous record automatically, reset an incident, or enlarge grants.
All 12 VM cases and all 11 qualification gaps remain NOT RUN. Parent owns new independent
artifact recheck, shared integration, signed shipping, separately authorized activation,
and exact-head functional/security/UX-performance QA. No full issue acceptance or claim
of ordinary native fault recovery is made.

### Restoration-window receipt correction

A sanitized INCOMPLETE receipt is flushed before restoring default signal handlers.
A second, stage-labelled receipt includes observed restoration failures; neither
is a qualification PASS. Restoration attempts each saved handler once, retaining
the original primary cause and recording cancellation/API uncertainty separately.
A later signal cannot remove the already-flushed first receipt. Output remains
best effort: closed output channels or abrupt process/kernel death cannot promise
a receipt, so the parent must retain available raw output and always destroy/reap
the outer disposable VM. Tests inject first SIGINT/SIGTERM and API faults during
restoration without delivering real host signals. The historical preflight FAIL
remains unchanged; independent correction review and VM proof remain pending.

### Parent host wiring correction

The separate campaign implementation is now in `tooling/debian-fixture/fixture.py`
and its manual workflow job; `parent-integration.diff` remains historical and
unapplied. See [host contract](debian-fixture.md#explicit-synthetic-policy-session-campaign-issue-14).
The old provision path-based report export has been removed: only current
in-memory report snapshots are embedded in the two existing flushed JSON
receipts. Each snapshot carries stage and complete authorization identity;
provision routes stdout to serial and emits one nonce/source-bound exit record.
No report is read from occupied state, no fixture-user privilege is added, and
publication/restoration errors remain separately observable. Guard failures before
report initialization can produce only an exit record or no output, hence remain
UNRESOLVED. The two-stage evidence is best effort, with strict host rejection of
missing/truncated/misbound evidence and mandatory outer VM destruction/reaping.
This wiring needs new independent preflight; prior artifact PASS covers its old
fifteen source files only. All new VM and qualification cases remain NOT RUN.
