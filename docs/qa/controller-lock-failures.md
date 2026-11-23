# Controller lock failure categories

Issue 3; Q14, Q153, Q190–195. Addresses automated review discussion
[4589510070](https://github.com/MrFrydae/ChunkMaestro/pull/6#discussion_r4589510070).
Implementation validation only; full storage qualification and independent QA
remain required.

The controller-lock catch previously translated every `IOException` into
`ControllerInUse`. File creation can instead fail because the filesystem is
read-only, full, over quota or unavailable. The translation now accepts only
Windows sharing/lock HRESULTs (`0x80070020`, `0x80070021`) or native macOS
`EWOULDBLOCK` (raw errno 35). Other I/O exceptions reach the existing sanitized
`StorageUnavailable` handler; no automatic retry, file deletion or repair occurs.
Platform-specific codes do not cross-match. Cancellation and lock release remain
unchanged.

The pinned [.NET Unix file-handle implementation](https://github.com/dotnet/runtime/blob/v10.0.15/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs)
uses nonblocking flock, and its [error translation](https://github.com/dotnet/runtime/blob/v10.0.15/src/libraries/Common/src/Interop/Unix/Interop.IOErrors.cs)
preserves the native errno for sharing refusal.

## Executed validation

On macOS arm64, SDK 10.0.404:

```sh
CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet DOTNET_SYSTEM_NET_DISABLEIPV6=1 python3 tooling/build/build.py verify
```

Result: locked restore, Release build, syntax and generated-inclusive formatting
passed with zero warnings/errors; **195 tests passed, zero failed/skipped**.
Fourteen new cases cover native exclusive-handle refusal and explicit reopen,
platform-specific contention codes, and synthetic disk-full/quota/device/read-only
and generic I/O codes. Synthetic code cases do not claim hardware fault injection.
The existing separate-process controller contention test also passed.

An external fixture created a new disposable 64 MiB HFS+ image, prepared an empty
owner-scoped directory, detached that image and reattached it read-only. A real
controller-file open returned `IOException.HResult == 30` (`EROFS`); public
`LocalFleetStore.OpenAsync` returned `StorageUnavailable`, sanitized guidance and
no backup path. The empty directory remained unchanged. Cleanup verified and
detached only this fixture's exact image/device/mount, then removed its image.
No existing volume or user database was modified.

The fixture script and raw command/stdout/stderr/result files are retained under
`/tmp/chunkmaestro-lock-io-probe.py` and its reported
`/private/tmp/chunkmaestro-lock-io-*` fixture directory. This manually executed
native refusal complements the committed regression suite; it is not a portable
CI acceptance result. Windows runtime, real quota/device faults and fresh safety
metrics remain unexecuted. No new mutation kill or full-PR QA PASS is claimed.

Recovery: revert this scoped correction to restore the former failure mapping.
There is no schema, database or migration change.
