# ADR 0013: Native windows and explicit background controller lifecycle

Status: Proposed — no implementation or security proof at bootstrap.

Source: [Q010](../../product/requirements-matrix.md#q010), [Q117](../../product/requirements-matrix.md#q117), [Q142](../../product/requirements-matrix.md#q142), [Q144](../../product/requirements-matrix.md#q144), [Q146](../../product/requirements-matrix.md#q146), [Q159](../../product/requirements-matrix.md#q159), [Q164](../../product/requirements-matrix.md#q164), [Q172](../../product/requirements-matrix.md#q172). Dependent checkpoint: M1/M7; OD-06/08.

## Decision

Avalonia C#/XAML MVVM calls local .NET 10 application services. Core owns listener/jobs independently of windows. Explicit persisted keep-running/tray preference controls last-window close; first use explains connection and schedule consequences. With active operations show state and safe exit/cancel choices, persist unknown outcome if disconnected, never claim agent-local approved schedules stop just because desktop exits. Sleep/wake/interface changes refresh listener and observations over approved LAN/VPN only. Single local controller instance guards DB/migrations; multiple windows share services. Bundle local assets, use native chrome, accessible themes/keyboard/focus and truthful offline/stale views.

## Alternatives

Window-bound backend silently drops fleet control; always-on public service expands exposure; web/Tauri/Electron substitution conflicts with selected stack.

## Consequences

Installed Windows x64/macOS Intel/ARM require matching native test environments and explicit minimum versions. UI close does not cancel delegated host schedules. No automatic firewall changes or public fallback; unsupported tray behavior needs clear preference handling.

## Failure and recovery

Listener bind failure, desktop sleep, killed process, unavailable tray, duplicate app launch or failed update shows actionable disconnected state and never duplicates operations. Resume fetches durable results before enabling mutation; update waits for safe checkpoint.

## Test strategy

Headless VM/model tests plus real Windows/macOS close/tray/quit/sleep/wake/multi-window/duplicate launch/network change tests; screen reader, keyboard/focus, scaling, all colorblind themes, signed/unsigned installer behavior and active-operation update interruption.

All cases are planned. Implementation must attach exact commands, sanitized logs, environment/version and independent verdicts to the matrix before claiming behavior. Design acceptance belongs to the lead/owner at the dependent slice; this ADR grants no new privilege.
