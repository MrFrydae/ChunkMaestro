# Persisted registration timestamp refusal

Issue 3 / PR 6, Q163–164 and Q189–195. Automated finding
[discussion_r4595427608](https://github.com/MrFrydae/ChunkMaestro/pull/6#discussion_r4595427608)
was reproduced against published base
`cbe546af16a1bb8ea3c425932e85f307d293e911`. This document records local
implementation evidence, not independent QA or qualification.

Before the correction, the new real-SQLite suite had 12 failed refusal cases:
startup accepted invalid timestamps in Hosts or Instances. Its three supported
boundary cases passed. The suite disposes an unexpectedly returned store even
when the refusal assertion fails.

`SupportedSchema.VerifyAsync` now rejects non-integer timestamps and integers
outside `DateTimeOffset`'s Unix-millisecond range, for both tables, before a store
is returned or a pending migration starts. The bounds come from the framework's
MinValue/MaxValue conversion. It returns sanitized `CorruptDatabase` guidance to
preserve files for reviewed recovery. It does not change schema, migrations,
stored rows, permissions or the recovery policy.

`RegistrationTimestampTests` covers long extrema, the immediately unsupported
values on both sides, text and fractional SQLite values, valid minimum/maximum
milliseconds and the Unix epoch. Invalid cases repeat startup refusal, compare
database SHA-256, raw SQLite type/value and completed-backup membership, then
perform explicit fixture recovery and reopen the public inventory. Valid
boundaries remain readable for hosts and instances. All test data is disposable.

Executed on macOS arm64 with pinned SDK 10.0.404:

```sh
/Users/derek/.local/share/chunkmaestro/dotnet/dotnet test tests/ChunkMaestro.PersistenceTests/ChunkMaestro.PersistenceTests.csproj -c Release --no-restore --filter FullyQualifiedName~RegistrationTimestampTests --logger trx --results-directory .artifacts/timestamp-before
CI=true CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet DOTNET_SYSTEM_NET_DISABLEIPV6=1 python3 tooling/build/build.py verify
```

The first command, before the production correction, reported **12 failed,
3 passed, zero skipped**. The final repository verifier passed locked restore,
Release build with zero warnings/errors, syntax/style and generated-inclusive
formatting, and **210 tests, zero failed/skipped**. Before/after TRX records remain
under `.artifacts/timestamp-before` and `.artifacts/tests/0` respectively.

Windows, mutation/coverage qualification and independent reviews of this
correction remain unexecuted. No approval or merge is claimed.
