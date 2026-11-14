# Proposed capability matrix

Source: [Q8–51, Q69–80, Q105–113, Q144–176, Q203–204](requirements-source.md); per-question tests in the [traceability matrix](requirements-matrix.md). **No cell is implemented at bootstrap `ab631d4`.** This describes intended gates, not tested product support or a current vendor/API claim. Exact version pins and N-2 semantics await [OD-03](open-decisions.md#od-03-concrete-compatibility-and-n-2); provider contracts must be checked against official documentation during implementation.

Legend: P = planned selected capability, C = conditional on detected compatible version/integration/permission, U = unavailable in this mode, X = explicitly excluded, ? = unresolved scope. Unsupported differs from temporarily offline/unavailable. UI and execution layers must enforce the same negotiated capabilities; a missing capability never produces successful no-op behavior.

| Platform | Service/files via registered native agent | Console/basic health | TPS/MSPT and rich players | World controls | Plugin/mod workflow | Version-specific gate |
| --- | --- | --- | --- | --- | --- | --- |
| Paper | P | C RCON/bridge | C Paper bridge | C Multiverse/API | P staged plugins | Exact MC/Java/API and plugin versions |
| Spigot | P | C RCON/bridge | C compatible bridge | C Multiverse/API | P staged plugins | Separate Spigot contract and bytecode |
| Vanilla | P | C supported console/status | U bridge-dependent richness; C independently proven source | U Multiverse; C guarded offline copy/backup | U Bukkit plugins; P staged configs/datapacks | No Paper/Spigot API assumption |
| Folia | P | C compatible adapter | C separate region-safe bridge | C only proven integration/thread semantics | C explicitly Folia-compatible plugins | Real region-thread tests; never Paper equivalence |
| Fabric | P | C supported console/status/adapter | C compatible integration, otherwise U | C supported APIs or guarded offline file workflow | P Fabric mods/modpacks in staging | Loader/MC/Java/dependency/license checks |
| Velocity | P proxy process | C proxy API/console | C proxy player/route data; U game TPS | U direct world controls | C proxy-specific plugins | Forwarding secret and backend compatibility |
| BungeeCord | P legacy proxy | C separate proxy adapter | C proxy player/route data; U game TPS | U direct world controls | C proxy-specific plugins | Legacy contract; new networks default Velocity |
| Geyser/Floodgate | C host platform lifecycle | C Bedrock UDP/status | C edition-authoritative identities | C underlying compatible server only | C detected version forms | Does not select native Bedrock Dedicated Server |
| Native Bedrock Dedicated Server | U unselected | C separately represented endpoint probe | U unselected | U | U | Explicit owner scope selection required |
| Other games | X | X | X | X | X | Outside product scope |

| Connection/provider mode | Status/metrics | Lifecycle | Files/deploy | Console | Agent-local autonomous schedules | Limits |
| --- | --- | --- | --- | --- | --- | --- |
| Registered native systemd + restricted agent | P measured with provenance | P exact approved instance/helper | P safe roots/approved immutable plan | C private RCON/bridge/journal source | P durable approved local revisions | Desktop offline is allowed; host offline yields missed, never catch-up |
| Agentless SSH/SFTP | C authorized observations | C only supported reviewed scoped action | C scoped SFTP/import/deploy with safety checks | C documented permitted channel | U no ChunkMaestro agent | Verify SSH host keys; never unrestricted shell fallback |
| Pterodactyl | C account/API | C account/API | C account/API | C account/API | U unless independently implemented local agent exists | Provider scheduling is a distinct C capability, not implied agent autonomy |
| Pelican | C account/API | C account/API | C account/API | C account/API | U unless local agent exists | Separate adapter tests, no inherited support assumption |
| Multicraft | C documented API/access | C documented API/access | C documented API/access | C documented API/access | U unless local agent exists | Account availability unverified; no Apex access assertion |
| XCP-ng | C VM power/resources/snapshots | C VM authority only | U game-file authority | U game console authority | U game agent autonomy | Optional independent hypervisor credentials/confirmation |
| Apex | X v1.0 | X v1.0 | X v1.0 | X v1.0 | X v1.0 | Post-v1 discovery, API/FTP unverified |
| Docker/Podman | U initial | U initial | U initial | U initial | U initial | Optional later adapter, no runtime/dev requirement |
| Cloudflare/DNS | C read-only diagnostics | U Minecraft lifecycle | C reviewed delegated DNS workflow only | U | U | OpenTofu-owned records use that workflow |

| Cross-cutting capability | Intended gate and explicit denial case | Planned evidence checkpoint |
| --- | --- | --- |
| Multiverse worlds/addons | Detected exact core/addon/engine version; absent addon disables portal/inventory graph/control. Rename/delete need recovery and stronger confirmation. | M5 platform fixture; M4 consistent world restore |
| LuckPerms/player inventory/op | Exact supported integration plus target authorization; elevated actions need stronger confirmation. No universal rich-player promise. | M5 positive/negative action tests |
| Java/Bedrock endpoints | Separate TCP/UDP/protocol/edition/vantage/time and identities; TCP success is no evidence of UDP success. | M1 data model/probe subset; M5 protocol fixtures |
| Plugin reload | Only documented exact plugin/version and owner approval; otherwise safe restart; global reload unavailable. | M3/M5 version-specific contract |
| Network list/transfer | C detected proxy/permission; read-only list first, reviewed transfer next. | M5 exact adapter tests |
| Network chat/shared inventory/network bans | ? OD-09; no enabled control until integration and scope resolved. | M5 dependent acceptance blocked |
| Blue/green/drain/cutover | C compatible proxy, consistent world-state handling and reviewed recovery plan; no zero-downtime guarantee. | M5 disposable replacement rehearsal |
| World resource policies | P disk/backup/permissions per world; U enforceable per-world CPU/RAM caps. | M4/M5 policy and truthful UI tests |
| Backups offline | C reachable configured NAS or bounded host-local storage; incomplete transfer is not off-host complete. | M4 real desktop-off/debian restore evidence |
| Third-party executable extensions | C proven OS confinement + versioned manifest + explicit grants + authenticated IPC; fail closed without sandbox. | M6 real Windows/macOS/Linux deny tests |
| AI/Git/catalog integrations | C explicitly enabled/documented; drafts/staging only, no independent execution or automatic apply. External AI discloses data categories. | M3/M6 no-auto-apply and egress tests |
| Public status/bot controls/mobile/browser | X built-in website, bot administration, mobile/PWA/browser admin. Sanitized exports/notifications remain selected. | M7 package/scope and export review |

For every future enablement record: provider/platform version, available grants, capabilities detected at time, positive result, denial result, official interface provenance, exact commands/fixtures and sanitized output. Contract mocks certify contracts only; live/native claims need matching integration evidence. No provider credentials or current host inventory were inspected by this docs-only task.
