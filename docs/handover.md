# Current handoff

This is a snapshot of what is true now, not a log — see Git history for the
individual changes.

## Completed work

- Electron's real entry point starts the backend through `startBackend`, including the
  secure setup/unlock dialog and exit-code-78 restart protocol.
- Received and compose attachments are implemented. The most recent attachment slice is
  `c4411ca`.
- `cid:` images now resolve before the isolated iframe is remounted; the focused Electron
  e2e proves the part becomes a `blob:` URL (`7d97c62`). The DOM status used by that test is
  intentionally retained as observable diagnostic state rather than a renderer-console
  probe that Playwright cannot reliably surface.
- Remote Drafts-mailbox observations now materialise as structured local `Draft`s, never as
  `Message`s (`2cc273d`). `RemoteDraftMaterializer` fetches and parses the raw MIME before
  the page commits, carries raw draft data in a staged Gmail page, and replays it transactionally.
  It preserves provider revisions, refuses a draft observation without one before a cursor
  can advance, and applies the same topology-generation guards as message upserts/removals.
  `GetDrafts` is exposed on the hub; `DraftUpdated` fires after remote changes commit.
- IMAP account configuration now selects credential reuse or an independent secure-store
  credential for both SMTP and the pending CalDAV path (`db5f723`, `104e3ac`). Independent
  SMTP includes its own user name and is resolved only at send time; reuse references the
  primary IMAP credential without copying it. Provisioning cleans all slots before an attempted
  database commit, while retaining them after an ambiguous commit failure so it never creates a
  committed account with no credential. Account removal deletes every slot after durable removal.
- Calendar provider pages now materialise through one transactional `CalendarSyncService`
  (`11577a6`). A collection token advances only with the final page it covers; invalid tokens
  and baseline continuations reset the local baseline before one fresh retry. Recurrence master
  links are repaired across pages and all affected events are announced only after commit.

## Next task

1. **Supply real provider registrations externally.** These are deployment configuration,
   never committed: `Providers__Gmail__ClientId`, `Providers__Gmail__ClientSecret`, and
   `Providers__Graph__ClientId` (plus optional `Providers__Graph__Authority`). The application
   correctly returns `501` when absent. Do not invent registrations or check credentials in.
2. **Calendar needs its first complete vertical slice.** The domain model, persistence schema,
   contracts and `ICalendarProvider` exist, but there is no provider factory/implementation,
   sync state machine/job, hub CRUD/read surface, or renderer. Pick one provider path and
   implement a genuine thin slice; do not add a no-op provider or a second source of truth.

   The agreed first path is CalDAV with HTTP Basic username/password. Its account configuration
   and credential storage plus crash-safe local materialisation are ready; implement the actual
   `CalDavCalendarProvider`, factory/registration, job wiring and hub read surface next. CalDAV
   and SMTP reuse references the primary IMAP credential rather than copying it.
3. **Finish event producers with their features.** Current producers exist for account status,
   mailbox tree/update, message received/updated, sync progress, outbox status and drafts.
   `ExportProgress`, `CalendarEventUpdated`, `CalendarConflictDetected`, and
   `ConnectivityChanged` remain correctly unproduced because those features do not yet exist.
   `MessageDeleted`/`MessageSyncFailed` should be audited against their physical-deletion and
   terminal-mutation transitions before adding events: a removal of one occurrence is not a
   deleted canonical message.
4. **Expose remote drafts in the renderer.** `GetDrafts` and `DraftUpdated` now make them
   queryable, but the compose UI has no draft list or reopen flow yet.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §§1, 2, 3, 6, 7, 9, 16
- `docs/skills/fault-injection.md`
- `docs/reviews/invariant-review.md`

## Verification

- `pnpm check` under Node 22: green — format, tsc, eslint, stylelint, build, backend tests
  (132), Vitest (35), before the final SMTP test addition; `pnpm status` subsequently reported
  tsc, eslint and backend checks clean. The full check is normally complete in about 30 seconds
  but this session's command runner returned at its 30-second ceiling before emitting its final
  summary after that additional test.
- Remote-draft coverage test proves a Drafts MIME observation yields a `Draft`, emits
  `DraftUpdated`, and does not create a `Message`.
- `SyncCrashWindowTests.A_staged_remote_draft_survives_server_deletion_and_crash_before_replay`
  is tagged `FaultInjection,Deep`: it stages a Gmail draft, deletes it on the fake provider,
  crashes before replay, restarts, and asserts the captured raw MIME still materialises. It
  has not been run in this session because `pnpm check:deep` is explicit-only; its required
  discrimination run (temporarily omitting `RemoteDrafts` from `SyncPagePayload`) also remains
  outstanding.
- An independent invariant review of `2cc273d` found and drove fixes for missing
  generation guards, missing provider revisions, and a potential cursor skip. The final review
  found no architectural invariant violation.
- An independent invariant review of the credential-slot slice found and fixed the pre-commit
  cleanup and ambiguous-commit handling; its final review found no architectural invariant
  violations.
- Calendar materialisation has deep-tagged crash tests for before-commit, after-commit and an
  invalidated baseline continuation. They have not been run because `pnpm check:deep` remains
  explicit-only. Two independent invariant reviews found and corrected cursor invalidation,
  recurrence ordering/deletion, stale-calendar topology and post-commit notification gaps.

## Live risks / decisions

- Gmail's observed `HistoryId` is the revision carried from mail sync. The eventual Gmail
  draft-update provider implementation must re-read/compare it before replacement because
  Gmail's Draft update API has no HTTP ETag precondition. Graph carries `@odata.etag`; IMAP
  carries the Drafts UID.
- Node 22 is required: `source "$HOME/.nvm/nvm.sh" && nvm use` before wrappers.
- Never invoke `dotnet`, `tsc`, `eslint`, or `vitest` directly — use `pnpm status`/`pnpm check`.
