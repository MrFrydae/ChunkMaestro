# Corrupt-storage recovery guidance contract

Issue #3; source Q153/Q163–164/Q189–191. Implementation test evidence only;
independent storage QA and current-head qualification remain required.

The [aggregate at `d064403`](https://github.com/MrFrydae/ChunkMaestro/actions/runs/43739907076/job/132961059479)
completed both platform collections and failed only the mutation requirement:
622/778 actual attributed kills (79.9486%), below 80%. Persistence line coverage
was 719/742 (96.90%); branch coverage was 260/278 (93.53%). No rounding or timeout
credit can turn this result into a pass.

Both immutable platform artifacts were downloaded and their archive SHA-256
verified against the gate's recorded digests:

- Windows artifact `14180727343`:
  `3072a6ae190d8eff45facd5c68ee9fcd97b35e69802f0d3b26bbcbbf6e719e53`.
- macOS artifact `14174158096`:
  `0457b5d3c00cc1c6f5c0811aed4745bad6a29150453d270e42493cd081a7f724`.

All four raw official/private reports show LocalFleetStore mutations 212 and 213
surviving: replacing or reversing the corruption-specific diagnostic condition
can return generic initialization guidance while retaining the correct structured
failure category. The existing public test already supplies actual non-SQLite
bytes, requires `CorruptDatabase`, and verifies that those bytes remain unchanged.
Its broad nonempty/sanitized-message checks did not distinguish recovery guidance
from a generic availability message.

The regression now also requires guidance about **sidecars** and **recovery**.
It does not copy the entire message or inspect private counters/source text.
Production code, migrations, mutation filters, denominators and thresholds are
unchanged. The same real corruption/refusal/data-preservation case is exercised.

Local macOS arm64 verification used the pinned SDK:
`CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet
DOTNET_SYSTEM_NET_DISABLEIPV6=1 python3 tooling/build/build.py verify`.
Locked restore, Release build, syntax/format checks and 181 tests passed with
zero warnings, errors or skips. Raw log: `/tmp/pr6-corruption-recovery-verify.log`.
No new mutation kill or combined-platform pass is claimed before fresh collection.
