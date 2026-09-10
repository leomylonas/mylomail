# Current handoff

## Completed work

- Reconciled calendar, draft, sync, MIME, and iTIP flows against the frozen architecture. Calendar creates retain durable ambiguity after dispatch, recovery is startup inventory work and executes behind the account guard, and Graph calendar rebases retain canonical local identity, preserve conflicts, and now resume durably after a crash.
- Gmail captures an account history baseline before coverage begins. A history-cursor invalidation now persists a rebase marker, fences queued/in-flight coverage with a durable null cursor and mailbox generations, resets coverage before the replacement baseline commits, and survives a crash at every transition. Account-scoped history remains one stream; topology only captures a baseline when none is durable or a persisted rebase requires recovery. Google credential-cache writes are serialized so a stale refresh cannot overwrite a successful scope upgrade.
- Mutation execution now cancels a move to a deleted target before a dispatched attempt exists.
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

- `MYLOMAIL_TEST_PROJECT=server/MyloMail.Api.Tests pnpm check` passed after the final Gmail rebase and baseline-ordering fixes: format, TypeScript, ESLint, Stylelint, build, 445 .NET tests, and 137 Vitest tests.

## Live risks / decisions

- Automatic iTIP RSVP updates require valid DKIM plus published DMARC with exact From/signing-domain alignment. Failed/unavailable or attendee-mismatched authentication never updates automatically; the UI warns before a user may accept it manually.
- Exact alignment is intentionally stricter than DMARC relaxed alignment because the app does not ship a public-suffix database. Relaxed-only mail remains manually reviewable.
- Missing creation/draft lookup results are never permission to retry a provider call. Keep durable ambiguity/conflict until evidence resolves it.
- Independent invariant review after the Gmail rebase-state and baseline-ordering changes found no invariant violations. The independent CalDAV security re-audit also found no actionable findings.
