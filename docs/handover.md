# Current handoff

## Completed work

- CalDAV responses stream to a 16 MiB cap; XML depth, parsed-line, VEVENT property, duration, DAV-member, per-resource event, page event, source-row, and calendar-view summary limits prevent resource amplification. Override merging uses a bounded, single-pass VEVENT scanner rather than a regex. Remote draft MIME is capped at 160 MiB, 512 user-visible attachments, 4,096 MIME entities, and depth 32 across fetch, replay, and keep-theirs. Google calendar authorization detects missing calendar scope without discarding the existing mail token until replacement succeeds; refreshed grants are scope-validated and all per-account authorization/cache access is serialized. Graph incremental consent falls through from silent consent failure, while tenant admin denial remains distinct. Native Google/Graph calendar updates persist returned revisions; Graph recurrence intervals use invariant RFC 5545 dates, recurrence masters are hydrated during delta sync, rolling delta windows persist their baseline start and rebaseline daily while retaining local IDs/conflicts and reconciling unseen non-conflict rows transactionally, and idempotent 404 deletes are handled. Legacy Gmail draft identity recovery resolves stable MIME identity through the draft container and stores returned identifiers. Ambiguous calendar creates enqueue polling-independent recovery after provider and post-dispatch failures; unresolved attempts reschedule bounded follow-ups. DKIM/DMARC-gated iTIP replies and explicit warning/acceptance for unauthenticated replies are implemented.
- Calendar sync now normalizes absolute CalDAV resource IDs transactionally, reconciles duplicate aliases while retaining a conflicted local row and retargeting recurrence links, and has regression coverage.
- Calendar-create attempts are now startup inventory work. Recovery runs even if periodic polling is paused, is requeued after reauthentication, and explicitly reschedules after throttling, network, or credential-store interruptions.
- Legacy draft conflict handling refreshes safely observed provider addressing, resolves a known message ID to a container before a user chooses a side, retains Gmail history revision on resolution, clears stale message identity after replacement, fences IMAP remote cleanup with its observed UIDVALIDITY, refuses unsafe cleanup for legacy bare-UID revisions, and now actually invokes provider deletion. Definitively unsupported or credential-store-failed initial creates clear their in-process dispatch marker. Dirty drafts are explicitly rescheduled after startup/reauthentication push interruptions.
- Calendar MIME parts are capped at 1 MiB decoded data and 64 VEVENTs; oversized parts are ignored. Automatic RSVP application requires authenticated sender/attendee identity, a reply sequence no older than the event, and strictly newer iTIP `DTSTAMP`; regressions prove older timestamps and sequences cannot overwrite a response. Unverified acceptance applies only the same first VEVENT presented to the user, including an instance reply. A DKIM-authenticated sender whose claimed attendee differs still receives manual-review treatment.

## Next task

1. Perform the final independent invariant/security review over the latest rebase and resource fixes; remediate any finding, then run the full check.
2. Keep `docs/handover.md` current with verification and live decisions.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §§1–3, 6, 8, 9, 13, 15, 16
- `docs/reviews/invariant-review.md`
- `docs/skills/sync-work.md`, `docs/skills/mutation-work.md`, `docs/skills/db-work.md`, `docs/skills/fault-injection.md`

## Verification

- `MYLOMAIL_TEST_PROJECT=server/MyloMail.Api.Tests pnpm check` passed after CalDAV XML-depth regression coverage: format, TypeScript, ESLint, Stylelint, build, 445 .NET tests, and 137 Vitest tests.

## Live risks / decisions

- Automatic iTIP RSVP updates require valid DKIM plus published DMARC with exact From/signing-domain alignment. Failed/unavailable or attendee-mismatched authentication never updates automatically; the UI warns before a user may accept it manually.
- Exact alignment is intentionally stricter than DMARC relaxed alignment because the app does not ship a public-suffix database. Relaxed-only mail remains manually reviewable.
- Missing creation/draft lookup results are never permission to retry a provider call. Keep durable ambiguity/conflict until evidence resolves it.
- Latest Codex review also identified remaining Graph/Gmail OAuth scope migration, native calendar revision persistence, and legacy IMAP draft revision compatibility work; these are not yet declared resolved.
