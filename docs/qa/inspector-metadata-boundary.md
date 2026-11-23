# Issue14 native metadata foundation

Worker base: `0df7245e52e910d12be00c4155c53ee03be6f03e`. Bounded
implementation; no helper, provider, deployment or issue acceptance. Trace:
Q20–22, Q31, Q151, Q158, Q168, Q189–195 and the retained-readable-FD correction
in `/tmp/chunkmaestro-14-fd-correction-0b4fe62/REPORT.md`. All four supplied
artifact manifests were verified: original design 25 entries, original FAIL 13,
correction 5, corrected independent design PASS 25. Design PASS is not runtime
proof. No managed Contracts changes or public capability are included.

## Contract and lifetime

`metadata.h` is an internal native API, not a managed ABI or policy API. The
internal caller supplies an already retained directory and expected native stat
snapshot. These inputs do not authenticate root policy or a safe anchor. Adoption
consumes the exclusively owned FD, sets the caller's slot to -1 even on failure,
and closes it on failure. Requires a readable directory with CLOEXEC; rejects
O_PATH, writable, non-directory and non-CLOEXEC descriptors. Output handle slots
must be empty and distinct from inputs. No concurrent use/dispose/FD close is
allowed, including by signal handlers. There is no asynchronous close cancellation.
Destroy nulls the handle slot; it never retries Linux close after interruption.

A child is exactly one trusted internal basename: 1..64 ASCII letters/digits,
underscore, hyphen or period, excluding `.` and `..`. Request/decoded-policy paths
are forbidden by the caller contract. No arbitrary path, recursion or fallback is
available. The child retains its own CLOEXEC duplicate of the parent's FD, so the
caller can dispose the parent handle after acquisition. openat2 requires BENEATH,
NO_SYMLINKS and NO_MAGICLINKS with O_RDONLY, O_DIRECTORY, O_CLOEXEC and O_NOFOLLOW.
Unsupported/denied/interrupted resolution refuses. Device changes refuse.

Snapshots compare device/inode/type/mode/uid/gid/link count and nanosecond
ctime/mtime. The initial parent and target binding remain fixed. Check reopens
through the retained parent, compares retained parent/target/reopened target,
then performs two bounded sweeps of both fixed ACL names on all three FDs with
snapshot comparisons after each pair. Acquisition also checks the parent before
and after. Any observed replacement/metadata/ACL drift refuses without adopting
new state. Positive ACL/error results may return immediately because they already
refuse; there is no usable success requiring their after-check. Matching snapshots
do not prove atomicity, exclude privileged ABA/namespace changes, or authenticate
an installation. A parent anchor adopted without a retained grandparent is not
itself pathname-authenticated; qualifying its ancestry remains the caller's work.

Only `system.posix_acl_access` and `system.posix_acl_default` are queried. Each
fgetxattr has a fixed 4096-byte stack buffer, no allocation-by-size, retry, parser,
or attribute-name input. Any present attribute, even zero-length, yields
`CM_ACL_PRESENT` and must disallow a protected V1 path. Oversize returns ERANGE and
refuses. EBADF/EACCES/EPERM/EOPNOTSUPP/EINTR and every error except ENODATA yield
unavailable with raw internal errno. An O_PATH fgetxattr EBADF is never absence.

When all queries return ENODATA, the only output is
`CM_NO_DATA_UNQUALIFIED`. ENODATA can hide attribute access. There is no trusted
boolean, qualification setter, authorization result or Observed result. A clean
fixture cannot qualify another filesystem, mount, namespace or LSM context.
`CM_FACTS` describes acquisition/snapshot only. All results remain unusable as
service/game observation or permission. FS/namespace/LSM qualification is absent.

No enumeration, game-file read, digest, socket, own-proc lookup, bus, peer query,
policy loader, root launcher, installer, grants, exec or managed integration is
implemented. Filesystem syscalls may block; no deadline or worker termination
claim is made. No third-party library, NuGet dependency, package install or SDK
change is needed: Linux libc and Linux UAPI openat2 headers are prerequisites.

## Reproduce on ordinary Linux

From repository root, as an ordinary nonroot Ubuntu user with compiler, Linux
headers, binutils and ASan/UBSan runtimes already available:

```sh
CC=cc sh tests/ChunkMaestro.InspectorNativeTests/run.sh
# Or the same command with CC=clang on a separately qualified compiler image.
```

The self-contained script compiles C11 with `-Wall -Wextra -Werror -Wpedantic`,
plain and ASan/UBSan builds, each in kernel-only and separate injected-control
variants; then inspects a separately compiled production object's symbols. No
.NET build/solution integration is needed. Unsupported sanitizers fail the command;
there is no silent retry without instrumentation. Exit 77 means NOT RUN, not PASS.
CI must treat every nonzero exit as failure. Script is invoked with `sh` and needs
no executable-bit change. Temporary build output is cleaned by its trap; failed
fixture assertions leave the owned `/tmp/cm-metadata-*` directory for diagnosis.
Fixtures are user-owned and unrelated live paths are never permission-modified.

Kernel assertions cover readable vs same-inode O_PATH/EBADF, closed/non-directory/
foreign/non-CLOEXEC descriptors, ownership transfer/disposal/reuse, invalid names,
symlinks/escape, independent parent lifetime, repeated FD acquisition without
leaks, retained/reopened snapshot identity, actual access/default ACL presence and
ACL drift, parent ACL, actual nonroot directory DAC denial, chmod drift and inode
replacement. Harness-only sentinel reads verify its owned data survived; production
contains no content reads. The production import check rejects read/enumeration,
network/exec and permission/xattr mutation symbols. This check complements source
review, not a claim to syscall tracing or whole-program behavioral proof.

The separately compiled controls route the *same* production state code through
two test syscall wrappers. They verify exact openat2 flags and inject ENOSYS,
EPERM, EACCES, EINTR and EINVAL resolution failures, plus EBADF/EACCES/EPERM/ERANGE/
EOPNOTSUPP/EINTR/EIO/ENOMEM at all 12 ACL query positions, zero-byte presence at
all positions and mid-query metadata drift. Oversize/unsupported/interrupt/LSM-like
errors in these controls prove error decisions only, not kernel/LSM behavior.
Test controls have no production definitions or exports without the explicit test
compile definition; the runner checks the production object.

## Local evidence and remaining gates

Local host: Darwin 27.2.0 arm64, Apple clang 21.0.0. Native runner returned **77**:
`NOT RUN: Linux kernel/openat2/xattr/ACL/sanitizer tests require Linux`.
All Linux compilation, actual kernel cases, injected controls and sanitizers are
**NOT RUN** locally. Shell syntax and diff whitespace checks are static evidence
only. Repository `build.py build` and `build.py format` both exited 1: first on
PATH SDK resolution (underlying dotnet exit 155), then with the verified existing
private SDK 10.0.404 on missing restore assets (NETSDK1004, 17 project errors)
and unresolved dependency symbols (underlying formatter exit 2). No restore or
install was attempted. These baseline managed checks do not compile this native
code. Exact logs and commands are in the sealed external handoff.
No Linux VM is available and none was created/activated. No deployment,
live host, permission grant, GitHub write, commit or push occurred.

Parent must add a standard Ubuntu job using the exact runner command above and
include this native path in the high-risk QA classifier. The separate handoff
contains the exact proposed CI/classifier diff, unapplied. No shared build,
solution, workflow or classifier file was edited here. No coverage/mutation score
is a release acceptance gate. Three independent implementation QA verdicts,
real Linux execution and later Debian full helper D01–D12 acceptance remain
pending. Socket/proc, systemd/peer/policy/launch proofs remain followups.

Rollback is removal of these two isolated native/test directories and this note;
no runtime consumer or deployed state exists to migrate or repair.

## Lead CI integration

The worker's original six-file patch and all 24 handoff artifacts were verified
before integration. Parent integration adds `inspector-metadata (Linux)` on the
standard Ubuntu runner, using the existing credential-stripping command supervisor
with a 180-second process-tree deadline and a six-minute job cap. Shell pipefail
preserves compiler/test/exit-77 failures through the diagnostic tee. No package
installation, VM, sudo, grant or live-host access is introduced. The same pinned
checkout/Python/artifact Actions are reused.

The independent QA job now depends on that job and records its outcome in the
machine artifact. Missing, failed, skipped, canceled or incomplete native execution
blocks even a complete attestation with green storage; an executable regression
checks this through the actual QA main function and written verdict. Native and
native-test source paths automatically require all three QA roles.

Managed jobs now explicitly execute Contracts and OwnerVerification tests and
retain their TRX on every existing platform. The earlier workflow built these
projects but omitted their test invocations. Mac-only cases remain explicit skips
on other platforms, never claimed as passes; no real authentication is invoked.

Fresh lead local validation: 22 build-tooling tests, 50 CI-tooling tests, 18
agent-tooling tests, pinned Release build, syntax/style, formatter verification,
specification, Ruby YAML parsing and cached actionlint passed. Initial Python YAML
parsing lacked PyYAML; built-in Ruby YAML plus actionlint supplied verification
without installing dependencies. The native runner again exited 77 on macOS,
so Linux compilation/kernel/sanitizer results remain pending hosted execution.
The worker's original SDK/assets failures remain unchanged historical evidence.
Logs, source bindings and original hashes are under
`.artifacts/inspector-native-integration/`. No final independent implementation QA
or Debian helper acceptance follows from these local checks.
