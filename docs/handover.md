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

## Next task

1. **Supply real provider registrations externally.** These are deployment configuration,
   never committed: `Providers__Gmail__ClientId`, `Providers__Gmail__ClientSecret`, and
   `Providers__Graph__ClientId` (plus optional `Providers__Graph__Authority`). The application
   correctly returns `501` when absent. Do not invent registrations or check credentials in.
2. **Calendar needs its first complete vertical slice.** The domain model, persistence schema,
   contracts and `ICalendarProvider` exist, but there is no provider factory/implementation,
   sync state machine/job, hub CRUD/read surface, or renderer. Pick one provider path and
   implement a genuine thin slice; do not add a no-op provider or a second source of truth.

   The agreed first path is CalDAV with HTTP Basic username/password. CalDAV and SMTP each
   need an explicit choice between the account's primary IMAP credential and an independently
   stored credential; reuse is a reference, never a copied password.
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
  (131), Vitest (35).
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

## Live risks / decisions

- Gmail's observed `HistoryId` is the revision carried from mail sync. The eventual Gmail
  draft-update provider implementation must re-read/compare it before replacement because
  Gmail's Draft update API has no HTTP ETag precondition. Graph carries `@odata.etag`; IMAP
  carries the Drafts UID.
- Node 22 is required: `source "$HOME/.nvm/nvm.sh" && nvm use` before wrappers.
- Never invoke `dotnet`, `tsc`, `eslint`, or `vitest` directly — use `pnpm status`/`pnpm check`.
