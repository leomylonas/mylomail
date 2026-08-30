# Current handoff

## Completed

**Stage C, fourth slice — closing test gaps, and the job layer.** Persistence `e085284`,
mutations `23dde6a`, sync `9b455bd`, testing discipline `9508f1e`/`514a7af`.

**Test gaps found by mutation testing.** Twelve of thirteen targeted survivors killed, and
one of them was a real defect rather than a missing assertion:

- `MutationExecutor` marked an attempt `Completed` even when the provider's batch reported
  on fewer items than were submitted. The recovery sweep queries *from attempts*, so a
  closed attempt took its unreported members with it and nothing ever reconciled them. The
  attempt now goes `Ambiguous` with `ResultPersistedAt` left null. Partial batch results are
  the normal case for a fifty-item Graph `$batch`, not an edge case.
- New coverage for: desired-state revert on failure, on dependent cancellation, and on
  cancellation at execution (three distinct paths); the unreported-batch-item case; the
  lease-expiry boundary; per-message sequence scoping and pending-change independence;
  change-stream flag application and membership removal — nothing had tested the incremental
  apply path at all, because the tests reached those rows through coverage; and the cursor
  advance on Gmail's staging path.

**The job layer (§3, §6).** Nothing drove any of the sync or mutation code before this.

- `SyncJobs` and `MutationJobs`, both `[AutomaticRetry(Attempts = 0)]` with a test asserting
  it per job type. Retry is decided in our code: auth must not retry at all and throttling
  must wait exactly `Retry-After`, and Hangfire's curve would fire underneath both.
- Successors are self-scheduled rather than registered with Hangfire's recurring scheduler,
  which is minute-granular and cannot express sub-minute polling. One coverage page per job,
  so a long backfill resumes at page granularity.
- `AccountGate` holds the whole account on one throttling response, so thirty mailbox jobs
  do not wake together and get throttled again. In-memory by design: a throttle window is
  short-lived advice, not a fact about the account. A shorter signal never shortens an
  existing pause.
- `StartupScheduler` releases leases orphaned by a dead process and rebuilds outstanding
  work from the app tables — the sole recovery mechanism, since job storage is in-memory.
  `ResumeAccountAsync` requeues blocked work immediately on reauthentication.
- `ProviderThrottledException` and `ProviderAuthenticationException` added, so the two
  failures that must not be retried blindly are distinguishable from every other failure.

## Next task

- **Send and the outbox** — see the risks below. The largest remaining hole in §6.
- **Per-operation reconciliation of ambiguous mutations.** The sweep identifies them;
  re-establishing location for a move or existence for a deletion is still to write.
- **Periodic integrity reconciliation** for the weakest IMAP tier, now that a scheduler
  exists to hang a cadence on. Keep it distinct from triggered resynchronisation.
- **Nothing runs end to end yet**: no production `ICredentialStore`, so no provider is
  registered; `Program.cs` still does not map controllers or apply the per-launch token
  middleware (§9), so Electron cannot poll `/health`. Wiring that is a short, separate piece
  of work and would surface problems no unit test will.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §3 and §6, §15 for send, §16 for the scenario table
- `docs/skills/fault-injection.md` — read this before writing any crash test
- `docs/reviews/invariant-review.md` before mutation, sync, reconciliation or persistence work

## Verification

- `pnpm check` green: `format · tsc · eslint · stylelint · build · tests(60) · vitest`.
- Thirteen `Category=FaultInjection` scenarios pass, each checked to fail against the
  invariant it protects being removed.
- Mutation baseline: **241 killed, 107 survived, 50.40%**, with `Mutations/`, `Sync/` and
  `Scheduling/` in scope. The score fell from 55.91% while the killed count rose from 205,
  because adding `Scheduling/` brought new unkilled mutants with it — compare killed counts,
  not scores, across a scope change. `break` is reset to 50 as the new ratchet.
- Remaining survivors are concentrated in `ChangeStreamService` (24), `MessageIngestor` (17),
  `TopologySyncService` (13) and `MutationExecutor` (12). Many are noise — log strings,
  counters, orderings — and a few in `AccountGate` are equivalent mutants that cannot change
  behaviour. They have not been triaged individually.

## Risks / decisions

- **Send and the outbox are still not built.** §6 gives send its own attempt with exactly one
  item and an `AmbiguousOutcome` policy, reconciled against Sent by a pre-generated
  `Message-ID`. `OutboxItem` has no table, so `StartupReconciliation` cannot enumerate
  `Scheduled`/`Sending` items, in-progress exports or undelivered notifications. Whoever adds
  those tables must extend that sweep in the same change.
- **Notification records are not derived from staged events yet.** §3 requires notifications
  come from the staging queue *before* canonical replay, or live mail goes unnotified for the
  hours a large backfill takes. `StagedChangeEvent.ScannedForNotifications` exists for it.
- The Hangfire dashboard is not mounted and the server is started with defaults; worker
  count, queues and shutdown timeout have not been considered.
- No production `ICredentialStore`, so no provider is registered and none of this runs
  against a real account. Deliberate; do not register the in-memory store.
- Graph topology enumerates top-level folders only; recursive hierarchy is a follow-up.
- Gmail's bounded-cursor conformance path has no green full run: its last one failed on a
  send rate limit. A flake, not a defect, but not currently backed by evidence.
