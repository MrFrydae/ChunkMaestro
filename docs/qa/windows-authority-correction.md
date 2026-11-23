# Windows authority correction

Issue #3, source Q149/Q153/Q163–164/Q189–195. This is implementation evidence,
not independent QA or storage qualification.

At `9db6715c2d49233833188201a450dbdb5f90daf6`, the
[Windows managed job](https://github.com/MrFrydae/ChunkMaestro/actions/runs/43713987546/job/132811444358)
executed 184 tests: 181 passed, three failed, zero skipped. The failures exposed
two authority defects and one fixture assumption:

- `BindFiles` accepted a closed controller on Windows. It now checks the native
  handle before assigning it, and `Verify` also refuses a subsequently closed
  controller. A regression closes a bound controller, verifies refusal, then
  explicitly rebinds an open controller and verifies recovery.
- Native identity handles requested zero access. These metadata-only handles did
  not enforce the intended denial of rename/delete sharing. Windows opens now
  request `FILE_READ_DATA` / `FILE_LIST_DIRECTORY` (the same access-mask bit) while
  retaining read/write sharing and withholding delete sharing. This uses the
  current user's existing access to confined storage; it changes no ACL or
  privilege. The existing regression requires rename refusal while bound,
  successful rename after disposal, and continued caller ownership of its
  controller stream. Microsoft's
  [sharing algorithm](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/8c0e3f4f-0729-49f4-a14d-7f7add593819)
  conditions these checks on the access rights of both handles.
- `Path.GetRelativePath` can return an absolute path when the checkout and temp
  directory use different Windows drives. The relative-path regression now
  constructs a relative path directly under a unique `.artifacts/tests` target,
  verifies refusal before creation, and cleans up only that target if the
  assertion fails. It does not change the working directory or the production
  requirement for fully qualified storage paths.

Local macOS arm64 validation used the pinned SDK and
`CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet
DOTNET_SYSTEM_NET_DISABLEIPV6=1 python3 tooling/build/build.py verify`.
Locked restore, Release build, syntax/format verification and **181 tests** passed
with zero warnings, errors or skips. Raw output is retained at
`/tmp/pr6-authority-windows-fix-verify.log`. This does not prove Windows execution.

The earlier 517/774 mutation measurement belongs to the prior head. Native
Windows execution, new source-bound mutation/coverage evidence, other applicable
safety acceptance and all three independent QA roles remain required. No
threshold, exclusion, test expectation for authority denial, or review gate was
relaxed. Recovery remains explicit: preserve rejected storage and reopen only
after validating ownership and a compatible recovery copy.

## Controller process handoff

The subsequent
[Windows managed job at `7603103`](https://github.com/MrFrydae/ChunkMaestro/actions/runs/43714588638/job/132813252061)
executed 185 tests: 184 passed, one failed, zero skipped. All three original
failures passed. The remaining failure was the public reopen immediately after
killing and awaiting the competing controller process; it reported
`ControllerInUse`. The sanitized exception does not establish which native
sharing failure occurred or its duration, so delayed release is not claimed as
a confirmed root cause.

The process test now bounds exit waiting to 30 seconds, then observes exclusive
access to the existing lock file before opening SQLite. It retries only native
Windows sharing/lock violations (32/33), with a ten-second deadline. Missing files,
other errors and persistent locks fail with the original native diagnostic.
The probe uses `FileMode.Open`, never recreates or deletes the lock, and performs
no database operation. The original assertion that a live second controller is
refused remains. This is test synchronization and failure diagnosis; production
locking and mutation retry behavior are unchanged. Windows CI must still prove
the handoff succeeds within the bound.
