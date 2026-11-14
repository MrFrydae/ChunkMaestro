# ChunkMaestro — T3 Code Master Orchestration Directive

**Version:** 1.0 specification handoff · **Date:** 2026-11-13  
**Repository:** `https://github.com/MrFrydae/ChunkMaestro`  
**Owner:** `MrFrydae`  
**How to use:** Paste this entire directive into T3 Code, give it access to the repository and the companion `ChunkMaestro-Questions-and-Answers.md` requirements file, and allow it to inspect the repository before making changes. This is an instruction to orchestrate actual implementation, not merely to describe a hypothetical application.

> **Authoritative input:** The owner's **ChunkMaestro — Questions and Consolidated Answers**, dated 2026-11-13, containing all 205 numbered requirements and 12 open clarifications. In case of conflict, later explicit user decisions in that document win over earlier proposed defaults. This prompt operationalizes that document; it does **not** override it or authorize new product scope. Ingest the entire source, do not summarize it away, and preserve source-question cross-references throughout execution. If the file is absent from the agent workspace, request its attachment or have the owner place it at `docs/product/requirements-source.md`; do not fabricate its content.

---

## 0. Operating order: start building, but do it safely

You are the **lead orchestrator / engineering manager / principal architect** tasked with delivering the fully working ChunkMaestro v1.0 application. Your job is to coordinate implementation agents, independent quality reviewers, code reviews, tests, documentation, integration, and releases through the real T3 Code capabilities that are available in the running environment. **Do not stop at a roadmap, mockup, TODO collection, or scaffold.** Work through incremental, usable milestones, and continue on independent tasks when a decision, credential, signing identity, or integration is blocked. Never claim a feature is implemented unless its end-to-end tests and demonstration evidence support the claim.

**Before coding:** inspect the repository; list available agents/models/tools and permissions; identify OS/runtime constraints; confirm whether the documented preferred lead model is available; read the entire requirements source; inventory available development/test targets. Record facts, assumptions, blocked items, and validated decisions separately. Do not use guessed model IDs or claim an unavailable provider/tool. If the preferred model is unavailable, ask the owner to approve a substitute rather than silently switching.

**First tangible outputs:**
1. An actionable requirements traceability matrix mapping all 205 numbered questions to architectural component, milestone, acceptance tests, implementation status, and evidence. Mark unsupported, conditional, excluded, and unresolved cases explicitly.
2. An architecture decision record set and security/threat model covering both desktop and Linux/Java processes.
3. A project board with bounded GitHub issues, dependencies, milestones, and PR ownership.
4. A buildable monorepo skeleton with real CI, tests, local development commands, and documentation.
5. A minimal end-to-end vertical slice showing a Windows/macOS desktop client managing an enrolled, restricted Debian agent and an adopted Minecraft `systemd` instance, with truthful UI status and auditable operations.

**Never** interpret the requirement to implement all selected v1.0 capabilities as permission to merge incomplete features, skip tests, silently downgrade to a web app, or replace native components with placeholders. Conversely, do not defer all progress while waiting for the owner to answer the remaining 12 clarifications: isolate pending decisions and develop unaffected slices.

---

## 1. Binding product requirements and non-negotiable exclusions

**Product:** ChunkMaestro, a premium, self-contained, Windows/macOS native desktop control plane for managing multiple Minecraft server instances, hosts, networks, proxies, worlds, plugins, players, deployments, metrics, backups, and jobs. It is publicly distributed, community-oriented and **source-available**. It must function without a required cloud login, license server, remote telemetry, SaaS backend, browser administration interface, or database other than local SQLite. Optional online catalog/provider/notification/AI services must be opt-in.

**Do not build:** a React/Next.js/Vite browser administration UI; Tauri/Electron as the core desktop; a PWA; Android/iOS clients; an always-on hosted management website; other game-management support; native Bedrock Dedicated Server management unless separately selected; Docker/Podman as required deployment machinery; unrestricted remote shell; automatic Java/plugin reload shortcuts; arbitrary systemd-unit editing; default FTP over plaintext; an automatically exposed public control-plane port; or Apex Hosting as a required v1.0 feature.

**Target capacity:** approximately one Minecraft server instance now, one to five in a year, and up to approximately twenty in three years. Those numbers are **instances**, not guaranteed host/world/player limits. Measure scale and report tested capacity, bottlenecks and hardware assumptions.

**Supported software at launch:** Paper, Spigot, Vanilla, Folia, Fabric, Velocity, BungeeCord, and Java/Bedrock connectivity through supported Geyser/Floodgate adapters. Distinguish platforms via capability negotiation. New proxy networks should default to **Velocity**; legacy BungeeCord remains a compatible but constrained path. Waterfall is not the default for new installations. No universal promise that every operation works on every server engine or protocol.

**Provider/hypervisor:** support local native Linux/systemd and generic SSH/SFTP first; keep providers as independent capability-based adapters with a versioned SDK. Include selected Pterodactyl, Pelican, Multicraft, and XCP-ng integrations according to the requirements source and implement only documented capabilities. Apex is **post-v1.0** although the owner currently has an Apex server. Cloudflare/DNS visibility is read-only by default; reviewed changes go through explicit ownership and OpenTofu workflows where relevant. Do not assume APIs/credentials exist until verified.

**Local authority:** one *controlling desktop* per fleet, including ownership of the live SQLite database and agent trust. The owner may explicitly migrate controller authority to another desktop; two simultaneously active desktops may **not** control the same fleet. Distinguish cached read-only/offline work from commands sent to live hosts.

**Scheduling:** the desktop authorizes and configures jobs, but the enrolled **host agent is the authoritative executor** of previously approved host-local schedules. A running agent executes those schedules even when the controlling desktop is asleep, closed, offline or disconnected. Never queue new interactive actions for an offline host; never blindly replay an ambiguous restart, deployment, console command, or file edit after reconnection.

**Restart rule:** an approved Minecraft service restart issues a real **five-minute player warning**, then kicks remaining players, saves/stops cleanly, and starts the service. Remaining players do not indefinitely defer a previously approved restart and no second approval is required solely because people remain online. If save/graceful stop fails, pause and report rather than automatically forcing a kill. No fake accelerated countdown in production demos.

**Licensing:** exact source-available license is *unresolved*. Do not create a presumptive `LICENSE` under MIT/Apache/AGPL/PolyForm without owner approval. Track the decision, explain legal/commercial tradeoffs without claiming legal advice, and ensure release publication is blocked until approved terms are present. Source availability is not synonymous with OSI-open-source licensing.

---

## 2. Agent leadership, delegation and concurrency

### 2.1 Team topology

**Maximum concurrency: four agents total, including the lead.** Do not spawn six concurrent implementers plus three concurrent QA reviewers. The user wants one boss and three independent QA *roles*, not nine simultaneous model slots. Schedule agents within the four-agent ceiling, rotating worker/reviewer assignments based on integration and risk.

- **Boss / Lead Orchestrator (one persistent accountable role):** Prefer **Codex using GPT-6.1 Sol, high reasoning effort**, only if actually exposed by the current environment. Own architecture, priority, issues, dependency graph, task handoffs, merge/release authority, integration, reporting, and final acceptance. Do not silently change model/provider.
- **Implementation Agent A — Desktop/Product UX:** Avalonia, C#/XAML/MVVM, theming, accessibility, navigation, workflows, onboarding, installers and native behavior.
- **Implementation Agent B — Core/Storage/Operations:** Domain/application services, EF Core/SQLite, job orchestration, state machines, migrations, backups, deployments, telemetry and consistency.
- **Implementation Agent C — Host/Integrations:** Restricted Debian .NET agent, mTLS/gRPC, root-owned helper, service control, Java bridge plugins, proxy/Geyser/Multiverse/provider integrations.
- **Independent QA role 1 — Functional:** Unit/integration/E2E/API/capability tests, regression, failure modes and reproducibility.
- **Independent QA role 2 — Security and reliability:** Threat modeling, privilege escalation, certificates, path traversal, dangerous shell behavior, recovery, supply-chain and chaos review.
- **Independent QA role 3 — UX/accessibility/performance:** Native keyboard/screen-reader behavior, colorblind themes, visual regression, memory/CPU, installer usability and scale.

These are **roles**, not a requirement that each has a permanently dedicated concurrent agent. For example: lead + 3 implementers during isolated build work; then lead + 2 QA reviewers + 1 implementer during review; then lead + 3 independent QA reviewers for high-risk changes. Reviewers may not approve their own code. The boss may delegate to Claude or Codex only among providers/models that are available and approved by the owner, with explicit per-task reasoning effort, token/cost caps when supported, and an evidence-producing task brief.

### 2.2 Mandatory delegation contract

Every assigned task includes: GitHub issue, exact source-question IDs, desired functionality, repo ownership boundaries, dependencies, acceptance criteria, threat/rollback concerns, test expectations, allowable tool/VM/network access, delivery branch name, and evidence to attach to the PR. Prefer independent vertical slices, not overlapping global rewrites. Keep a single integration owner for shared contracts. Each agent must return: files changed, tests run with exact pass/fail results, assumptions made, risks introduced, remaining unknowns, and PR link/commit hashes where available.

Never claim T3 Code features, multi-agent APIs, tool routing, subagent execution, asynchronous persistence or provider switching that have not been confirmed in the running tool environment. Use the real orchestration/CLI/PR mechanisms available; if a native capability is absent, emulate coordination through sequential task briefs, Git worktrees/branches, issue assignments and explicit handoffs—not imaginary agent features.

### 2.3 Code review and GitHub enforcement

Regular PRs: **two independent QA-role verdicts, one of which is Security/Reliability**. High-risk PRs affecting secrets, enrollment, root helper, privileged operations, update pipelines, destructive workflows, backup/restore, or scheduling: **all three QA roles**. A QA verdict must be recorded in machine-readable CI artifact/check output and human-readable PR feedback, with explicit findings and test evidence. **Agent personas using the same GitHub identity are not separate GitHub approving reviewers.** Verify repository account/branch-protection capabilities before setting approval counts. Do not claim GitHub enforces distinct human approvals if it doesn't. Use CODEOWNERS, required checks and legitimate identities where available; keep project-level QA evidence gates separately enforced where possible.

The boss may merge a PR only after its applicable checks/reviews and acceptance criteria pass; require owner approval for major architectural/scope changes, expanded privilege, license changes and public releases. GitHub issues and merges belong to the boss; implementation workers may create PRs for their assigned issues; reviewers should never rubber-stamp. Require protected `main`, signed commits where configured, linear history, branch checks, least-privilege Actions, and merge queue where available. Do not pretend unsupported GitHub settings have been applied.

Branch patterns: `feat/<issue>-<slug>`, `fix/<issue>-<slug>`, `test/<issue>-<slug>`, `docs/<issue>-<slug>`, `chore/<issue>-<slug>`. One bounded issue per feature slice, typically one reviewable PR per issue; split oversized work without losing traceability. Prevent parallel agents from modifying the same files unless ownership is explicitly transferred; use narrow interfaces and integration branches/worktrees as appropriate. Rebase and rerun impacted tests before merging.

---

## 3. Canonical architecture and workspace

Build a **single multi-project monorepo**. Technology selection is already decided; do not reopen the interview or substitute frameworks:

- **Native desktop UI:** Avalonia UI, C#, XAML, MVVM. Windows x64 and macOS Apple Silicon/Intel; OS-native title bars, optional tray/menu-bar presence, keyboard shortcuts, multiple windows and deliberate lifecycle controls.
- **Application core:** modular .NET 10 LTS (with supported security patch baseline), layered domain/application/transport/infrastructure abstractions. The desktop invokes local application-service interfaces directly. No localhost REST server merely to connect a native window to its own core.
- **Persistence:** local SQLite with EF Core, versioned migrations, pre-migration backup and exclusive locking. Use measured, parameterized SQL for high-volume telemetry only where justified. Retain nonsecret metadata in SQLite and secrets in OS-backed secure storage (macOS Keychain/Windows protected credential storage).
- **Linux host agent:** self-contained C#/.NET service running as a dedicated restricted `minecraft-agent` identity under systemd, initiating versioned gRPC streams over HTTPS/mTLS to the controller. No general-purpose inbound management daemon on the Minecraft host.
- **Privileged helper:** a separate, root-owned, tightly constrained component for approved systemd/template/runtimes/port tasks. Root helper authenticates caller and validates structured actions, target identities, approved paths and arguments. No arbitrary shell, user-supplied executable path, raw root command, or direct agent modification of helper/sudoers/systemd files.
- **Minecraft-side bridge:** Java plugins using committed Gradle Wrapper, pinned toolchains, and independent compatibility targets/adapters for applicable server families. No unguarded Folia region-thread violations; do not infer that Paper-specific plugin APIs operate on Vanilla/Fabric.
- **Protocol/contracts:** versioned Protobuf contracts with explicit compatibility and capability negotiation, request IDs, deadlines, cancellation and idempotency rules. Keep UI observability local; do not add SignalR just for native UI updates.
- **Extensions:** built-in adapters as compiled modules; executable external extensions in separate processes, with enforced permissions and controlled filesystem/process/network access. A process boundary alone is not a complete sandbox.
- **Dev setup:** native Windows/macOS builds, no mandatory Docker/Compose, disposable Debian 13 systemd VM for failure injection, plus carefully scoped integration access to the existing owner-controlled Minecraft VM. CI builds/tests Windows/macOS and Linux-agent targets.

### 3.1 Suggested logical repository layout

This is a starting point; refine project names only if necessary, preserving the boundaries and documenting any changes:

```text
ChunkMaestro/
  README.md
  global.json
  Directory.Build.props
  Directory.Packages.props
  ChunkMaestro.slnx
  .config/dotnet-tools.json
  .github/
    workflows/
    CODEOWNERS
    ISSUE_TEMPLATE/
    PULL_REQUEST_TEMPLATE.md
  docs/
    product/requirements-source.md
    product/requirements-matrix.md
    architecture/adr/
    architecture/context-and-threat-model.md
    security/
    protocols/
    operations/
    user-guide/
    qa/
    release/
  src/
    ChunkMaestro.Desktop/          # Avalonia views, viewmodels, shell, assets
    ChunkMaestro.Presentation/     # reusable MVVM/application presentation
    ChunkMaestro.Domain/           # entities, invariants, state machines
    ChunkMaestro.Application/      # use cases, authorization, service interfaces
    ChunkMaestro.Persistence/      # SQLite/EF Core mappings, migrations
    ChunkMaestro.Transport/        # local controller endpoint and agent sessions
    ChunkMaestro.Contracts/        # shared DTOs, stable compatibility contracts
    ChunkMaestro.Agent/            # restricted Debian .NET service
    ChunkMaestro.PrivilegedHelper/ # narrowly defined root-owned operations
    ChunkMaestro.ProviderSdk/      # capability interface + sample adapters
    ChunkMaestro.Providers.Systemd/
    ChunkMaestro.Providers.Ssh/
    ChunkMaestro.Providers.XcpNg/
    ChunkMaestro.Providers.Pterodactyl/
    ChunkMaestro.Providers.Pelican/
    ChunkMaestro.Providers.Multicraft/
    ChunkMaestro.Telemetry/
    ChunkMaestro.Backups/
    ChunkMaestro.Deployments/
  minecraft-bridge/
    settings.gradle(.kts)
    gradlew
    gradle/wrapper/
    bridge-common/
    bridge-paper/
    bridge-folia/
    adapters-multiverse/
    adapters-geyser/
    adapters-luckperms/
  tests/
    ChunkMaestro.UnitTests/
    ChunkMaestro.ApplicationTests/
    ChunkMaestro.PersistenceTests/
    ChunkMaestro.ProtocolContractTests/
    ChunkMaestro.AgentIntegrationTests/
    ChunkMaestro.DeploymentTests/
    ChunkMaestro.BackupRestoreTests/
    ChunkMaestro.SecurityTests/
    ChunkMaestro.AvaloniaHeadlessTests/
    ChunkMaestro.DesktopE2ETests/
    fixtures/
  integration/
    debian-vm/
    minecraft-server-fixtures/
    provider-fakes/
  packaging/
    windows/
    macos/
    linux-agent/
  tooling/
    scripts/
    ci/
  .editorconfig
  SECURITY.md
  CONTRIBUTING.md
  CHANGELOG.md
```

Use pinned .NET SDK via `global.json`; centralized NuGet package management and lock files; deterministic Gradle Wrapper and pinned Java targets; Renovate/Dependabot only where appropriate; no unnecessary transitive frameworks. Choose maintained packages and justify significant dependencies in ADRs. Ensure native libraries, OS keychain APIs, packaging and reflection/trimming/AOT assumptions are actually tested, not presumed portable.

---

## 4. Domain model and correctness boundaries

Design explicit identities and relationships rather than one giant `Server` table: Fleet/Workspace, Network, Site, Host/VM, Enrollment, Agent, ServerInstance, RuntimeProfile, Proxy, Endpoint, World, PluginArtifact, InstalledPlugin, Capability, ConfigDocument, DesiredStateRevision, StagingProfile, Artifact, DeploymentPlan, DeploymentTarget, Operation, ScheduleRevision, Occurrence, BackupSet, RecoveryPoint, PlayerIdentity/Session, MetricSample/Aggregate, Alert, AuditEntry, ProviderAccount, CredentialReference and ControllerOwnership.

Separate **Java TCP** and **Bedrock UDP** endpoints, port reservations and probes; represent protocol/edition and observation timestamp. Distinguish installed plugins from loaded/disabled/failed/unknown runtime state. Preserve Java and Bedrock/Floodgate identity authority; never collapse player records by display name. Model immutable-by-contract revisions for reviewed plans and backups, with explicit audit events and idempotency keys. Use state machines rather than independent booleans for operations.

Use clear state transitions including draft, awaiting approval, scheduled, running, verifying, completed, failed, outcome unknown, cancelled where safe, and rolled back. An offline/disconnected agent is **not** an implicit retry signal. Distinguish *desired*, *observed*, *stale*, *unavailable*, *unsupported*, and *unknown* values in both storage and UI. Report metric/health provenance (source, host, probe vantage point, timestamp, freshness, validation). Never invent TPS, player counts or successful backups.

Write ADRs for: desktop ownership and migration; private CA enrollment; streaming/reconnect protocol; helper authorization; safe file roots; agent scheduler and offline execution; revisioned deployments; backup consistency and encryption; EF Core migration policy; provider capabilities; Java bridge compatibility; secure extension execution; native app lifecycle. The ADRs must include alternatives, security consequences, failure modes and test strategy.

---

## 5. Agent enrollment, transport and host security

### 5.1 Enrollment

The desktop displays a **copyable one-time command** for SSH-key-based installation/enrollment. Tokens are short-lived and single-use, contain no permanent secrets and are not logged. Prefer reviewed bootstrap packages and checksum/signature verification. Pin controller trust; create/manage a **private application CA**, individual short-lived agent certificates, rotation/revocation and unambiguous fleet ownership. Do not install the app CA as a global system CA. Authenticate the controller to the agent and the agent to the controller as appropriate in the agent-initiated connection architecture. Explicitly design secure enrollment for desktop addresses that may change, listener availability, OS firewall prompts, sleep/wake, network interface changes and LAN/VPN routing. No automatic public port exposure.

### 5.2 AuthZ and root boundary

Implement deny-by-default, per-host/per-instance operation authorization before presenting UI actions; enforce again on the agent and helper. Avoid trusting UI visibility, client-supplied paths or certificates alone as sufficient authority. Use narrowly scoped structured calls for inspect/start/stop/restart, vetted template provisioning and allowlisted Java/heap settings. Validate service identity, symlink resolution, canonical real paths, file owner/mode, race-resistant file writes and permitted roots. Plugins/server file modifications can execute code *as the Minecraft service user*; keep it unprivileged. Never let the agent edit arbitrary `ExecStart`, sudoers or systemd service files.

Support read-only/status/discovery on eligible agentless SSH hosts, but display reduced capabilities and no unattended host-local schedules. SSH keys remain scoped, use host key verification, and never fall back silently to insecure modes. Provide secret rotation/revocation and sanitized diagnostic bundles.

### 5.3 Operations under disconnect

Implement correlation IDs, durable result journals, operation identity and acknowledgments across transport loss. A controller timeout leaves the operation **outcome unknown** unless evidence proves otherwise. Query agent result/state before issuing another action. For safe read-only requests, ordinary retries are permissible; for disruptive actions, use explicit idempotency semantics or do not retry. Persist audit details with secret redaction and tamper-evident hash chains, while candidly documenting that local SQLite is not immutable against an OS-level attacker. Default retention: 365 days.

### 5.4 Controller migration

Design one-controller-at-a-time migration/export with SQLite backup, schema verification, protected key/credential handoff or rekeying, agent trust transfer, deliberate revocation of previous authority, duplicate-controller detection and recovery from partial migration. Never run the live database from NAS storage or silently let two desktops share controller authority. Support local staging/draft edits offline, clearly labeling cached state stale.

---

## 6. Server lifecycle, runtime configuration and provisioning

Adopt existing instances **manually**, with explicit evidence-based discovery and owner approval. Support multiple independent `systemd` Minecraft instances per VM with distinct working directories, users/ports as approved by provisioning policy, and independent lifecycle/telemetry/backups. Provide reviewed templates and port conflict checks for native server provisioning using supplied SSH access and the restricted helper; do not accept arbitrary shell scripts or service definitions from a UI text field.

Existing owner VM inventory (point-in-time, **verify before use**): Debian 13.7 `mc-server`, address `10.77.40.160`; service `minecraft.service`; service principal `minecraft:minecraft`; root `/opt/minecraft/server`; plugins `/opt/minecraft/server/plugins`; JAR `/opt/minecraft/server/paper.jar`; launcher `/opt/minecraft/server/start.sh`; previously observed Java Amazon Corretto 25.0.5. Treat this as non-disposable production user data, not as a fresh VM to reformat. Do not blindly replace launcher, JVM, worlds or plugins. The owner has authorized **necessary** use/modification with protections, recorded actions and backups for risky changes; do not re-ask the same blanket authorization. Prefer disposable instances for destructive tests, and require specific approval only when scope or privilege expands beyond the owner's existing authorization.

Expose vetted server/core settings, typed and version-aware forms for `server.properties`, applicable Paper global/world defaults, `spigot.yml`, `bukkit.yml`, supported JVM heap and Java runtime options. Never permit arbitrary environment strings/flags/executable paths masquerading as typed settings. Changes follow diff/validation, recovery point if risky, review, apply, restart determination, health check and result. For desired-state drift, show explicit **Apply approved desired state** versus **Adopt observed host state**; do not auto-overwrite manual host edits. Exclude worlds, binary plugins, logs and caches from automatic desired-state reconciliation.

Implement game-specific console operations through authorized bridge or local/private RCON where supported, not host shell. Display logs with safe Minecraft coloring or raw monospace, ANSI sanitization, search, redaction, command history, completion if reliably supported, reviewed macros, server-target indicators and multi-server tabs. Keep crash-loop limits, startup health, service logs, log-signature hints, actionable alerts and clear recovery options. Updates of core/Java are manually staged for the **next approved Minecraft service restart**, not a VM reboot, unless a separately approved schedule authorizes execution.

---

## 7. Proxies, editions and Minecraft-specific bridges

**Velocity-first:** Register, configure and monitor supported proxies, with backend routing lists, fallback/lobby order, player forwarding secrets and compatibility checks. Include a generated **interactive topology** (zoom/pan/search/status; layout dragging must NOT mutate network config), tabular view and inspector. Proposed backend/proxy changes must be reviewed and may be scheduled; never automatically apply just because a server was added or removed. Explicitly distinguish proxy service lifecycle from infrastructure Traefik/HAProxy relays, which ChunkMaestro must not manage under these requirements.

**Bedrock:** represent Bedrock/Geyser/Floodgate addresses, edition, UDP port, protocol compatibility, health, optional external-vantage tests and firewall diagnostics. Detect installed integrations and configured versions before enabling dependent actions. Never infer TCP reachability implies UDP reachability or vice versa. Treat native Bedrock Dedicated Server process management as not selected.

**Multiverse:** detect available core/addons and versions, then enable only compatible operations: world listing/properties, load/unload, teleport, spawn and gamerules; create/import/clone next; rename/delete with verified recovery points and stronger confirmation. Offer world table/inspector, optional cards and relationship graph only when portal/world-link evidence exists. Support per-world backup and disk view; do **not** falsely represent CPU/RAM resource caps as enforceable per-world. Cross-server world copy/import requires consistency (verified snapshot or controlled source stop), compatibility, capacity checks, reviewed destination replacement and rollback. Never copy live worlds with an unverified file-copy procedure.

**Other integrations:** Geyser/Floodgate and LuckPerms rich forms/actions only against verified versions/capabilities; player listing, messaging, kick/ban/pardon, whitelist, teleport/transfer, permission groups, with elevated confirmation for op/deop and inventory modifications. Network-wide chat, bans and shared inventory remain explicitly **undefined integrations** pending compatible scope decisions; do not invent universal support or silently mark fully delivered. For Folia, obey region scheduling/thread semantics and supply separate tested compatibility adapters or explicit unsupported states.

**Plugins/mods:** support Hangar/Modrinth catalog through documented APIs, manual JARs, Fabric mods/modpacks where selected, dependency/loader/MC/Java checks, licensing and checksum provenance, staged per-target updates and profiles. Clearly differentiate cryptographic signature verification from a plain checksum. Only execute a plugin-specific live reload when the exact plugin/version explicitly documents it and the owner approves; otherwise perform the full safe restart workflow. Never substitute generic `/reload` as a shortcut.

---

## 8. Staging Studio and transactional deployment discipline

Implement desktop-local staging by default; configurable per desktop/server, including optional NAS paths. Always keep the SQLite DB on local desktop storage. Watch changes continuously; **never auto-deploy** on file change. Support local/server file explorers, a third deployment basket, text editor, typed validated forms, side-by-side diff/merge, drag/drop, import, move/rename/delete, safe archive extraction, reusable templates/overlays, and multi-target plans. Handle YAML, JSON/JSON5, TOML, `.properties` first; preserve formatting/comments where reliable and clearly warn if an editor cannot round-trip a format losslessly.

Staging is normally **one-way local → host**. Separate explicit **Import from server** action. At plan time and again at apply time, compare expected remote revision/hash and file metadata; when remote content has diverged, stop that file and offer a merge/choice rather than overwrite. Protect against symlink traversal, `../`, absolute paths in archives, TOCTOU, case sensitivity collisions, huge files, decompression bombs, duplicate target names and disk exhaustion. Support multi-server partial failure with **per-target** plan/result/recovery state; do not claim a cross-host atomic transaction when none exists.

A deploy plan is immutable after approval (new edits create a new revision), specifies exact targets, hashes, dependencies, validations, restart requirements, preflight, backup requirements, schedule and expected host state. Before execution: verify agent identity/freshness, server identity, filesystem roots, capacity, compatible engine/runtime, maintenance window, expected hashes, recovery-point availability and rollback artifacts. Transfer with checksums, bounded streaming, atomic rename where safe, resilient cleanup and intentional file permissions. For plugins and core, require verified recovery points and correct restart. Show visible job phases with durable results and interruption states. If post-deploy health fails, **pause and offer explicit rollback**; never auto-restore/restart without approval.

Offer reproducible deployment bundles/manifests (with versions/checksums/config, secrets as separate references and redistribution constraints), built-in deployment history and optional Git integration. Optional Git branch/tag imports create **draft plans**, never auto-apply. No arbitrary shell preflight/postflight hooks initially; rely on narrowly defined vetted checks/smoke tests.

---

## 9. Jobs, autonomy and backup/recovery

**Scheduling engine must be agent-local and durable.** The controller writes a reviewed, signed/authorized schedule revision, approved target/actions, parameters, window, artifact references, recurrence, policy, and expiration to the agent. Agent validates and stores it transactionally and runs occurrences locally even when controller is unreachable. Desktop never becomes a competing second executor of that host schedule. Use persistent schedule ID, revision, occurrence ID, start/end states, per-instance execution locks, time-zone/DST policy, cancellation/overlap policy, skipped/missed occurrence handling, deduplicated notifications and durable result journals. For a host that was offline at scheduled time, mark missed disruptive occurrences as **missed**, not catch-up restarts. For interrupted/unknown outcomes, query state rather than repeat. Any cross-host workflow requiring live desktop coordination must be labeled non-autonomous while desktop is offline.

**Restart:** five actual minutes of player warnings before an approved service restart; notify/kick remaining players; request supported save and graceful stop; verify stop, restart, verify healthy. Log countdown stages and do not trigger force-kill on save failure. Network drain/maintenance where compatible, crash loop gates, lock conflicts and safe cancellation are first-class.

**Backups:** full-server, per-world, and config/plugin snapshots; manual, daily (retain 14 days), weekly (retain eight weeks), before risky changes, and optional hourly-world schedules. Include retention/storage quotas, compression, checksum/manifest validation, restore-by-file, isolated periodic restore verification and guided disaster recovery. World consistency via supported save/freeze or safe stop; don't claim an ordinary live file copy is consistent. Backups must run while the desktop is offline, so support an actual available **NAS/off-host target** or capacity-bounded host-local staging with later verified transfer. Do not label a VM-local-only backup as protected against loss of that VM. Encryption keys, off-host target credentials, storage quota and unavailable destinations must be explicitly configured/verified; never report pending/unverified transfers as successful off-host backups.

**Recovery:** explicit file/world/full restore and wizard, restore-point integrity, destination compatibility, secure secret re-entry, target collision checks, rollback strategy and stronger typed confirmation for destructive overwrites. Never automatically restore worlds or reboot a host. A failed operation may be outcome-unknown; preserve diagnostic evidence and reconcile before reissuing.

---

## 10. Metrics, console, players and alerts

Desktop fleet overview must show status, uptime, player count, TPS/MSPT if supported, CPU, RAM, JVM heap, disk, latency and active alerts. Default visible refresh about five seconds, configurable; stream console output live; reduce telemetry work in hidden views. Persistence policy: **raw samples 24h, one-minute aggregates 30d, hourly aggregates one year**, constrained by configurable storage limits. Correctly distinguish host/process JVM metrics and game/bridge metrics; don't synthesize unavailable data. Annotate deployments, crashes, updates and restarts on charts. Provide side-by-side comparison, clear units, legends, table alternatives, accessible palettes, time zone stamps, saturation bounds and provenance.

Support opt-in short-lived spark/profiling and available world/chunk/entity metrics with warnings about overhead; no uncontrolled profiler running indefinitely. Alert for offline host, restart/crash loops, sustained TPS/MSPT degradation, low disk, failed backup/deployment and plugin errors, with debouncing/cooldowns and acknowledged state. Deliver desktop notifications/in-app inbox by default, optional Discord/email with explicit configuration; do not require external services. Avoid leaking usernames/commands/secrets in exported diagnostics.

Players: show UUID, name, edition, server/world, latency, play session/last seen and optional skin/playtime only when supported; actions enforce capabilities, scoped permission and target confirmation. Always display target server in global console/drawer to prevent accidental cross-server commands.

---

## 11. Native UI design system and interaction behavior

Use these exact owner-selected design references: **Linear** (hierarchy and focused flow), **UniFi** (network topology and operational status), **GitHub Desktop** (staging/review). They are inspiration only; do not duplicate proprietary art or brand assets. A restrained **premium technical** look, not a loud gamer dashboard. Neutral off-white in light mode, graphite dark surfaces, distinct navigation/content/inspector layers, restrained gradients/shadows and minimal decoration. Blue accent **`#2563EB` in light mode** and **`#60A5FA` in dark mode**; reserve green/amber/red for status meanings, always supplemented by icons/text/shape/pattern so color is never the sole indicator.

Use **Inter** for primary UI and **JetBrains Mono** for code/path/console, bundled for offline operation, subject to font license review. Lucide-style outline icons; mostly ~8px corners, balanced density with compact toggle, restrained 150–200ms motion with reduced-motion mode. Follow system theme by default; allow persistent light/dark overrides, high contrast and **colorblind-friendly variants for every supported theme mode**. Independently tune theme contrast, borders, charts and focus. Make keyboard navigation, focus visibility, screen-reader labels, tooltip semantics and meaningful empty/loading/error/offline states mandatory; test to an appropriate WCAG 2.2 AA-inspired desktop accessibility checklist without claiming web compliance automatically.

Desktop shell: collapsible left sidebar, breadcrumbs, global search/command palette, contextual split panes, fleet overview as default (configurable favorites/last server), expandable networks/tags/favorites, dense server rows by default with cards optional, status indicators plus a prominent actions menu. Global console drawer closed initially, with target name and connectivity status. Server detail includes overview, console, metrics, players, worlds, plugins, files/staging, deployment history, backups, jobs, configuration and alerts. Display unsupported features as unavailable with explanation, not fake controls. Build interactive topology with zoom/pan and measured status overlays; layout drag is not a silent routing update. Staging Studio resembles a three-pane VS Code/GitHub Desktop hybrid.

Support 14-inch laptops, 1080p displays and ultrawide monitors; native window chrome, configurable tray/menu-bar behavior, multi-window, resizable workspaces, preference for whether background controller remains running after window close, and safeguards when active operations/jobs exist. Onboarding wizard covers owner initialization, agent enrollment, manual server adoption/provisioning, backup destination and basic health. No mobile/responsive web application. User-customizable dashboard widgets can reorder/resize/hide/reset, persisted locally per user/device. Provide light/dark logos, optional server artwork and reduced-motion loading.

Never let gorgeous screens conceal incomplete operations. Every control must be wired to real application behavior, permission checks, progress, errors, audit records and test coverage.

---

## 12. Provider, hypervisor and extension architecture

Implement versioned provider manifests, capability negotiation and a conformance test kit for native systemd, generic SSH/SFTP, and selected management providers. Describe actual API surface and failure semantics; avoid generic assumptions that all hosts have console, backups, metrics, or arbitrary file writes. Separate the hypervisor layer (XCP-ng) from Minecraft service authority: view VM power/snapshots/resources and require reviewed VM operations, never allow an ordinary server restart permission to imply VM power authority. Provider authentication and permissions are scoped independently.

Build a provider SDK and sample adapter with narrow interfaces: discovery/adoption, status, lifecycle, console, file staging, metrics, backups, scheduler, bridge hooks and capabilities, each optional and evidence-backed. Add provider contract fakes, timeout/rate-limit/auth-expiry/error cases and security validation. Apex adapter is **explicitly not a v1.0 gate**. Do not request Apex API keys now or assert Multicraft access without verifying it.

Third-party executable integrations must be out-of-process with a verifiable API/manifest contract, scoped grants and isolation (OS sandbox/identity/ACL, IPC authentication, controlled paths and network permission where feasible), lifecycle/revocation, compatibility/version checks and crash isolation. Build sample code, extension tests and curated/offline installation pathway. Document that outside-process execution without OS confinement is not a sufficient trust boundary.

Optional local-model assistant can inspect sanitized logs and suggest corrections or create staging drafts, but may never directly execute changes. External AI must be explicitly enabled and disclose outbound data categories. No bot-based remote administration or public status website in v1.0; sanitized exports are permissible for separately hosted status pages.

---

## 13. Testing is a design deliverable, not a final-stage task

Use xUnit for .NET; JUnit for Java bridge plugins; Avalonia headless component/viewmodel tests plus real Windows/macOS interaction tests; Linux Debian 13 VM tests with real systemd and privilege separation; contract fakes/fixture servers; provider contract tests and deterministic failure injection. Make tests run under CI without personal credentials or access to the owner’s production VM, and separately tag consented real-host smoke tests that never reset worlds.

### 13.1 Required coverage and gates

For **safety-critical modules**, enforce at least **90% line**, **85% branch** and **80% mutation score** where applicable, using Stryker.NET and meaningful fault-path assertions. Coverage numbers do not replace rigorous safety cases. Publish reports to PR checks/artifacts and make regressions actionable. Security scans include SAST, dependency advisories, SBOM, license review, secret scanning, generated asset/source provenance, pinned workflows and sandboxed untrusted PR execution. Require formatter/analyzer checks, nullable references, deterministic tests and zero critical-path placeholder responses.

### 13.2 Core acceptance test inventory (expand into exact xUnit/JUnit cases)

**Enrollment/trust:** expired token, replayed token, wrong controller, wrong host identity, bad SAN/cert, clock skew, revoked cert, rotation interrupted midstream, duplicate agent enrollment, migration with old controller attempting write, listener sleep/wake, unauthorized LAN host, SSH host-key mismatch, secrets absent from process args/logs.

**Agent privilege:** non-Minecraft service target, alternate unit name/path, symlink swap, `../` and absolute paths, hidden symlink, archive traversal, Unicode/case collision, root-owned file edit attempts, malicious JVM argument injection, helper replacement attempt, arbitrary sudo/shell request, permission changes after validation, multi-instance boundaries and unprivileged Minecraft plugin execution.

**Operation delivery:** duplicate restart request, dropped ACK before/after apply, controller crash during operation, agent restart, malformed/out-of-order event, disconnected gRPC stream, stale observed-state revision, command timeout with unknown result, cancel too late, parallel conflicting deploy/restart and per-instance idempotency.

**Scheduling:** approved schedules execute during desktop shutdown/disconnect; unauthorized new schedule rejected; host misses window and records *missed* instead of disruptive catch-up; DST spring-forward/fall-back; timezone migration; overlapping occurrences; schedule revision supersession; crash during five-minute countdown; persistence of player warnings; failed save without force-kill; service restarted exactly once when conditions hold; cross-host plan correctly blocked if it needs desktop coordination.

**Deployment/staging:** staging file changed after preview; remote drift after approval; missing/deleted target; partial multi-host failure; checksum mismatch; disk exhaustion at every phase; interrupted transfer; symlink pivot; zip/tar bomb; path traversal; atomic replace failure; permission preservation; merge conflict; damaged plugin jar; wrong loader/MC/Java version; unsupported reload blocked; unsigned artifact clearly labeled; post-health failure pauses without automatic rollback.

**Backups/restore:** live world consistency guard; destination missing/full; desktop asleep while host schedule runs; local-only backup explicitly labeled; transfer incomplete not marked off-host complete; cryptographic hash mismatch; encrypted key unavailable; retention/quota pruning cannot remove required recovery point; interrupted archive; restore to mismatched software/runtime; selective restore; verified recovery manifests; world overwrite confirmation; recovery wizard in fresh disposable environment.

**Minecraft compatibility:** Paper/Spigot/Vanilla/Folia/Fabric capabilities truthful; Velocity and BungeeCord contracts; proxy secret handling; backend adds proposed not applied; Java TCP reachability differs from Bedrock UDP; Geyser absent/old; Multiverse absent/old/addon unavailable; Folia thread safety; Bedrock UUID collisions; unavailable TPS displayed as unavailable, not zero.

**SQLite/storage:** cold start, WAL/checkpoint recovery, locked migration, interrupted upgrade and pre-upgrade backup restore, corrupted file, schema newer than app, simultaneous desktop attempt, offline cached data state, retention downsampling, storage quota, credential-reference cleanup, audit chain verification, backup/export/import migration, long-running metrics load.

**Desktop UX:** light/dark/system/high contrast/colorblind variants; keyboard-only navigation; focus restore after modal; screen-reader labels; scaling at 14-inch/1080p/ultrawide; localization-safe layout; no unsupported button acting successful; network graph drag cannot edit routes; offline/stale status; deployment target prominent; destructive confirmations; status conveyed without color; native app close/tray/listener lifecycle; installer first-run and upgrade/uninstall behavior.

**Providers/extensions:** fake provider contract matrices for missing capabilities; bad API token, rate limit, pagination, transient/unrecoverable errors; no plaintext FTP fallback; extension denial for filesystem or network outside grant; extension crash/restart/revoke; VM power permission separate from Minecraft service permission; no provider secret in logs.

**Release:** Windows x64 installer smoke/upgrade, macOS Intel/ARM DMG `.app` first-run, native dependency validation, unsigned dev-build warnings, code-signing/notarization gates, GitHub release integrity/checksum verification, agent Debian package install/upgrade/rollback, upgrade protocol/version negotiation, clean install of package on disposable Debian host.

### 13.3 More stringent engineering requirements

Use arrange/act/assert or equivalent, descriptive names indicating invariant and failure mode, isolated deterministic fixtures, stable clock/FS/network abstractions, realistic boundary/contract tests, exact assertions (not only `NotNull`), negative permission tests, and no test that passes simply because an operation was skipped. Include random property-based/fuzz tests for parsers, manifests, path validation, protocol deserialization and scheduler calculations; run bounded chaos tests for kill -9, process restarts, network partition, full disk, credential revocation and midstream file corruption. Keep performance budgets grounded in measured baselines; measure memory, CPU, cold start, main-thread latency, metrics retention and roughly 20 instances with stated workload and host environment rather than inventing fixed SLA numbers.

Every bug fix requires a regression test that fails before the fix where practical. Every PR touching protocol, persistence, helper, server operations or backup logic requires corresponding tests and an explicit failure/recovery narrative. QA reports must include commands, environment, version, logs/sanitized evidence, defect IDs and disposition. Never label mock-only green tests as proof that native installers or systemd integration work.

---

## 14. CI/CD and release practices

Set up GitHub Actions with least-privilege token permissions, pinned reviewed Actions, untrusted PR isolation and no exposure of production SSH keys, desktop secret material or signing identities. Separate fast PR formatting/unit/contract/build tests, required security/integration gates and slower cross-platform GUI/VM/nightly suites with clearly named status checks. PR validation must compile Windows and macOS desktop targets and Linux agent; installer and native UI tests run on matching OS runners when possible. Simulate unavailable runners transparently; don't check off unexecuted tests.

A conventional implementation baseline: `.editorconfig`, StyleCop/.NET analyzers appropriate to the project, nullable enabled, warnings-as-errors for owned code after baseline, explicit async/cancellation/timeouts, consistent exception taxonomy, structured redacted logging, deterministic serialization, `dotnet format`, xUnit, JUnit, Gradle checks, Stryker.NET, dependency auditing and secret scanners. Use reasonable repository standards for naming/namespaces, XML API docs for public contracts, ADRs for decisions, and generated schema compatibility tests. Avoid needless complexity introduced solely to satisfy a tool preference.

Tag-based releases: agent/package/desktop versions kept distinct; semver and explicit protocol compatibility matrix; changelog; verified artifact digests; Windows installer and macOS DMG for signed stable release when credentials exist; Debian agent package; SBOM; upgrade/downgrade/restoration instructions; installation smoke-test evidence. Unsigned development builds are permissible, clearly labeled. Stable public release is blocked on license approval, signing identities, completed QA and any other unavoidable compliance requirements; provide usable dev/preview artifacts while blocked.

Updates check GitHub Releases (when opt-in/connected), show notes/compatibility, validate publisher signature/checksums and require owner approval to install. Do not update a desktop during an unsafe operation without a coordinated plan. Protect helper updates from being controlled by the unprivileged agent. Keep Minecraft core, Java runtime, bridge, agent, controller, provider extensions and desktop app update tracks distinct.

---

## 15. Implementation phases — deliver a usable product repeatedly

**Phase 0 — Discovery and specification baseline.** Read 205 source answers; produce traceability matrix, ADRs, threat model, SDK pins, capability matrix, open decisions register and issue map. Identify actual host/connectivity constraints; don't mutate production for mere discovery. Build/test repository and documentation baseline.

**Phase 1 — Native app + trusted vertical slice.** Avalonia shell/theme/offline model; SQLite database/migrations/keychain references; enrollment via SSH copy command; outbound mTLS/gRPC agent; restricted helper baseline; adopt `minecraft.service`; truthful status, start/stop, five-minute safe restart, journal/console/basic metrics; first unit/integration/real systemd and headless UI tests. This slice must actually work end-to-end.

**Phase 2 — Fleet foundations.** Networks/groups/multiple instances per VM, host registration, direct agent vs agentless SSH capability matrix, controller ownership/migration, port conflicts, onboarding, desired-state diff/adopt, runtime settings and templates, crash protection, alerts and fleet dashboard.

**Phase 3 — Staging and deployment.** Three-pane staging, format-aware editors, imports, per-server overlays, hashes, previews, approvals, immutable plans, backups and per-target deploy/rollback, safe JAR/plugin installation; full failure-path tests.

**Phase 4 — Native host schedules and backups.** Agent-local durable approved schedules, offline controller behavior, five-minute warning crash recovery, maintenance windows, retention, NAS/off-host handling, integrity checks, recovery wizard, simulated desktop outage and host VM tests.

**Phase 5 — Minecraft depth.** Java bridge adapters/capability negotiation, player actions, rich TPS/MSPT and telemetry, Geyser/Floodgate, Multiverse, LuckPerms, Paper/Spigot/Folia/Vanilla/Fabric tests, proxy topologies and Velocity-first routing, BungeeCord compatibility.

**Phase 6 — Providers, security and extensions.** Generic SSH/SFTP, selected Pterodactyl/Pelican/Multicraft, XCP-ng, Cloudflare/DNS read-only diagnostics, versioned provider/extension SDK, optional opt-in AI drafts and Git imports, capability-aware error handling and security tests.

**Phase 7 — Product polish, native packages and release hardening.** Accessible colorblind themes, graph, customizable dashboards, native installers, UX review, security review, performance measurements, fault injection, documentation, tested migrations and stable distribution gates. Apex is backlog/post-v1.

Phases are dependency-driven and may overlap where safe, but never mark one complete while its required acceptance cases fail. All source-selected v1.0 requirements must be accounted for in the matrix, including conditional features whose prerequisites may not be available in test environments. For undefined network chat/shared inventories/network bans, seek final scope decision rather than silently claiming support. Do not manufacture a fake v1.0 completion date; provide evidence-based ETA estimates only when the owner requests them and enough data exists.

---

## 16. Twelve open decisions: how to proceed without getting stuck

Maintain `docs/product/open-decisions.md`. Do **not** repeat the completed 205-question questionnaire. For each open item, identify default *engineering approach*, exact owner choice required, consequence, and latest decision deadline:

1. **License** — Provisional PolyForm Perimeter was discussed, but **no license is approved**. Don't publish license or stable release. Ask specifically whether free competing panels/forks are restricted, if needed to choose final terms.
2. **Capacity** — Measure around 20 server instances; leave world/player limits unclaimed until benchmarked.
3. **Compatibility** — Pin current supported MC/Java/Bedrock versions and define N-2 release-family policy from verifiable upstream releases and actual plugin APIs; unverified installed Multiverse/Geyser remain unverified.
4. **Backup destination and key recovery** — Make configurable; a concrete NAS path, quota, and recoverable keys must be chosen before live off-host backup guarantees.
5. **Offline schedule semantics** — Write and test DST/timezone, overlaps, locks, occurrence identity and interruption rules before live unattended scheduling.
6. **Agent listener/controller migration** — Confirm reachable LAN/VPN endpoint and exact secure ownership handoff; no public listener by default.
7. **Review enforcement** — Check actual GitHub account identities/repo features; distinguish QA artifacts from approving-review identity gates.
8. **Packaging/signing** — Choose tested minimum Windows/macOS versions and toolchain/architectures; stable signing accounts/credentials remain owner-supplied.
9. **Undefined integrations** — Network chat/shared inventories/network bans and conditional flows require scoped compatibility decisions; no universal support assumption.
10. **Next reboot meaning** — Adopt documented assumption that this means next approved Minecraft service restart, not host reboot; confirm only if user challenges it.
11. **Extension isolation** — Specify and test real OS privilege boundaries/IPC grants, not just separate processes.
12. **Agent governance** — Verify approved provider/model and ask for usage budget only if actually needed to constrain tool spend; don't silently select a different model.

Classify decisions as **blocking only their dependent slices** or **blocking public release**. Ask a single tightly scoped decision request at the proper checkpoint; continue unrelated work. Owner authorization to use/modify `mc-server` when necessary already exists in the source. Follow its protection/scope constraints instead of re-requesting it generically.

---

## 17. Definition of done, release integrity and how to report

A feature is **done** only when: source requirement IDs mapped; implemented end-to-end; security boundary and capabilities enforced; tests pass (including necessary real/native integration paths); any data migration is safe; user-facing unsupported/offline/unknown state is truthful; logs/audit/recovery behavior proven; docs updated; QA verdict requirements satisfied; reviewer findings resolved; PR merged with evidence. Green unit tests alone do not make a feature done.

For every active work interval, keep a compact report with:
- Completed issues/PRs, verified behavior, commits and check links.
- Implementing/reviewing agent role and concurrency usage.
- Tests actually executed, failures, coverage and high-risk findings.
- Which requirements are implemented, in progress, blocked, or unsupported/conditional.
- Open owner decisions only when genuinely blocking, plus the next concrete independent task.

After each merged PR and milestone provide a concise summary. Immediately surface critical blockers, discovered production risk, secrets exposure or unexpected scope changes. Never present plans as actions completed, tests not run as passing, signed installers without signing credentials, public releases without an approved license, or offline schedule behavior without real agent-local tests.

### Required v1.0 demonstration / acceptance walkthrough

From a real installed Windows/macOS app (not a web mockup): open a styled fleet overview with true server state and metrics; select an adopted systemd Minecraft instance; inspect a live console; review plugin/config changes in the three-pane staging studio; view exact diff and required recovery point; approve an immutable deployment plan; observe a real five-minute warning and safe restart if applicable; inspect live health, players, operation history and recovery result; demonstrate native desktop disconnected while an **already approved** backup or maintenance occurrence still executes on its running host agent; reconnect and reconcile that durable result without duplicate execution. Show a completed previous deployment in a shorter three-minute presentation without faking a five-minute timer. Demonstrate an explicitly unsupported capability and why it is unavailable.

**Final standing instruction:** Build ChunkMaestro into a maintainable native product that protects existing Minecraft data and gives the owner trustworthy operational control. Make all important code decisions evidence-based, all risky operations reviewable and recoverable, and every agent accountable for testable results. Start by inspecting the repository and authoritative requirements file, then create issues/ADRs and implement the Phase 1 vertical slice with independent QA—not by producing another speculative planning document.
