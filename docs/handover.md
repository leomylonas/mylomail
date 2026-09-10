# Current handoff

## Completed work

- Reconciled calendar, draft, sync, MIME, and iTIP flows against the frozen architecture. Calendar creates retain durable ambiguity after dispatch, recovery is startup inventory work and executes behind the account guard, and Graph calendar rebases retain canonical local identity, preserve conflicts, and now resume durably after a crash.
- Gmail captures an account history baseline before coverage begins, stages history while coverage is incomplete, and serializes credential-cache writes so a stale token refresh cannot overwrite a successful scope upgrade.
- Mutation execution now cancels a move to a deleted target before a dispatched attempt exists.
- CalDAV parsing rejects bounded-but-oversized observations rather than silently truncating them: XML node/depth, physical/unfolded lines, properties/parameters, text fields, attendees, recurrence sets, date-duration arithmetic, and calendar view inputs are bounded. Sync cursors only advance for complete parsed recurrence sets.
- Full independent security re-audit of the CalDAV parser found no actionable resource/security findings.
## Next task

1. Obtain a final independent invariant review over commits `b90d102`, `6154f8a`, and `51b187b`. The dedicated reviewer was unavailable after its quota error, so this remains an explicit closure requirement.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §§1–3, 6, 8, 9, 13, 15, 16
- `docs/reviews/invariant-review.md`
- `docs/skills/sync-work.md`, `docs/skills/mutation-work.md`, `docs/skills/db-work.md`, `docs/skills/fault-injection.md`

## Verification

- `MYLOMAIL_TEST_PROJECT=server/MyloMail.Api.Tests pnpm check` passed after the final CalDAV, recurrence, and Gmail baseline fixes: format, TypeScript, ESLint, Stylelint, build, 445 .NET tests, and 137 Vitest tests.

## Live risks / decisions

- Automatic iTIP RSVP updates require valid DKIM plus published DMARC with exact From/signing-domain alignment. Failed/unavailable or attendee-mismatched authentication never updates automatically; the UI warns before a user may accept it manually.
- Exact alignment is intentionally stricter than DMARC relaxed alignment because the app does not ship a public-suffix database. Relaxed-only mail remains manually reviewable.
- Missing creation/draft lookup results are never permission to retry a provider call. Keep durable ambiguity/conflict until evidence resolves it.
- The final dedicated invariant re-audit is outstanding only because the configured reviewer failed with a quota-exceeded error after the prior reviewer found and the implementation resolved the Gmail baseline and recurrence-truncation violations. Do not declare closure until an independent reviewer can report on the current tree.
