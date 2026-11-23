# Debian 13 systemd fixture — issue #15

This is bounded test infrastructure for Q145, Q151, Q158, Q186, Q189,
Q191–195 and later #13/#14 work. The owner has no disposable Debian VM.
The patch prepares an explicitly activated workflow; it has **not executed a VM**, enrollment,
the product helper, schedules, permissions or recovery acceptance. No production
host, retired storage workflow, product code, package graph or existing CI changes.

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
lists standard `ubuntu-24.04` x64 runners and Linux hardware acceleration support.
This supports choosing the standard ephemeral runner; it does not prove usable
nested KVM/QEMU on a particular allocation. The harness requires Linux x86_64,
non-root execution, an openable character `/dev/kvm` and all of `curl`,
`qemu-system-x86_64`, `qemu-img`, `cloud-localds`, `ssh-keygen`, `ssh` in
`/usr/bin:/bin`, plus Python 3.11+. It never installs tools or grants KVM access.
A missing tool or KVM permission is a hard failure for the owner/lead to assess.
No udev rules, chmod, group edits, sudo, persistent settings, TCG, container
substitution, self-hosted runner or paid larger pool is configured. A future
runner-only ACL exception would require a separately reviewed bounded change.

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
they can edit workflows. Preparation has not activated either route or executed a VM.

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
or verification bypass. Boot polls for the SSH banner; the authenticated probe
runs once. A host-key mismatch fails that probe; it never rewrites trust.

| Resource/phase | Hard bound |
| --- | --- |
| Runner | Standard ubuntu-24.04; job 25 minutes |
| Harness work | SIGALRM 1200 seconds; per-phase deadlines also enforced |
| Guest | 2 vCPUs, 2048 MiB RAM, KVM only |
| Disk | Base download ≤1 GiB; guest virtual disk/overlay ≤4 GiB; ≥10 GiB free required |
| Child files | Each ≤4 GiB, core dumps disabled; command output over 1 MiB fails at next poll |
| Child CPU | 900 CPU seconds per subprocess; VM wall clock bounded by harness |
| Metadata | Checksums ≤64 KiB, metadata ≤128 KiB, each download 30 seconds |
| Image download | 300 seconds, connect 20 seconds, HTTPS only, no retries/redirects |
| Tools/probe | 30 seconds each; SSH connect 5 seconds, one attempt |
| Boot readiness | 240 seconds, fixed 5-second banner polling, no reboot/retry |
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
and never copied into evidence. Smoke checks Debian ID/version, fixture username,
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

The offline suite checks opt-in/platform/tool/KVM denials, provenance tampering
and identity, child credential stripping, strict host-key options, seed trust,
QEMU isolation arguments, timeout termination, sanitized failures, and Linux
subreaper behavior (explicitly skipped on macOS). Mocks of QEMU are unit contract
tests, not VM execution. The Linux offline test must run before a hosted smoke.
Actual hosted QEMU boot, cloud-init behavior, host-key mismatch over real SSH,
KVM availability/access, resource use and complete descendant teardown remain
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

Rollback: while uncommitted, remove only the three new owned paths after saving
review evidence; after integration, use a reviewed revert. No installed software,
production data, host settings or existing workflow needs rollback. Keep initial
failure logs outside tracked source. Independent QA and hosted proof remain lead
integration gates.

Lead integration retains the original six-file delivery and hashes under
`.artifacts/debian-15-integration/original-delivery/`. The lead independently
retrieved the dated official checksum/metadata, verified the pinned provenance,
and evaluated the actual workflow condition across 384 event/actor/repository/
label combinations with zero authorization mismatches. Local offline validation
still reports 15 passing tests and one Linux-only skip; actionlint and specification
checks pass. This policy check is not GitHub execution, a VM boot or host-key
integration proof. No activation label or dispatch has been issued.

The bounded independent integrity preflight at `9f615fa` passed with one low
finding, SR-01: background QEMU output was not included in the 1 MiB polling check.
The lead reproduced it with a failing regression, then added checks of every owned
child's output during boot, command supervision and the final shutdown boundary.
The corrected local suite passes 16 cases with one Linux-only skip. The prior
exact-head hosted Linux suite passed all 16 original cases; it is not execution
proof for the new regression. Original review and failure logs remain retained.
Runner tool/KVM availability, real boot/host-key behavior, QEMU teardown and final
independent acceptance are still pending; no VM has been activated.
