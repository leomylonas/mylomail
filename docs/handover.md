# Current handoff

## Completed

**Stage C, third slice — the sync state machines (§3).** Persistence landed in `e085284`,
the mutation model in `23dde6a`. This drives the four sync tables, which existed but had
nothing behind them.

- `TopologySyncService` reconciles mailboxes per account, links parents in a second pass
  (a provider may report a child before its parent), refreshes the provider-reported counts
  the sidebar displays, and re-parents the children of a vanished mailbox rather than
  cascading a subtree away. `BumpGenerationAsync` handles delete-and-recreate.
- `CoverageService` commits each backfill page together with the resume token that covers it.
- `ChangeStreamService` commits page and cursor in one transaction, never advances on a null
  `NewCursor`, and routes every provider's invalid cursor through one triggered-
  resynchronisation path — `UIDVALIDITY`, expired `historyId` and `410 Gone` are the same
  event.
- Gmail's ordering problem is handled as §3 specifies: while coverage is incomplete, history
  is drained durably into `StagedChangeEvent` but left unapplied, then replayed in
  observation order once backfill completes. Each event is applied and deleted in one
  transaction, so a crash mid-replay resumes without reapplying or skipping.
- `MessageIngestor` is the shared upsert, following §1's matching precedence. Every write is
  an upsert, which is what makes replaying a page safe.
- `GenerationSnapshot` carries the topology generation each mailbox held when work was
  issued, and is checked on upserts, removals, flag changes and staged replay alike.

## Next task

The remainder of Stage C:

- **Scheduling.** Nothing drives any of this yet — no Hangfire worker, no self-scheduling
  jobs, no account throttle gate. Every job must be `[AutomaticRetry(Attempts = 0)]`; auth
  must not retry at all and throttling must wait exactly `Retry-After`.
- **Periodic integrity reconciliation** for the weakest IMAP tier. The state table is
  written but no cadence exists, and it needs the scheduler first. Keep it distinct from
  triggered resynchronisation: same machinery, different trigger and meaning.
- **Send and the outbox**, still absent — see the risks below.
- **Per-operation reconciliation of ambiguous mutations.** The sweep identifies them;
  re-establishing location for a move or existence for a deletion is still to write.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §3 and §6, and §16's scenario table
- `docs/reviews/invariant-review.md` before mutation, sync, reconciliation or persistence work
- `server/MyloMail.Api/Sync/GenerationSnapshot.cs` — short, and the reasoning generalises

## Verification

- `pnpm check` green: `format · tsc · eslint · stylelint · build · tests(40) · vitest`.
- Thirteen `Category=FaultInjection` scenarios pass, covering the mutation kill points, the
  sync page and cursor boundaries, staged replay, topology-generation mismatch on three
  paths, and the migration backup window.
- **Mutation testing is now wired into `pnpm check:deep`** (Stryker.NET, scoped to
  `Mutations/` and `Sync/`). First honest run: 205 killed, 113 survived, 55.91%. The `break`
  threshold is set to 55 as a ratchet — raise it as survivors are killed, never lower it.
  Note the first run scored 85.56% while killing nothing at all: it included the live
  conformance tests, every mutant timed out, and Stryker scores a timeout as a kill. Read
  the statuses, not the score.
- **Survivors worth killing** (the rest are log strings, counters and orderings):
  cursor advance on the staging path (`ChangeStreamService` line ~201, both the null check
  and its block survive); the whole live-path `ApplyContentAsync` call (line ~119) —
  nothing asserts change-stream application, only coverage; desired-state revert on failure
  (`MutationExecutor` ~163, `MutationChainEvaluator` ~55 and ~97); the unreported-batch-item
  `continue` (`MutationExecutor` ~201); pending-change creation on enqueue (`MutationQueue`
  ~120); and the expired-lease comparison (`MutationClaimService` ~95).
- **A third false-pass test was found this way.**
  `Desired_state_is_never_orphaned_by_a_crash_around_enqueue` asserts that the mutation and
  pending-change counts are equal — but the crash rolls the transaction back, so it asserts
  `0 == 0` and holds even with pending-change creation deleted entirely. It needs a
  surviving-enqueue case to mean anything.
- **Every crash scenario was checked for discrimination**, and this is worth continuing.
  Two tests written this slice passed against code that had the bug they existed to catch:
  a single-page cursor test that could not observe a skip, and a generation test that
  exercised only the one path that was already guarded. Both were rewritten and both now
  fail against the broken version. Writing the test to confirm the mechanism rather than to
  attack the failure is the recurring mistake.
- Independent invariant review found a real violation in the first version of this slice —
  the generation guard was missing on removals, flag changes and replay — which is fixed and
  re-reviewed clean.

## Risks / decisions

- **Send and the outbox are still not built.** §6 gives send its own attempt with exactly one
  item and an `AmbiguousOutcome` policy. `OutboxItem` has no table, so
  `StartupReconciliation` cannot enumerate `Scheduled`/`Sending` items, in-progress exports or
  undelivered notifications. Whoever adds those tables must extend that sweep in the same
  change.
- **Notification records are not derived from staged events yet.** §3 requires notifications
  come from the staging queue _before_ canonical replay, or live mail goes unnotified for the
  hours a large backfill takes. `StagedChangeEvent.ScannedForNotifications` exists for that
  path; there is no `NotificationRecord` table yet.
- No production `ICredentialStore`, so no provider is registered and none of this runs
  against a real account yet. Deliberate; do not register the in-memory store.
- `Program.cs` still does not map controllers or apply the per-launch token middleware (§9),
  so `HealthController` is unreachable and Electron cannot poll it.
- Graph topology enumerates top-level folders only; recursive hierarchy is a follow-up.
- Gmail's bounded-cursor conformance path has no green full run: its last one failed on a
  send rate limit. A flake, not a defect, but not currently backed by evidence.
