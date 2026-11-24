# Debian 13 systemd fixture — issue #15

This is bounded test infrastructure for Q145, Q151, Q158, Q186, Q189,
Q191–195 and later #13/#14 work. The owner has no disposable Debian VM.
The patch prepares an explicitly activated workflow; it has **not executed a VM**, enrollment,
the product helper, schedules, permissions or recovery acceptance. No production
host, retired storage workflow, product code, product package graph or unrelated CI changes.

## Reviewed upstream provenance (2026-11-23)

Use `tooling/debian-fixture/image.json` as the pin, alongside the exact upstream
`SHA512SUMS`. The image is
`debian-13-genericcloud-amd64-20261103-2651.qcow2`, from the dated
[official directory](https://cloud.debian.org/images/cloud/trixie/20261103-2651/).
Its [JSON metadata](https://cloud.debian.org/images/cloud/trixie/20261103-2651/debian-13-genericcloud-amd64-20261103-2651.json)
identifies official, trixie/13, genericcloud, amd64, build 20261103-2651.
The harness checks both metadata hash/identity and the complete checksum manifest
hash before downloading the disk, then checks the disk SHA-512 before QEMU reads it.
The metadata and image checksums are also in the pinned manifest.

**Signature status: not published.** The dated directory lists no signature, and
`SHA512SUMS.sign` returned HTTP 404 during review. The
[Debian cloud-image page](https://cloud.debian.org/images/cloud/)
recommends TLS retrieval for current official images and distinguishes signed
legacy OpenStack images. This is a reviewed TLS/checksum trust anchor, not a
verified Debian OpenPGP signature. The
[installation-media signature guide](https://www.debian.org/CD/verify)
does not establish a signature for this cloud image. Do not invent a signer,
reuse an unrelated ISO signature, or describe the image as signature-verified.
An owner requiring publisher signatures must leave this fixture unexecuted until
an acceptable signed source is separately reviewed.

[GitHub's runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
lists standard `ubuntu-24.04` x64 runners. This fixture uses one fixed software
system-emulation backend: `-machine q35 -accel tcg,thread=multi -cpu qemu64`.
It never probes or opens `/dev/kvm`, grants device access, or selects another
accelerator after a failure. Inventory names all six required commands (`curl`,
`qemu-system-x86_64`, `qemu-img`, `cloud-localds`, `ssh-keygen`, `ssh`), Linux
x86_64, nonroot, Python 3.11+, and `execution-mode=tcg-system-qemu64-multi`.
The selected-mode event records configuration, not successful guest execution.

[QEMU's system introduction](https://www.qemu.org/docs/master/system/introduction.html)
defines TCG as CPU emulation in a full machine model. The
[official Noble QEMU manual](https://manpages.ubuntu.com/manpages/noble/man1/qemu-system-x86_64.1.html)
identifies the 8.2.2 Ubuntu series and documents `-accel tcg` and `thread=multi`
(one thread per vCPU). No icount/replay option is used. The
[8.2.2 CPU definitions](https://github.com/qemu/qemu/blob/v8.2.2/target/i386/cpu.c)
define `qemu64` with long mode, SSE2 and NX; it is an emulated baseline x86_64
CPU, independent of the runner CPU. Debian 13's
[amd64 baseline](https://www.debian.org/releases/trixie/amd64/ch02s01.en.html)
is consistent with that choice for the pinned amd64 genericcloud image.
This is source-level compatibility evidence; actual boot of that pinned disk
remains unproven. The runner resolves signed Noble package versions, so actual
versions remain evidence inputs rather than a new package pin.

TCG remains a real disposable Debian/systemd guest for Q158. This changes an
implementation choice, not the product architecture. Timing measures emulation
only: no hardware acceleration, performance equivalence, production performance,
privileged-helper or full issue acceptance is claimed. No access grants,
new runner users, paid/self-hosted pool or local installation is introduced.

The new workflow reuses the checkout v5 and upload-artifact v4 commit pins already
in repository CI. Checkout credentials are not persisted. No new third-party
Action or Python dependency is introduced. GitHub's standard OS label is mutable;
the pinned guest disk and hashes make the guest input explicit while the actual
runner identity remains available in GitHub's job logs.

## Execution boundary and limits

Pull requests changing fixture paths can run the offline contracts on
standard Linux, including the Linux-only descendant test. Opening, synchronizing
or reopening a PR never starts a VM. A label already on a PR grants no execution
on later pushes.

After independent review of the exact head, the verified owner account (numeric
ID `38875853`) can apply `run-debian-fixture` to a same-repository PR to request
one bounded smoke run. Only that exact `labeled` event can enter the VM job;
unrelated labels, fork heads and other actors are denied. Remove/reapply is a
new explicit request, not an automatic retry. The lead records the reviewed SHA
and resulting run. This route permits premerge validation: GitHub manual dispatch
requires the workflow file to exist on the default branch first.

After merge, `workflow_dispatch` also permits that account to provide exact
acknowledgement `RUN-SYNTHETIC-DEBIAN-13` for a reviewed ref. No push, schedule,
workflow-run or privileged PR trigger exists. VM runs serialize without cancelling
one another; offline checks have their own three-minute job bound and do not wait
for that VM concurrency group. Repository administrators remain a trust boundary:
they can edit workflows. Preparation alone does not activate either route; the first reviewed attempt is
recorded below and did not reach VM creation.

The separate opt-in CLI is for an explicitly approved ephemeral Linux runner:

```sh
python3 -B tooling/debian-fixture/fixture.py --execute-owner-reviewed-fixture
```

It creates one private mode-0700 `/tmp/cm-debian-fixture-*` directory. Base,
overlay, seed, home, keys, known_hosts and command-output files belong exclusively
to that run. Base becomes read-only; no cache, host mount, SSH agent, environment
credential, production token, guest agent, product deployment or public listener
is passed to QEMU. Child environment is an allowlist: PATH, private HOME, LANG,
LC_ALL. User networking is restricted, with only an ephemeral
`127.0.0.1:<port>` SSH forward. A port reservation race fails boot closed.

Fresh Ed25519 client and guest host keys are synthetic. Cloud-init installs the
expected host key and an unprivileged fixture user without sudo, disables password
and root login and key emission to console. OpenSSH uses a dedicated known_hosts,
`StrictHostKeyChecking=yes`, Ed25519 only, no global configuration, no agent,
no forwarding and one connection attempt. There is no `ssh-keyscan`, accept-new
or verification bypass. Cloud-init's fixed `runcmd` executes once in the final scripts-user stage
([module contract](https://docs.cloud-init.io/en/latest/reference/modules.html#runcmd)),
after fixture-user/key/SSH configuration. It verifies the fixture user and a
nonempty authorized_keys file, validates sshd configuration, restarts only the
disposable guest's SSH service to apply the fixed policy, then writes a complete
serial line containing a fresh 128-bit nonsecret nonce to `/dev/ttyS0`. These
are narrowly scoped guest bootstrap effects, not product remote operations.
Host boot supervision accepts only the exact current nonce line, within the
existing 240 seconds; a banner or stale/partial marker cannot release the probe.
The authenticated probe runs once after that marker. The marker proves only
fixture bootstrap readiness, not cloud-init-wide success or acceptance. A host-key mismatch fails that probe; it never rewrites trust.

| Resource/phase | Hard bound |
| --- | --- |
| Runner | Standard ubuntu-24.04; job 25 minutes |
| Harness work | SIGALRM 1200 seconds; per-phase deadlines also enforced |
| Guest | 2 vCPUs, 2048 MiB RAM, fixed TCG system emulation |
| Disk | Base download ≤1 GiB; guest virtual disk/overlay ≤4 GiB; ≥10 GiB free required |
| Child files | Each ≤4 GiB, core dumps disabled; command output over 1 MiB fails at next poll |
| Child CPU | 900 CPU seconds per subprocess; VM wall clock bounded by harness |
| Metadata | Checksums ≤64 KiB, metadata ≤128 KiB, each download 30 seconds |
| Image download | 300 seconds, connect 20 seconds, HTTPS only including at most three redirects, no retries |
| Tools/probe | 30 seconds each; SSH connect 5 seconds, one attempt |
| Boot readiness | 240 seconds, fixed 5-second private serial polling, no reboot/retry |
| Shutdown/cleanup | TERM, 1-second grace, KILL; direct waits ≤5 seconds each; adopted reaping ≤5 seconds |
| Evidence retention | Only fixed sanitized status/hashes, 7 days |

The 10 GiB free-space preflight reserves the base, 4 GiB overlay and up to one
4 GiB failing output file before its 100 ms size poll; successful command
output files remain at most 1 MiB each. This is a finite file-size boundary,
not a filesystem quota or a promise that the runner cannot run out of space.

Cleanup intentionally terminates the disposable guest; it does not claim graceful
guest shutdown or product recovery proof. Each subprocess has an owned process
group. The Linux harness is a process-local child subreaper, kills groups even
when their leaders have exited, waits direct children and reaps adopted descendants
before disk deletion. Reaping failure retains the private directory for ephemeral
runner destruction and fails the job. SIGINT/SIGTERM initiate cleanup; repeated
signals are ignored during cleanup. SIGKILL/host loss cannot run Python cleanup;
GitHub ephemeral runner destruction is the outer boundary, never a persistent
machine cleanup promise. There are no automatic VM/probe/job retries.

Phase events contain fixed identifiers, start/wait/failure states, with 10-second
command heartbeats and 5-second boot heartbeats. Child stdout/stderr is private
and never copied into evidence. QEMU serial uses that same private supervised
stream (1 MiB polling cap, existing file-size limit and cleanup), read with
`pread` so the writer offset is never moved. Only a SHA-256 of the bounded
readiness snapshot and fixed status are emitted; neither raw console, nonce,
user-data, keys nor images are uploaded. Output remains supervised during the
probe and shutdown boundary. Smoke checks Debian ID/version, fixture username,
`/proc/1/comm=systemd`, and a read-only `systemctl show --property=Version --value`.
Only the exact fixed successful response is accepted. No helper or enrollment test
is implied. Evidence contains input SHA-256 hashes and sanitized JSON events only;
never upload a directory glob, disk, user-data, private key or raw guest output.

## Offline validation and pending proof

```sh
python3 -B -m unittest discover -s tooling/debian-fixture -p 'test_*.py' -v
ruby -rpsych -e 'Psych.load_file(ARGV[0]); puts "YAML parsed"' .github/workflows/debian-fixture.yml
actionlint .github/workflows/debian-fixture.yml
git diff --check
```

The offline suite checks opt-in/platform/tool denials and no-KVM emulation selection, provenance tampering
and identity, child credential stripping, strict host-key options, seed trust,
QEMU isolation arguments, timeout termination, sanitized failures, and Linux
subreaper behavior (explicitly skipped on macOS). Mocks of QEMU are unit contract
tests, not VM execution. The Linux offline test must run before a hosted smoke.
Actual hosted QEMU boot, cloud-init behavior, host-key mismatch over real SSH,
TCG boot timing, resource use and complete descendant teardown remain
pending. A ready harness is not issue acceptance or product acceptance.

Update procedure: read the official dated index and current Debian provenance
policy; review advisories/package metadata and runner changes. Retrieve the new
checksum/JSON via official HTTPS with bounded commands. Review signature status;
if signatures become available, validate against independently established Debian
key fingerprints and add verification before use. Change the dated URL, exact
filename, manifest, hashes and expected metadata identity together. Independently
review the diff and rerun offline/YAML/actionlint checks; only then seek separately
authorized owner execution of the reviewed ref. Never switch to `latest` or
silently repin a failed download.

Rollback: while uncommitted, reverse only this reviewed patch after saving
evidence; after integration, use a reviewed revert. The original delivery installed no software. Corrected setup installs packages
only on an ephemeral runner; runner destruction is its rollback boundary.
No production data is involved. Keep initial
failure logs outside tracked source. Independent QA and hosted proof remain lead
integration gates.

Lead integration retains the original six-file delivery and hashes under
`.artifacts/debian-15-integration/original-delivery/`. The lead independently
retrieved the dated official checksum/metadata, verified the pinned provenance,
and evaluated the actual workflow condition across 384 event/actor/repository/
label combinations with zero authorization mismatches. Local offline validation
still reports 15 passing tests and one Linux-only skip; actionlint and specification
checks pass. This policy check is not GitHub execution, a VM boot or host-key
integration proof. At that historical checkpoint no activation label or dispatch had been issued.

The bounded independent integrity preflight at `9f615fa` passed with one low
finding, SR-01: background QEMU output was not included in the 1 MiB polling check.
The lead reproduced it with a failing regression, then added checks of every owned
child's output during boot, command supervision and the final shutdown boundary.
The corrected local suite passes 16 cases with one Linux-only skip. The prior
exact-head hosted Linux suite passed all 16 original cases; it is not execution
proof for the new regression. Original review and failure logs remain retained.
Runner tool/KVM availability, real boot/host-key behavior, QEMU teardown and final
independent acceptance are still pending; no VM had been activated at that checkpoint.

## Historical corrected KVM runner prerequisites (issue #15, frozen source d9082b9)

The following records the superseded KVM-only implementation and its failures.
Its KVM access prerequisite and proposed execution steps do not govern the TCG
patch described above and below. Original failure evidence remains unchanged.

The first owner-reviewed activation, [run 45236698337 job 137910330399](https://github.com/MrFrydae/ChunkMaestro/actions/runs/45236698337/job/137910330399),
failed with `preflight-tools` before image download or VM creation. The old aggregate
failure does not establish which commands were absent. It provides no evidence of
KVM access or guest boot. The lead retains the original ZIP/log/assessment under
`.artifacts/debian-15-integration/activation-45236698337` in its checkout.
Earlier no-activation/no-install statements describe the historical delivery.

The corrected workflow separates read-only inventory, fixed runner setup, and a
nonroot post-check. Setup also refuses before APT unless KVM is already openable
by the runner user; package installation cannot be used to acquire missing access.
Every inventory emits all six fixed command names, platform,
Python version compatibility, nonroot status and KVM status, without resolved
paths, exception strings, keys or environment values. KVM distinguishes missing,
non-character-device, not-openable and ready. Setup failure still emits inventory;
the post-check also runs after failure unless cancelled. A failed setup blocks the
VM even if a later inventory finds the tools present. No automatic retry exists.

Only an ephemeral GitHub standard `ubuntu-24.04` x64 runner may run setup. The
workflow owner/event condition remains the execution authority; environment/OS
checks are an additional accident guard, not attestation of a physical runner.
The fixed APT direct-package allowlist and actual harness usage are:

| Package | Used commands | Primary Ubuntu metadata |
| --- | --- | --- |
| curl | HTTPS provenance/image retrieval | [files](https://packages.ubuntu.com/noble/amd64/curl/filelist) |
| qemu-system-x86 | KVM VM, restricted user networking | [metadata](https://packages.ubuntu.com/fr/noble/qemu-system-x86), [files](https://packages.ubuntu.com/noble/all/qemu-system-x86/filelist) |
| qemu-utils | qcow2 inspection/overlay | [metadata](https://packages.ubuntu.com/noble/qemu-utils), [files](https://packages.ubuntu.com/da/noble-updates/amd64/qemu-utils/filelist) |
| cloud-image-utils | cloud-localds seed creation | [metadata](https://packages.ubuntu.com/ca/noble/amd64/admin/cloud-image-utils), [files](https://packages.ubuntu.com/noble/all/cloud-image-utils/filelist) |
| openssh-client | synthetic Ed25519 generation/strict SSH probe | [files](https://packages.ubuntu.com/noble/amd64/openssh-client/filelist) |

Ubuntu's cloud-image-utils (source cloud-utils, 0.33-1 at review) depends on
qemu-utils, genisoimage, ca-certificates, e2fsprogs, file, python3 and wget. Its ISO
seed path needs genisoimage; these are package dependencies, not extra user inputs.
QEMU's package dependencies include libslirp0 (restricted user networking), seabios,
qemu-system-common/data and its runtime libraries. Optional GUI/OpenGL, OVMF,
Samba, extra block backends and recommended tools are not requested. Existing
Python 3.11+, sudo, GNU timeout, dpkg and official Ubuntu archive keyring are runner
prerequisites, not installed by bootstrap. No .NET/NuGet/Java/product dependency
changes occur. Versions are resolved from signed configured noble indexes, rather
than freezing a security-update version that may disappear; execution evidence
records actual direct package versions and the SHA-256 of all installed package
versions, configured source bytes and Ubuntu keyring bytes. This does not claim
bit-for-bit host reproducibility or that preinstalled runner packages came from
Ubuntu; existing runner image/package provenance remains a trust boundary.

The setup constructs a private source file with fixed HTTPS
`archive.ubuntu.com/ubuntu/` (noble/noble-updates) and
`security.ubuntu.com/ubuntu/` (noble-security), main/universe only and explicit
Ubuntu archive keyring Signed-By. These reviewed bytes, rather than the runner's
mutable source/mirror lists, select acquisition origins. Validation still rejects
unknown fields, insecure overrides and alternate keys/repositories. Private APT
lists and no sourceparts prevent cached third-party indexes from supplying missing
packages. No system source, key or host trust file is added or modified. See Ubuntu's [source policy](https://manpages.ubuntu.com/manpages/noble/fr/man5/sources.list.5.html)
and [archive authentication](https://manpages.ubuntu.com/manpages/resolute/man8/apt-secure.8.html).

This explicitly changes the old no-install boundary: two fixed noninteractive
sudo invocations run official Ubuntu APT update/install through GNU timeout.
There is no user package/command/source input, shell execution or curl-to-shell.
APT signature authentication is required, insecure/downgrade/unauthenticated
options are false, update partial errors fail, retries are zero, HTTP(S) timeouts
15 seconds, lock timeout 30 seconds. Root timeout terminates each privileged APT
process group (TERM at 230/290 seconds, KILL after 5); outer supervision bounds
are 240/300 seconds. Only DEBIAN_FRONTEND=noninteractive and NEEDRESTART_MODE=l
are added to the credential-stripped child environment; sudo preserves those fixed
values. Install disables recommends, removals and upgrades of already-installed
direct packages. Required transitive dependencies and their package maintainer
scripts execute as root on this disposable runner. This is real privileged package
installation, not a guarantee of zero system side effects. No KVM chmod/udev/group/
ACL, firewall, account or host-trust changes are scripted. Private APT indexes and
root-owned setup files remain for ephemeral runner destruction; no privileged
cleanup command is introduced. Abrupt cancellation may leave the bounded root
APT process until its timeout or runner destruction. No persistent-host promise.

APT stdout/stderr stays private with the existing supervised output/file caps;
only fixed phase/status events, validated direct version strings and hashes are
uploaded. Evidence uses explicit filenames, never directory globs. Setup has a
10-minute step ceiling within the unchanged 25-minute job; harness work remains
1200 seconds, 2 vCPU/2 GiB/KVM only, with existing download/disk/network/SSH/cleanup
limits. These nested bounds can cause the outer job to cancel before every phase
finishes; they are ceilings, not reserved extra time. Nonroot openable `/dev/kvm`
remains mandatory. Failure requires a lead decision; no software emulation,
expanded access, paid/self-hosted pool or repeated activation is authorized here.

Proposed next execution, after independent review of the corrected exact source:
one owner-issued labeled event, no retries. First require passing offline Linux
contracts, source-format/keyring guard and all tools; require the nonroot KVM check
before any image download/VM. Retain inventory/setup/post-check evidence even on
failure. Stop on tool, APT, provenance, KVM, resource, boot or SSH failure. This
patch itself grants no new activation authority. Actual APT resolution, KVM access,
guest boot, real SSH trust behavior and teardown remain unexecuted for this patch.
Prior security and functional reports remain immutable historical evidence, not
independent review of this change or product acceptance.

### Primary failure preservation

The independent `f127575` correction preflight found SR-CORR-01: inventory or
cleanup exceptions could replace the original APT error in sanitized evidence.
Setup now records the first error immediately, emits fixed separate statuses for
inventory/cleanup failures, always records retained workspace responsibility,
and rethrows the first error. A final inventory failure cannot hide it. If only
a secondary phase fails, that phase still makes setup fail; no VM is started.
Exception details are never included in events, and no cleanup privilege is added.

Local correction proof: 28 tests passed, one Linux-only skip. The new preservation
tests reject historical source; the independent success-path secondary-failure
guard still passes it. Original review evidence remains unchanged. An initial
regression-fixture keyring mismatch was retained, corrected, and followed by an
actual historical-source negative control. YAML/actionlint, syntax, whitespace
and specification checks pass. Logs are retained under
`.artifacts/debian-15-integration/prerequisites-correction/`. This is offline
diagnostics proof, not hosted APT, KVM, VM or product acceptance. A new independent
correction review remains required before another bounded activation.

## Fixed TCG correction prepared from cd5721e

The lead reports [corrected run 45245131572](https://github.com/MrFrydae/ChunkMaestro/actions/runs/45245131572)
passed offline contracts, named missing QEMU/qemu-img/cloud-localds, and found
KVM not openable. Setup refused before APT, image retrieval or VM creation.
No successful VM proof exists. The lead retains digest-verified sanitized evidence
at `.artifacts/debian-15-integration/corrected-run-45245131572`; that directory
is absent in this implementation worktree, so its hashes were not independently
recomputed here. Original f127 FAIL and cd preflight PASS assessments remain
immutable in the lead's `/tmp/chunkmaestro-pr17-security-*` directories. Their
primary-error preservation correction is retained and tested, not superseded.

The setup now proceeds to its unchanged fixed APT commands after runner/source
checks without requesting KVM access. URI validation accepts one optional trailing
slash only for the existing exact official hosts and `/ubuntu` path; Ubuntu's
[Noble deb822 examples](https://manpages.ubuntu.com/manpages/noble/man5/sources.list.5.html)
use unslashed archive/security URIs. Original configured bytes are still hashed
and copied, with no broad URL normalization, trust override or source mutation.
Unknown formats/fields/hosts/path variants still fail closed.

Revised one-run proof, for the lead after independent review of the patched exact
head: require hosted Linux offline PASS (including descendant cleanup), review
source/keyring hashes and named prerequisite inventory, then authorize one exact
owner `38875853` same-repository `run-debian-fixture` label event. No push
activation, retry, access grant or deadline extension is permitted. Retain all
sanitized setup/post-check/status evidence on failure. Require fixed TCG mode,
signed-source APT success and actual package inventory, pinned image verification,
current serial nonce readiness within 240 seconds, one strict authenticated SSH
probe proving Debian 13/fixture identity/systemd PID1/exact output, output caps,
and complete process reaping before owned workspace deletion. Stop at the first
failed prerequisite/provenance/boot/SSH/cleanup boundary; a timeout is a failed
emulation attempt, not permission to increase bounds. A future real host-key
mismatch test still needs separately reviewed execution; mocked denials are not
real SSH trust proof.

This preparation performs no APT/sudo/install, image retrieval, VM activation,
commit/push, label or other GitHub mutation. One reviewed run remains pending;
independent review and real guest evidence remain integration gates. Reversing
this patch restores the prior KVM-refusal implementation; do not reactivate it
without a lead decision. Disposable runner destruction remains the package/setup
rollback boundary after a later authorized run.

Local preparation validation: 36 offline tests discovered, 35 passed and one
Linux-only descendant test skipped on macOS. In-memory Python syntax, Ruby YAML,
existing cached actionlint 1.7.12, specification and whitespace checks passed.
The workflow is byte-identical to frozen cd5721e except its TCG smoke step name.
Two new backend contracts reject historical cd5721e (KVM preflight error and KVM
argv assertion); those logs remain separate from passing patched-source results.
No C# inputs changed, so .NET format/build checks do not apply to this patch.

## Fixed private source selection after the first TCG activation

Run `45254358055` at `faaf8fa` passed all offline contracts but stopped at
`setup-source-policy`, before APT, image retrieval or VM creation. Its runner
reported image `ubuntu24/20261116.376`. The corresponding
[image provisioning script](https://github.com/actions/runner-images/blob/ubuntu24/20261116.376/images/ubuntu/scripts/build/configure-apt-sources.sh)
rewrites direct Ubuntu URIs to `mirror+file:/etc/apt/apt-mirrors.txt`; the earlier
validator accepts direct URLs only. The run did not upload its actual source-list
bytes, so the image recipe establishes the incompatible assumption rather than a
byte-for-byte observation of that runner file.

Setup now constructs the fixed private HTTPS source file described above. It
never reads or rewrites the runner's source or mirror lists, and it still uses the
existing Ubuntu archive keyring and signature authentication. The same two APT
commands, five-package allowlist, private indexes, credential stripping, deadlines,
no automatic retry, and primary-failure preservation remain. No source/trust bypass
or hardware privilege grant is introduced.

The new offline regression runs the setup boundary with ambient source/mirror
reads forbidden, verifies both staged HTTPS origins/suites/components/key binding,
and checks the real generated APT argv. It fails against the old implementation
at the ambient source read. Local suite: 36 passed, one Linux-only skip; syntax,
actionlint, specification and whitespace checks passed. No local APT, privileged
operation or VM ran. Hosted installation/boot/SSH/teardown and final independent
acceptance remain outstanding; the original failed run and raw artifact digest
are retained under `.artifacts/debian-15-integration/tcg-run-45254358055/`.

## Bounded HTTPS redirects for the pinned image

Corrected-source run `45257568353` at `fb3cfbd` passed the fixed official APT
update/install, recorded package versions and passed post-setup prerequisites.
The fixture then stopped at `image-hash`, before disk parsing, overlay creation
or guest execution. Cleanup reaped the children and deleted its owned workspace.
The original hash guard remained effective; an unexpected download never booted.

A read-only request to the pinned image URL returned HTTP302 with an HTTPS
Debian mirror location. The old curl command did not follow redirects, so a
successful curl exit did not establish an image download. The correction follows
at most three redirects, restricts both initial and redirect protocols to HTTPS,
and preserves certificate verification, file-size/connect/whole-transfer limits
and zero retries. It uses ordinary `--location`, not credential-forwarding
`--location-trusted`; child environment/private HOME remain noncredential.
See the [curl manual](https://curl.se/docs/manpage.html) for these flags.
The pinned URL, image SHA512, checksum-file digest and metadata digest are unchanged.
Byte length and computed image SHA512 are now emitted as bounded nonsecret
provenance diagnostics before the mandatory hash comparison; raw content is not
logged and a mismatch still stops before any image/VM command.

Local verification: 38 tests passed, one Linux-only skip. New cases verify the
bounded HTTPS redirect command and refusal of redirect-body/corrupt bytes before
disk/VM commands. The redirect contract rejects the old implementation. A real
read-only retrieval through the corrected harness validated checksum/metadata and
retrieved 341,180,416 image bytes matching the original pinned SHA512. No guest,
APT or privileged operation ran locally. Raw download/provenance and failure
records are retained in `.artifacts/debian-15-integration/tcg-run-45257568353/`.
Corrected hosted boot/SSH/teardown and final independent acceptance remain open.

## Preserve the first fixture failure during teardown

Historical run `45261150002` at `8262a59` subsequently passed package setup,
the unchanged image pins, TCG boot, strict authenticated Debian13/systemd probe,
reaping and owned workspace deletion. Its raw evidence remains attributed to
that execution head; ancestry-only restack `6d26e36` preserved all fixture inputs.
It is infrastructure proof, not helper/enrollment or product acceptance.

Final diagnostics review UX-PR17-01 reproduced a separate failure path: when a
probe failed and reaping or workspace deletion also failed, the latter exception
hid the original error from uploaded sanitized events. The fixture now records
the primary failure before teardown, records secondary cleanup/deletion failures
separately, and rethrows the original exception. Unknown exception details remain
suppressed. Successful reaping is still required before deleting disks. Any
cleanup failure prevents smoke success; remaining workspace responsibility is
explicitly assigned to ephemeral runner destruction. No retry or deadline change
is introduced. Regression controls inject failures at these boundaries; they do
not claim a new guest execution or naturally occurring OS cleanup failure.

Correction validation: 44 tests passed, one Linux-only descendant case skipped
on macOS; the six new main-boundary cases include successful teardown as well
as independent primary/cleanup/deletion errors. Three new regressions reject
`6d26e36`. Python syntax, specification and whitespace checks passed. No C# build
input changed. The old successful VM run remains narrower, separately attributed
evidence; the corrected main wrapper has not been executed in a fresh VM run.
Final current-head CI and new independent role verdicts remain required.

## Explicit synthetic policy-session campaign (issue #14)

The ordinary smoke command and `run-debian-fixture` label retain their existing
scope. Only the new `policy-session` job accepts the distinct manual dispatch
acknowledgment `RUN-SYNTHETIC-POLICY-SESSION-12-CASES`, owner actor ID `38875853`,
exact reviewed 40-hex commit equal to the dispatch SHA, and an externally reviewed
manifest SHA256. No existing label, push or reopen enables the campaign. This is
isolated test infrastructure, not a product installer/helper or qualified agent.

Before runner setup, CI constructs the external manifest from `git show` of the
fixed `common.SOURCE_FILES` allowlist at that exact commit. Canonical bytes are
UTF-8 `json.dumps(value, sort_keys=True, separators=(',', ':'))` without a newline,
with schema 1, commit and files mapping each relative filename to SHA256. The
constructed digest must equal the independently supplied review digest. This
construction does not establish review authority: the owner/independent review
must supply it after the final signed source commit. The manifest stays outside
the payload to avoid a self-hash cycle. `prepare` additionally verifies exact
current payload membership and committed bytes; this uncommitted patch cannot
activate as reviewed source. No PR-body or ambient environment authorization is
accepted. Invalid input stops before setup, download, cloud-config or VM effects.

The host CLI accepts `--policy-session-campaign` only alongside the existing
execution opt-in, `--campaign-acknowledge`, `--reviewed-source-commit`,
`--reviewed-manifest-sha256`, `--reviewed-manifest` and a private existing
`--campaign-evidence` directory. `--campaign-preflight` validates this contract
without execution; `--write-reviewed-manifest` constructs only the external
manifest and requires the supplied expected digest. Neither command constitutes
source review. Fixed APT commands, provenance/image pins, nonroot TCG, loopback
networking, unprivileged fixture SSH, caps and deadlines remain unchanged.

Only campaign mode installs the reviewed static payload in the disposable guest
and runs the fixed provision entry in cloud-final. Firmware binds nonce, image,
source and canonical template. Guest guards precede synthetic service effects.
The fixture login receives neither root SSH nor sudo. It performs the original
read-only strict SSH smoke plus a deliberate wrong-host-key denial check.

Stage-labelled receipts carry bounded base64 JSON from this invocation's memory,
including partial/failure reports, before and after signal-handler restoration.
Provision never reads `campaign.json` for export, so a collision cannot export an
unknown prior owner's report. There is no arbitrary guest-file extraction. The
host accepts only fixed schemas, exact identities/case/gap sets, strict duplicate
rejection, bounded depth/membership/frame/total sizes, ordered stages and one
nonce/source-bound exit record. Readiness alone never establishes campaign success.
Raw serial (which may include SSH material), cloud data, disks, private keys and
environment are never artifacts. Only validated campaign reports and a fixed
summary are written as `campaign-raw.json` and `campaign-summary.json`.
The summary remains UNRESOLVED with a separate candidate flag: only a matching
`campaign-complete` record with `synthetic_protocol: CANDIDATE` in sanitized
status.jsonl after successful artifact flushes can support a synthetic protocol
PASS only together with successful terminal host/job exit. The record requires
host exit0; it never independently publishes PASS. An after-effect artifact-write
failure or cancellation cannot authorize PASS from a partial file or record.

The host kills/reaps the outer VM on every outcome, before disk deletion and any
protocol PASS publication. Uncertain death retains the private workspace for
ephemeral runner destruction. Guest command containment remains UNKNOWN. A narrow
Synthetic protocol PASS requires two bound receipts, exit2, all twelve raw cases
individually checked, no guest or host failure, strict SSH and wrong-key denial,
and successful outer cleanup. Missing/failed/unknown evidence stays UNRESOLVED;
exit2 alone never passes. `qualification: INCOMPLETE` and eleven NOT RUN gaps
remain machine-readable even after a protocol PASS. Logs and job names do not
claim installed-runtime/product qualification or issue14 acceptance.

Host finalization records SIGINT/SIGTERM without throwing while mandatory reaping,
workspace deletion and artifact writes run. Cancellation is sticky and preserves
any earlier primary failure. Once mandatory cleanup/publication finishes, terminal
handlers raise that same first cause, including cancellation during the candidate
record write or return. A stale summary candidate flag or an already-written
candidate record never overrides failed host/job exit. No signal handler performs
I/O. No automatic retry follows cancellation.

Raw pidfd rows require the exact nine-field unsigned tuple, pidfd link type,
non-thread read/write flags, two distinct device/inode identities, and a common
epoch matching the retained journal. Internally correlated corrupt journal/case
records are refused too. Incremental serial reads tolerate a pending frame, but
cannot authorize completion from it. After outer reaping and before closing the
private serial descriptor or deleting disks, final validation checks the full
bounded output and rejects recognized unterminated evidence/exit/receipt frames.
Every later case tuple and journal identity must match the validated originals
recursively with exact JSON types. Booleans and floats cannot stand in for unsigned
integers, and integer/float values cannot stand in for HUP booleans. Ordinary
Python numeric equality is not evidence equivalence.

All twelve new VM cases remain NOT RUN until parent-owned dispatch after a new
independent source/artifact preflight. Offline tests and diagnostic metrics are
not Linux/systemd execution proof. Parent owns signing/shipping, dispatch, new CI
and native evidence, all three FINAL QA roles and merge. Rollback before shipping
is removal/reversal of this owned uncommitted patch; after future execution the
VM/overlay and runner destruction are the rollback boundaries. Never clear
uncertain escrow or rerun an occupied campaign.
