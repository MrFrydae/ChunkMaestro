# Persisted identity validation

Issue 3 / PR 6; Q163–164 and Q189–195. Addresses
[discussion_r4601715043](https://github.com/MrFrydae/ChunkMaestro/pull/6#discussion_r4601715043).

Startup validates the supported schema and foreign keys, then reads all seven
persisted GUID columns through the SQLite provider's `GetGuid` conversion.
Malformed conversions and empty identities are rejected as `CorruptDatabase`
with sanitized preservation/recovery guidance before returning a usable store
or starting a pending migration. No schema, migration or stored value is repaired.
Using the provider conversion keeps validation consistent with materialization.

`PersistedIdentityTests` creates real disposable SQLite data and damages fleet,
host or instance primary identities using malformed text, an empty GUID, an
integer or a short blob. Dependents are removed by the fixture before damage;
`PRAGMA foreign_key_check` remains empty, so referential checks alone cannot
explain refusal. Each case proves repeated startup refusal, unchanged database
SHA-256/raw identity/backups, released controller ownership, and successful public
inventory reopening only after explicit fixture recovery.

On macOS arm64 / pinned SDK 10.0.404, the new 12-case suite against the original
`2555cb6` production source failed all 12 refusal assertions: startup returned a
store. Before evidence is retained under `.artifacts/identity-before`.

After the correction:

```sh
CI=true CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet DOTNET_SYSTEM_NET_DISABLEIPV6=1 python3 tooling/build/build.py verify
```

Locked restore, warning-free Release build, syntax/style/generated-inclusive
formatting and **222 tests, zero failures/skips** passed. Windows, current-head
independent QA, and full coverage/mutation/native/volume qualification remain
outstanding. No merge or full product acceptance is claimed.
