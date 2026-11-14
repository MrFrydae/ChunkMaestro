---
name: "chunkmaestro-native-desktop"
description: "Implement or verify Avalonia desktop interactions, accessibility, truthful capability states and native evidence for ChunkMaestro."
---

# Deliver native desktop behavior

Read [AGENTS.md](../../../AGENTS.md), affected Q25/Q45/Q81/Q114–148/Q164/Q191/Q205
in the [source](../../../docs/product/requirements-source.md), and the UI/acceptance
sections of the [directive](../../../docs/product/orchestration-directive.md).
Read the branch's native operations document and existing XAML/viewmodel contracts
when available. Reuse the established design tokens and controls.

Implement Avalonia/XAML/MVVM with direct local application-service calls. There is
no browser substitute or localhost REST layer solely for native-to-core calls.
Keep blocking storage/network work off the dispatcher. Validate close-during-work,
resource disposal and selected lifecycle behavior, rather than relying on an
`Async` name or a headless fixture to establish responsiveness.

Keep registered metadata, enrollment, observation, capability and permission
distinct. Cached data has an age/stale state; unknown metrics are unavailable,
not zero or healthy. Java TCP and Bedrock UDP observations are separate. Disabled
controls explain why and cannot return success through another execution path.
Offline interactive host operations are not silently queued for later replay.

For changed states, assess keyboard order/shortcuts, focus restoration, accessible
names/announcements, text beyond color, theme contrast and native scaling. Read
the actual selected font/theme/motion requirements; do not claim complete theme
or accessibility support from a small light/dark token test.

Use headless tests for viewmodel/interaction contracts and deterministic fixtures.
Exercise the installed/running native application with available native automation
for platform behavior. Read the automation API before use. A browser rendering,
mock inventory, screenshot or process that merely remains alive is narrower
evidence, not native composition, screen-reader or installer acceptance.

Capture relevant before/after states when requested or needed for the authorized
UI workflow. Use fabricated nonsecret fixtures and preserve the actual UI; do not
edit an incident image or mock away a failed operation and call it runtime proof.
Inspect final images for readability and record head, OS/architecture, scale and
fixture. Upload/comment only within the existing authorization; local artifacts
remain useful when publication is unavailable.

Report exact interactions executed, unsupported states, native/installer/screen-
reader gaps and performance conditions. Do not advertise a real five-minute restart
from an accelerated demo; host lifecycle acceptance belongs to the real boundary.
