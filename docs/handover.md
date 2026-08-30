# Current handoff

## Completed

**Stage C, second slice — the mutation model (§6).** Persistence landed in `e085284`; this
builds the mutation core on it, with the first real fault-injection scenarios.

- `MutationItem` holds stable local intent and nothing else. Sequences are monotonic per
  `(AccountId, MessageId)`, assigned in the same transaction as the insert and backed by a
  unique index, so a concurrent enqueue collides rather than producing two items that each
  believe they are next. Ordering is keyed on the message, never the occurrence.
- `MutationClaimService` claims with one conditional `UPDATE` that re-checks lease state and
  chain position together. It compare-and-swaps on the state and owner just read rather than
  comparing timestamps in SQL: EF's textual `DateTimeOffset` has variable-length fractional
  seconds and does not sort lexicographically, so a timestamp predicate there is quietly
  wrong.
- `MutationExecutor` runs §6's five steps. The `Dispatched` write is synchronous, durable and
  immediately before the provider call, and results plus terminal attempt state commit in one
  transaction with the chain re-evaluation that follows.
- `MutationChainEvaluator` re-evaluates later intent after a terminal failure instead of
  cancelling the chain. A membership-scoped removal whose membership is gone is cancelled
  carrying the originating failure; a message-scoped intent resolves wherever the message now
  is and proceeds.
- `MessagePendingChange` is per message per field. A later enqueue takes ownership of the
  field's row, so an earlier item's revert correctly touches nothing.
- `StartupReconciliation` enumerates backfills, non-terminal chains, dispatched attempts with
  no persisted result, and queued/fetching content, and releases leases orphaned by a dead
  process. Releasing a lease says only that no worker owns the item — never that the server
  did not see it.
- `MailProviderFactory` and a database-backed `IProviderMailboxResolver` were added because
  neither existed and `AddMutations` would otherwise have registered an unresolvable service.
  The factory resolves providers optionally and reports the gap rather than constructing one
  around the §4 credential store that is still deliberately unregistered.

## Next task

Continue **Stage C** with the sync state machines (§3): coverage backfill, change streams,
topology reconciliation and integrity reconciliation. The four tables exist and nothing drives
them. The governing rule throughout is that a cursor and the data it covers commit in one
transaction — replay is fine, skipping is silent and permanent.

Then extend the fault-injection harness to §16's remaining scenarios: mid-page before cursor
commit, Gmail staged history before canonical replay, mailbox deleted and recreated mid-sync.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §3, and §16 for the scenario table
- `docs/reviews/invariant-review.md` before mutation, sync, reconciliation or persistence work
- `server/MyloMail.Api/Mutations/` and `server/MyloMail.Api.Tests/Mutations/MutationHarness.cs`
  — the harness restarts the host over the same database file, which is what makes the crash
  scenarios mean anything

## Verification

- `pnpm check` green: `format · tsc · eslint · stylelint · build · tests(33) · vitest`.
- Seven `Category=FaultInjection` scenarios pass: the four mutation kill points, occurrence
  identity after a crash mid-move, startup reconciliation after a dead process, and the
  migration-backup window from the previous slice.
- **The crash scenarios were checked for discrimination.** Deferring the `Dispatched` write —
  the exact optimisation the invariant table warns about — fails four of the six mutation
  scenarios. A crash-window test that still passes with the invariant removed is worse than
  none.
- Independent invariant review found no violation, covering the dispatch boundary, provider
  identity in durable intent, execution-time resolution, claim atomicity, step 5's
  transactionality, terminal-failure re-evaluation, and reconciliation coverage.

## Risks / decisions

- **Send and the outbox are not built.** §6 gives send its own attempt with exactly one item
  and an `AmbiguousOutcome` recovery policy, but `OutboxItem` has no table yet, so
  `StartupReconciliation` cannot enumerate `Scheduled`/`Sending` items, in-progress exports or
  undelivered notifications. Those omissions are named in its doc comment rather than left to
  look complete. Anyone adding those tables must extend that sweep in the same change.
- Nothing drives the executor yet: there is no Hangfire worker, and `[AutomaticRetry(Attempts
  = 0)]` therefore appears nowhere so far. It must be on every job when jobs arrive.
- The ambiguity sweep identifies ambiguous items but does not yet reconcile them. Per-operation
  reconciliation — re-establishing location for a move, existence for a deletion — is sync
  work and belongs with §3.
- No production `ICredentialStore` registration, so no provider is registered and the executor
  cannot run against a real account yet. Deliberate; do not register the in-memory store.
- Graph topology enumerates top-level folders only; recursive hierarchy is a follow-up.
- Gmail's bounded-cursor conformance path has no green full run: its last one failed on a send
  rate limit. A flake, not a defect, but it is not currently backed by evidence.
