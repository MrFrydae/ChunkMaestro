# CMPJ V2 offline codec subslice

Status: implementation validation only; uncommitted at frozen accepted main
`7fe084bc14ce87d17182cd32851a0f904488846a`. No independent FINAL verdict,
operational admission or full issue 14 acceptance is supplied.

This patch adds internal immutable unqualified correlation/metadata values, an
exact 288-byte V2 codec and pure tuple comparisons. There is no IO, native API,
factory, owner, runtime consumer, migration or action result. SHA256 detects
corruption; it does not authenticate a file, process, measurement or proof.
Serialized proof bits remain claims whose numerical consistency can be checked.
Both acquisition architecture codes are inspectable offline. The caller-supplied
boot/profile comparison cannot establish which OS/profile is running.

The V2 layout preserves V1 offsets 0–201 and mixed-endian .NET GUID bytes. The
extension uses presence/profile at 202–205, flags at 208/212, reserved zero bytes
206–207/216–255, and SHA256 of 0–255 at 256–287. Decode checks exact size before
header parsing or allocation; subsequent allocations have fixed bounds. Presence
zero means unknown, and only pristine Empty permits it. Present flags must be
exactly 2 or 0x802, with no normalization or coercion. Role mapping explicitly
returns Worker at native index 0 and Agent at index 1; there is no shared array.
Conservative invocation/inode distinction includes same-device/inode duplicates,
even with different flags. Existing proof, failure and correlation rules are
retained in the separate V2 validator.

V2 Decode rejects all V1 and future versions, including Empty. The existing V1
codec, Journal.Read, recovery and tests are byte unchanged. In particular, this
patch does **not** make existing Journal.Read operationally refuse V1. No file
conversion, fallback, sidecar, flag default or rewrite is implemented. Pure codec
refusal has no operational action to emit; that establishes nothing about later
journal admission or native effects.

Requirements: Q9/Q20–22/Q31/Q151/Q162/Q168 restrict authority;
Q25/Q150/Q164 prohibit ambiguous replay; Q14/Q149/Q153/Q163 cover local authority,
storage and secrets; Q174 unlock is separate; Q158/Q189–195 govern evidence.
The owner testing decision of 2026-11-23 supersedes numeric Q190 gates. Directive
2.3/5.3/13/17 applies. Coverage and negative controls are diagnostic only.

Design inputs reopened with exact membership, every payload size/hash, read-only
modes and no symlinks:

- `/private/tmp/issue14-native-flags-contract-7fe084b-1osjemhr`: 41 payloads;
  MANIFEST SHA256 `27dd835330591d31d17c3bcd65dd21087123d03a11900825c9b452a613d2e555`.
- `/private/tmp/issue14-security-design-7fe084b-4ZWHne`: 9 payloads;
  MANIFEST SHA256 `fdb3e0fa477995dd229ba436a6a41a6fc73c1b33ee62ff042c14c6f09ba24209`.

All 29 design Git/source bindings and four primary snapshots were checked against
the sealed sizes/hashes. The design review's 961 offline reference checks are
historical reference evidence, not product tests or FINAL implementation review.
Pending bridge/coordinator worktrees were not consumed and are not dependencies.

Validation on macOS 27.2.1 arm64, SDK 10.0.404/runtime 10.0.15,
`CHUNKMAESTRO_DOTNET=/Users/derek/.dotnet/dotnet`, Python 3.13.18 at
`/opt/homebrew/bin/python3.13 -B`:

- `tooling/build/build.py restore`: locked restore, exit 0.
- `tooling/build/build.py build`: Release build, zero warnings/errors, syntax
  check including generated sources, exit 0.
- `tooling/build/build.py format`: generated-inclusive verification, exit 0.
- `dotnet test tests/ChunkMaestro.HelperPolicyTests/ChunkMaestro.HelperPolicyTests.csproj --no-build --no-restore -c Release`:
  297 passed, 15 Linux-only skipped, 0 failed (312 total; 9 new codec cases).
- Unchanged HelperPolicy cases selected by `--filter FullyQualifiedName!~PolicySessionRecordV2Tests`:
  288 passed, 15 skipped, 0 failed (303 total). The proposed baseline count 420
  was not observed in this checkout; no broad suite was run to manufacture it.
- Contracts project with the same test options: 18 passed, 0 failed/skipped.
- `tooling/specification/check.py`: all 205 source IDs/matrix cases, exit 0.
- Targeted V2 `--collect 'XPlat Code Coverage'` with include
  `[ChunkMaestro.PrivilegedHelper]*V2Unqualified`: 9 passed. Diagnostic coverage
  114/114 lines, 204/210 branches; no threshold gate or native coverage claim.

Tests exercise independently literal V1/V2 golden bytes and explicit V2 values,
both profiles/asymmetric flags, every byte value for phase/fault/Boolean/presence/
profile codes, each reserved position, all 32 flag bits with recomputed checksums,
absence versus zero, duplicate roles, phase/retained proof/fault consistency,
immutable value/byte-buffer separation, all single-byte corruption positions,
wrong sizes 0–1024 plus 1 MiB, 2,000 random and 2,000 structural mutations
(seed 1402026; random lengths 0–1024). There are no collection inputs or shared
allowlists to mutate. Full same-generation transition immutability is outside
this patch; record equality includes metadata but does not implement transitions.

Five external disposable negative controls compiled successfully, then failed the
intended assertions (no timeout/compile-failure credit): drop flags comparison,
allow missing presence, swap roles, allow V1, weaken duplicate-role guard.
Initial local failures were CA1861 in the test refusal helper, import ordering,
and whitespace in new initializers; corrected without suppressing policy. No
product behavioral test failed. Raw commands/logs/TRX, mutation evidence, source
hashes, seal and machine handoff are retained at
`/private/tmp/issue14-v2-codec-7fe084b-p2iqwl9m`.

NOT RUN / remaining: versioned journal dispatch, durable readback/integration,
recovery/evidence/inventory matching, all same-generation transition guards,
EMPTY finishing exclusion/close-once/failure guards, qualified pre-worker original
Agent acquisition, manager proof/authentication, both native architectures,
power loss/disk full, G1–G6, ENODATA, installation/path/ancestry, pre-CLR/TSYNC/
old-ring exclusion, whole Agent/descendant closure, request-send atomicity,
handback/store-loss/reexec/GC, all 11 product gaps, native/runtime/GUI/auth/vault
acceptance, full issue acceptance, hosted CI, signing/shipping and three NEW
independent exact-head FINAL QA roles. The current two-role ABI cannot measure
an original Agent alone before a Worker exists. A separately qualified adapter
is required; constants, binding Agent twice or starting Worker early are forbidden.
Codec success does not supply any EMPTY or owner finishing guard.

Rollback before integration: discard only these four new files. There is no
runtime or journal data to undo. After future operational V2 integration, rollback
requires compatible qualified drain or quarantine; never reset/downgrade/replay or
synthesize metadata. The parent owns implementation preflight, later integration,
and signed shipping after PR35/PR36 land. No staging, commit, push, GitHub write,
branch/config change, native execution or live-host mutation occurred here.
