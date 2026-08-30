# Fault injection

Read `docs/architecture.md` §16. Fault injection is the primary correctness evidence for
changes to mutation, sync, or persistence: the failures are crash-window races that
ordinary tests do not expose.

The harness must start the backend, drive it to a named durable boundary, kill it hard,
restart it, and assert recovered observable state. A deterministic in-process kill hook
is acceptable; graceful shutdown is not.

Cover these scenarios: optimistic commit before enqueue; enqueue before `Dispatched`;
`Dispatched` before provider call; provider call before results persist; partial batch;
move after UID change; page before cursor commit; Gmail staged history before replay;
mailbox delete/recreate during sync; `Sending` outbox recovery; and startup reconciliation
for all of them.

Startup reconciliation is the sole recovery mechanism with in-memory job storage. It must
enumerate backfills, non-terminal mutation chains, dispatched attempts without results,
scheduled/sending outbox items, queued/fetching content, in-progress exports, and
undelivered notifications.

Arrange using `FakeMailProvider`, kill, restart, and assert the externally observable
outcome—not internal call order. Mark scenarios `FaultInjection` and `Deep` so they run
in `pnpm check:deep`, not the inner loop.
