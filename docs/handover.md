# Current handoff

## Completed

**Stage C is built except for two items** (below). Commits: persistence `e085284`,
mutations `23dde6a`, sync `9b455bd`, testing discipline `9508f1e`/`514a7af`, jobs `0a5b6d6`,
send/outbox `7df5fb1`.

- **Persistence** — `MyloMailDbContext` over one `app.db`. Pragmas applied per connection,
  not at startup, because three of the four are per-connection. Migration runs behind a
  `VACUUM INTO` backup that is deleted only on success.
- **Mutations (§6)** — the five concerns kept separate: per-`(AccountId, MessageId)`
  ordering, atomic leased ownership, stable local intent, execution-time identity
  resolution, and `MutationExecutionAttempt` for remote uncertainty with the synchronous
  `Dispatched` write. Terminal failure re-evaluates the chain rather than cancelling it.
- **Sync (§3)** — topology, coverage and change streams. Cursor and the data it covers
  always commit together. Gmail's history is drained durably but unapplied during backfill,
  then replayed in observation order. Every provider's invalid cursor takes one triggered-
  resynchronisation path. `GenerationSnapshot` guards upserts, removals, flag changes and
  staged replay alike.
- **Jobs** — `SyncJobs`, `MutationJobs`, `OutboxJobs`, all `[AutomaticRetry(Attempts = 0)]`
  because retry is decided in our code. Self-scheduling successors; `PollRegistry` stops a
  second loop doubling the poll rate; `AccountGate` holds a whole account on one
  `Retry-After`. `StartupScheduler` rebuilds outstanding work from the app tables, which is
  the only recovery mechanism with in-memory job storage.
- **Send (§15)** — compare-and-swap statuses, a stable `Message-ID` generated before the
  first attempt, and reconciliation against Sent within a bounded window that never resends.

## Next task

1. **Per-operation reconciliation of ambiguous mutations.** `StartupReconciliation`
   identifies them; re-establishing location for a move or existence for a deletion is
   unwritten. `SendReconciler` is the worked example of the shape.
2. **Periodic integrity reconciliation** for the weakest IMAP tier — UID-set reconciliation
   and the flag scan a valid cursor cannot express. There is now a scheduler to hang the
   cadence on. Keep it distinct from triggered resynchronisation: same machinery, different
   trigger and meaning.

Then, before Stage D: **the app still does not run as an app.** No production
`ICredentialStore`, so `MailProviderFactory` refuses every provider; `Program.cs` does not map
controllers or apply the per-launch token middleware (§9), so Electron cannot poll `/health`.

The sync engine itself is no longer fake-only: `ImapLiveSyncTests` drives topology → coverage
→ change stream against the Dovecot matrix (`pnpm imap:up`) and passes, including a real
`UIDVALIDITY` cursor. Mutation and send have no equivalent yet, and neither does Gmail or
Graph.

## Read first

- `AGENTS.md`
- `docs/skills/fault-injection.md` — **before writing any crash test**; it carries three real
  false passes from this repository and the rule that catches them
- `docs/skills/db-work.md` — the SQLite traps section; two of them have cost time twice
- `docs/architecture.md` §3 and §6 for the next task, §16 for the scenario table
- `docs/reviews/invariant-review.md` before mutation, sync, reconciliation or persistence work

## Verification

- `pnpm check` green: `format · tsc · eslint · stylelint · build · tests(71) · vitest`.
- The IMAP matrix (`pnpm imap:up`) passes 36 conformance cases across all three tiers, plus
  `ImapLiveSyncTests` — the sync engine end to end against real Dovecot.
- Eighteen `Category=FaultInjection` scenarios, **each checked to fail with the invariant it
  protects removed**. This is not optional here: three tests in this repository have passed
  against the exact bug they existed to catch, and two were found by an independent reviewer
  rather than by running the suite.
- Mutation testing runs in `pnpm check:deep` over `Mutations/`, `Sync/` and `Scheduling/`.
  Last full baseline 241 killed / 107 survived; `break` is a ratchet at 50. Read the mutant
  statuses, not the score — a run that times out reports an excellent score having measured
  nothing. Do not run it alongside a build.
- Every slice has had an independent invariant review. Three found real violations: the
  topology-generation guard missing on removals and replay; a decorative account-removal exit
  check plus `ChangeStreamAsync` being dead code so live sync never ran; and send treating an
  explicit provider rejection as an unobserved outcome.

## Risks / decisions

- **No production `ICredentialStore` registration.** Deliberate; do not register the
  in-memory store. Nothing runs against a real account until this exists.
- **`StartupReconciliation` omits export jobs and notification records** — those tables do
  not exist. Whoever adds them must extend the sweep in the same change, or it silently
  under-enumerates.
- **Notifications are not derived from staged events.** §3 requires they come from the
  staging queue *before* canonical replay, or live mail goes unnotified for the hours a
  backfill takes. `StagedChangeEvent.ScannedForNotifications` exists for it; there is no
  `NotificationRecord` table.
- **Nothing drives content acquisition.** `MessageContentState` is enumerated by the startup
  sweep as `Queued`/`Fetching`, but no job fetches raw messages, so those states are
  unreachable and §8's search has nothing to index.
- **Send reconciliation reads the local Sent mailbox**, so a lagging sync can report a
  successful send as unconfirmed. A false negative in status only — never a duplicate or a
  silent loss.
- **`OutboxStatus.Pending` is never set.** Part of §15's status set; cancel and claim already
  treat it as takeable, so introducing it later does not reopen the race.
- **Hangfire runs with default worker count, queues and shutdown timeout.** Unconsidered, and
  the shutdown timeout interacts with §9's graceful-shutdown requirement.
- Graph topology enumerates top-level folders only; recursive hierarchy is a follow-up.
- Gmail's bounded-cursor conformance path has no green full run — its last failed on a send
  rate limit. A flake, not a defect, but not currently backed by evidence.
