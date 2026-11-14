# Native fleet shell development preview

Issue #2 implements a bounded subset of Q25, Q45, Q81, Q114–148,
Q164, Q189 and Q191. It is not Phase 1 acceptance or a release.
The engineering baseline (PR5) and requirements baseline (PR7) are merged.
This draft remains stacked on PR6, `feat/3-local-fleet-storage`, currently
`9122a1595c9571df5af05578770c62f9bbffc473`; storage independent QA is pending.
The desktop diff adds its projects and documentation to that storage base.

## Run and local authority

Use .NET SDK 10.0.404 pinned by global.json. On this Mac:

```sh
export CHUNKMAESTRO_DOTNET=/Users/derek/.local/share/chunkmaestro/dotnet/dotnet
python3 tooling/build/build.py restore
python3 tooling/build/build.py build
python3 tooling/build/build.py run
```

Normal Release compilation, style and formatting checks run without analyzer
suppressions. Use a forced locked restore after dependency integration to expose
stale lockfiles that incremental assets could hide.

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

## Validation and integration evidence (2026-11-14)

Local macOS arm64 validation uses SDK 10.0.404 and the normal repository policy:

```sh
"$CHUNKMAESTRO_DOTNET" restore ChunkMaestro.slnx --locked-mode --force
python3 tooling/build/build.py verify
python3 tooling/specification/check.py
python3 -m unittest discover -s tooling/specification -p 'test_*.py' -v
```

Forced locked restore, Release build, syntax-aware explicit-type/multiline-brace
checks and generated-inclusive formatting pass with zero build warnings/errors.
The desktop stack executes 7 Avalonia headless tests plus 25 real storage and
platform-security tests on macOS: 32 passed, zero failed/skipped. The headless
inventory is a deterministic application-contract fake; SQLite runs separately.
These results do not establish complete desktop-to-storage composition or native
interaction. The requirements integrity checker and 14 regressions also pass.

The storage dependency creates Windows directories with an explicit owner and
confined inheritable ACL, rejects unsafe existing directories without repair,
and preserves Unix permission/ownership checks. Its exact earlier head
`30d6edb487641a78e5d338726cc332f4c3f0fb2e` passed 25 tests on hosted Windows
and macOS, and one unsupported-platform contract test on Linux, with zero skips
(run 43615973451). Linux desktop storage remains unsupported; Linux CI does not
claim the Windows/macOS persistence suite executed. The subsequent rebase onto
merged requirements changes no storage implementation; hosted checks for this
new stack and independent storage/desktop QA remain required.

The storage base owns central dependencies, probe and lock integration. The
solution adds Desktop, Presentation and AvaloniaHeadlessTests. No storage source,
shared build policy or lock contents are changed by the desktop layer. A prior
NU1004 failure was reproduced by forced locked restore and corrected in the
shared dependency integration. Cold locked restore is required after restacks.
Signed restacks retain backup refs and use exact force-with-lease checks.

Headless cases cover consent/empty state, registration hierarchy with TCP vs UDP,
cached reload failure and mutation gating/recovery, sanitized initialization
failure/retry, invalid ports before service invocation, labels/focus, light/dark
tokens and close-during-open session disposal. These tests do not prove installed
native behavior, host enrollment, authorization, screen readers or performance.
An earlier unbundled executable remained running, but native automation could
not address it (`Invalid app`); it was stopped. Native interaction is unverified.

No desktop Windows runtime interaction, installer/signing/notarization,
screen-reader, native visual, performance, coverage or mutation acceptance is
claimed. No live host/VM, enrollment, restart, production data, paid service,
release, independent approval or milestone completion is implied by local checks.
