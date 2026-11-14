---
name: "chunkmaestro-worktrees"
description: "Isolate ChunkMaestro tasks and manage explicitly requested worktree or build-output cleanup without disturbing active agents or evidence."
---

# Use an isolated checkout

Inspect branch/status and registered worktrees before changing a checkout. Reuse
the task's existing branch and preserve unrelated edits, staging, signing and
recovery refs. Branch names follow the [directive](../../../docs/product/orchestration-directive.md).

For a T3 delegated child requiring isolation, use the supported delegation tool's
`isolation: worktree` after the required capability discovery. Reuse a valid
capability result already recorded in the thread. The tool prepares the checkout
from the parent's committed head; verify that head first. Never shell-create the
child checkout, put checkout setup in its prompt or substitute a top-level thread.
Use only approved, available model/settings and the directive's total concurrency.

Ordinary user-requested local worktrees may use Git when no task-specific runtime
mechanism is required. Inspect existing branch occupancy and base first. Do not
copy all ignored files or credentials into a new worktree. Read `global.json` and
use the existing user-owned SDK; platform toolchains remain actual prerequisites.

For stacked work, inspect dependency deltas before a signed rebase. Resolve shared
solution/central-package/probe conflicts intentionally; run discovery and cold
locked restore rather than replacing locks with another branch's old files.
Keep a recovery ref and use an exact old-head lease for an authorized rewrite.
Rerun affected checks and invalidate superseded QA. Link every pushed stack layer.

An audit authorizes inspection, not deletion. Cleanup needs the user's existing
cleanup scope. Prove a checkout is inactive before removing regenerable outputs;
a detached branch, missing process or absent task in one list is insufficient.
Do not poll a running delegate to discover inactivity. Preserve tracked files,
SDKs, databases, configuration, reports and recovery points. Do not follow symlinks
or treat arbitrary `bin`/`obj` paths as safe. Whole-worktree/branch deletion is a
separate operation and must respect unmerged or dirty work.

Report the exact checkout/base/branch and outcome. Missing activity information
is a limitation, not evidence that cleanup is safe.
