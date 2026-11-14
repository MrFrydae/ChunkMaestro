# ADR 0003: Versioned streams and durable operation reconciliation

Status: Proposed — no implementation or security proof at bootstrap.

Source: [Q025](../../product/requirements-matrix.md#q025), [Q067](../../product/requirements-matrix.md#q067), [Q150](../../product/requirements-matrix.md#q150), [Q156](../../product/requirements-matrix.md#q156), [Q164](../../product/requirements-matrix.md#q164). Dependent checkpoint: M1.

## Decision

Use agent-initiated gRPC/mTLS with protocol/capability handshake. Authorize operation envelope with controller epoch, host/instance ID, unique operation ID, approved plan hash, expected state revision, deadline and typed action. Agent persists accepted operation before execution and journal result afterward under per-instance lock. Transport acknowledgments are distinct from accepted/applied/completed. Timeout/disconnect becomes outcome unknown. Reconnect fetches snapshot and journal since cursor, handles gaps with full resync, and never automatically resends disruptive work. Duplicate identity/hash returns durable prior state; same ID with different content is rejected. Retention must exceed approved retry/reconciliation window and expired identities cannot become fresh execution.

## Alternatives

At-least-once blind retries duplicate restarts; synchronous request success conflates delivery with completion; desired-state auto-apply would override manual changes.

## Consequences

No exactly-once claim across external systemd/game side effects. Persist execution intent, inspect observed state and pause ambiguous stages. Bounded queues/backpressure prevent logs starving commands.

## Failure and recovery

Dropped ACK before/after effect, agent crash between effect and journal, stale revision, cursor loss and late cancellation preserve unknown status until evidence resolves it. Reconciliation can require owner action; read-only retries remain bounded and safe.

## Test strategy

Drop each stream/ACK boundary; duplicate/reorder/malformed event; simultaneous conflicting actions; controller/agent restart; deadline expiry; journal truncation; cancel after point of no return. Assert no second restart, fresh-state fetch and explicit unknown outcomes.

All cases are planned. Implementation must attach exact commands, sanitized logs, environment/version and independent verdicts to the matrix before claiming behavior. Design acceptance belongs to the lead/owner at the dependent slice; this ADR grants no new privilege.
