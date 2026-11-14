# ChunkMaestro — Questions and Consolidated Answers

Updated: **2026-11-13**

Project owner: **MrFrydae**  
Repository: [MrFrydae/ChunkMaestro](https://github.com/MrFrydae/ChunkMaestro)

This document preserves all 205 original questionnaire questions and consolidates the owner's answers, accepted recommended defaults, later corrections, and review clarifications. Answers are paraphrased for consistency; quoted statements are identified explicitly. An answer is a requirement or planning decision, not evidence that its feature is already implemented.

Later explicit answers take precedence over earlier defaults. In particular:

- **Name:** ChunkMaestro replaces CraftHelm.
- **Distribution:** Source-available with commercial/competition restrictions; the exact license is still provisional.
- **Applications:** Installed Windows and macOS applications only. No browser administration, mobile app, or PWA.
- **Architecture:** Avalonia/C#/XAML, a shared .NET application core, SQLite/EF Core, and a restricted C# Linux agent. The desktop UI calls local services directly; agents connect outbound over HTTPS/mTLS.
- **Ownership:** One controlling desktop per managed fleet, with explicit migration to another desktop.
- **Execution:** A running host agent executes previously approved local schedules even when the desktop is asleep/off/disconnected. Do not queue new interactive commands for offline hosts or replay ambiguous disruptive operations.
- **Scale:** The estimate of 1 now, 1–5 in a year, and approximately 20 maximum refers to Minecraft server instances. Player/world/host/network counts remain unspecified.
- **Restarts:** An approved restart gives a five-minute warning, then kicks remaining players, saves/stops gracefully, and restarts.
- **Themes:** Colorblind-friendly options for every supported theme mode, with non-color status cues.
- **Scope:** All selected functionality targets v1.0 except Apex integration. Explicit exclusions and undefined integration scope are called out rather than silently filled in.
- **Development access:** Necessary use/modification of the existing `mc-server` is authorized, with existing data protected and risky changes backed up. The VM is not disposable.

## Interpretation and provenance

- Questions retain their original numbering. The initial response had shifted OS/service/path answers around questions 11–14; those answers are aligned to the original questions here. Question 14 is answered by the later explicit desktop-control-plane decision.
- “Accepted default” means the owner answered “use recommended” for the recommendation presented in the conversation. Those defaults are written out here instead of leaving that phrase unresolved.
- **PROVISIONAL**, **unverified**, and **recorded assumption** identify details that have not been finally selected, inspected, or confirmed. Do not treat them as implementation facts.
- Existing-host versions, paths, service status, and networking are recorded inventory from prior inspection and owner-provided output. Creating this document did not perform another SSH/live-state check.
- The latest review recommendations are incorporated where the owner accepted the review; remaining uncertainties are listed after question 205. Creating this document does not install software, deploy changes, change repository policy, or publish a license.

## Contents

- [A. Product identity and scope](#a-product-identity-and-scope)
- [B. Hosting, VM topology and connectivity](#b-hosting-vm-topology-and-connectivity)
- [C. Server software and runtime management](#c-server-software-and-runtime-management)
- [D. Proxies, Bedrock and Multiverse](#d-proxies-bedrock-and-multiverse)
- [E. Local staging, sync and deployment](#e-local-staging-sync-and-deployment)
- [F. Plugins, configs and extensions](#f-plugins-configs-and-extensions)
- [G. Live console, players and statistics](#g-live-console-players-and-statistics)
- [H. Backups, jobs and incident recovery](#h-backups-jobs-and-incident-recovery)
- [I. Hosting providers and infrastructure integrations](#i-hosting-providers-and-infrastructure-integrations)
- [J. Visual design and product interaction](#j-visual-design-and-product-interaction)
- [K. Engineering stack, apps and packaging](#k-engineering-stack-apps-and-packaging)
- [L. Security, accounts and permissions](#l-security-accounts-and-permissions)
- [M. GitHub development and agent governance](#m-github-development-and-agent-governance)
- [N. Optional ambitious features and longer-term roadmap](#n-optional-ambitious-features-and-longer-term-roadmap)
- [Open decisions and implementation clarifications](#open-decisions-and-implementation-clarifications)

## A. Product identity and scope

### 1. **★** What should the product be called (working name, final name, branding/domain)?

**Answer:**

ChunkMaestro. Owner: MrFrydae. Project repository: https://github.com/MrFrydae/ChunkMaestro. The name should communicate comprehensive Minecraft management with a sleek, modern presentation. CraftHelm was rejected after an existing Minecraft manager with that name was found. ChunkMaestro passed the preliminary web/repository/package checks performed in this conversation; trademark and domain availability have not been established.

### 2. **★** Private personal tool, a shareable self-hosted app, an open-source community product, or commercial SaaS?

**Answer:**

A publicly available, community-oriented, source-available product. The initial description was “open source,” but the later decision to restrict commercial exploitation takes precedence. Do not describe the selected commercial-restriction model as an OSI-approved open-source license.

### 3. **★** GitHub repo name/owner, public vs private, and preferred license (MIT, Apache-2.0, AGPL-3.0, proprietary, undecided)?

**Answer:**

Owner: MrFrydae. Public repository: https://github.com/MrFrydae/ChunkMaestro. Licensing must address resale/repackaging and competing paid panel services without automatically prohibiting people from managing their own Minecraft servers. Exact license: PROVISIONAL. PolyForm Perimeter 1.0.0 was discussed, but it also restricts free competing products and has not been finally selected. Do not publish under MIT, AGPL, or another license by assumption.

### 4. **★** Which features must be in an initial usable v0.1 versus a polished v1.0?

**Answer:**

All selected functionality is targeted for v1.0 except Apex Hosting integration. Deliver through smaller, independently usable implementation milestones; earlier references to “initially,” “next,” or “later” describe sequencing and must not silently remove selected v1.0 scope. Conditional features remain gated by supported integrations. Container support is an optional later provider adapter; mobile/browser interfaces and public administration websites were explicitly removed. Undefined integrations need an explicit scope decision rather than an invented implementation.

### 5. Are you the only administrator, or will friends/moderators have separate accounts and granular permissions?

**Answer:**

Initially only me, as the local owner/administrator. Build authorization boundaries into the application core, but separate moderator/friend accounts and a complete multi-user product are not required initially.

### 6. Do you foresee supporting more than one Minecraft network/household/account at once?

**Answer:**

Yes. Support managing more than one Minecraft network/household/account, with explicit grouping and scoped credentials.

### 7. Expected maximum number of hosts, servers, concurrent online players and worlds (today / 1 year / 3 years)?

**Answer:**

These estimates refer to Minecraft SERVER INSTANCES: 1 today, approximately 1–5 in one year, and up to approximately 20 in three years. Host counts, network counts, concurrent player counts, and world counts were not specified. Establish and report tested capacity without inventing a guaranteed player/world limit.

### 8. Should the app manage only Minecraft Java/Bedrock proxies, or eventually other games/processes?

**Answer:**

Minecraft Java and Bedrock connectivity through Geyser/Floodgate. No other games. Native Bedrock Dedicated Server process management has not been explicitly selected; first-class Bedrock endpoints do not by themselves commit to that additional provider.

### 9. Do you want the panel to create and initialize brand-new Minecraft servers or only adopt existing ones?

**Answer:**

Both provision new servers using supplied SSH access and adopt existing servers. Provisioning must use reviewed templates and narrowly scoped privileged operations. Do not bypass the restricted-agent model to initialize a server.

### 10. Should the product work fully without any cloud account, license server, telemetry or external database?

**Answer:**

Fully local operation, with no required cloud account, license server, telemetry service, or external database. Optional external catalogs, providers, notifications, or AI services must be explicitly enabled and must not be prerequisites for local management.

## B. Hosting, VM topology and connectivity

### 11. **★** What is the host OS and version of your current Minecraft VM (Debian, Ubuntu, etc.)?

**Answer:**

Recorded host inventory: Debian GNU/Linux 13.7 (trixie); hostname `mc-server`; address `10.77.40.160`. These are point-in-time facts from the earlier inspection and supplied output, not a requirement to force all managed hosts onto these exact patch versions.

### 12. **★** What is its `systemd` unit name and service user (no secrets needed)?

**Answer:**

Recorded systemd unit: `minecraft.service`, enabled and running at inspection. Service user/group: `minecraft:minecraft`.

### 13. **★** What are the Minecraft working directory, plugin directory, server JAR path, Java version, and launch method?

**Answer:**

Recorded Minecraft runtime:

- Working directory: `/opt/minecraft/server`
- Plugin directory: `/opt/minecraft/server/plugins`
- Server JAR: `/opt/minecraft/server/paper.jar` (the supplied `readlink` output resolved to this same path)
- Java: Amazon Corretto OpenJDK 25.0.5, build `25.0.5+9-LTS`
- Running Java executable: `/usr/lib/jvm/java-25-amazon-corretto/bin/java`
- Launch method: systemd executes `/opt/minecraft/server/start.sh`; the script changes to the server directory and executes:

```bash
#!/usr/bin/env bash
set -euo pipefail

cd /opt/minecraft/server

exec /usr/bin/java \
  -Xms4G \
  -Xmx10G \
  -XX:+UseG1GC \
  -XX:MaxGCPauseMillis=200 \
  -XX:+ParallelRefProcEnabled \
  -XX:+DisableExplicitGC \
  -jar paper.jar \
  --nogui
```

Treat this as existing-host inventory. Validate runtime compatibility before proposing any Java or launcher change.

### 14. **★** Where should the main control-plane backend run: desktop machine, existing Linux VM, new VM, Docker host, or selectable install modes?

**Answer:**

The control plane runs on my desktop computer, bundled with the installed Windows/macOS application. One desktop controls a given set of servers. Moving control to another desktop is an explicit migration, not concurrent Windows/Mac management of the same fleet. The migration design must account for the database, credentials, agent trust, and backup-key access without sharing an active SQLite database over a NAS.

### 15. Which virtualizers are relevant: Proxmox, XCP-ng, VMware, Hyper-V, another, or none?

**Answer:**

XCP-ng is the relevant hypervisor.

### 16. Are VMs all on one trusted LAN, across VLANs, VPNs, remote sites, or public hosting?

**Answer:**

One VLAN for now. Design connectivity for approved LAN/VPN use; do not assume that every future host is on the same trusted segment.

### 17. **★** How should remote host connectivity work: installed agent dialing out, SSH with keys, direct HTTPS/mTLS, WireGuard/Tailscale, or multiple modes?

**Answer:**

Use SSH with keys for installation and enrollment. After enrollment, the installed agent initiates outbound HTTPS/mTLS connections to the desktop control plane. The agent continues previously approved local schedules when the desktop is offline.

### 18. Do you want an agentless SSH fallback for one-off or legacy machines?

**Answer:**

Yes to an agentless SSH fallback for one-off or legacy hosts. Declare reduced capabilities explicitly; do not imply that an agentless host can run ChunkMaestro's autonomous agent schedules.

### 19. Should enrollment use a temporary pairing code, QR code, signed provisioning file, command copied from UI, or all?

**Answer:**

A command copied from the UI. Use a short-lived, single-use enrollment mechanism; do not put permanent credentials or private keys in a copied command.

### 20. Can the host agent run as a restricted service account with preapproved service-specific `sudoers` rules, or will you explicitly permit elevated functionality?

**Answer:**

Run the agent as a dedicated restricted account, such as `minecraft-agent`, separate from the unprivileged Minecraft service account. Permit only approved, service-specific operations, status/log access, and narrowly scoped file access. Never grant unrestricted sudo, arbitrary host-shell execution, or direct write access to systemd units, sudoers, or privileged helpers. Additional privileges require explicit approval. Plugin/server-file modification permits code execution as the Minecraft user, which must remain unprivileged.

### 21. Can one VM host more than one Minecraft instance (`systemd` units, Docker containers, or both)?

**Answer:**

Yes. A VM may host multiple native systemd-managed Minecraft instances on distinct ports, with a planned Velocity proxy in front. Track Java TCP and Bedrock UDP endpoints separately and check conflicts.

### 22. Should the panel discover Minecraft instances automatically, prompt before adopting them, or require manual registration?

**Answer:**

Manual registration/adoption. Do not silently claim ownership of discovered processes or server directories.

### 23. Is Docker/Podman Minecraft management an initial requirement or a later provider adapter?

**Answer:**

Docker/Podman Minecraft management is not an initial requirement. The current server runs directly on Debian through systemd. Container support may be an optional later provider adapter; Docker is not required for running Minecraft or developing the application.

### 24. Should hypervisor integration display VM power, snapshots, CPU/memory/disk and optionally create/restart VMs?

**Answer:**

Yes. Provide optional hypervisor visibility for VM power, snapshots, CPU, memory, and disk, with reviewed VM lifecycle operations. Prioritize XCP-ng and keep hypervisor authority separate from Minecraft service authority.

### 25. How should offline hosts behave in the UI—last-known state, queue operations for later, or reject changes until online?

**Answer:**

Show offline hosts clearly with last successful contact and cached state marked stale. Disable interactive operations requiring that agent. Do not automatically queue/replay start/stop/restart, console commands, file edits, or plugin changes. An interrupted operation has an unknown outcome until the agent reports it.

LATEST OVERRIDE: previously approved schedules already stored on a running host agent continue while the DESKTOP is asleep/off/disconnected. This is distinct from queuing new interactive commands. If the HOST/agent itself misses a scheduled maintenance occurrence, do not silently replay disruptive work on reconnect. Retain job identifiers and results to reconcile outcomes without duplicate execution.

## C. Server software and runtime management

### 26. **★** Which server software must be supported at launch: Paper, Purpur, Spigot, Vanilla, Folia, Fabric, Forge, NeoForge, Velocity, BungeeCord?

**Answer:**

Paper, Spigot, Vanilla, Folia, Fabric, Velocity, and BungeeCord. Publish a per-platform capability matrix rather than claiming identical support. Bridge plugins, mod management, world controls, and rich player actions depend on the platform and detected compatible integrations.

### 27. **★** Which Minecraft version(s) are you running and which version families must be supported (current only, N-2, broad legacy)?

**Answer:**

Support N-2 version families, with separate Minecraft Java, Bedrock/Geyser/Floodgate, server software, bridge, and Java-runtime compatibility checks. The exact meaning of N-2 and concrete release families remain to be pinned in the compatibility policy; do not assume one version number governs both editions. The current Minecraft server release was not established by the supplied Java-version output.

### 28. **★** Do you already use RCON; should the app use it, avoid it, or use it only when a bridge/agent is not installed?

**Answer:**

RCON is not currently confirmed in use. I would like it supported. Use it where appropriate for game-console operations; keep its credentials scoped and its listener private, preferably local to the agent. Game-console access is not permission for arbitrary host-shell access.

### 29. Should the app install a small optional Paper/Spigot plugin for rich stats, plugin state, player actions and Multiverse integration?

**Answer:**

Yes. Offer an optional bridge plugin for richer metrics, plugin/runtime state, player actions, and supported Multiverse integration. Detect compatibility before installation; do not claim that one bridge works on every server implementation.

### 30. Should JVM flags, Java runtime versions, RAM allocation, environment variables and unit overrides be editable from the UI?

**Answer:**

Yes to editable runtime settings, subject to question 31. Expose an allowlist of safe supported fields such as heap sizes and approved Java versions. This is not permission to edit arbitrary JVM flags, environment values, executable paths, or unit overrides.

### 31. Should an agent be permitted to edit the systemd service file itself or only selected configuration fields after explicit confirmation?

**Answer:**

The agent must not directly edit systemd units or privileged launchers. Validate allowed settings and apply them through a root-owned helper with narrowly scoped permissions. Show proposed changes and restart requirements before approval. Keep arbitrary commands, executable paths, service users, and unrestricted JVM arguments outside editable fields. Use the same boundary for provisioning approved per-instance service templates.

### 32. Should start/stop/restart support warning broadcasts, countdowns, player-drain and maintenance-mode gates?

**Answer:**

Yes to warnings, countdowns, player-drain handling, and maintenance gates. The explicit restart policy is a five-minute warning, then kick remaining players, save/stop gracefully, and restart. Do not defer indefinitely because players remain online or demand a second approval solely for their presence.

### 33. Should server startup include automated health checks, log-signature detection and crash-loop protection?

**Answer:**

Yes. Include startup health checks, log-signature detection, and crash-loop protection. Report unavailable/uncertain health explicitly and keep crash recovery distinct from automatically retrying a disruptive deployment.

### 34. Should Minecraft core updates and Java runtime updates be manually initiated, scheduled with approval, or optionally automatic?

**Answer:**

Manually queue Minecraft core and Java updates for installation at the next restart. RECORDED ASSUMPTION FROM REVIEW: “next reboot” means the next approved Minecraft service restart, not automatic VM/OS reboot. Host reboot authority remains separate. Stage and validate updates first; unattended execution requires an approved schedule/policy and all required artifacts to be available.

### 35. Should new servers be provisioned from templates and allocated unused TCP/UDP ports with port-conflict checks?

**Answer:**

Yes. Provision from reviewed templates, allocate unused TCP/UDP ports, and perform port-conflict checks. Apply privileged setup through the restricted helper; do not expose arbitrary service names, paths, or commands.

### 36. Should the app inspect logs for probable crashes and propose troubleshooting steps, or only show raw logs and links?

**Answer:**

Yes. Inspect logs for probable crashes and propose troubleshooting steps, while preserving raw logs and evidence. Suggestions do not automatically change files or execute commands.

### 37. Should server configuration be tracked as a desired state with drift detection versus only on-demand edits?

**Answer:**

Track approved selected settings as desired state and compare with observed host configuration. Show differences, detection time, and restart requirements. Let the owner explicitly Apply desired state or Adopt current host configuration. Preserve manual host changes pending that decision. Exclude worlds, logs, caches, and plugin binaries from automatic reconciliation. Offline observations are stale; reassess after reconnection.

## D. Proxies, Bedrock and Multiverse

### 38. **★** Are you running an existing proxy (Waterfall/BungeeCord/Velocity), planning one, or starting with independent servers?

**Answer:**

No Minecraft proxy is currently verified on the VM. I plan to use Velocity.

### 39. **★** Are you willing to standardize new networks on Velocity while retaining legacy compatibility?

**Answer:**

Yes. Standardize new supported networks on Velocity while retaining explicit legacy compatibility.

### 40. Should the app install/manage the proxy process, or only edit and monitor an existing proxy?

**Answer:**

Manage and monitor registered Minecraft proxies, with reviewed configuration and lifecycle operations. Proxy installation was sequenced after initial existing-proxy management; do not present an unverified installation as already deployed. Keep infrastructure Traefik/HAProxy relays outside ChunkMaestro's control. See the v1.0 scope rule in question 4.

### 41. Should the network topology be an editable drag-and-drop graph, a generated diagram, a tree, or several views?

**Answer:**

Use a generated interactive graph, searchable table, and detail inspector. Initially make configuration changes through reviewed forms. Dragging nodes changes visual layout; drag-and-drop topology editing is a separate, reviewed capability rather than an implicit network change.

### 42. Should the panel configure routing (lobby/fallback/priority), backend server lists, health checks and secure player forwarding?

**Answer:**

Yes, for a supported registered proxy: backend lists, lobby/fallback order, priorities, health checks, and secure player forwarding. Use validated fields, explicit review, and protected secret storage; expose only supported capabilities.

### 43. Should backend additions/removals automatically propose proxy config changes with a review and maintenance window?

**Answer:**

Automatically propose proxy changes when backends are added/removed. Require review before application, show affected players, and support immediate or scheduled maintenance. Never apply those proposals automatically.

### 44. Do you need network-level chat, proxy-wide player list, cross-server teleport/transfer, network bans or shared inventory hooks?

**Answer:**

Provide a read-only network player list first, with permission-controlled server transfers as the next capability. Network chat, shared inventories, and network-wide bans require defined compatible integrations and semantics. They were deferred pending definition; do not silently promise them across every platform. Reconcile their final v1.0 scope explicitly under question 4.

### 45. **★** Is Geyser/Floodgate already in use; should Bedrock players and endpoints be first-class in v0.1?

**Answer:**

Bedrock works in a separate setup, but Geyser/Floodgate on this VM is unverified. Java and Bedrock endpoints are first-class from the first usable milestone: edition/protocol, address, port, and health. Detect installed integrations before presenting controls. Native Bedrock Dedicated Server management remains unselected.

### 46. Should network health include UDP probes, external connect checks, compatibility alerts and firewall troubleshooting?

**Answer:**

Yes. Include Java status checks, Bedrock UDP probes, optional external checks, and compatibility alerts. Provide firewall diagnostics and proposed fixes; firewall changes require explicit approval. Label probe location and observation time.

### 47. **★** Are you using Multiverse-Core today? Which version and addons (Portals, Inventories, NetherPortals, etc.)?

**Answer:**

Multiverse-Core, its version, and addons on the current VM are unverified. Detect them before enabling controls. Do not assume Portals, Inventories, or NetherPortals is installed.

### 48. Which Multiverse operations matter most: list worlds, create, clone, import, load/unload, rename, delete, teleport, gamerules, spawn, portals?

**Answer:**

Prioritize world listing/properties, load/unload, teleport, spawn, and supported gamerules; then create/import/clone. Rename/delete require recovery points, stronger confirmation, and version-specific checks. Operations depend on the detected server/integration capability.

### 49. Do you want a visual world graph/cards, or a compact searchable table with detailed inspector?

**Answer:**

A compact searchable world table with a detailed inspector, optional cards, and a generated relationship graph where portal/world-link integrations provide reliable data.

### 50. Should worlds have independent resource/disk/backup policies and individual role permissions?

**Answer:**

Yes to per-world backup schedules, retention, disk monitoring, and scoped permissions. CPU/RAM enforcement belongs to the server process; do not present those resources as enforceable independently per world.

### 51. Do you need cross-server world-copy/import workflows? Should such operations require a scheduled stop or freeze?

**Answer:**

Support cross-server world-copy/import as a reviewed workflow within the selected feature plan. Use a verified consistent backup or stop the source; stop the destination before replacing an existing world. Check compatibility and disk space and provide recovery/rollback. Never copy live world files without a supported consistency mechanism.

## E. Local staging, sync and deployment

### 52. **★** Where is your preferred staging directory physically located: your Mac/Windows desktop, the backend VM, a NAS share, a Git checkout, or configurable per machine?

**Answer:**

Desktop-local staging by default, with configurable locations per machine/server and optional NAS support. Keep the active application database on local desktop storage rather than in a shared staging folder.

### 53. **★** Should staging sync be one-way staging → server, bidirectional with conflicts, or selectable per folder?

**Answer:**

Staging → server for deployments, plus an explicit “import from server” action.

### 54. Should the desktop monitor staging changes continuously (filesystem watcher) or refresh on demand?

**Answer:**

Watch continuously and show detected changes.

### 55. Should any changed local file deploy automatically, or always require a reviewed plan and approval?

**Answer:**

Always show a deployment plan and require approval.

### 56. **★** Is a required restart after plugin JAR changes acceptable, with only verified plugin-specific commands offered as optional reloads?

**Answer:**

Require a restart; offer live reload only when a specific plugin explicitly supports it, with your approval.

### 57. Should each server have independent staging profiles, shared templates, and environment overlays (`dev`, `test`, `production`)?

**Answer:**

Yes, with a clear preview of inherited settings and server-specific overrides.

### 58. Should files be assigned to multiple servers at once, with per-target previews and rollback?

**Answer:**

Yes, with per-server previews, checks, results, and rollback points.

### 59. What should happen when the server-side file changed after the last sync: stop, prompt diff/merge, prefer local, prefer remote?

**Answer:**

Stop deployment for that file and show a conflict. Let you merge or explicitly choose which version wins.

### 60. Should the staging UI be two-pane file trees, three-pane with a deployment basket, or Git-style changed-file list?

**Answer:**

Three-pane staging workspace with local/server file navigation and a deployment basket containing the changed-file list and diff viewer.

### 61. What editing modes do you want: raw editor, validated form, side-by-side YAML/JSON/properties, visual diff/merge, all?

**Answer:**

Raw text, validated forms for supported settings, and side-by-side diff/merge. Preserve comments and formatting where supported.

### 62. Should drag-and-drop uploads, folder import, copy/paste, create/delete/rename/move and archive extraction be supported?

**Answer:**

Drag-and-drop uploads, folder import, create/delete/rename/move, and archive extraction into staging. Preview extracted contents and reject paths that escape the staging directory.

### 63. Should the app create Git commits and tags for each staged deployment, or use a built-in version history only?

**Answer:**

Built-in deployment history always, with optional Git integration. Keep secrets, worlds, logs, and binaries out of Git by default.

### 64. Should deployment support scheduled maintenance windows, preflight scripts and post-deploy smoke tests?

**Answer:**

Yes. Include port, disk, compatibility, backup, and health checks. Keep arbitrary shell-script hooks disabled initially.

### 65. Should failed health checks automatically roll back files and restart the server, or pause for confirmation?

**Answer:**

Pause and show recovery options. Offer an explicit rollback; don’t automatically restore files and restart the server.

### 66. What backup threshold should be required before deployment: always, only risky changes, configurable, or manual?

**Answer:**

Require a verified recovery point before destructive changes, updates, plugin replacement, and world operations. Make requirements configurable for lower-risk edits.

### 67. Should deployments support draft, queued, approval-needed, in-progress, verifying, completed, failed and rolled-back statuses?

**Answer:**

Draft, awaiting approval, scheduled, running, verifying, completed, failed, outcome unknown, and rolled back. A scheduled remote operation must have an approved plan and fresh execution-time checks. An agent may run a previously approved host-local schedule while the desktop is offline; the UI retrieves its durable status later. No new interactive work is silently queued for an offline agent.

### 68. Do you want a shareable deployment manifest and reproducible release bundle for moving a server between hosts?

**Answer:**

Yes. Include versions, configuration, checksums, and restoration instructions. Reference secrets separately and flag artifacts that cannot legally be redistributed.

## F. Plugins, configs and extensions

### 69. Should the panel browse/install plugins from Hangar and Modrinth, with CurseForge or other sources later?

**Answer:**

Yes, using documented APIs, plus manual JAR uploads. Downloads enter staging and require deployment approval. Add other sources through adapters.

### 70. Should installed plugin metadata, dependencies, version compatibility, signatures/checksums and release notes be visible?

**Answer:**

Installed/latest versions, source, dependencies, compatibility, checksums, release notes, and licensing information. Clearly distinguish verified signatures from ordinary checksums.

### 71. Should it detect plugin JARs on disk that are not actually loaded, and failed/disabled plugins at runtime?

**Answer:**

Yes. Distinguish installed, loaded, disabled, failed, and unknown states using runtime information when available.

### 72. What config formats must have rich editors (YAML, JSON/JSON5, TOML, `.properties`, XML, HOCON, INI)?

**Answer:**

YAML, JSON/JSON5, TOML, and `.properties` first. Provide raw editing for other text formats; add format-aware validation and forms where reliable schemas exist.

### 73. Would you like custom forms for known plugins (e.g., Geyser/Floodgate/Multiverse/LuckPerms) rather than raw file editing only?

**Answer:**

Yes—Geyser, Floodgate, Multiverse, and LuckPerms, gated by detected versions and supported capabilities. Keep raw editing available.

### 74. Should the panel identify whether a plugin supports a documented live config-reload command and invoke it only with your approval?

**Answer:**

Yes, with explicit approval and version-specific support. Require a restart otherwise; don’t use global server reload as a shortcut.

### 75. Should a plugin profile/template be reusable across every server in one network?

**Answer:**

Yes, with shared defaults, server-specific overrides, and per-target deployment previews. Changes never deploy automatically.

### 76. Do you need a plugin dependency graph, incompatible-version warnings and per-server staged updates?

**Answer:**

Yes. Include missing dependencies, conflicts, unsupported server software, and staged updates. Label uncertain compatibility rather than treating it as verified.

### 77. Should the app edit `server.properties`, `paper-global.yml`, `paper-world-defaults.yml`, `spigot.yml`, `bukkit.yml` and JVM settings through typed forms?

**Answer:**

Yes, for supported settings and versions, respecting the existing allowlist for privileged launcher settings.

### 78. Do you want per-field descriptions, inline documentation, dangerous-setting warnings and schema validation for common options?

**Answer:**

Yes. Show defaults, restart requirements, and validation errors before deployment.

### 79. Should the app create and edit datapacks/resource-pack configuration and upload world templates?

**Answer:**

Yes, through staging, compatibility checks, reviewed deployment, and appropriate backups.

### 80. Do you want a pluggable plugin-specific UI/extension SDK for third-party authors later?

**Answer:**

Yes, by v1.0: versioned APIs, capability declarations, scoped permissions, and sample integrations. Run executable panel extensions outside the privileged control-plane process.

## G. Live console, players and statistics

### 81. **★** Which metrics are non-negotiable on the landing page: online, uptime, TPS, MSPT, CPU, RAM, heap, disk, players, ping, network bytes, errors?

**Answer:**

Server status, uptime, players, TPS/MSPT where supported, CPU, memory, heap, free disk, latency, and active alerts. Show unavailable metrics explicitly.

### 82. How frequently should metrics update (1s, 2s, 5s, 10s, configurable), and how much historical detail do you want?

**Answer:**

Configurable, with five-second updates by default. Stream console output live and reduce polling for hidden views.

### 83. How long should SQLite retain raw/aggregated telemetry (24h, 7d, 30d, 90d, custom)?

**Answer:**

Raw samples for 24 hours, one-minute aggregates for 30 days, and hourly aggregates for one year, with a configurable storage cap.

### 84. Should the app integrate with existing Prometheus/Grafana/Loki systems when available, or remain self-contained first?

**Answer:**

Remain self-contained, with optional integrations. Existing infrastructure must not be required.

### 85. Do you want per-world/per-chunk/entity statistics, JVM GC charts and flamegraphs using spark where supported?

**Answer:**

Yes, where supported. Make profiling explicitly initiated, time-limited, and clear about potential overhead.

### 86. Should the metrics view compare servers side by side, include alerts and show timeline annotations for deployments/restarts?

**Answer:**

Yes—side-by-side views, configurable alerts, and timeline markers for deployments, updates, restarts, and incidents.

### 87. Should the console support search, color/ANSI display, multi-server tabs, command completion, safe macros, and downloadable logs?

**Answer:**

Search, safe ANSI rendering, server tabs, supported command completion, reviewed macros, and downloadable redacted logs. Clearly distinguish game-console commands from host-shell commands.

### 88. Should commands entered in the panel be visible to all admins, only the author, or optionally masked for privacy?

**Answer:**

Authorized operators of that server. Record the author and outcome, redact recognized secrets, and avoid retaining sensitive command arguments.

### 89. **★** Which player actions must work: list, kick, ban, pardon, whitelist, op/deop, teleport, message, inventory, permission group?

**Answer:**

List, kick, ban/pardon, whitelist, message, teleport/transfer, and supported permission-group changes. Require stronger confirmation for op/deop and inventory modification.

### 90. Should player cards show names, avatars/skins, ping, playtime, session history, server, dimension/world and last seen?

**Answer:**

Name, UUID, avatar, edition, server, world/dimension, ping, session duration, playtime, and last seen—only where available.

### 91. Should Bedrock/Floodgate identities be visibly distinguished and safely normalized with Java UUID handling?

**Answer:**

Yes. Preserve edition and authoritative identifiers; never merge identities solely because display names match.

### 92. Which alerts should exist: host offline, crash/restart loop, high MSPT, low TPS, low disk, failed backup, failed deployment, abnormal players, plugin errors?

**Answer:**

Host offline, crashes/restart loops, sustained poor performance, low disk, failed backups/deployments, and plugin errors. Make unusual-player-activity alerts optional, with deduplication and cooldowns.

### 93. Delivery methods: desktop push, in-app inbox, email, Discord webhook, Slack, Telegram, SMS, mobile push?

**Answer:**

In-app inbox and desktop notifications by default. Offer opt-in Discord webhooks and email; keep other delivery channels extensible.

## H. Backups, jobs and incident recovery

### 94. **★** Backup targets: same VM, separate NAS/NFS/SMB, S3-compatible object storage, external cloud provider, or multiple?

**Answer:**

Backups must work while the controlling desktop is offline. REVIEW DEFAULT: support a configured NAS destination and bounded local backup storage when necessary, then verified transfer to the desktop when it returns. The desktop remains a supported off-host destination, but cannot be the only executable destination for an offline schedule. Actual NAS/path, quota, and encryption-key recovery arrangements are not selected yet. Mark VM-local-only backups as lacking protection against loss of that VM; never report an unverified transfer as an off-host recovery point.

### 95. Do you want full snapshots, per-world backups, config/plugin-only backups and selective file restore?

**Answer:**

Full-server, per-world, and configuration/plugin backups, plus selective restore. Use a supported consistency mechanism or stop the server before copying live world data.

### 96. Default frequency and retention (e.g., hourly 24, daily 14, weekly 8), storage quota and compression level?

**Answer:**

Daily backups retained for 14 days and weekly backups for eight weeks, plus recovery points before risky changes. Offer optional hourly world backups. Retention must respect a configurable storage quota.

### 97. Should backups be triggered before any restart, before deploy only, on fixed schedules, or manual?

**Answer:**

Run backups on approved schedules, manually, and before risky deployments/world operations. Ordinary restarts do not require a new backup unless pending changes make one necessary. The host agent executes approved host-local schedules even if the desktop is offline; use an available destination and report failures/quotas explicitly.

### 98. Should the app verify restore integrity automatically by extracting into a sandbox and checking manifests?

**Answer:**

Yes—verify checksums, manifests, and archive readability. Offer periodic isolated restore tests; distinguish integrity verification from a successful server-start test.

### 99. Should a failed deployment automatically restore backups, only restore staged config, or await owner confirmation?

**Answer:**

No. Pause and offer reviewed recovery options. Restore affected configuration when sufficient; restore worlds or the whole server only with explicit confirmation.

### 100. Should jobs include cron schedules, event-triggered actions, one-click maintenance presets, and dependency-aware task chains?

**Answer:**

Recurring schedules, approved event triggers, maintenance presets, and dependency-aware task chains. Show scope, prerequisites, progress, and outcome. Keep the host agent the authoritative executor of host-local schedules at all times; the desktop configures and approves them. Retain schedule revisions, occurrence identifiers, and durable job results to avoid duplicate execution. Define timezone/DST, overlap, and interrupted-job policies in the implementation specification.

### 101. Can the panel ever run unattended server updates/restarts, or only outside peak hours with prior approval?

**Answer:**

Unattended updates/restarts are allowed only under an explicitly approved schedule or maintenance policy, with execution-time checks. Approved host-local schedules continue while the desktop is offline. Required artifacts must already be available. If the host/agent was unavailable, mark a missed maintenance occurrence as missed rather than silently replaying it. Ambiguous disruptive outcomes are reconciled, not automatically retried.

### 102. Should you be able to delay a restart until no players are online and send scheduled chat broadcasts?

**Answer:**

USER OVERRIDE: give players a five-minute warning, then kick remaining players, save/stop gracefully, and restart. Their continued presence is not a reason to defer or request another approval. REVIEW DEFAULT: if save/graceful shutdown fails, pause with an actionable error rather than automatically forcing termination. Preserve the job/countdown outcome across disconnects.

### 103. Should backups and jobs be tied to networks/fleets or to individual servers?

**Answer:**

Keep execution and recovery history per server, with reusable network/fleet policies and coordinated plans. Agents can execute preapproved host-local jobs independently. Cross-host workflows must declare whether they require desktop coordination; offline autonomy does not imply an automatic distributed cutover.

### 104. Do you want a one-click disaster recovery wizard to reconstruct a host/server from a manifest and backups?

**Answer:**

Yes. Guide host enrollment, artifact retrieval, configuration restoration, secret re-entry, compatibility checks, and health verification. Require review before replacing existing files or starting the restored server.

## I. Hosting providers and infrastructure integrations

### 105. **★** Do you currently have an Apex Hosting server, or is Apex only a desired future integration?

**Answer:**

Yes. Apex integration remains excluded from v1.0.

### 106. If Apex: does your account visibly provide a Multicraft API key or enable API access, FTP access, and/or any other documented API?

**Answer:**

Leave this unverified until Apex integration begins. Discover supported capabilities then; no credentials needed now.

### 107. If only unencrypted FTP exists, would you permit it after a security warning, only through a trusted tunnel, or disable it?

**Answer:**

Disabled by default. Prefer encrypted transport; require an explicit exception and a trusted encrypted tunnel if FTP is unavoidable.

### 108. Which providers should be supported after Apex: Pterodactyl, Pelican, Multicraft, Bloom, Bisect, Shockbyte, generic SFTP/SSH?

**Answer:**

Generic SSH/SFTP, Pterodactyl, Pelican, and Multicraft. Implement against documented interfaces and declare each adapter’s supported capabilities.

### 109. Should a hosting connector be able to manage files only, or also power, console, metrics, backups and scheduling as allowed?

**Answer:**

Support every capability the provider safely exposes. Clearly mark unavailable operations instead of presenting controls that cannot work.

### 110. Do you want an official adapter SDK with versioning, provider manifests, capability negotiation and sample connector?

**Answer:**

Yes—versioned interfaces, provider manifests, capability negotiation, scoped credentials, contract tests, and a sample connector.

### 111. Should hypervisor providers (Proxmox/XCP-ng) be separate from Minecraft hosting providers and expose VM snapshots/power with confirmation?

**Answer:**

Yes. Prioritize XCP-ng for your environment. Show VM resources and power state; require explicit approval for VM power changes and snapshot restoration. Snapshots supplement server backups.

### 112. Do you want Cloudflare DNS, public IP, SRV record and connection endpoint visibility/read-only checks or actual DNS editing?

**Answer:**

Read-only endpoint, A/AAAA, and SRV inspection by default. Support reviewed DNS changes where ownership is explicitly delegated. For your OpenTofu-managed records, propose changes through that workflow.

### 113. Should health probes run from a public remote vantage point, or only from the local control plane and agents?

**Answer:**

On the desktop control plane and agents by default, with an optional explicitly enrolled external probe. Label the probe’s location and time so a failure from one network isn’t presented as proof of a global outage.

## J. Visual design and product interaction

### 114. **★** Name **three desktop/web products whose design you want to emulate** (e.g., Linear, Raycast, Grafana, Proxmox, UniFi, Pterodactyl, Discord, Apple Settings) and what specifically attracts you to each.

**Answer:**

Linear for visual hierarchy and focused workflows; UniFi for topology and operational visibility; GitHub Desktop for staged changes and review. Use these as design references, not copied branding/assets.

### 115. **★** Should the aesthetic be minimalist/Apple-like, premium SaaS, developer/terminal-first, gaming-themed, glassy/futuristic, or a blend?

**Answer:**

A restrained, premium technical interface: clean typography, clear status, and minimal decoration. Minecraft character can appear in optional server artwork.

### 116. **★** Preferred accent(s) with hex codes or reference images; are Minecraft greens desired or should the interface avoid Minecraft branding colors?

**Answer:**

Blue—`#2563EB` in light mode and `#60A5FA` in dark mode. Reserve green, amber, and red for operational status.

### 117. **★** Theme behavior: independent light and dark themes, follow OS, manual override, AMOLED black, high contrast, scheduled auto-switch?

**Answer:**

Follow the OS by default with persistent light/dark overrides and a high-contrast option. USER ADDITION: provide colorblind-friendly choices for every supported theme mode. Use labels, icons, and patterns as appropriate so color never conveys status by itself.

### 118. Base surface colors: near-black, charcoal, graphite, warm gray, cool slate, pure white, off-white, or custom?

**Answer:**

Off-white surfaces in light mode and graphite surfaces in dark mode, with distinct layers for navigation, content, and inspectors.

### 119. Accent usage: subtle on buttons and highlights or saturated chart/status-heavy dashboard?

**Answer:**

Use them selectively for primary actions, selection, focus, and important chart series. Keep most surfaces neutral.

### 120. Layout: permanent left sidebar, collapsible rail, top navigation, command palette, split-pane workspace, or user-configurable?

**Answer:**

A collapsible left sidebar, breadcrumbs, global search/command palette, and contextual split-pane workspaces.

### 121. Content density: spacious cards, balanced pro UI, compact IDE-like tables, or density toggle?

**Answer:**

Balanced professional density by default, plus a compact mode. Keep operational tables efficient without crowding forms.

### 122. Corner radius preference: square, subtle 4–6px, 8–12px, soft 16px+?

**Answer:**

Mostly 8px, with smaller radii for controls and slightly larger ones for dialogs. Avoid excessive rounded cards.

### 123. Fonts: Inter, Geist, SF Pro/system, IBM Plex, JetBrains Mono for data, another?

**Answer:**

Inter for the interface and JetBrains Mono for consoles, paths, identifiers, and code. Bundle fonts for fully local operation.

### 124. Icon preference: Lucide/thin outline, filled, duotone, pixel/Minecraft hints, OS-native?

**Answer:**

Consistent Lucide-style outline icons, with labels or tooltips for unfamiliar actions.

### 125. Should light/dark palettes be symmetric or optimized independently (contrast, shadow, surface colors)?

**Answer:**

Yes. Tune contrast, surfaces, borders, shadows, charts, and colorblind palettes separately.

### 126. Do you want tasteful translucency/glass blur, gradients, grain textures, shadows, or a flat utilitarian look?

**Answer:**

Subtle shadows and restrained gradients. Optional translucency for navigation or overlays; keep operational content opaque and readable.

### 127. Animation: none, restrained 100–200ms interactions, playful microinteractions, or sophisticated transitions?

**Answer:**

Restrained 150–200ms interactions, with reduced-motion support. Avoid decorative animation that distracts from status or delays actions.

### 128. Should server cards show artwork/status banners, small minimalist rows, customizable widgets or multiple view modes?

**Answer:**

Compact status rows by default, with an optional card view and optional artwork. Preserve the same information and actions across views.

### 129. How many metrics and actions should appear before opening a server detail page?

**Answer:**

Status, player count, TPS/MSPT where available, memory use, and active alerts. Provide console access and a clearly labeled actions menu.

### 130. What should the first screen be: whole-fleet dashboard, last-opened server, network topology, favorites, or configurable?

**Answer:**

A fleet overview, configurable to favorites or the last-opened server.

### 131. Should there be a persistent global console/drawer accessible from anywhere in the app?

**Answer:**

Yes—a collapsible drawer with server tabs, a prominent target-server label, and connection state. Keep it closed by default.

### 132. How do you envision server grouping visually: folders, color-coded tags, expandable clusters, draggable groups, geographic sites?

**Answer:**

Expandable networks/groups, searchable text tags, and favorites. Optional tag colors must also work with colorblind palettes.

### 133. Do you want an interactive topology canvas with zoom/pan, edges, proxy layers, world nodes and live traffic indicators?

**Answer:**

Yes, with zoom/pan, proxy and backend relationships, host grouping, and status overlays. Show traffic indicators only when measured data exists. Dragging changes layout; configuration changes use reviewed forms.

### 134. Should the staging studio be visually modeled after VS Code, GitHub Desktop, a two-pane SFTP client or a custom deployment wizard?

**Answer:**

VS Code-style navigation and editing, with GitHub Desktop-style change review and a dedicated deployment basket.

### 135. Which chart style: simple thin-line sparklines, Grafana-like technical panels, gradient area charts, gauges, or user-selectable?

**Answer:**

Clear line charts and sparklines, with detailed technical views when opened. Include units, accessible palettes, tooltips, and a table alternative. Use gauges sparingly.

### 136. Alerts: prominent status rail, toast notifications, notification center, per-server badges, or all with configurable severity?

**Answer:**

A notification center, per-server badges, and a persistent banner for critical conditions. Use toasts for immediate feedback without making them the only record.

### 137. Destructive actions: type server name, hold-to-confirm, two-step dialog, passkey reauthentication, or configurable by risk?

**Answer:**

Scale confirmation to risk. Ordinary operations get a clear confirmation; deleting servers/worlds or overwriting restores requires typing the target name and reviewing the recovery point.

### 138. Do you want dark/light app-specific logos, animated loading mark, server icons and cover images?

**Answer:**

Yes—light/dark app logos, server icons, and optional cover images. Keep loading indicators restrained and respect reduced motion.

### 139. Should console/logs use colored Minecraft chat components, plain monospace, or switchable rendering?

**Answer:**

Switchable colored Minecraft formatting and plain monospace, with safe rendering, search, and plain-text export.

### 140. Should custom dashboards support drag/reorder/resize/save dashboard layouts per user?

**Answer:**

Yes—reorder, resize, hide, and restore widgets. Save layouts per user/device, with sensible defaults and a reset option.

### 141. Which screen sizes are critical (e.g., 14-inch laptop, ultrawide desktop, 1080p monitor, tablet, phone)?

**Answer:**

Prioritize installed desktop use on 14-inch laptops, 1080p monitors, and ultrawide displays. The earlier phone/tablet/browser recommendation is superseded by the explicit Windows/macOS-only decision. Do not add mobile/PWA scope.

### 142. Preferred native desktop UX: traditional title bar, custom chrome, tabbed windows, tray app, global keyboard shortcuts, multi-window?

**Answer:**

Native title-bar/window controls, an optional tray/menu-bar presence, keyboard shortcuts, and multiple windows. Closing a window should follow an explicit preference for whether the control plane keeps running.

### 143. Would you like a polished onboarding wizard and optional in-app guided tours/tooltips, or a no-nonsense power-user interface?

**Answer:**

A concise wizard for owner setup, host enrollment, server adoption/provisioning, and backup configuration. Offer contextual help and optional tours that experienced users can skip.

## K. Engineering stack, apps and packaging

### 144. **★** Frontend preference: React + TypeScript, Vue, Svelte, Angular, or no preference? Vite, Next.js, TanStack Start, or another framework?

**Answer:**

Avalonia with C#/XAML and MVVM. The later installed-desktop-only decision replaces the earlier React/TypeScript/Vite recommendation. No browser frontend is required.

### 145. **★** Backend preference: C#/.NET, Node/TypeScript (Nest/Fastify), Go, Rust, or another? Do you want the host agent in the same language?

**Answer:**

C#/.NET application core, using the accepted .NET 10 LTS baseline with maintained security patches. Separate domain/application logic, jobs, persistence, transport, and desktop presentation. The UI calls local application services directly. A narrowly exposed agent-facing HTTPS/mTLS endpoint remains necessary; no browser administration backend is required.

### 146. **★** Desktop preference: Tauri 2, Electron, Avalonia/.NET, Qt, Flutter, or compare before deciding?

**Answer:**

Avalonia for installed Windows and macOS applications. This supersedes the earlier Tauri/Rust sidecar approach. Keep the management core independent of windows/screens, with an explicit tray/menu-bar lifecycle preference.

### 147. **★** Mobile preference: responsive web/PWA only, native iOS+Android via Expo/React Native, Tauri mobile, Flutter, or later milestone?

**Answer:**

No mobile applications, responsive browser administration, or PWA. Installed Windows and macOS applications only.

### 148. For a React UI: Tailwind/shadcn+Radix, Material UI, Mantine, Fluent UI, custom primitives, or another?

**Answer:**

Avalonia-native controls, templates, and shared theme tokens. The earlier Tailwind/shadcn/Radix recommendation is not applicable to the chosen UI. Include independent light/dark/high-contrast and colorblind palettes, keyboard access, focus states, and accessible labels.

### 149. For .NET backend: EF Core, Dapper/raw SQL, or mixed; Minimal APIs vs controllers; OpenAPI client generation style?

**Answer:**

Direct local application-service interfaces for the desktop UI; EF Core for application data and versioned SQLite migrations. Use parameterized SQL for telemetry batching only when justified by measurements. Do not require local HTTP Minimal APIs/OpenAPI clients merely to connect the UI to its own core.

### 150. Should telemetry/console use WebSockets, Server-Sent Events, gRPC streams (agent), or let architecture team decide?

**Answer:**

Use local observable/event interfaces for UI metrics, console, and job progress. Use outbound agent-initiated, versioned gRPC streams over HTTPS/mTLS for host communication. The earlier UI SignalR/WebSocket recommendation is superseded. Reconnection retrieves fresh state and recorded operation results without replaying disruptive commands.

### 151. Should host agents use C#/.NET, Go (single static binary), Rust, Java, or align with backend?

**Answer:**

C#/.NET agent, sharing appropriate contracts and validation with the application. Distribute self-contained Linux builds under systemd, with a separate root-owned restricted helper. Include durable execution of previously approved host-local schedules while the desktop is unavailable.

### 152. Should JVM bridge plugins be Java or Kotlin? Which JDK and build tools (Gradle/Maven)?

**Answer:**

Java bridge plugins built with the Gradle Wrapper. Select pinned JDK toolchains and bytecode targets per supported server family; use separate compatibility adapters where needed. Do not assume the host's current Java 25 runtime sets the bytecode target for every bridge.

### 153. **★** SQLite is fixed: do you prefer EF Core migrations, raw SQL + FluentMigrator, or another explicit migration mechanism?

**Answer:**

Versioned EF Core migrations, exclusive migration locking, and a database backup before migration. Keep the active SQLite database on local desktop storage. Recover using a compatible backup rather than attempting blind schema downgrades. Use explicit desktop migration/export/import; do not share a live database between Windows and Mac.

### 154. Monorepo orchestration preference: pnpm + Turborepo, Nx, Bun, yarn workspaces, custom `just` commands, or no preference?

**Answer:**

A .NET solution containing desktop UI, application core, persistence, agent, privileged helper, shared contracts, and tests. Keep Java bridge plugins in a separate Gradle project within the same repository.

### 155. Dependency and build tooling: pinned versions via mise/asdf/Volta, `global.json`, Gradle wrapper, dev containers, Nix, or normal local installs?

**Answer:**

Pin the .NET SDK through `global.json`, centralize NuGet versions, commit dependency lock files, and use the Gradle Wrapper with pinned Java toolchains.

### 156. API style: REST+OpenAPI, GraphQL, gRPC, or hybrid with streaming transports?

**Answer:**

Direct service interfaces inside the desktop app, and versioned gRPC contracts over mTLS for agents. Add local IPC only where separate processes require it.

### 157. Should plugin/provider integrations be dynamically loadable runtime plugins, compile-time modules, or versioned external processes?

**Answer:**

Built-in adapters as normal modules; executable third-party extensions in separate processes with explicit capabilities and permissions. Don’t load arbitrary extension code into the privileged helper.

### 158. Docker/Compose local development required? Linux development environment with actual systemd test VM available?

**Answer:**

No Docker/Compose requirement. Support native Windows/macOS development and a disposable Debian 13 VM for real systemd, enrollment, permissions, schedules, and recovery tests. USER OVERRIDE TO QUESTION 195: the real `mc-server` may also be used/modified when necessary, with existing worlds/configuration protected, risky changes backed up, and changes recorded. It is not a disposable test target.

### 159. Native desktop packaging: DMG/PKG, signed `.app`, Windows MSIX/MSI/EXE, Linux AppImage/DEB/RPM/Flatpak—what must ship first?

**Answer:**

A Windows installer and a macOS `.app` distributed through a DMG. Target Windows x64 and macOS Apple Silicon/Intel, with explicit minimum OS versions and installer tests.

### 160. Are you able/willing to sign macOS/Windows releases and use Apple/Windows developer accounts? Unsigned dev builds okay initially?

**Answer:**

Unsigned development builds are acceptable. Public stable releases should use Windows code signing and macOS signing/notarization. Required accounts and signing credentials must be arranged separately and kept outside Git.

### 161. Update mechanism: GitHub Releases with user-approved updates, auto-update service, manual installers only, or custom host?

**Answer:**

Check GitHub Releases, show release notes, verify authenticity, and require approval before installation. Warn about active jobs and provide recovery for failed updates. Keep panel, agent, Java, and Minecraft updates distinct.

### 162. Backend deployment packaging: standalone binary, Docker image, systemd service, Debian package, Kubernetes Helm chart, all or prioritized?

**Answer:**

Bundle the control plane with the desktop application. Distribute the Linux agent as a self-contained executable with a Debian package and reviewed SSH installer. The restricted agent must not be able to replace its privileged helper.

### 163. Where should user settings/secrets live: OS keychain, control plane secret store, optional external Vault, or both with explicit ownership?

**Answer:**

Use Windows-protected credential storage and macOS Keychain. Store references and non-secret metadata in SQLite. Keep agent credentials narrowly scoped, support rotation/revocation, and exclude secrets from ordinary exports and logs.

### 164. Should the desktop app function offline for read-only cached data/staging edits, or require backend connection?

**Answer:**

Allow cached inspection, local staging edits, diffs, and draft plans without agent connectivity; mark observations stale. Disable new remote execution until reconnection and fresh checks, without silently queuing/replaying interactive operations. Approved schedules already installed on a running agent continue independently while the desktop is offline. One controlling desktop per fleet; switching desktop requires explicit migration.

## L. Security, accounts and permissions

### 165. **★** Who will access the panel: just you on a LAN, VPN users, trusted friends, moderators, or internet-wide public administration?

**Answer:**

You are the sole owner/admin initially. The application remains local, with host connectivity over LAN or VPN.

### 166. **★** Should login be local accounts, OS-auth desktop unlock, GitHub/Google OAuth, passkeys, TOTP, SSO/OIDC, or combinations?

**Answer:**

An identity tied to the OS user, with OS-backed reauthentication where supported and an optional separate application passphrase. No mandatory online account.

### 167. Is multi-user RBAC required now? If yes, which roles and exact allowed actions (viewer, moderator, operator, deployer, owner)?

**Answer:**

One owner account initially. Build permission checks into the core so additional roles can be introduced without redesigning privileged operations.

### 168. Do you want per-host and per-server permissions, not just global roles?

**Answer:**

Yes. Scope agent capabilities, credentials, service operations, file access, and integrations to their approved targets.

### 169. Should destructive deploy/restore actions require a second approval or an MFA step?

**Answer:**

Risk-based owner reauthentication, target-name confirmation, and recovery-point review for destructive restores and deletions. No second-person approval requirement.

### 170. Is outbound-only agent connectivity important so that no inbound firewall holes are required on VMs?

**Answer:**

Yes—outbound HTTPS/mTLS to the desktop control plane. No inbound agent-management listener is required on Minecraft hosts.

### 171. Are you willing to run a private CA/certificate provisioning system for mTLS, or prefer enrollment tokens + HTTPS and rotation?

**Answer:**

An application-managed private CA, individual agent certificates, short-lived single-use enrollment tokens, rotation, and revocation. Keep trust scoped to the application rather than installing a globally trusted CA.

### 172. What is your trusted network model: LAN-only, WireGuard/Tailscale-only, Cloudflare Access tunnel, exposed HTTPS with authentication?

**Answer:**

Restrict the agent-facing endpoint to approved LAN/VPN access. Require explicit approval for firewall changes or public exposure; provide no browser administration endpoint.

### 173. Should session/audit logs be immutable/exportable and how long should they be retained?

**Answer:**

Append-only through application APIs, exportable, secret-redacted, and retained for 365 days by default. Include tamper detection, while clearly stating that local SQLite storage is not inherently immutable.

### 174. Should the UI offer emergency read-only mode or lock deploys/restarts during incidents?

**Answer:**

Yes. Block new mutations, show active jobs, and provide safe cancellation options where supported. Require owner confirmation to unlock operations.

### 175. Should secrets be centrally shared among servers or isolated per integration/server with explicit scope and rotation?

**Answer:**

Isolate them per host, server, and integration by default. Support explicit sharing only when required, with visible scope, rotation, and revocation.

### 176. Is there any need for guest/public read-only server status pages separate from the private admin panel?

**Answer:**

No built-in public website for v1.0, consistent with installed-app-only scope. Allow sanitized status exports for a separately managed status service.

## M. GitHub development and agent governance

### 177. **★** How many implementation agents may run concurrently (2, 3, 5, more), considering API quota and cost?

**Answer:**

Four total, including the lead. Allocate the other slots between implementation and review, with explicit usage limits. Run additional reviews sequentially when necessary.

### 178. **★** Which should be lead orchestrator—Claude or Codex—and which specific *currently available* model/reasoning level do you prefer?

**Answer:**

Codex using **GPT-6.1 Sol with high reasoning effort**, matching this session. Confirm availability at implementation kickoff; don’t silently substitute providers or models. This is my proposed baseline, consistent with [official OpenAI guidance](https://developers.openai.com/api/docs/guides/deployment-checklist).

### 179. Which agents are preferred for backend, frontend, UI design, JVM plugins, integrations, and security, and should the orchestrator select dynamically?

**Answer:**

Use the approved provider/model initially, with focused roles for desktop UX, application core, agent/security, JVM integrations, and testing. Give each task clear ownership and acceptance criteria.

### 180. **★** Three QA agents as functional, security/reliability and UX/accessibility/performance: good, or would you change their specializations?

**Answer:**

Three: functional correctness; security/reliability; and UX/accessibility/performance. Reviewers must not approve their own implementation work.

### 181. Are you happy requiring three independent QA verdicts on high-risk PRs and two including security on regular PRs?

**Answer:**

Two independent agent review verdicts, including security, for regular code PRs; all three QA roles for privileged operations, enrollment, secrets, updates, backups/restores, and destructive workflows. REVIEW CORRECTION: agent verdicts and GitHub approving reviews are different controls. Agents sharing one GitHub account do not supply multiple independent GitHub approvals, and PR authors cannot approve their own PRs. Define an enforceable reporting/check/identity mechanism before enabling approval-count requirements; do not claim the reviews are enforced when they are not.

### 182. Should the boss merge eligible PRs automatically, or request your approval for every PR/major milestone?

**Answer:**

The lead may merge eligible PRs after required checks and reviews. Owner approval is required for major scope/architecture changes, licensing changes, expanded privileges, and public releases. The general live-server approval recommendation is qualified by the later explicit authorization in question 195 to use/modify `mc-server` when necessary; preserve that scope and data-protection requirements rather than requesting the same authorization again for routine necessary work.

### 183. Should workers create GitHub Issues and PRs directly, or only have lead agent do GitHub mutations?

**Answer:**

The lead owns issue planning, priorities, and merges. Implementation workers may open PRs for assigned issues. QA agents provide findings and review verdicts.

### 184. Do you want one issue per feature slice, one PR per issue, or more granular subfeature PRs?

**Answer:**

One issue per bounded feature slice and one PR per issue. Split oversized work into independently testable changes; keep each PR reviewable.

### 185. Branch naming: `feat/*`, `fix/*`, `agent/*`, issue-number prefixes, or another convention?

**Answer:**

`feat/<issue>-<description>`, `fix/<issue>-<description>`, and equivalent `docs/`, `test/`, or `chore/` prefixes.

### 186. GitHub Actions CI only, or add local Git hooks, pre-commit, Buildkite, self-hosted runners?

**Answer:**

GitHub Actions plus local formatting, build, test, and secret-scan commands. Untrusted PR jobs receive no production credentials or signing secrets.

### 187. Should branch protection demand signed commits, CODEOWNERS, merge queue, mandatory status checks and history linearity?

**Answer:**

Protected main, required checks and review evidence, CODEOWNERS for sensitive paths, signed commits, and linear history; use a merge queue where available. Validate repository/account capabilities and signing setup first. The exact GitHub review-identity/check mechanism remains to be configured; multiple agent personas on one account must not be represented as multiple independent approving users.

### 188. Do you want release builds for macOS/Windows/Linux on every PR, nightly, weekly, or tagged releases only?

**Answer:**

Build/test Windows, macOS, and Linux-agent targets on PRs. Produce signed public installers only from approved release tags. Nightly preview packages can be optional.

### 189. Preferred test frameworks by language (xUnit/NUnit/MSTest, Vitest/Jest, Playwright/Cypress, JUnit5/Kotest)?

**Answer:**

xUnit for .NET, Avalonia headless UI tests plus real Windows/macOS interaction tests, and JUnit for Java bridges. Add provider contract tests and disposable Debian VM integration tests. [Avalonia testing documentation](https://docs.avaloniaui.net/docs/testing/setting-up-the-headless-platform)

### 190. What minimum critical-code coverage and mutation-testing targets do you expect; accept initial defaults of >=90% line, >=85% branch, >=80% mutation for safety modules?

**Answer:**

At least 90% line coverage, 85% branch coverage, and 80% mutation score for safety-critical modules. Prioritize meaningful failure-path tests over reaching numbers through superficial assertions. Use Stryker.NET for applicable C# mutation testing. [Stryker.NET documentation](https://stryker-mutator.io/docs/stryker-net/introduction/)

### 191. Should QA include accessibility (WCAG 2.2 AA), visual screenshots, chaos testing, fuzzing, provider contract tests and cross-platform installers?

**Answer:**

Keyboard and screen-reader testing, contrast/colorblind checks, reduced motion, visual regression, installer tests, fuzzing, provider contracts, and controlled failure injection. Include interrupted backups, lost connections, full disks, and partial deployments.

### 192. Which external coding tools may agents use: GitHub CLI, Codex MCP, Claude SDK, container runtime, SSH to test VMs, browser automation, Figma?

**Answer:**

Git/GitHub CLI, .NET and Gradle tooling, approved MCP tools, native UI automation, and SSH to designated test VMs. Additional paid services or credentials require your approval.

### 193. Should agents enforce zero `TODO` placeholders in shipped critical paths, complete API documentation, SBOM, SAST, license checks and secret scans?

**Answer:**

No placeholder success responses or unfinished critical paths. Require maintained documentation, SBOMs, dependency/license checks, static analysis, and secret scans. Document unsupported capabilities explicitly.

### 194. How often should lead agent report progress: after each merged PR, after every milestone, on failures only, or scheduled summaries?

**Answer:**

Brief updates during active work, summaries after merged PRs and milestones, and immediate notice of blockers, failed checks, or decisions needing your input. Include completed work, evidence, and the next step.

### 195. **★** Can agents modify **sandbox/test** VMs automatically, and which actions must always require authorization on the real live server?

**Answer:**

USER OVERRIDE: “you can use/modify my mc-server if necessary.” Necessary implementation/testing on `mc-server` (`10.77.40.160`) is authorized, with existing worlds/configuration protected, risky changes backed up, and actions/results recorded. Prefer disposable instances/VMs for destructive failure injection. This does not make production worlds disposable or authorize unrelated infrastructure/public exposure/paid resources.

## N. Optional ambitious features and longer-term roadmap

### 196. Should the app expose an assistant that reads logs and suggests fixes, or may also draft staged changes (never autoapply)?

**Answer:**

Explain logs, suggest fixes, and draft staged changes. It gets no independent execution authority. Support an optional local model; external providers require explicit configuration and disclosure of the data being sent.

### 197. Do you want optional GitOps: deploy when a GitHub branch/tag changes, after review and preflight?

**Answer:**

Optional Git integration may import approved branches/tags into deployment drafts. Applying changes still requires review, approval, and preflight checks.

### 198. Should the app offer plugin compatibility testing against an isolated duplicate Minecraft server before production promotion?

**Answer:**

Yes—a disposable native test instance with separate files, ports, and resource limits. Disable external integrations and never point it at production world files.

### 199. Would you like dynamic resource suggestions (e.g. JVM heap, chunk distance, GC) backed by telemetry with human approval?

**Answer:**

Yes. Explain the evidence, proposed adjustment, and restart requirements. Changes remain staged until approved.

### 200. Should users be able to configure Discord notifications, community status pages, public dashboards or a bot for basic server controls?

**Answer:**

Discord notifications and sanitized status exports. Keep public websites and bot-based administration outside the selected desktop-only scope.

### 201. Should the server network have a built-in player-facing maintenance page, MOTD configuration, icon, custom domains and SRV record checks?

**Answer:**

Yes—Minecraft MOTD, server icon, maintenance messages, connection endpoints, and SRV checks. Respect existing DNS ownership; no browser-based maintenance page is needed.

### 202. Do you want automatic blue/green server replacement, rolling migrations or traffic draining across compatible proxy backends?

**Answer:**

Support reviewed replacement plans, traffic draining, and controlled cutovers. Enable blue/green workflows only where compatibility and world-state handling are validated; don’t promise zero-downtime world migrations.

### 203. Should managing custom mods/modpacks via Modrinth/CurseForge be part of the MVP or postponed?

**Answer:**

Include it for the selected Fabric support by v1.0. Validate loader, Minecraft/Java versions, dependencies, and redistribution permissions. Use staging and reviewed installation, including manual imports.

### 204. Should the app support a third-party extension marketplace later (theme, provider adapter, Minecraft plugin integration, dashboard widget)?

**Answer:**

A curated extension catalog with versioned manifests, compatibility checks, explicit permissions, and reviewed installation. Support manual/offline installation without requiring a marketplace account.

### 205. Finally: **describe your ideal three-minute demo**. What exact clicks, screens, animations, actions and results would make you say, “This is precisely the panel I wanted”?

**Answer:**

Open the fleet overview, inspect live metrics, edit a setting or stage a plugin, review the diff and backup requirements, approve deployment, and observe job progress. Show the real five-minute restart countdown without shortening it to fit a three-minute demo; inspect a previously completed deployment and its recovery history for the completed outcome. No additional custom demo moment was specified.

## Open decisions and implementation clarifications

These entries do not require repeating the completed interview. Resolve them at the relevant specification or implementation checkpoint.

1. **License:** The exact license is not approved. Decide whether free competing forks should also be restricted, and select terms that match the actual resale/competition goal. Do not add a permissive or restrictive license by assumption.
2. **Capacity:** Approximately 20 server instances is the planning estimate, not a guaranteed capacity claim. Concurrent player/world counts and hardware assumptions need measured acceptance targets.
3. **Compatibility:** Define concrete supported Minecraft/Java/Bedrock and integration versions, the precise N-2 policy, and the capability matrix. Geyser/Floodgate and Multiverse on the current VM remain unverified. Native Bedrock Dedicated Server management is not a confirmed requirement.
4. **Backup destinations and recovery:** Select actual NAS/directory destinations, local-storage quota/retention, behavior when a target is unavailable, and recoverable encryption-key ownership. Distinguish a local recovery point from a verified off-host copy.
5. **Offline schedules:** Specify schedule timezone/DST rules, overlap locks, persisted occurrence IDs, restart/countdown recovery, and interrupted-job handling. The agent executes approved local jobs; the desktop's absence must not cause a second scheduler to take over and repeat them. The host's absence must not silently trigger delayed disruptive catch-up work.
6. **Desktop enrollment/migration:** Define the reachable LAN/VPN endpoint, listener lifecycle, certificate/key protection, and an explicit migration procedure that transfers ownership without concurrent controllers or losing agent trust and backup recovery access.
7. **Review enforcement:** Agent QA verdicts are not automatically GitHub approving reviews. Select the actual review identity/check mechanism and verify available repository protections before enabling blocking approval counts. Preserve independent review requirements without relying on impossible self-approvals.
8. **Packaging and signing:** Pin minimum Windows/macOS versions, agent architectures, dependency/toolchain versions, signing identities/accounts, and native CI/install-test coverage before stable releases. No signing credential is included here.
9. **Scoped or undefined integrations:** Resolve the final scope of network chat/shared inventory/network bans, later provider adapters, and conditional proxy/world workflows against the v1.0 requirement. Do not interpret an earlier “later” note as permission to silently remove a selected feature or promise unimplemented universal support.
10. **Restart wording:** The recorded assumption is that “install at next reboot” means the next approved Minecraft service restart. If VM/OS reboot was intended, it requires a separate explicit policy.
11. **Extension isolation:** Executable extensions run in separate processes, but a separate process alone does not provide a security sandbox. Specify the permissions and data access needed to enforce their declared scope.
12. **Agent governance:** Pin any cost/usage budgets and verify the approved provider/model at implementation kickoff. The questionnaire's future development-agent plan is not a claim that agents or branch protections have already been configured.

## Reference links

These links explain specific review findings; the owner's conversation answers remain the source of the product decisions.

- [Original questionnaire attachment](../.t3/userdata/attachments/986be1d8-b2a6-4dcd-9a5b-c65eded3c61a-2b7052db-9807-4d13-a47d-95577a3d93ea-md.md)
- [PolyForm Perimeter 1.0.0 license text](https://github.com/polyformproject/polyform-licenses/blob/1.0.0/PolyForm-Perimeter-1.0.0.md)
- [Avalonia supported platforms](https://docs.avaloniaui.net/docs/supported-platforms)
- [GitHub required-review approval rules](https://docs.github.com/en/pull-requests/how-tos/review-pull-requests/approving-a-pull-request-with-required-reviews)
- [Folia region/threading overview](https://docs.papermc.io/folia/reference/overview/)
- [Paper server.properties and RCON settings](https://docs.papermc.io/paper/reference/server-properties/)

## Document verification

The generated document was checked locally for exactly 205 questions in the original order, one nonempty answer per question, the corrected question 11–14 mapping, and the later desktop/scheduling/restart overrides. This is document verification only; it does not verify application behavior, current host state, networking, implementation completeness, or legal clearance.
