# Native fleet shell development preview

Issue #2 implements a bounded subset of Q25, Q45, Q81, Q114–148,
Q164, Q189 and Q191. It is not Phase 1 acceptance or a release.
The engineering baseline (PR5) and requirements baseline (PR7) are merged.
PR6 is merged into main as `4e155562d19a48c2d611a31a437ce5d926216ae1`.
The desktop-only commits are rebased onto that accepted storage and direct-CI
baseline. Historical validation below remains tied to its original date/source;
current desktop acceptance requires fresh integration and native evidence.

## Run and local authority

Use .NET SDK 10.0.404 pinned by global.json. macOS also needs Xcode command-line
tools to compile the storage identity shim against the host SDK. On this Mac:

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
An ambiguous local write failure retains the displayed rows and blocks further
registration until a manual reload succeeds. Validation errors remain correctable
without that recovery gate; no write is automatically replayed. Closing waits for
an in-flight local action and disposes the acquired store once, including
overlapping close requests, to release controller locks. If cleanup throws, a fixed
assertive message preserves recovery guidance, storage actions remain blocked,
and the window stays enabled so a second explicit close can exit without retrying
cleanup. That exit reports failure (code 1); raw exception details are not shown.
No tray/background process or automatic live host commands run.

## Appearance and accessibility subset

Native OS titlebar/window controls, system theme default, off-white #FAFAF9 and
graphite #242427 surfaces, accent #2563EB light / #60A5FA dark. Status uses text,
not color alone. Standard focusable native Avalonia controls have automation
names, persistent visible labels targeting all seven registration inputs,
access keys, default focus indicators and a scrollable form. Java TCP and
Bedrock UDP remain identified even when both populated inputs show the same port. Inline
state/errors request polite/assertive accessibility announcements. These are
implementation details, not proof of screen-reader interoperability.

Not implemented: persistent UI preferences, manual theme override UI, high
contrast/colorblind variants, bundled Inter/JetBrains Mono, sidebar, global
search, topology, console, staging, custom dashboards, compact mode, tray,
multi-window or installers. System fonts are used. No animation is introduced.
These selected v1.0 requirements remain open; do not advertise full accessibility,
colorblind support or complete Q114–148 fulfillment.

## Current integration (2026-11-23)

The managed CI jobs now execute the desktop headless test project on Windows,
macOS and Linux after building it. Tests have live output, raw TRX retention, a
60-second test hang timeout and the direct command wrapper's 150-second limit.
This is separate from the accepted storage gate; fake-service headless contracts
do not establish native desktop-to-SQLite or operating-system interactions.
The restack cold locked restore, normal Release build, syntax and formatting
passed. Combined verification now executes 28 headless/composition/lifecycle
cases and 270 macOS storage cases: 298 passed, zero failures/skips. Four
composition cases use real disposable SQLite with public window/viewmodel/store
operations; twelve lifecycle cases use explicitly injected service faults.
The original lifecycle implementation failed ten of those twelve cases.
Four additional contracts cover persistent filled-field labels, cleanup failure,
deferred normal closing, and repeated close requests during pending disposal.
A separate actual-App lifetime probe checks exit codes; its inventory is injected,
so it is narrower than the real-store native proof below.

Native macOS inspection used a development-only launch bundle and the real
application/window. Consent and keyboard focus gates, scrolling, disabled remote
actions and unknown/unavailable labels were observed without opening the normal
user store. A separate native harness supplies the same App with a real-store
opener targeting an owned `/private/tmp` fixture, with that actual path displayed
before consent. Native buttons/keyboard registered fleet/host/instance metadata
and separate TCP/UDP endpoints; after closing the process, read-only SQLite
inspection found preserved identities, two endpoints and a passing integrity
check. Only location selection uses the existing internal test seam; no storage
security, schema or service operation is mocked. This does not prove the normal
AppData opening path, Windows native behavior, screen readers or installers.

The App opener/path injection is a composition boundary; the parameterless
production constructor still uses the normal current-user storage location and
explicit consent. Native application identity is ChunkMaestro. Parent selectors
expose selected names through their automation peer instead of the underlying
record representation, with a focused headless regression. The pinned upstream
[collapsed selection behavior](https://github.com/AvaloniaUI/Avalonia/blob/12.1.6/src/Avalonia.Controls/Automation/Peers/ComboBoxAutomationPeer.cs)
explains that boundary. Native proof and harness/source/binary hashes are retained
under `.artifacts/native-preview/` and `.artifacts/native-composition/`; these are
development evidence, not installer or release packages. The attempted native close-fault harness could not be addressed by automation
(`cgWindowNotFound`), including a fresh launch of the previously working bundle
and the Finder control check. These processes were stopped without opening the
fixture. No native close-fault or corrected-label visual result is claimed from
that attempt. Current-head hosted checks, corrected native interaction and
independent desktop acceptance remain pending.

## Historical validation and integration evidence (2026-11-14)

Local macOS arm64 validation uses SDK 10.0.404 and the normal repository policy:

```sh
"$CHUNKMAESTRO_DOTNET" restore ChunkMaestro.slnx --locked-mode --force
python3 tooling/build/build.py verify
python3 tooling/specification/check.py
python3 -m unittest discover -s tooling/specification -p 'test_*.py' -v
```

Forced locked restore, Release build, syntax-aware explicit-type/multiline-brace
checks and generated-inclusive formatting pass with zero build warnings/errors.
The desktop stack executes 7 Avalonia headless tests plus 41 real storage and
platform-security tests on macOS: 48 passed, zero failed/skipped. The headless
inventory is a deterministic application-contract fake; SQLite runs separately.
These results do not establish complete desktop-to-storage composition or native
interaction. The requirements integrity checker and 14 regressions also pass.

The storage dependency now validates ancestor authority, holds native directory/
file identities and a SQLite connection, checks committed schema structure and
foreign-key integrity, sanitizes operational failures and runs serialized storage
I/O on background tasks. Windows precreates absent WAL/SHM files with explicit
ownership while refusing unsafe existing sidecars without repair. Actual Windows
confirmation of the latest correction remains pending. Linux desktop storage is
unsupported; its CI suite tests that refusal, not Windows/macOS persistence.
Current-head hosted checks, coverage/mutation/chaos acceptance and independent
storage/desktop QA remain required. See fleet-storage.md for the original FAILs,
correction evidence and precise unexecuted scopes.

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
