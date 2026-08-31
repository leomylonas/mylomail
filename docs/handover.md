# Current handoff

This is a snapshot of the current state, not a history; use Git for history.

## Completed work

- Electron starts the backend through `startBackend`, including setup/unlock and the exit-78
  restart protocol.
- Received/compose attachments work (`c4411ca`). `cid:` HTML images are rewritten to `blob:`
  URLs and the isolated iframe remounts on its resolved document (`7d97c62`).
- Remote Drafts-mailbox observations materialise as local `Draft`s, never `Message`s
  (`2cc273d`): raw MIME is captured before cursor commit, revisions/generation guards are
  enforced, and `DraftUpdated` is emitted after commit. The renderer now has an account-scoped
  Drafts panel that refetches on `DraftUpdated` and reopens a draft in compose with To/Cc/Bcc,
  subject, HTML and attachments (`b1314be`).
- SMTP and CalDAV configuration may reuse the primary IMAP credential or use an independent
  secure-store slot. Reuse is a reference, never a copied password. SMTP resolves its selected
  credential at send time (`db5f723`, `104e3ac`). CalDAV Basic configuration requires HTTPS
  (`904908f`).
- Calendar foundation is crash-safe: `CalendarSyncService` transactionally materialises provider
  pages, advances a collection token only with its final page, resets invalid cursors/baselines,
  repairs recurrence links, and emits `CalendarEventUpdated` after commit (`11577a6`). CalDAV
  has per-request credential resolution, WebDAV XML/Depth/If-Match primitives, and `207`
  multi-status parsing (`2e629aa`, `8a0fbe3`, `6d12d05`).
- Hub producer audit: `MessageSyncFailed` already emits after durable terminal failures;
  `CalendarEventUpdated` now emits from calendar sync. Do not emit `MessageDeleted` for a
  mailbox-occurrence removal: the canonical message still exists.

## Next task

1. **Complete the CalDAV vertical slice.** Implement a genuine `CalDavCalendarProvider` and
   `ICalendarProviderFactory` registration using the existing request/parser helpers; then wire
   account calendar scheduling, hub reads/CRUD and renderer calendar UI. Do not add a no-op
   provider or a second calendar store. Preserve the token/page transaction invariant and use
   `If-Match` conflicts to drive `CalendarConflictDetected` only after calendar mutations exist.
2. **Deferred external configuration:** Gmail/Graph client registrations remain intentionally
   unsupplied per user request. They are deployment environment values and must never be
   committed: `Providers__Gmail__ClientId`, `Providers__Gmail__ClientSecret`,
   `Providers__Graph__ClientId`, optional `Providers__Graph__Authority`.
3. `ExportProgress` and `ConnectivityChanged` remain feature-blocked; implement their underlying
   export/connectivity state before adding broadcasts.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §§1, 2, 3, 6, 7, 9, 16
- `docs/skills/fault-injection.md` and `docs/reviews/invariant-review.md` for calendar sync,
  persistence or mutation changes

## Verification

- `pnpm status` under Node 22 was clean for tsc, ESLint and backend checks after the renderer
  draft work. The command runner's 30-second ceiling prevented capturing the final summary from
  later full `pnpm check` invocations; rerun `pnpm check` before further handoff/completion.
- Remote-draft and calendar crash tests are tagged `FaultInjection,Deep` and have not been run:
  `pnpm check:deep` is explicit-only. The remote-draft discrimination run (temporarily omitting
  `RemoteDrafts` from `SyncPagePayload`) is also outstanding.
- Independent invariant reviews were completed for remote drafts, credential slots and calendar
  materialisation; calendar review drove cursor invalidation, recurrence ordering/deletion,
  topology deletion and post-commit notification fixes.

## Live risks / decisions

- Gmail draft update must compare the stored `HistoryId` before replacement; Graph carries an
  ETag and IMAP carries the Drafts UID.
- Use Node 22 (`source "$HOME/.nvm/nvm.sh" && nvm use`) and wrappers only: `pnpm status`,
  `pnpm check`; do not invoke raw `dotnet`, `tsc`, ESLint or Vitest.
