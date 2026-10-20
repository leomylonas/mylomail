# Current handoff

## Current state

Everything below is implemented, tested and described in `docs/architecture.md` (§13 epics and the sections named). This file lists only what is live: results of the last verification pass, open decisions, and risks.

- Outlook-style renderer and compose (command bar, ruled From/To/Cc/Bcc/Subject rows, cross-account From menu, Outlook quote header with the signature above it), message window with the full action set, background-activity dialog, 12/24-hour setting, folder pinning/hiding/multi-select and server-projected unread counts, Gmail account-wide backfill with an adaptive request budget.
- Message HTML is prepared and served by the backend (`MessageHtmlPreparer`, `/messages/{id}/frame` under its own CSP); the renderer holds no sanitiser.
- Mutation recovery asks the provider about one message (`IMailProvider.FindMessageAsync`); a failed settle never stalls unrelated changes; failing batches back off.

## Verification (pass of 2026-10-20)

- **Unit and integration:** `pnpm check` green (format, tsc, eslint, stylelint, website, build, 1026 .NET tests, 430 Vitest). CI on `5c94ba5` green.
- **E2E:** the whole Playwright suite ran: 30 of 32 passed, 1 skipped (`NativeAttachmentOpen`, packaged build only) and 1 failed — the compose-switch spec, on leftover server-side drafts (the matrix IMAP server keeps its Drafts across runs). That spec now uses a unique draft subject and passes on repeated runs.
- **Fault injection:** `Category=FaultInjection` — 73 scenarios pass. Scenarios added this period were each shown to fail with the behaviour removed (reconciliation without a folder walk; an occurrence-free delete not settled as done; a failed settle not blocking other changes; draft pushes serialised; drain backoff).
- **Conformance:** IMAP on QRESYNC, CONDSTORE and BASIC — pass, including `FindMessage_follows_a_message_through_a_move_and_reports_it_gone_after_deletion`. The live IMAP sync tests (`ImapLiveSyncTests`) are flaky when the whole conformance category runs in parallel (a different one fails on each run, all pass alone): shared QRESYNC INBOX state, not investigated. **Gmail and Graph live conformance have not been run:** `pnpm check:live-providers gmail mail` hung for 15 minutes with no output, most likely waiting on the interactive consent screen the Gmail test account now needs for the full-access scope; the Gmail and Graph `FindMessageAsync` are covered by request-shape tests (`MessageLookupTests`) only.
- **Independent invariant reviews (reviewer role, two slices) and a parity review** found: (1) the online-backup connections bypassed the SQLite pragma interceptor — fixed (`SqlitePragmaInterceptor.ApplyTo`); (2) a permanent delete with no local occurrences was completed without asking the provider, which wrongly completes for a Gmail message that has no labels — fixed (`MutationReconciler.ConfirmGoneAsync`); (3) the unread-snapshot decision below; (4) documentation drift, now corrected.
- Not run: `pnpm check:deep` as one command (it produced no output for 29 minutes; its steps were run individually as above); the Windows/macOS matrix (`cross-platform-check` runs only on tags or manual dispatch); the pre-migration backup against a copy of the real database.

## Open decision

- **`UnreadSnapshot.AdjustAsync` rewrites provider-owned counts.** Both independent reviewers flagged that settling a confirmed mutation adds the local delta to `Mailbox.ProviderUnreadCount` and `UnreadAtProviderCount`, which AGENTS.md says are provider/topology snapshots. The projection (`MailboxUnreadProjection`) was designed around it (the display uses their difference, so a confirmed change does not make the figure jump back), and the snapshot can briefly read below the truth if topology sync reads the post-mutation count before the settlement adds its delta again (self-heals at the next topology pass; a stored value can go negative, display clamps at zero). Alternatives: keep confirmed deltas in a separate durable field that topology sync zeroes with each snapshot (same race, opposite sign), or accept the current behaviour and amend the AGENTS.md wording. Not changed: it is a frozen-architecture call for the owner.

## External verification still required

- **Gmail expired history:** provide `GMAIL_EXPIRED_HISTORY_ID` from an account with a genuinely expired history cursor. Gmail exposes no API to create one; cursor `1` is not a deterministic substitute.
- **Native Gmail and Graph RSVP:** configure attendee-owned invitations from second consenting Gmail and Microsoft 365 calendar accounts.

## Next task

- Watch `coredumpctl list gnome-keyring-d` after the credential cache and serialisation; if crashes continue, move to one long-lived D-Bus connection closed cleanly on shutdown.
- Possible notification flood after switching an account to full history: the baseline and epoch rules should prevent it; confirm on a real account.
- Date-group headers ("Last week", "This month") in the message list are not done.
- Do not restructure the frozen per-window shell/store/query boundaries.

## Required reading

- `AGENTS.md`
- `docs/architecture.md` §6, §12 and §13
- `docs/skills/frontend-shell.md`, `docs/skills/db-work.md`
- The relevant guide under `docs/skills/` before changing persistence, providers, sync, mutations, fault injection, or the frontend shell.

## Live risks / decisions

- Hub additions (`GetProblems`, `GetContentQueue`, `PrioritiseContent`, `RetryFailedDownloads`, `EmptyMailbox`) enqueue or read only; `EmptyMailbox` goes through the existing `DeletePermanently` mutation per message and refuses any folder whose effective role is not Trash or Junk.
- `ContentPriority` and the credential cache are in-memory by design (hints, not recovery state); the durable queue is `MessageContentState`.
- The legacy migration tests seed through the current model; they add and drop newer `Accounts` columns around the seed (`AddNewerAccountColumnsAsync`) and must be extended when another Account column is added.
- Gmail expired-history and native RSVP coverage remain conditional on external fixtures.
- **Gmail account-wide backfill (written without running anything; unverified).** Gmail coverage is one walk per account (`messages.list` with no label filter, `includeSpamTrash`, one `format=full` get per message). Cursor on the new `AccountCoverageState` (migration `AddAccountCoverageState`, hand-written snapshot entry), mailbox rows mirror it, progress reported once on the Inbox row, one owner per account (`CoverageRegistry.AccountWalkScope`, `SyncJobs.StartCoverage`/`AccountCoveragePageAsync`). Late labels are caught up through the per-mailbox path. Per-mailbox ranges are refused for Gmail. Every Gmail request goes through `GmailRequestBudget` (10,000 units/min, burst 2,500). Messages mapping to no local mailbox are fetched, counted and **not stored** (tombstone GC would collect them): confirm this against "do not drop them". Review: `docs/reviews/gmail-account-backfill.md` (self-review only; an independent `reviewer` pass is still owed). `MutationReconciler.ObserveAsync` still walks every label in full on `ReconcileThenRetry` recovery.
