# Invariant review — Gmail account-wide backfill

Follows `docs/reviews/invariant-review.md`. **Status: implementer's self-review, not independent.**
That document requires a fresh `reviewer` pass over the changed files; this note is the brief for
it and the implementer's own answers, which must not stand in for it.

Changed invariant-bearing paths: `Sync/CoverageService.cs` (account walk, catch-up, failure
record), `Sync/ChangeStreamService.cs` (`TriggerResynchronisationAsync` resets the walk),
`Scheduling/SyncJobs.cs` (`StartCoverage`, `AccountCoveragePageAsync`), `Scheduling/CoverageRegistry.cs`,
`Hubs/MailHub.cs` (`UpdateAccount`, `SetMailboxInitialSyncOverride`), migration
`20261003120000_AddAccountCoverageState`, `Providers/Gmail/*` (walk, request budget).

## The question

Does the diff violate an architectural invariant? Answers per item in the checklist's
"be most suspicious of" list, and per principle.

### Principle 3 — never persist a cursor past changes that have not been durably persisted

- The walk's `AccountCoverageState.ResumeToken` is written in `RunWalkPageAsync`'s single
  transaction with `IngestAsync`, `drafts.ApplyAsync`, `BackfillMessageIdsAsync` and the
  `MirrorWalkAsync` rows. There is no separate cursor write. A token committed earlier would be
  `"2"` over an empty store after a crash at `SyncPageBeforeCommit`.
- `BeginWalkAsync` commits the transition to Backfilling (walk row + every participant row) in
  one transaction **before** the provider call. It writes no token and no data, so a crash in
  between loses nothing and skips nothing.
- Resume reads the token **only** from the walk row, and the commit compares it with the token the
  page was fetched from (`walk.ResumeToken != resumeToken` → discard). A second walker cannot move
  the cursor from a position it no longer holds.
- Replay is safe: pages are upserts (`MessageIngestor`), and the walk lists newest-first from the
  committed token. A skipped page is the only silent failure; none of the guarded paths skip.
- A message with no mapped mailbox is **not stored** but **is counted** and the token advances
  past it. This is deliberate and is not a skip: it has no membership to lose, and history
  re-introduces it if it is ever labelled. See "Decision to confirm" below.

### Mailbox.TopologyGeneration / policy fencing

- `GenerationSnapshot` is captured for every mailbox before the provider call. At commit, inside
  the transaction, the mailbox set is re-read and **any** mailbox whose generation changed
  discards the whole page (`CoverageBaselinePendingException`). Committing the token anyway
  would skip the dropped occurrences of the replaced mailbox. The per-mailbox path
  (`RunPageAsync`) is unchanged for IMAP and Graph.
- `AccountCoverageState.PolicyGeneration` is the account-level analogue of
  `Mailbox.CoveragePolicyGeneration`. It is bumped by `UpdateAccount` (same transaction as the
  mailbox resets and the account bound) and by `TriggerResynchronisationAsync` (same
  `SaveChangesAsync` as the cursor invalidation). The bound is read **after** the walk state,
  so a stale generation can pair with a newer bound (discarded) but not the reverse.
- `RecordAccountFailureAsync` is fenced by the generation captured at job start, so a failure
  from a restarted walk is not recorded against its replacement.

### Notification eligibility

Unchanged: backfill ingestion never records a notification. `BackfillMessageIdsAsync` links a
staged-path record to the message the walk created, as the per-mailbox page already did.

### Gmail ordering (baseline, staged history, replay)

Unchanged and re-asserted: baseline `H0` must exist (`CoverageBaselinePendingException`), history
stays staged while any mailbox is not `Covered` (`CoverageCompleteAsync`), replay waits for all
rows. The walk marks participants `Covered` in the transaction of its final page, so replay cannot
observe `Covered` ahead of the data. Mailboxes that were not part of the walk stay uncovered and
therefore keep history staged until their catch-up page completes.

### Checklist items

- `Dispatched` write, `Message-ID`, `[AutomaticRetry]`, Graph client: not touched.
- Provider ids stay on `MessageMailbox`; `Message` gains none. `MutationItem` unchanged.
- **Counts from local rows**: not introduced. `MessagesFetched`/`EstimatedTotal` are the walk's
  consumed count and the provider's `resultSizeEstimate`, as before; `ProviderTotalCount` untouched.
- **`Availability` and `Coverage` collapsed**: no. Each mailbox still reports both; the walk only
  decides the per-mailbox `Coverage` value.
- **A cursor advanced separately from its data**: the one place to look is `BeginWalkAsync`
  (state transition without data). It writes no cursor.

## Concurrency / ownership (item 7)

Exactly one coverage owner per Gmail account (`CoverageRegistry.AccountWalkScope`), started only
through `SyncJobs.StartCoverage` from topology and settings; startup reaches it through
`TopologyAsync`. The catch-up of a late label is a page of the same job, so no two Gmail coverage
pages run at once. IMAP/Graph owners are per mailbox as before.

## Decision to confirm (brief said "do not drop them")

Messages that map to no local mailbox are fetched and counted but not materialised. Persisting a
membership-less row would create a `Message` that `TombstoneGcJobs` marks orphaned and collects
after 30 minutes, after `MessageContentState` has queued a raw download for it. Keeping them
needs a home (an All Mail pseudo-mailbox) or a GC exemption marker on `Message` — a structural
change this review did not make.

## Residual risks for the independent reviewer

1. `MutationReconciler.ObserveAsync` still calls the per-label `InitialSyncMailboxAsync` for every
   mailbox of a Gmail account on `ReconcileThenRetry` recovery (move/trash/delete after a crash).
   It now goes through the request budget but still fetches each label in full.
2. `ServiceAsync` → `BuildService` is the only `GmailService` construction site; the budget wiring
   is covered by `Every_service_the_provider_builds_for_an_account_shares_that_accounts_bucket`.
3. The migration seeds `Covered` only when every provider-backed mailbox row is `Covered`.
   An account with a mailbox that has no coverage row is treated as mid-backfill and restarts.
4. The hand-written model snapshot entry for `AccountCoverageState` was not generated by
   `dotnet ef`; a pending-model-changes failure on startup would mean it differs from
   `MyloMailDbContext`.
