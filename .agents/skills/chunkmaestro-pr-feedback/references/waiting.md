# Event-driven PR waiting

Use tools actually available in the active runtime. T3 instructions take
precedence over any general workflow copied from another project.

- After finishing needed updates/pushes, register a PR watch when an authorized
  task still waits on checks/reviews. End the turn after the watch call.
- A watch persists across pushes and wakes. Do not register it again, relink or
  list the PR on a wake unless a new push/takeover requires linking.
- On a wake, inspect the notified item and relevant current state once. Resolve
  stale heads/attempts before deciding to act. Do not implement a shell waiter,
  `gh --watch`, sleep/poll loop, scheduled wakeup or a parallel background waiter.
- Follow an active delegate's `next`. Completion wakes the parent; do not poll
  its status or read its running thread to simulate a waiter. Steering a real
  new constraint is different from asking for repeated progress.
- Before handback, unwatch the PRs being returned to the user. Continuing work
  through an already-established callback is not a claim of final completion.
  After merge/closure, stop its watch; delayed notices may still arrive.

If watch tools are unavailable, use a one-shot host status read and report what
is pending. Do not claim automatic monitoring or invent a tool. When a user asks
to keep monitoring, use a genuinely supported callback mechanism or explain the
specific availability limit without starting an ad-hoc poller.
