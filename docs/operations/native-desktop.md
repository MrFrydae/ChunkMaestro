# Native fleet shell development preview

Issue #2 implements a bounded subset of Q25, Q45, Q81, Q114–148,
Q164, Q189 and Q191. It is not Phase 1 acceptance or a release.
Dependencies: unmerged/unapproved PR5 baseline through 53b592a and PR6
storage through b2b421a. This stacked PR targets `feat/3-local-fleet-storage`;
it temporarily also contains baseline changes until dependencies integrate.

## Run and local authority

Use .NET SDK 10.0.404 pinned by global.json. On this Mac:

```sh
export CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet
python3 tooling/build/build.py restore
python3 tooling/build/build.py build
python3 tooling/build/build.py run
```

The full clean build currently has dependency analyzer failures listed below.
Do not treat a successful incremental build using diagnostic binaries as a clean
policy build. No suppression or policy change is committed by this shell.

The native Avalonia 12.1.6 window invokes IFleetInventory directly. It opens no
HTTP listener, makes no host commands/probes and has no browser frontend.
Before opening SQLite the user must explicitly check that the displayed OS-user
application-data directory is local, nonsynchronized and unshared. Confirmation
is session-only, defaults false, and is passed to StorageLocation. The checkbox
is an attestation, not portable proof of disk locality. Check the directory in
your OS before confirming; do not share a live database or synchronize it via
NAS/cloud. Storage's ownership/link/lock checks still apply.

Open the store, register a fleet, select that fleet and register a host, select
that host and register an instance with at least one endpoint. Java TCP and
Bedrock UDP ports can be supplied independently; blank omits an edition. The
instance endpoint address uses its selected host address in this slice. Storage
validates metadata, parent identity, duplicates and endpoint conflicts. No secret
fields exist; enter only non-secret names and addresses.

Registration is local metadata only. Targets are **Not enrolled / unknown**,
not adopted, authorized, connected or proven offline. Last host contact and
endpoint health remain unavailable/unknown. Cache read time is not contact time.
Uptime, players, TPS/MSPT, CPU/RAM/heap/disk, latency and alerts are unavailable.
Remote operations have a permanently disabled explanatory control and no service
execution path. No operations are queued. Local inventory can be read without
internet or host connectivity; no remote cached observations are supplied by
this storage version. Previously approved agent schedules are outside this slice.

## Errors, lifecycle and recovery

Initialization/registration/read failures are shown as fixed category-based
messages without exception text, SQL, input values or backup paths. Initialization
can be retried; no delete/reset/restore shortcut is offered. Preserve SQLite,
sidecars and completed before-migration backups and follow fleet-storage.md for
reviewed recovery. A failed reload retains the prior displayed snapshot, marks
it potentially stale and blocks registration until reload succeeds. If a write
succeeded but reload failed, reload before retrying to avoid duplicate attempts.
Closing waits for an in-flight local action and disposes the store to release
controller locks. No tray/background process or automatic live host commands run.

## Appearance and accessibility subset

Native OS titlebar/window controls, system theme default, off-white #FAFAF9 and
graphite #242427 surfaces, accent #2563EB light / #60A5FA dark. Status uses text,
not color alone. Standard focusable native Avalonia controls have automation
names, access-key labels, default focus indicators and a scrollable form. Inline
state/errors request polite/assertive accessibility announcements. These are
implementation details, not proof of screen-reader interoperability.

Not implemented: persistent UI preferences, manual theme override UI, high
contrast/colorblind variants, bundled Inter/JetBrains Mono, sidebar, global
search, topology, console, staging, custom dashboards, compact mode, tray,
multi-window or installers. System fonts are used. No animation is introduced.
These selected v1.0 requirements remain open; do not advertise full accessibility,
colorblind support or complete Q114–148 fulfillment.

## Implementation evidence (2026-11-13, macOS 27.2.1 arm64)

Implementation role only; no self approval, independent QA or merge. One agent,
no delegates, no host/VM changes, paid services or production credentials.

- `sync`, `update-locks`, locked `restore`: successful through repository tooling.
- `format` (full solution, verify-no-changes): successful.
- Normal Release build and full repository `verify`: **PASS**, zero warnings/errors, without analyzer suppressions or `BuildProjectReferences=false`. Storage dependency analyzer compatibility was corrected before this result.
- `python3 tooling/build/build.py test`: headless xUnit v3 suite and storage
  xUnit 2 suite: **7 passed / 0 failed / 0 skipped** desktop; **20 passed /
  0 failed / 0 skipped** storage. Build-tool Python unit tests: **8 passed**. No skipped tests
  count as evidence. Root discovery includes both Release suites.
- Headless scenarios: consent/empty, registration hierarchy with TCP vs UDP,
  cached reload failure and mutation gating/recovery, sanitized initialization
  failure/retry, invalid port before service invocation, labels/focus, exact
  light/dark surface/accent tokens, close-during-open lock-session disposal.
  The headless inventory is a deterministic application-contract fake; the
  separate storage tests exercise real SQLite. This does not prove installers,
  host enrollment, authorization enforcement or screen readers.
- Native executable launch remained running without terminal error. Mac CUA
  could not address the unbundled executable (`Invalid app`); it was stopped.
  **Native interaction not verified.** No browser/preview tool was used.
- No Windows run, installer/signing/notarization, screen-reader, visual/native
  interaction, performance, coverage or mutation acceptance is claimed.

Integration changes: solution now discovers four storage projects plus Desktop,
Presentation and AvaloniaHeadlessTests. One central version added:
SQLitePCLRaw.bundle_e_sqlite3 3.0.5, matching the storage dependency's existing
lock (the baseline only had a transitive 2.1.13). No existing central version
changed. Tool-generated lock updates align storage tests with baseline Test SDK
18.12.0 and VS runner 4.0.1, keeping xunit 2.9.3. Three new project locks and
Validation's central dependency classification update are included. Existing
Domain/Application/Persistence lock contents did not change.

## Later owner formatting policy

All checked-in C# brace-delimited constructs have been expanded to multiline Allman form, including properties, empty blocks, lambdas and initializers. String interpolation is preserved. Formatter verification and the engineering baseline's Roslyn brace checker passed. The separate storage suite passed all 19 tests after formatting. The subsequent dependency integration fixes below resolve the clean-build analyzer errors. Independent QA and real native interaction remain required.

## Current dependency integration evidence

Full `build.py verify` now passes normal locked restore, Release build (zero warnings/errors), formatting, 7 Avalonia headless tests and 20 SQLite tests, with zero failures/skips. The new storage case verifies a Unicode directory name is preserved through native path/ownership checks. Native string marshalling is explicitly UTF-8 with best-fit conversion disabled; database numeric conversions use invariant culture; migration index arrays are cached without schema changes; endpoint health remains unknown per registered endpoint; test method names satisfy the normal analyzer policy. Initializer punctuation layout was also normalized after multiline expansion. No analyzer suppression, installer/native interaction or independent approval is claimed by this validation.

The desktop stack now carries the finalized engineering tooling from PR5 head `86b9b6d`: the same read-only brace check, secret scan, QA evidence gate and artifact-producing cross-platform CI. Its normal `verify` passes locked restore, Release compilation, brace/format checks, 7 headless and 20 SQLite tests. All 14 build/QA regression tests pass. PR5's reviewed head is unchanged by this stack integration. Hosted execution and independent native-product QA remain pending.

The stack incorporates the subsequent PR5 generated-source enforcement and scanner ignore/redaction fixes. Normal locked restore, Release build, owned generated-source formatting and all 27 .NET tests still pass locally; no independent approval is inferred.

The desktop branch was restacked on storage head `76c0be2` after the base advanced. Storage source is unchanged relative to that base, and its updated operations documentation is preserved. The application tree and current tooling remain intact. Normal `verify` passed after restacking: locked restore, zero-warning Release build, style/format, 7 headless and 20 SQLite tests. The previous feature history is retained in a local backup ref; only the draft feature branch is rewritten with an exact lease.

Hosted CI exposed NU1004 in the build-probe lock after the shared tooling copy: this stack centrally declares SQLitePCLRaw.bundle_e_sqlite3, so the probe lock requires the matching CentralTransitive classification. Local incremental assets had hidden the stale lock. The mismatch was reproduced with `dotnet restore ChunkMaestro.slnx --locked-mode --force`; after deliberate lock regeneration, that forced locked restore and normal full verification passed with all 27 .NET tests. No dependency version or runtime behavior was changed by this correction. Hosted confirmation remains required.
