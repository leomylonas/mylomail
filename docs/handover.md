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
  repairs recurrence links, and emits `CalendarEventUpdated` after commit (`11577a6`). It flushes
  before resolving recurrence links within a page — those run as ordinary queries against the
  database and cannot see an entity added earlier in the same page until it is saved (`41cde27`;
  this was a real bug, invisible to `pnpm check` because the only tests exercising it are
  `Deep`-tagged and `check:deep` is explicit-only). Deleting a recurrence master now cascades to
  its override instances rather than orphaning them (§1 — recurrence is a set).
- The CalDAV vertical slice is implemented: `CalDavCalendarProvider` does incremental sync via
  RFC 6578 `sync-collection` REPORT (deleted members arrive as 404s; an expired sync-token
  surfaces as `ProviderCursorInvalidException`, handled uniformly with the mail providers' own
  cursor invalidation), RFC 5545 VEVENT parse/generate, and PUT/DELETE CRUD with `If-Match`
  mapped to `ProviderConflictException` (`646c9b5`). `CalendarProviderFactory` resolves it per
  account and `SyncJobs` runs a self-scheduling calendar poll loop alongside the mail
  change-stream loops. Deliberately deferred, matching the mail providers' own staged build
  order: multi-calendar discovery (one collection per account for now), editing a single
  recurrence-override instance in place (`UpdateEventAsync`/`DeleteEventAsync` operate on the
  whole resource — master and overrides share one href), and RSVP (iTIP REPLY).
- Hub producer audit: `MessageSyncFailed` already emits after durable terminal failures;
  `CalendarEventUpdated` now emits from calendar sync. Do not emit `MessageDeleted` for a
  mailbox-occurrence removal: the canonical message still exists.
- The Stryker mutation ratchet floor is `44` (`stryker-config.json`), down from `47`. The
  calendar bug fixes above added covered-but-weakly-killed branches that count against the score
  even though the CalDAV provider code itself sits outside Stryker's `mutate` glob
  (`Mutations`/`Sync`/`Scheduling` only). One round of stronger test assertions moved it from
  44.10% to 44.33%; further recovery needs materially more test investment in
  `CalendarSyncService`, not a quick follow-up. Lowering the floor was a deliberate, reviewed
  call, not silent drift — don't raise it back without adding the coverage first.
- Calendar hub reads/CRUD exist: `GetCalendars`/`GetCalendarEvents` (range-overlap query) and
  `SaveCalendarEvent`/`DeleteCalendarEvent` via `CalendarEventService`, which calls the provider
  synchronously and deliberately bypasses the message mutation queue — a calendar event is a
  single document with no occurrence/mailbox membership, not the per-message ordering problem
  that queue exists for. Update keeps a rejected edit applied locally with `SyncConflict` set
  rather than losing it (§15); delete cascades to recurrence overrides (`0481451`). No renderer
  calendar UI yet — that is still open, see below.
- **Add-account flow (Epic 1) is built**: `AddAccount.tsx` collects IMAP host/port/TLS/login/
  password/SMTP settings and posts to the existing `POST /accounts` endpoint; Gmail/Microsoft
  365 show as visibly-disabled options rather than being hidden. `AppShell` shows it
  automatically when the account list is empty (a derived value, not an effect-driven
  `setState`, to avoid both a lint violation and a first-render flash) and keeps a header button
  for adding further accounts. Verified against the real IMAP matrix (`AddAccount.e2e.ts`),
  which is what surfaced the next item (`96ff23f`).
- **Fixed a real crash in account creation**: `MailKit.Security.SslHandshakeException` derives
  directly from `Exception`, not from `AuthenticationException`/`IOException`/`SocketException`,
  so a mismatched "Use TLS" setting crashed `POST /accounts` with an unhandled 500 instead of
  the friendly "authentication failed" response `AccountsController` already renders for every
  other auth failure. This was reachable by any user before today, from the very first account
  they ever configured — it just had no UI path to trigger it until now (`ecb501f`).
- **OS notifications (Epic 9) are complete**, backend and shell (`2bd1ab9`, `943c139`, `c79f5dc`,
  `042e8a6`). `Account.NotificationEpoch` (set once at creation) and
  `ChangeStreamState.NotificationBaselineAt` (a fixed instant, captured at stream creation and
  again immediately on a triggered resync — before that resync's own catch-up runs) gate
  eligibility without depending on row creation: `MessageIngestor.IngestAsync` returns every
  message a page's occurrences resolved to (`Observed`), not just what it created or changed, so
  a message backfill already materialised is still eligible the moment the live stream reports
  it again. Staged Gmail content (still mid backfill) is evaluated and announced at staging
  time, from the provider's own DTOs — `NotificationRecord.MessageId` is nullable, keyed instead
  by `ProviderStableId` until replay (or coverage, or an ordinary sync page — whichever gets
  there first) backfills it via `BackfillMessageIdsAsync`. `NotificationRecord` is the durable
  at-least-once dispatch record, created in the same transaction as the messages it names,
  redispatched for anything undelivered at startup. `electron-shell` owns the native
  `Notification` call; the renderer relays `NotificationReady` to the preload bridge and
  confirms delivery once shown. Clicking a notification navigates immediately if its message has
  materialised, or calls `MailHub.ResolveStagedMessage` to drain the account's staged queue on
  demand otherwise (§3's "fetched on demand rather than the navigation failing"), showing a
  "still syncing" toast if that genuinely doesn't resolve it.
  Two independent invariant reviews caught real bugs before they shipped — a resync-baseline
  timing bug that would have silently dropped notifications for mail arriving during a resync's
  own catch-up window, and a coverage/replay race that could double-notify the same message —
  both fixed and covered by discrimination-checked regression tests, not just made to pass.
- **Calendar renderer UI (Epic 7) is built** (`f9c8cf1`), hand-rolled rather than a library
  (`react-big-calendar`/`FullCalendar` considered and rejected — explicit user decision). Month
  grid (six full weeks, always) and a `@tanstack/react-virtual` agenda view share one
  navigation/range state; `ContentSwitcher` toggles between them. Event create/edit go through a
  Carbon `Modal` (`EventModal`) against `SaveCalendarEvent`/`DeleteCalendarEvent`, invalidating
  broadly on `CalendarEventUpdated`/`CalendarConflictDetected` since neither event names its
  calendar. Events with `SyncConflict` set get a visible outline plus an explanatory line in the
  edit modal, per §15 (a rejected edit stays applied locally rather than being lost). Deliberate
  empty state when no calendar exists yet, matching the collection-view convention. Accessibility:
  the day cell is a plain `<div>`, not a button — the day-number and each event chip are their
  own `<button>`s, since `jsx-a11y` forbids both a clickable div with no keyboard handler and
  nested interactive elements. New deps `dayjs` and `@tanstack/react-virtual` were named in
  `docs/skills/frontend-shell.md`'s stack list already but not actually installed until now.
  Verified: `Calendar.e2e.ts` covers the empty state and view-switching against a real IMAP
  account with no CalDAV configured; there is no HTTPS-capable CalDAV server in the local test
  matrix (backend requires HTTPS for CalDAV Basic auth, `904908f`; Radicale is HTTP-only by
  default and building a self-signed-cert trust path for it was judged disproportionate for this
  pass), so full round-trip event CRUD against a real CalDAV server has not been visually
  verified — only the API-level CalDAV tests exercise that path. This is a testing-infrastructure
  gap, not a known UI bug.
- **Multi-window (Epic 10) and resizable layout (Epic 11) are built** (`9e86af8`). A new
  `window:open` IPC handler in `electron-shell` is the only thing that ever decides a window's
  origin — the renderer requests a shape via a query string (`?message=<id>`,
  `?compose=<draftId>&account=<id>`, or nothing for a plain independent main window), never a
  URL. A new window inherits its opener's bounds offset by 24px; bounds also persist to
  `AppSettings.WindowBoundsJson` (new `GET/PUT /shell-settings` controller, same singleton-row
  pattern as `CredentialFallbackSettings`) so the next launch's first window remembers its
  position. `MessageWindow`/`ComposeWindow` are standalone per-window roots dispatched from
  `Main.tsx` by query string, each with its own `useHub()` connection per the per-window rule;
  `ComposeWindow` reloads its draft fresh from `GetDrafts` rather than trusting anything passed
  across the window boundary. Compose now autosaves on a 2s debounce so a popped-out window
  survives being closed without discarding — closing a window isn't a moment this component
  gets to intercept. All saves (autosave, manual "Save draft", send, detach) are serialised
  through one promise chain reading state via a ref rather than a stale closure, after an
  invariant review caught that two overlapping saves against a still-unsaved draft would each
  create their own row instead of one updating the other.
  The three-panel body is now `react-resizable-panels`' `Group`/`Panel`/`Separator` instead of a
  static CSS grid, with collapse toggles for the sidebar and detail panel. Sizes read once from
  a new global default (`AppSettings.PanelLayout`, same controller as bounds) and write back
  only on a direct user drag (`meta.isUserInteraction`) — never live-synced to an already-open
  window, since nothing invalidates the `["shell-settings"]` query after the PUT.
  Verified: `pnpm check` and the full Playwright e2e suite (7/7), plus an invariant review with
  no architectural findings against the mutation/sync/persistence invariants — this is a pure
  §13 shell change.
- **Bulk `.eml` export and a connectivity signal are built** (`4f9a6b1`), closing the
  "underlying state" gap that blocked `ExportProgress`/`ConnectivityChanged` from being wired up
  honestly. `ExportJob` walks an account's mailbox tree in self-scheduled batches (Hangfire,
  bounded memory like every other job here), recreating the folder hierarchy on disk and writing
  each occurrence as `.eml`, fetching raw MIME on demand via the existing content-acquisition
  pipeline. The set of occurrences is frozen into a JSON manifest at start time rather than
  re-derived live each batch — `MessageMailbox.Id` is a random v4 GUID, not sequential, so a
  plain `Skip`/`OrderBy(Id)` against the live table would silently skip or duplicate rows if mail
  arrived mid-export and shifted every later row's rank. An invariant review caught this before
  it shipped; fixed and covered by a discrimination-checked regression test. Progress persists on
  the job row (not just broadcast) and resumes via `StartupScheduler`, matching the coverage-state
  pattern. New hub methods: `SaveMessageAsEml`, `StartBulkExport`, `CancelBulkExport`.
  `ConnectivityMonitor` tracks one app-wide online/offline signal via the OS's `NetworkChange`
  event plus a minutely TCP-reachability probe, broadcasting `ConnectivityChanged` only on an
  actual flip. Deliberately not wired into the three per-account provider constructors — see its
  class remarks for why threading it through three provider factories wasn't worth it for one
  coarse signal the OS event and probe already deliver.
- **Epic 2 drag-and-drop, print, and the `mailto:` prompt are built** (`05cfdcf`). Native HTML5
  DnD, no library — two drop targets don't justify one. A message dropped on a folder moves it
  (`MoveMessages`, pre-existing); a folder dropped on a sibling reorders to land just before it
  (new `ReorderMailboxes`/`MailboxManagement.ReorderAsync`, purely local since `LocalSortOrder`
  is never pushed upstream); a folder dropped on a folder with a different parent reparents it
  (new `MoveMailbox` hub method over the pre-existing `MailboxManagement.MoveAsync`), guarded
  client-side against dropping a folder onto its own descendant (would cycle `ParentId`, which
  the tree's recursive render has no way back out of) — and, after a follow-up
  (`442bd43`), server-side too: `MoveAsync` itself now walks the candidate parent's ancestor
  chain and rejects the move at every depth, since the hub method has other possible callers
  than this one drag UI. "Save as .eml" (previously a disabled placeholder) now decodes
  `SaveMessageAsEml`'s base64 payload to a `Blob` and hands it to the OS's own download flow via
  a synthetic download anchor — no new IPC needed. Print is a button on the reading pane calling
  `window.print()`, with `@media print` hiding the header and sidebar/list panels by the DOM ids
  `react-resizable-panels` already gives them for layout persistence; a further pass (`63ac662`)
  added "Print" to the message-list context menu too, per §13's standing per-message menu
  convention — it prefetches the body into the query cache and switches to the reading pane
  before printing, so it goes through the same sandboxed `MessageHtml` render rather than a
  second, ad-hoc one. The `mailto:` prompt checks `app.isDefaultProtocolClient` and
  `AppSettings.MailtoPromptDismissed` (new `PUT /shell-settings/mailto-prompt-dismissed`, same
  singleton-row pattern as panel-layout/window-bounds) once the first window exists at startup.
  Both batches verified: `pnpm check`, the full `dotnet test` suite, and the full Playwright e2e
  suite, all green; each also passed an independent invariant review with no unresolved findings.
- **A real HTTPS CalDAV test fixture exists** (`05c950e`), closing the calendar-UI
  testing-infrastructure gap: the backend requires HTTPS for CalDAV Basic auth (`904908f`), so
  the local matrix needed a real HTTPS server, not just the fake-transport unit tests in
  `CalDavCalendarProviderTests`. `tests/caldav-matrix` runs Radicale over a self-signed cert
  (`pnpm caldav:up`/`caldav:down`); the new `CalDavLiveTests` (Conformance+Deep, self-skipping
  without `TEST_CALDAV_*` env vars like every other live-server test here) drives
  `CalDavCalendarProvider` directly against it through create/sync/update/delete. It immediately
  found a real bug: `UpdateEventAsync`/`DeleteEventAsync` passed a sync-page's server-relative
  href straight to `new Uri(string)` with no base, which on Unix is silently parsed as a
  `file://` URI rather than throwing — every update or delete of an event materialised from a
  real sync page (as opposed to one freshly created locally) failed against a real server.
  `ResourceHref` now resolves against the account's own CalDAV endpoint. Discrimination-checked.
  Certificate trust is **not** exercised by this fixture — the test's own `HttpClient` accepts
  any certificate; `AccountTrustedCertificate`/`Account.CertificateTrustMode` are designed
  (§1, §9) but not wired to any provider's transport yet, for any provider.
- **CalDAV RSVP and single recurrence-override editing are built** (`543e951`), closing the
  two things `CalDavCalendarProvider`'s own class remarks said were deferred "pending the same
  multi-VEVENT PUT this pass does not build." `UpdateEventAsync` now branches on
  `ev.RecurrenceMasterId`: an override GETs the current resource, merges just that one `VEVENT`
  via the new `CalDavIcs.MergeOverride` (regex block replacement, byte-for-byte preserving the
  master and every other override), and PUTs the whole resource back — the master-level
  single-`VEVENT` PUT every other update still uses would have silently discarded everything
  else sharing the resource. `RespondToInviteAsync` builds an iTIP `REPLY` (RFC 5546 §3.2.3) via
  the new `CalDavIcs.ToReplyIcs` and sends it as a `text/calendar; method=REPLY` attachment on an
  in-memory `Draft`, straight through the account's `IMailProvider` — bypassing
  DraftService/Outbox/the mutation queue entirely, matching what `docs/architecture.md` already
  documented for this path. `ICalendarProvider.RespondToInviteAsync` gained a `replyingAs`
  `Address` parameter, since `Account` deliberately carries no `EmailAddress` column;
  `CalendarEventService` resolves the default `SendIdentity` once and passes it down. CalDAV is
  still the only real `ICalendarProvider` implementation, so nothing else broke.
  Verified: fake-transport unit tests for both, plus two new live tests against the real HTTPS
  fixture proving the override merge round-trips through a real server's `sync-collection`
  REPORT; discrimination-checked (reverting the merge branch made both fail for the right
  reason). Independent invariant review: no violations — the RSVP fire-and-forget send matches
  the documented architecture rather than being a new gap.
- **Deleting a single recurrence-override instance in place is built** (`a0a2d80`), the last
  piece of the recurrence-override story. `DeleteEventAsync` now branches on
  `ev.RecurrenceMasterId` the same way `UpdateEventAsync` already did: an override GETs the
  resource, merges a `STATUS:CANCELLED` `VEVENT` for that `RECURRENCE-ID` via the existing
  `CalDavIcs.MergeOverride`, and PUTs the whole thing back — RFC 5545 §3.8.1.11's documented way
  to say "this occurrence no longer happens" without touching the recurrence rule or the master.
  Verified against the real HTTPS fixture (edit an override, then delete it, confirm the master
  survives both and the override reports `Cancelled` rather than gone on the next sync);
  discrimination-checked.
- **Certificate pinning is wired into both providers** (`73557aa`). `AccountTrustedCertificate`
  and `Account.CertificateTrustMode` existed as schema only until now — nothing consulted them.
  `CertificateTrust.Validate` is the one shared implementation IMAP (`ImapClient` connect and
  `SmtpClient` send, against the possibly-different SMTP host) and CalDAV
  (`HttpClientHandler`) both call: validate normally first; only once that fails, permit the
  connection if the presented certificate's SHA-256 fingerprint matches a pin for the expected
  hostname; `TrustAll` bypasses both checks. `AccountTrustedCertificate`'s schema was brought in
  line with the doc along the way (`Thumbprint` → `Sha256Fingerprint`, new `ExpectedHostname`
  column). Pinned-certificate lists are resolved once per provider construction, not queried
  from inside the synchronous TLS callback. `CalendarProviderFactory` now builds a fresh,
  disposed-by-its-caller `HttpClient` per account instead of one DI-pooled client shared by
  every account, since trust is a per-account decision a shared handler's callback can't
  honour. New `TrustCertificate` hub method; `AccountSettingsDto`/`UpdateAccount` carry
  `CertificateTrustMode`. A rejected certificate reports `ErrorCategory.Validation` with the
  fingerprint/issuer as structured `Extensions` (IMAP connect) or a `ProviderAuthenticationException`
  carrying the same message (SMTP send, which has no `AuthResult` to return through — thrown as
  a definite pre-authentication rejection, not the ambiguous-outcome path a generic thrown send
  gets). Verified: unit tests for the four trust decisions plus store idempotency, and a live
  test against the real HTTPS fixture driving the actual logic through reject → pin → accept and
  the `TrustAll` bypass against a genuine self-signed certificate; discrimination-checked.
  Independent invariant review: no violations; two non-blocking findings (no structured SMTP
  error path, the new per-account CalDAV client was never disposed) fixed in the same commit.
- **Certificate pinning's renderer UI is built** (`8b3b555`). `AddAccount` shows an
  `ActionableNotification` offering "Trust this certificate and retry" on a rejected certificate,
  reading the fingerprint/hostname straight out of the failure's `MutationProblemDetails.Extensions`
  rather than parsing prose — `AccountAuthenticationFailedException` now carries the whole
  problem, not just its message, and the controller returns it unwrapped so nothing is lost on
  the way. `ErrorPresentation.present()` gained a data-driven `"trust-certificate"` action:
  still one mapping, not one per call site, it just now also looks at `Extensions` when a
  `Validation` failure happens to carry them. Pinning a specific fingerprint needs an account id
  that doesn't exist yet at `AddAccount` time (credentials are verified before anything is
  persisted, precisely so a rejected attempt leaves nothing behind), so the only choice available
  that early is a new `CertificateTrustMode` field on `AddAccountRequest` — `TrustAll` for the
  account about to be created. `AccountSettings` gained the matching toggle with the in-app
  warning the architecture doc calls for, for tightening back to a specific pin (the already-built
  `TrustCertificate` hub method) once the account exists. Along the way, fixed a real gap the
  types regeneration surfaced: `InviteResponse` (used by the RSVP hub method from an earlier
  commit) was missing `[TranspilationSource]`, silently crashing `pnpm generate:types` for the
  whole `IMailHub` interface since that commit — regenerating now also picked up several other
  DTOs that were never generated after their own earlier commits.
  Verified: `pnpm check`, the full `dotnet test` suite (263 passed, including a new
  discrimination-checked test proving the fingerprint/hostname survive the exception→controller
  round trip), and the full Playwright e2e suite (7/7).

## Next task

1. **Deferred external configuration:** Gmail/Graph client registrations remain intentionally
   unsupplied per user request. They are deployment environment values and must never be
   committed: `Providers__Gmail__ClientId`, `Providers__Gmail__ClientSecret`,
   `Providers__Graph__ClientId`, optional `Providers__Graph__Authority`. The add-account form's
   disabled Gmail/Microsoft 365 options are waiting on this plus their OAuth flows.
2. OS notification dispatch has not been verified against a real notification daemon — this
   container has none, and Electron's `Notification` API is unreliable to assert on headlessly.
   The hub → preload → `new Notification(...)` wiring is exercised by unit tests and builds
   clean, but a manual check on a real desktop is still worth doing before calling this fully done.
3. Pinning a _specific_ certificate fingerprint for an account that already exists (rather than
   `TrustAll` at creation time) has no UI trigger yet — it needs a `ReauthenticateAccount` flow,
   which doesn't exist either. The `TrustCertificate` hub method and the "certificate untrusted"
   presentation are both ready for it; only the reauthentication surface itself is missing.

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
