# Persisted identity validation

Issue 3 / PR 6; Q163–164 and Q189–195. Addresses
[discussion_r4601715043](https://github.com/MrFrydae/ChunkMaestro/pull/6#discussion_r4601715043).
The canonical encoding correction also addresses
[discussion_r4603059439](https://github.com/MrFrydae/ChunkMaestro/pull/6#discussion_r4603059439).

Startup validates the supported schema and foreign keys, then reads all seven
persisted GUID columns through the SQLite provider's `GetGuid` conversion.
Every value must also be stored as the provider's canonical text encoding:
uppercase hyphenated `D` format. Alternate valid spellings and 16-byte GUID blobs
are refused: SQLite compares keys literally, while public callers compare `Guid`
values, so accepting both encodings can permit duplicate public identities.
Malformed conversions, empty identities and noncanonical encodings are rejected as `CorruptDatabase`
with sanitized preservation/recovery guidance before returning a usable store
or starting a pending migration. No schema, migration or stored value is repaired.
Conversion alone is insufficient; both semantic identity and its persisted
representation must agree with normal provider writes.

`PersistedIdentityTests` creates real disposable SQLite data and damages fleet,
host or instance primary identities using malformed text, an empty GUID, an
integer or a short blob. Dependents are removed by the fixture before damage;
`PRAGMA foreign_key_check` remains empty, so referential checks alone cannot
explain refusal. Each case proves repeated startup refusal, unchanged database
SHA-256/raw identity/backups, released controller ownership, and successful public
inventory reopening only after explicit fixture recovery.

The later canonical-format regression adds compact `N`, braced `B`, lowercase
hyphenated and full-length GUID-blob cases for each primary identity. Fixed GUIDs
containing letters make case-sensitive fixtures deterministic. These twelve new
cases failed against `7fc110d` before the correction because startup returned a
usable store; the prior twelve malformed/empty cases passed. The before/after
TRX files are under `.artifacts/guid-canonical-before` and
`.artifacts/guid-canonical-after`. All twenty-four focused cases then passed,
zero failures/skips, including preservation and explicit recovery. This changes
neither the schema nor any committed migration; data is refused without repair.

On macOS arm64 / pinned SDK 10.0.404, the new 12-case suite against the original
`2555cb6` production source failed all 12 refusal assertions: startup returned a
store. Before evidence is retained under `.artifacts/identity-before`.

After the correction:

```sh
CI=true CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet DOTNET_SYSTEM_NET_DISABLEIPV6=1 python3 tooling/build/build.py verify
```

The original correction passed locked restore, warning-free Release build,
syntax/style/generated-inclusive formatting and **222 tests, zero failures/skips**.
The later canonical correction passed the same full verifier with **234 tests,
zero failures/skips**, locked restore, zero build warnings/errors, syntax/style
and generated-inclusive formatting. The raw log is
`.artifacts/guid-canonical-verify.log`; the machine delivery record and hashes
are in `.artifacts/guid-canonical-delivery.json`. Windows, current-head
independent QA, and full coverage/mutation/native/volume qualification remain
outstanding. No merge or full product acceptance is claimed.
