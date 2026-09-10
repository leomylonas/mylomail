# Current handoff

## Completed work

- Reconciled calendar, draft, sync, MIME, and iTIP flows against the frozen architecture. Calendar creates retain durable ambiguity after dispatch, recovery is startup inventory work and executes behind the account guard, and Graph calendar rebases retain canonical local identity, preserve conflicts, and now resume durably after a crash.
- Gmail rebase now also atomically discards expired-generation staged pages; an account-scoped stream lease serializes baseline observation and staged replay, and replay remains blocked until every mailbox completes coverage. A staged-notification click returns unavailable until coverage has completed, avoiding a stale-backfill resurrection.
- Mutation reconciliation retries or observes only unresolved items in a partial batch, and recovery matches known provider occurrence IDs with their provider mailbox IDs so IMAP UIDs never cross-match folders. Moves to deleted target mailboxes are cancelled before dispatch.
- CalDAV parsing rejects bounded-but-oversized observations rather than silently truncating them: XML node/depth, physical/unfolded lines, properties/parameters, text fields, attendees, recurrence sets, date-duration arithmetic, and calendar view inputs are bounded. Sync cursors only advance for complete parsed recurrence sets.
- Full independent security re-audit of the CalDAV parser found no actionable resource/security findings.
## Next task

- No pending engineering task. Begin from the current handover and architecture review procedure.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §§1–3, 6, 8, 9, 13, 15, 16
- `docs/reviews/invariant-review.md`
- `docs/skills/sync-work.md`, `docs/skills/mutation-work.md`, `docs/skills/db-work.md`, `docs/skills/fault-injection.md`

## Verification

- `MYLOMAIL_TEST_PROJECT=server/MyloMail.Api.Tests pnpm check` passed after the stream lease, staged-replay, and mutation recovery fixes: format, TypeScript, ESLint, Stylelint, build, 446 .NET tests, and 137 Vitest tests.

## Live risks / decisions

- Automatic iTIP RSVP updates require valid DKIM plus published DMARC with exact From/signing-domain alignment. Failed/unavailable or attendee-mismatched authentication never updates automatically; the UI warns before a user may accept it manually.
- Exact alignment is intentionally stricter than DMARC relaxed alignment because the app does not ship a public-suffix database. Relaxed-only mail remains manually reviewable.
- Missing creation/draft lookup results are never permission to retry a provider call. Keep durable ambiguity/conflict until evidence resolves it.
- A final high-reasoning invariant re-audit found no actionable violations. `pnpm check:deep` fault-injection tests passed; its renderer E2E stage requires the external IMAP matrix and failed in this environment, and its Stryker stage terminated internally before reporting mutant results.
