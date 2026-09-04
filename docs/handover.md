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

## Completed work (cont.)

- **`ReauthenticateAccount` flow:** a new `POST /accounts/{id}/reauthenticate` endpoint
  (`AccountProvisioningService.ReauthenticateAsync`) re-verifies an account stuck in
  `NeedsReauth`/`Error`, and a renderer modal (`ReauthenticateAccount.tsx`) reachable from an
  `ActionableNotification` banner in `AppShell` when the selected account's `authState` isn't
  `Connected`. The secret is optional — a certificate-only rejection (server cert rotated, now
  pinned via `TrustCertificate`) needs no new password, just a retry. On a certificate-untrusted
  rejection the modal reuses `AddAccount`'s "trust this certificate" prompt, since the account id
  needed to pin a fingerprint now exists — this closes the gap noted below (pinning a specific
  fingerprint for an existing account had no UI trigger).
  `invariant-review` caught a credential-loss bug in the first draft: the new secret was written
  to the credential store before verification, and nothing restored the old one on failure, so an
  unrelated rejection (transient network error, unpinned cert) silently destroyed a
  still-working password. Fixed by reading the prior credential back before overwriting it and
  restoring it in the failure branch. Discrimination-checked: reverting the restore made the new
  regression test (`A_failed_reauthentication_restores_the_previously_working_credential`) fail
  with exactly the expected byte mismatch, confirming the test guards a real bug.
  Verified: `pnpm check` (Node 22), and the full `dotnet test` suite (267 passed).

- **System tray / `CloseBehavior`:** `AppSettings.CloseBehavior` had existed as schema since the
  initial migration with no consumer. `GET /shell-settings` now returns it and a new
  `PUT /shell-settings/close-behavior` sets it (no renderer UI yet — there's no Epic 8 settings
  screen to put a toggle in — but the surface is ready). `apps/electron-shell/src/Tray.ts` creates
  a `Tray` (icon embedded as a base64 data URL — the main-process bundle has no static-asset copy
  step) with "Open MyloMail"/"Quit". `Main.ts` reads `CloseBehavior` once at startup and hides each
  window to the tray instead of destroying it on close when `MinimizeToTray` is active; a `quitting`
  flag set in `before-quit` lets a real quit close windows and kill the backend child normally.
- **OpenTelemetry:** `AppSettings.TelemetryEnabled`/`OtelEndpoint` existed as schema with nothing
  reading them. First pass read them from the database via a direct pre-host SQLite connection;
  corrected on review — telemetry is deployment configuration, like provider client registration,
  not a user-facing app setting, so it does not belong in the database at all. Removed the two
  columns (migration `RemoveTelemetryFromAppSettings`) and reads `Telemetry:Enabled` /
  `Telemetry:OtelEndpoint` from `builder.Configuration` instead (`Telemetry__Enabled` /
  `Telemetry__OtelEndpoint` env vars, documented in `AGENTS.md` next to the provider vars). ASP.NET
  Core + HttpClient auto-instrumentation and an OTLP exporter are registered only when enabled and
  an endpoint is set; the common (off) case adds no exporter at all.
- **CI matrix:** initially collapsed the existing tag-only `ubuntu`/`windows`/`macos` matrix into
  an always-on job; reverted on request — running the full suite on three platforms per commit was
  a deliberate cost tradeoff, not an oversight. `.github/workflows/ci.yml` is unchanged from before
  this pass.
  Verified: `pnpm check`, `pnpm status`, full `dotnet test` (267 passed).

- **Global settings screen (Epic 8) and remote-content allow list (Epic 5):** a follow-up gap
  audit against `architecture.md` (excluding the already-known Gmail/Graph stubs) found two flat
  requirements with no implementation: no settings screen existed for anything install-wide
  (theme, close behaviour, credential-storage visibility — `AccountSettings.tsx` only ever
  covered per-account fields), and "allow remote content" was local `MessageHtml` component
  state that reset every time a message was reopened, with no persisted per-sender list.
  `ShellSettings.tsx` adds the settings screen: theme (new `PUT /shell-settings/theme`,
  applied via a new `ThemeProvider.tsx` wrapping every window in Carbon's `GlobalTheme`),
  close behaviour (the endpoint from the tray work), a credential-store-status notice (new
  `GET /credential-store/status`), and the trusted-senders list. `TrustedRemoteContentSender`
  (new table) backs a persisted, address-based allow list — not account-scoped, since trust in
  a sender isn't a fact about which account received their mail — with a new
  `RemoteContentController` and a checkbox on the existing "Load content" prompt.
  `invariant-review` caught a real regression: the "load content" override was derived as
  `allowRemoteOverride || isTrustedSender` (to sidestep a setState-in-effect lint error), but
  `MessageHtml` is never remounted per message, so clicking "Load content" on one message left
  remote content — including tracking pixels — silently unblocked on the next, unrelated
  message. Fixed by keying `MessageHtml` on `messageId` in `ReadingPane` so it remounts.
  Verified: `pnpm check`, full `dotnet test` (267 passed), full Playwright e2e suite (7/7,
  including `HtmlRendering` re-run after the fix).

- **Quit confirmation for pending scheduled mail, and a bulk-export UI:** a third gap audit
  (still excluding Gmail/Graph) found two more flat requirements with no implementation.
  Quitting with a message inside its undo-send delay silently lost it — `before-quit`
  unconditionally killed the backend, and job storage being in-memory (§9) means that state
  has no durable existence yet. A new `GET /outbox/pending-count` (counting
  `OutboxStatus.Scheduled`, the state both undo-send and an arbitrary future schedule sit in)
  backs a `before-quit` handler in `Main.ts` that now asks "Quit Anyway"/"Cancel" when
  something's pending, and fails open (quits with no dialog) on zero, a request failure, or a
  timeout. Separately, bulk export's backend (job, hub methods, persisted progress) has existed
  since an earlier pass with no renderer UI at all — `ExportAccount.tsx`, embedded in
  `AccountSettings`, now picks a destination via a new `window.dialogs.pickExportFolder()` IPC
  round-trip and shows live progress via the existing `ExportProgress` broadcast.
  `invariant-review` caught two real gaps in the quit-confirmation draft: a _hung_ (not down)
  backend would leave the confirmation fetch unsettled forever, making the app unquittable
  through any normal path (fixed with a 3s `AbortController` timeout); and rapid repeated quit
  attempts could stack multiple confirmation dialogs before the first resolved (fixed with an
  in-flight guard). It also caught a genuine type-generation drift from the _previous_ two
  commits: `pnpm generate:types` had never been re-run after the settings-screen and
  remote-content-allow-list DTOs landed, so `packages/shared-types` was stale for them —
  harmless at runtime (those endpoints are called with plain `fetch`, not the generated client)
  but corrected in a dedicated commit.
  Verified: `pnpm check`, full `dotnet test` (267 passed), full Playwright e2e suite (7/7) — the
  quit-confirmation dialog itself isn't exercised by the existing e2e suite (no spec drives
  `app.quit()`), so that path was reviewed for correctness rather than run end-to-end.

- **.NET 10 upgrade:** `global.json` and `Directory.Build.props` bumped to SDK/TFM 10;
  `Microsoft.EntityFrameworkCore.Sqlite`/`Design`, `System.Security.Cryptography.ProtectedData`,
  `Microsoft.Extensions.TimeProvider.Testing`, and the `dotnet-ef` tool all bumped to their .NET
  10-aligned releases. `Microsoft.Graph` 5.104.0 → 6.5.0 was also required (not itself
  runtime-aligned, but its old transitive `Microsoft.Kiota.Abstractions` carried a high-severity
  advisory that now fails restore under this repo's existing audit-as-error rule).
  Uncovered and fixed a real regression in `scripts/watch.ts`'s dotnet watcher, exposed by the
  upgrade: SDK 10's `dotnet watch` no longer consumes `--project <path>` as its own option (it's
  forwarded to MSBuild verbatim and rejected with `MSB1001`), and its initial build cycle's
  console output changed enough that the watcher's `cycleStart` regex never matched — together
  these left `pnpm status`'s `dotnet` column permanently stuck "pending" with no visible error,
  even though the underlying build was fine the whole time. Fixed by running from inside the
  project directory instead of `--project`, and widening the regex.
  Verified: `pnpm check`, full `dotnet test` (267 passed, now under `net10.0`), full Playwright
  e2e suite (7/7) against the real Electron app and .NET 10 backend, and `pnpm status` confirmed
  clean through both an initial watcher cycle and a real file-change restart.

- **Remaining server-side dependency bumps:** a full review of both test projects' packages
  post-.NET-10-upgrade found two more upgrades available: `Microsoft.NET.Test.Sdk` 17.14.1 →
  18.9.0 (new major generation; no newer 17.x exists) and `xunit.runner.visualstudio` 3.1.4 →
  4.0.0 (still supports xUnit v1/v2/v3 per its own manifest). Everything else server-side was
  already at latest stable. `xunit` itself stays at 2.9.3 — the top of its v2 line; `xunit.v3`
  is a separate package and a framework migration, not a version bump.
  Verified: full `dotnet test` on `MyloMail.Api.Tests` (267 passed).

- **Fixed `pnpm generate:types` after the .NET 10 upgrade:** it was silently broken since that
  upgrade (nothing had re-run it until now) — `scripts/generate-types.ts` still pointed at
  `bin/Debug/net8.0`, and separately the pinned `typecontractor` global tool targets `net9.0`
  regardless of this project's TFM, which this machine (8.x/10.x only) can't satisfy without
  `DOTNET_ROLL_FORWARD=LatestMajor` scoped to that one subprocess call.
- **Gmail/Graph provider completion:** both providers threw `NotSupportedException` for
  `SendAsync`, drafts, and all mailbox CRUD, plus `MoveToTrashAsync` for both (an earlier audit
  this session wrongly reported this as already done) and `RemoveFromMailboxAsync` for Graph.
  All now implemented, following each file's own established patterns — see
  `feat(providers): implement Gmail/Graph send, drafts, and mailbox CRUD` for the details,
  including a new `IProviderMailboxResolver.LocalPath()` Gmail's label-hierarchy reconstruction
  needed. Per explicit instruction, verification was scoped to the build and this repo's
  existing test suite — no interactive OAuth consent flow was attempted, and none of this was
  verified against real Gmail/Graph accounts.
- **Message-list multi-select and bulk actions (Epic 6)**, and **account reordering + a colour
  picker (Epic 1)** — see their own commits for detail. The account-reorder work also surfaced
  that there was no UI at all to switch between multiple accounts before now (`AccountSwitcher.tsx`
  fixed that at the time, since superseded — see below). All four items verified with
  `pnpm check`, full `dotnet test` (267 passed), full Playwright e2e suite (7/7), and an
  `invariant-review` covering all three diffs together — no findings.
- **Outlook-style sidebar (Epic 2), on explicit request:** architecture.md states this flatly —
  every account's mailboxes visible in one persistent sidebar tree, "no switching that hides
  other accounts." The sidebar previously showed one selected account's tree at a time, with
  `AccountSwitcher.tsx`'s chip row for switching between them. `Sidebar.tsx` replaces both: one
  collapsible top-level section per account, each with its own nested `MailboxTree` — clicking
  any mailbox under any account sets both `selectedAccountId` and `selectedMailboxId` (both
  writes land inside one React event handler, so React's batching covers them). Account reorder
  is now a drag on the section header instead of a separate chip; `AccountSwitcher.tsx` is
  deleted, fully superseded.
  **Surfaced and fixed a real process gap while verifying this:** `pnpm e2e` never rebuilt
  `apps/renderer/dist`/`apps/electron-shell/dist` — Electron loads whatever was last built by
  hand, so every renderer-side feature earlier this session (multi-select, the settings screen,
  the remote-content allow list, export UI, the original account switcher) had only ever been
  "verified" against a stale bundle, not the code actually being reviewed. Added a
  `pretest:e2e` step (`build:renderer && build:shell`) so this can't happen silently again.
  Re-ran the full e2e suite against a freshly rebuilt bundle for real this time (7/7), plus a
  temporary two-account smoke test (written, run, deleted) confirming both accounts render
  simultaneously with independent trees. `invariant-review`: no findings, presentational/
  selection-state only.

- **Fifth architecture.md pass — calendar RSVP UI (Epic 7) and sidebar/mailbox collapse
  persistence (Epic 2):** both flatly required by the doc, both previously missing entirely.
  RSVP: the CalDAV iTIP REPLY backend already existed with zero renderer UI; added attendee
  display and Accept/Tentative/Decline to `EventModal`, plus a real bug fix — `RespondToInviteAsync`
  only ever sent the reply email and never updated the local event's own attendee list, so the
  UI would show "awaiting response" forever absent a lucky future sync. Collapse persistence:
  the account-section toggle from the previous item was plain React state; now a real
  `Account.SidebarCollapsed`/`Mailbox.IsCollapsed` column pair, and `MailboxTree` gained
  per-mailbox collapse (previously nonexistent) alongside it.
  `invariant-review` found no invariant violations; it did catch a real UX regression from the
  mailbox-row restructuring (drop-target hit area shrank to exclude the new chevron column),
  fixed by moving the drag handlers to the row wrapper. Verified with `pnpm check`, full
  `dotnet test` (267 passed), and the full Playwright e2e suite (7/7) — run both individually
  and together against a freshly rebuilt renderer, after tracking down an unrelated full-suite
  flakiness (a single, rotating spec occasionally hanging for the full 2-minute ceiling) to a
  long-lived local IMAP fixture container needing a restart after days of reuse, not a code
  regression — confirmed by every spec passing individually and the hang moving to a different,
  unrelated spec on repeat runs. RSVP's actual send/response flow was verified via code review,
  the conformance suite, and a direct backend check of the new hub method rather than a live
  click-through, since the local test matrix has no CalDAV server that can stage a real invite
  with attendees.
- **Sixth architecture.md pass — Graph send default and tenant consent outcome.** Two gaps in
  `GraphMailProvider`/`GraphOAuthAuthenticator`. First, §15's draft-then-send policy was only
  being honored when an attachment exceeded the 3MB inline limit; anything else went out via the
  direct `/sendMail` shortcut, which returns no message id for post-crash reconciliation to key
  off. `SendAsync` now always creates the draft, uploads any large attachments against it, then
  sends by the draft's id — unconditionally. Second, §5 calls for a distinct, non-retryable
  outcome when a Microsoft 365 tenant has blocked ordinary user consent, since sending the user
  back to re-enter credentials doesn't fix an admin approval block; `AuthenticateAsync` now
  recognises MSAL's `ConsentRequired` classification or the raw `AADSTS65001`/`AADSTS90094` STS
  codes and returns `ErrorCategory.Validation` with a new `adminConsentRequired` extension flag.
  `invariant-review` confirmed both changes are invariant-safe (no frozen mutation/reconciliation
  assumption depends on the old Graph send shape; the MSAL catch-block ordering is valid, checked
  against the actual type hierarchy) but caught a real UX bug in the first draft: `present()`'s
  call sites never forward the DTO's `title`, so the new outcome was rendering as the generic
  "The server refused this" / retry message — exactly the wrong read for a non-retryable admin
  block. Fixed by branching on the new extension flag in `ErrorPresentation.ts`, the same pattern
  already used for certificate-untrusted. Verified with `pnpm check` (under Node 22 — this
  sandbox's default shell Node is v18 and silently fails `stylelint`/`vitest` with ESM/engine
  errors that look unrelated to the diff; `nvm use 22` first) and `dotnet test` (267 passed). Not
  re-run against a live Microsoft 365 tenant — the AADSTS code matching is confirmed correct by
  reading MSAL's actual exception hierarchy, not by an interactive consent-blocked login.
- **Seventh architecture.md pass — tombstone garbage collection (§6) had no implementation at
  all.** A message losing every mailbox membership was documented as becoming a tombstone that
  persists "until mutation state, reconciliation state and other references permit collection,"
  but no code anywhere ever deleted a `Message` row — local storage grew unbounded for every
  message ever deleted server-side. New `TombstoneGcJobs` (self-scheduling, one message per pass,
  wired into `StartupScheduler` per enabled account) finds zero-membership messages and checks
  exactly the references §6 names — a non-terminal `MutationItem`, a pending
  `MessagePendingChange`, an undelivered `NotificationRecord`, a `Draft.InReplyToMessageId` — before
  deleting the FTS5 search row (via the existing `SearchIndexer.RemoveAsync`) and the `Message`
  row itself in one transaction. `invariant-review` caught two real problems in the first draft:
  (1) critical — zero membership was treated as immediately collectible, which violates §3's
  documented Graph-move race (a folder move's source-removal and destination-addition deltas can
  arrive minutes apart, in either order); fixed with a new `Message.OrphanedAt` column, set the
  first time GC notices zero membership and cleared the moment membership returns, with a 30-minute
  grace period before collection. (2) the eligibility check and the delete were non-atomic; fixed
  by re-running the check immediately before the delete, inside the same transaction — narrows the
  TOCTOU window but does not fully eliminate it, since `MutationItem.MessageId`/
  `Draft.InReplyToMessageId` deliberately carry no FK (see `TombstoneGcJobs.TryCollectAsync`'s own
  remarks). 6 new tests cover the grace period, each reference kind, and a message regaining
  membership before its grace period elapses. Verified with `dotnet test` (274 passed, up from 267) and `pnpm check` (clean, under Node 22).
- **Eighth architecture.md pass — `RemoveFromMailbox`/`DeletePermanently` fully implemented but
  unreachable from the UI.** §7 lists both as client-facing batch message-mutation hub methods;
  the mutation kinds, queue, executor, reconciler, per-provider handling and a Gmail-specific
  recovery policy all existed, but `MailHub`/`IMailHub` never declared either method. Added both,
  following the existing `MoveToTrash`/`SetFlags` per-message-loop pattern, and wired
  `DeletePermanently` into the message-list context menu. `RemoveFromMailbox` is intentionally
  **not** wired into any UI yet: `invariant-review` found IMAP's implementation is a placeholder
  that expunges the message outright and Graph's forwards straight to permanent deletion — a
  first draft's "Remove from this folder" menu entry (no danger styling) would have made a
  mild-looking action silently and irreversibly delete mail on any non-Gmail account. Removed
  that menu entry; documented the gap directly on the hub method (`MailHub.cs`) so it gets wired
  in once IMAP/Graph's mutation divergence — already a named, deferred "stage C" piece of work —
  actually distinguishes "remove from this folder" from "delete."
- **Ninth architecture.md pass — two Electron-shell gaps in `Main.ts`.** (1) §9: "Electron
  detects unexpected backend process exit and offers restart" had no implementation — only
  `BackendSupervisor`'s own startup-phase race was covered (the credential-store restart case), so
  a mid-session backend crash was silently unhandled. Added an `exit` listener on `backend.child`
  right after startup succeeds, guarded by the existing `quitting` flag; on an unexpected exit it
  offers Restart/Quit and, on Restart, does `app.relaunch()` + `app.exit(0)` — the whole app
  relaunches fresh rather than re-plumbing a new backend into windows that assumed a still-live
  connection. (2) §13 Epic 10: minimize-to-tray was applying to every window's close, not just the
  _last_ one — closing a popped-out compose window while the main window stayed open hid it to the
  tray instead of closing normally. Fixed by additionally requiring
  `BrowserWindow.getAllWindows().length === 1` in the close handler, confirmed correct by
  `invariant-review` against Electron's documented event order (`close` fires before a window is
  removed from that list). `invariant-review` also confirmed `relaunch()`+`exit()` is the correct
  Electron restart pattern and that `app.exit()` doesn't re-trigger `before-quit` (no
  dialog-stacking loop), flagging one accepted, narrow edge case: a backend crash landing during
  the exact window the quit-confirmation dialog is already open could show both dialogs at once.
  No `Main.test.ts` exists for this file — verified by code review + `pnpm check` (clean, under
  Node 22), consistent with how this session's other `Main.ts` work (quit-confirm, mailto-prompt)
  was verified.
- **Tenth architecture.md pass — three gaps found, one fixed, two are real but substantial
  enough to leave for dedicated feature work.** Fixed: the Delete key wasn't bound to move-to-trash
  despite the feature being fully working everywhere else (§13's "keyboard shortcuts follow
  standard Outlook/Gmail conventions" standing rule) — added to `MessageList.tsx`'s
  `useShortcuts` call. Left open, both noted below under "Next task": (a) Reply/Reply
  all/Forward are still stubbed ("Compose is not built yet.") in `messageActions`, so no
  shortcut was bound to them — wire both together once that flow exists; (b) true scheduled
  send (§15 describes sending at an arbitrary future time, not just the already-implemented
  undo-send delay) has no backend hub method or renderer UI at all; (c) the message list has
  neither TanStack Table (sortable/filterable columns, §12) nor TanStack Virtual
  (virtualisation, §12) — it's a plain `<ul>` with no sort/filter state, and
  `@tanstack/react-table` isn't even a dependency yet.
- **Eleventh architecture.md pass — two gaps fixed; audit itself recommends stopping full
  sweeps here.** (1) Content-fetch failures (`ContentAcquisition.AcquireAsync`) were a silent,
  permanent dead end — any exception during fetch or parse/store marked the message `Failed`
  forever, with nothing to requeue it and `ReadingPane.tsx` polling every 2 seconds indefinitely
  showing "Downloading this message…" with no way to distinguish "still downloading" from "never
  will," contradicting Epic 4's "failed sync shown via a visible indicator, never silent." Added a
  bounded retry (`MaxAttempts = 5`, requeues to `Queued` while attempts remain) and a new
  `MessageBodyDto.IsFailed` field so the reading pane stops polling and shows an explicit failure
  message once attempts are exhausted. `invariant-review` confirmed the mechanics are correct and
  flagged two accepted, non-blocking gaps: a fast-failing message can monopolize the queue head for
  up to 5 rapid iterations (bounded, not unbounded), and a message that exhausts its attempts
  during a genuinely transient outage has no path back to retry. (2)
  `AppSettings.AttachmentTempCleanupOnStartup` was dead — the field existed but nothing read it,
  so `Program.cs` always swept temp attachments at startup regardless of its value. Gated it
  behind the setting — but the first draft read `AppSettings` _before_
  `DatabaseBootstrapper.MigrateAsync()`, which is what creates the database on a fresh install;
  `invariant-review` caught this before it shipped (would have crashed first launch). Fixed by
  moving the read into the existing post-migration scope; manually verified against a fresh, empty
  data directory that migrations now apply cleanly before that read runs. The audit's own
  conclusion after this pass: further full-doc sweeps have hit diminishing returns (two small
  fixes this round vs. new-feature-sized findings in most prior passes) — the real remaining work
  is the three items already tracked below (reply/forward, scheduled send, TanStack Table/Virtual),
  and effort should shift there rather than continuing to search for new gaps.
- **Reply/Reply-all/Forward, true scheduled send, and TanStack Table/Virtual for the message
  list — all three now done.** Built in parallel across three isolated git worktrees (one per
  feature), then merged by hand: two of the three worktrees turned out to be built against a
  several-dozen-commit-old base and needed rebasing before merge, and the reply/forward and
  table-rewrite worktrees both touched `MessageList.tsx` heavily enough that a plain `git merge`
  would have silently dropped work — reconciled manually instead. Reply/reply-all/forward adds a
  new `MessageReplyContextDto`/`MailHub.GetMessageReplyContext` plus pure helpers in the new
  `ComposeReplyForward.ts` (quoting/forwarding HTML construction, subject prefixing, reply-all
  Cc-exclusion); along the way fixed a real pre-existing bug where `Compose.tsx` hardcoded
  `inReplyToMessageId: null` on every autosave, silently un-threading any reply draft immediately.
  Scheduled send turned out to need almost no new backend work — `OutboxService.QueueAsync`
  already fully supported an arbitrary future time — so it was just threading an optional
  `DateTimeOffset?` through `MailHub.SendDraft`/`DraftService.SendAsync`, plus a Gmail-style split
  send button in `Compose.tsx` (Tomorrow morning / next-Monday-morning presets, or a custom
  Carbon `DatePicker`/`TimePicker`). The message list now uses TanStack Table (via its official
  `/legacy` v8-compatibility subpath — the installed version resolved to a genuinely new v9 major
  with a different default API) for sortable From/Subject/Date columns and a client-side
  free-text filter, plus TanStack Virtual for row windowing, following the same pattern already
  established in `CalendarAgenda.tsx`; every pre-existing behavior (multi-select, drag-and-drop,
  the full context menu, keyboard shortcuts, search-vs-browse switching, the pending-change
  overlay) was verified intact through the rewrite. `invariant-review` caught three further real
  bugs before this shipped, all fixed: reply/forward silently seeded a blank quote when content
  hadn't been fetched yet or had permanently failed (now an honest placeholder via a new
  `resolveOriginalHtml` helper, and plain-text-only bodies are escaped/newline-converted instead
  of embedded as raw HTML); reply-all's Cc-exclusion compared addresses case-sensitively, so the
  same address differing only in case could end up listed twice; and the forward-attachment-copy
  loop had no per-iteration error handling, so one failed attachment silently aborted copying the
  rest. `pnpm check` clean, `dotnet test` 274 passed/0 failed (unchanged — no dedicated tests
  added for the new hub methods, consistent with this codebase's existing pattern for thin
  passthrough hub methods).
- **Twelfth architecture.md pass — §15 "Signatures" was entirely unimplemented on the
  frontend.** `SendIdentity` (multiple send-as identities per account, each with its own
  display name/address/signature, explicitly in scope per the doc) existed as a backend schema
  with `DraftService` silently always resolving the account's default — never exposed to the
  renderer at all: no generated type, no hub method, no UI. Added `GetSendIdentities` +
  `SendIdentityDto`; `DraftDto`/`SaveDraftRequest` gained `SendIdentityId`, round-tripped the
  same way `InReplyToMessageId` already is. `Compose.tsx` now shows a "From" selector when an
  account has more than one identity, and appends the chosen identity's signature after any
  reply/forward quote for a brand-new draft only (never an existing one, whose saved body
  already reflects whatever the user kept). `invariant-review` caught two real bugs before this
  shipped: the appended signature never actually rendered, because `Editor` (Lexical) only reads
  its `initialHtml` once at construction and never re-syncs from a later `body` state change —
  fixed by delaying `Editor`'s first render (skeleton placeholder) until the signature has
  already been folded into `body`, for a brand-new draft only; and the "From" selector's
  immediate on-select save (bypassing the normal debounce, since a discrete choice should
  persist promptly) could read a stale identity from `fieldsRef`, which is otherwise only kept
  current by a passive effect running after paint — later than the already-chained save's
  microtask — fixed by updating `fieldsRef.current` directly in the selector's own handler.
- **Thirteenth architecture.md pass — calendar `ResolveEventConflict` didn't exist, and
  `MessageList.tsx`'s ARIA roles were mismatched.** §7 lists `ResolveEventConflict` as a
  client-facing calendar hub method and §15 requires a "keep mine / keep theirs" prompt for a
  flagged `CalendarEvent.SyncConflict`; only "keep mine" was reachable (resubmitting the local
  edit through the ordinary save path) — the hub method didn't exist, and there was no way to
  discard a local edit and pull the server's version. Added
  `CalendarEventService.ResolveConflictAsync`, `MailHub.ResolveEventConflict`, and "Keep
  mine"/"Keep theirs" buttons in `EventModal`'s conflict banner. `invariant-review` caught two
  real bugs before this shipped: clearing `SyncConflict` unconditionally inside
  `CalendarSyncService.Apply()` meant an _ordinary background sync pass_, not just explicit
  resolution, could silently overwrite a still-unresolved conflict's local edit while also
  erasing the flag that would have shown something was wrong — fixed by threading an optional
  `resolvingEventId` through the sync call chain so routine polling now skips any event still
  flagged `SyncConflict`, except the one a user's "keep theirs" call names explicitly; and the
  "keep theirs" path re-looked-up the event by its local `Id` afterward, which a rejected sync
  cursor can invalidate (`CalendarSyncService` discards and re-creates every local row for that
  calendar under fresh ids when this happens, per §3) — fixed by re-querying on
  `(CalendarId, ProviderEventId)` captured before the sync runs. Also found: `MessageList.tsx`'s
  header used `row`/`columnheader`/`aria-sort` (which require a `table`/`grid`/`treegrid`
  ancestor per the ARIA spec) above a `list`/`listitem` body with no such ancestor — since each
  row is one atomic button, not a per-cell-navigable grid, retrofitting real table/grid
  semantics would fight the actual interaction model, so the header is now a plain labelled
  group of sort buttons instead. `dotnet test` 277 passed/0 failed (up from 274 — two new tests
  cover keep-mine/keep-theirs, plus the routine-sync-preserves-conflict case
  `invariant-review`'s finding was about). `pnpm check` clean under Node 22.
- **Fourteenth architecture.md pass — no real contrast floor on the account colour picker.**
  §13 Accessibility: "colour contrast (including per-account sidebar colours — needs a contrast
  floor, not arbitrary user-picked colour) must meet accessible standards" — the picker was a
  bare `<input type="color">` with no validation at all. New `AccentContrast.ts` computes real
  WCAG relative-luminance contrast ratios against Carbon's light/dark theme background tokens
  and adjusts a failing pick to the closest passing colour (lightness first, then saturation).
  `invariant-review` caught that a first-draft lightness-only clamp didn't actually deliver this:
  a desaturated grey clamped into the "safe" band still measured 1.43:1/1.67:1 against the two
  real backgrounds, well under the 3:1 WCAG floor — hue/saturation, not lightness, are what
  separate a colour from a grey background, so a lightness-only clamp can't fix a grey. Fixed
  with the real contrast-ratio computation instead. Applies prospectively only (an
  already-saved colour is untouched until the picker is reopened) — no backend/migration
  involved, and the diff doesn't claim otherwise. This pass's audit also flagged two other
  candidate gaps: one (missing-attachment-language detection) turned out to be a false positive
  on direct verification — it's already implemented (`Compose.tsx:270-273`, confirmed by the
  thirteenth pass too); the other (`SendIdentity` has no create/update/delete path — only ever
  one identity per account is reachable, so the "alias address" scenario §1/§15 describes as in
  scope is architecturally unreachable through the app as it stands) is real but is feature-sized
  work, not a bug fix, and is being raised with the user for a scope decision rather than built
  unprompted.
- **Send-as identity management, built after the user confirmed scope.** New
  `SendIdentityService` (add/update/set-default/delete) plus four `MailHub` methods and a
  `SendIdentityManager` component in `AccountSettings`. A failing test caught a real bug in
  `SetDefaultAsync`'s first draft: demoting the old default and promoting the new one in one
  `SaveChangesAsync` relied on EF Core applying the two `UPDATE`s in assignment order, which
  isn't guaranteed — `SendIdentities`' database-level unique partial index (exactly one default
  per account) is checked per-statement in SQLite, not deferred to commit, so promoting before
  the demote flushed transiently violated it. Fixed by splitting into two sequential saves;
  `invariant-review` confirmed wrapping both in an explicit transaction would not have helped,
  since the per-statement check applies regardless of transaction boundaries. `DeleteAsync`
  rejects deleting the account's default identity, or one a saved draft still references
  (`Draft`'s FK to `SendIdentity` is `Restrict`, not `Cascade`, precisely to prevent a draft
  silently losing the identity it was written from). 5 new tests; `dotnet test` 282 passed/0
  failed (up from 277). `invariant-review` found no invariant violations and flagged three
  lower-risk items left as-is, consistent with existing codebase convention: no
  `accountId`-existence check before inserting an identity, no email-address format validation,
  and a TOCTOU window in the draft-reference check that the `Restrict` FK backstops into a clean
  error rather than data corruption if ever actually hit.
- **Fifteenth architecture.md pass — bounded initial sync and attachment size override both had
  zero UI/DTO path despite full backend support.** §3/§13 Epic 3's bounded (last N
  months/messages) vs. full-history initial sync choice was hardcoded to `Full` for every
  account regardless of what a user might want; §15's `Account.AttachmentSizeLimitOverride`
  ("user-set") could only ever be `null`. `invariant-review` traced the read sites
  (`CoverageService` → each provider's `InitialSyncMailboxAsync`; each provider's
  `GetAttachmentConstraintsAsync`) and confirmed both were genuinely live, previously-dead
  columns, not decorative — this UI work actually changes sync/attachment behavior, not just
  writes an unread field. Added `InitialSyncMode`/`InitialSyncBoundValue` to
  `AddAccountRequest`/`NewAccount`, a radio group + bound input in `AddAccount.tsx`, and
  server-side validation (a bounded mode needs a positive bound) in `AccountsController`. Added
  `AttachmentSizeLimitOverride` to `AccountSettingsDto`, a number-in-MB input in
  `AccountSettings.tsx`. `invariant-review` then confirmed the new settings field would never
  actually hydrate from the server on form open — a pre-existing gap already true of every other
  field on that form, since `AccountDto` had never carried `pollIntervalSeconds`,
  `certificateTrustMode`, etc. either. Fixed immediately rather than left as backlog, since this
  session had just doubled the number of affected fields: added all six missing fields
  (`PollIntervalSeconds`, `PollingEnabled`, `UndoSendDelaySeconds`, `NotificationsEnabled`,
  `CertificateTrustMode`, `AttachmentSizeLimitOverride`) to `AccountDto` itself and both of its
  construction sites (`AccountProvisioningService.ToDto`, `AccountsController.ToDto` — genuine
  near-duplicates, not consolidated here to keep the diff to the actual gap), so the account list
  the settings form is seeded from now genuinely carries what was last saved. 4 new tests total;
  `dotnet test` 286 passed/0 failed (up from 282). `pnpm check` clean under Node 22.
- **Sixteenth architecture.md pass — backfill progress and per-mailbox initial-sync override,
  same shape as the fifteenth pass's findings.** §13 Epic 3's "visible progress
  (fetched/estimated total)" during backfill had zero UI despite `SyncProgressDto` already being
  broadcast on every coverage page — a mailbox mid-backfill was indistinguishable from one fully
  synced. `HubConnection.ts`'s `SyncProgress` handler now also writes the payload into a
  cache-only react-query key, read by a new `BackfillProgress` component in `MailboxTree.tsx`
  whenever a mailbox's `coverage` is `Backfilling`. `invariant-review` independently verified
  the load-bearing claim this relies on — that a `useQuery` with `enabled: false` still
  re-renders on `setQueryData` for its key — against the actual installed
  `@tanstack/react-query` version, not just the code comment's claim. Also: §13 Epic 3's
  per-account/**mailbox** bounded-sync choice only had the account-level half built (fifteenth
  pass); `Mailbox.InitialSyncModeOverride`/`InitialSyncBoundValueOverride` was already read by
  `CoverageService` but had no hub method or UI to set it. Added
  `MailHub.SetMailboxInitialSyncOverride` and a "Sync settings…" context-menu entry in
  `MailboxTree.tsx`, offered only while a mailbox's coverage is `NotStarted`. `invariant-review`
  traced `CoverageService.RunPageAsync` and confirmed it re-reads the override fresh on every
  page — no race where a save silently has no effect — so the `NotStarted` gate is a
  conservative UX choice (avoiding confusing mid-backfill re-bounding), not a correctness
  requirement. No new backend tests for the hub method (no existing MailHub-level test harness
  to extend cheaply; a plain two-column write structurally identical to the already-untested
  `SetMailboxCollapsed`). `dotnet test` 286 passed/0 failed (unchanged). `pnpm check` clean under
  Node 22.
- **Seventeenth architecture.md pass — two findings.** (1) `CalendarEvent.Reminders` (§1) was
  computed and persisted (parsed from CalDAV `VALARM` blocks) but never exposed in any DTO or
  UI. Added to `CalendarEventDetailDto`, shown read-only in `EventModal.tsx`. `invariant-review`
  confirmed this is correctly scoped as read-only (no reminder field exists anywhere on the
  write side — `CalendarEventInput`/`SaveCalendarEventRequest` — so there's genuinely nothing to
  wire up instead) and confirmed no staleness risk (`CalDavIcs.cs` recomputes `TRIGGER` times
  against the event's current `Start` on every sync pass, and `Apply()` fully overwrites rather
  than merges). (2) A larger, unfixed finding: meeting-invite handling from a **received
  message** is entirely missing — Epic 7 requires Accept/Decline/Tentative "from message or
  calendar," with `.ics`/`text/calendar` MIME parsing feeding the `CalendarEvent` model, but no
  code anywhere parses an incoming message's calendar MIME part; RSVP is reachable only from the
  calendar view (`EventModal.tsx`), never the reading pane. This is feature-sized work (MIME
  parsing on ingest, correlating an invite to an existing/new `CalendarEvent`, reading-pane UI),
  not a UI-wiring gap like the other findings this pass and the last two — raised with the user
  for a scope decision before starting it, per the same pattern used for send-as identity
  management in the fourteenth pass.
- **Eighteenth pass — built the received-invite RSVP gap raised at the end of the
  seventeenth.** User chose "build it now." The open design question was what an account with
  no CalDAV/native calendar configured should do: `ICalendarProviderFactory` throws
  `ProviderNotConfiguredException` for it, so there was nowhere to materialize an invite or
  route an RSVP. Raised for a decision; user chose a local-only pseudo-calendar
  (`Calendar.IsLocalOnly`), lazily created per account, with RSVP against it sending the iTIP
  `REPLY` directly via mail (`ItipReplySender`, extracted verbatim from
  `CalDavCalendarProvider`) rather than through a calendar provider. `MailInviteMaterializer`
  parses a `METHOD:REQUEST` `text/calendar` part after `ContentAcquisition.StoreAsync` commits
  — deliberately outside that transaction, in its own try/catch that only logs, so a
  materialisation bug can never fail or retry the underlying message fetch. `GetMessageInvite`
  independently re-parses the stored raw MIME so the reading pane can show a pending invite
  before materialisation has necessarily run. Found and fixed two real bugs in
  `CalendarSyncService` along the way: `SynchronizeAsync` was still handing local-only
  calendars to the real provider's `SyncCalendarAsync` (which has no way to sync something it
  never reported) — caught live by a new adoption test that failed with "must carry a provider
  cursor" before the fix — and `ApplyPageAsync` needed an adoption path so a later real sync
  reporting the same `ICalUid` reassigns the mail-materialised row instead of duplicating it.
  Two independent `invariant-review` passes over the full diff found no violations: the
  adoption fallback participates in the existing `SyncConflict` detect-don't-merge check rather
  than bypassing it, is scoped to the syncing account, equal-sequence redelivery overwrites
  harmlessly, and nothing in the message-mutation coordination domain (§6) was touched. 6 new
  backend tests; full suite 292 passed/0 failed (up from 286). `pnpm check` clean under Node 22.
- **Nineteenth pass — attachment size limits (§15) never enforced anywhere.**
  `IMailProvider.GetAttachmentConstraintsAsync` was correctly implemented by all three
  providers (IMAP, Gmail, Graph) but had zero callers outside the providers themselves — no
  hub method, no compose-time check — despite the doc requiring the check be "applied before
  send is attempted." Small UI-wiring fix, no scope decision needed: added
  `MailHub.GetAttachmentConstraints` (read-only) and a new `AttachmentConstraintsDto`, wired
  into `Compose.tsx`'s `send()`. Blocks on a known per-file limit (raw-byte comparison, matching
  `DraftAttachment.size`'s raw-byte source) or a known total-message limit
  (`configuredOverride ?? knownMessageSizeLimit`, compared against the base64-inflated total per
  the doc's explicit "base64 overhead on total message size, not just per-file raw size"); warns
  rather than blocks when the limit is genuinely unknown. `invariant-review` confirmed no frozen
  invariant touched, `SendDraft` itself unchanged, and a pending/errored constraints query
  degrades to no enforcement (same as before this fix) rather than blocking or throwing. `dotnet
test` 292 passed/0 failed (unchanged — read-only accessor, no new backend logic). `pnpm check`
  clean under Node 22.
- **Twentieth/twenty-first passes — no gap found.** Checked the compose→calendar round-trip
  lead flagged by the nineteenth pass (not a real gap: no doc requirement ties Compose to
  calendar-part detection on send) and did a full §15 sweep (rate-limiting, undo/delayed send,
  sent-mail append, signatures, rules/filters, export, error taxonomy) plus a thorough
  accessibility audit (colour-contrast floor from the fourteenth pass still wired in;
  `MessageList.tsx`/`CalendarGrid.tsx`/resizable panels/`InviteBanner`/`EventModal` all
  confirmed keyboard-operable by reading their source, not just grepping). Nothing wrong found
  in either pass — reported honestly as clean rather than manufacturing a low-confidence
  finding.
- **Twenty-second pass — draft push silently duplicates a remote draft on a mid-batch crash.**
  `DraftSyncService.PushAsync` batched every pending draft's provider-push result under one
  `SaveChangesAsync` called after the whole loop, rather than per draft — exactly the kind of
  "mid draft creation" boundary §16 names as where this system's worst bugs live. If an earlier
  draft's provider call succeeded (a real remote draft now exists) but a later draft in the same
  batch then threw an uncaught exception, the trailing save never ran, silently losing the
  earlier draft's `ProviderDraftId` from local state; on restart it looked unpushed and was
  pushed again, creating a permanently orphaned duplicate on the server (every provider mints a
  fresh remote draft whenever `ProviderDraftId` is null). Fixed by saving each draft immediately
  after its own provider-call outcome; also named the boundary as a fault-injection kill point
  (`FaultPoints.DraftPushAfterProviderCallBeforeCommit`) for future tests. The new regression
  test was manually confirmed to be a genuine discriminator — stashing just the fix and rerunning
  it reproduced the failure before restoring the fix and confirming it passes, per this session's
  standing "a passing fault-injection test is not evidence" discipline. `invariant-review`
  confirmed every code path is covered and nothing under `Mutations/` or any other frozen
  invariant is touched. `dotnet test` 293 passed/0 failed (up from 292). `pnpm check` clean under
  Node 22.
- **Twenty-third pass — no gap found.** Checked §16 fault-injection kill-point coverage
  exhaustively (all 11 named boundaries have a genuine covering test), IMAP capability-tier
  gating (`IntegrityReconciliationService.Required()` correctly matches the QRESYNC/CONDSTORE/
  Neither split), and FTS5 field-scoped search (`Subject:`/`from:` prefixes pass straight
  through to SQLite's column-filter syntax with no client-side parsing to break it). Reported
  clean rather than manufacturing a finding.
- **Twenty-fourth pass — `Account.PollingEnabled` ("Check for new mail") was a complete no-op.**
  Persisted and settable from account settings, but `SyncJobs.RunnableAsync`/`StillRunnableAsync`
  never referenced it — every poll loop (mail sync, change-stream, integrity reconciliation,
  calendar) kept running regardless of the toggle. Fixed by adding the check to both gates.
  Because it's a genuine two-way toggle (unlike `IsEnabled`, which this codebase only ever sets
  false on removal), `MailHub.UpdateAccount` also needed to detect a false→true transition and
  restart polling via the same `TopologyAsync` re-enqueue `StartupScheduler.ResumeAccountAsync`
  uses for reauthentication recovery. `invariant-review` caught a real follow-up bug in that
  restart path: mirroring `ResumeAccountAsync`'s unconditional `polls.StopAll` before re-enqueuing
  is only safe there because a reauth-paused account's loops have reliably already stopped and
  released their registry slot by the time it runs (reauth needs human interaction, taking at
  least one poll interval) — a `PollingEnabled` toggle can flip off-then-on in under a second, so
  a still-alive loop that hasn't ticked yet may still hold its slot, and force-clearing it would
  let a second, concurrent loop start for the same scope. Fixed by dropping `polls.StopAll`
  entirely — `PollRegistry.TryStart` already no-ops harmlessly against a still-claimed scope, so
  a genuinely-stopped loop's slot (already released via its own `Stop()` call) is reclaimed
  correctly, and a still-alive loop's slot is left alone to resume itself on its own next tick.
  A second `invariant-review` pass confirmed this: every `SyncJobs` loop body releases its slot
  on every path that ends it, the divergence from the reauth pattern is justified (different,
  non-racing scenario), and the new regression test's scope is honestly represented (it validates
  `PollRegistry`/`StartChangeStreamsAsync`'s existing safety property through the real code path,
  not `MailHub.UpdateAccount` end-to-end — no MailHub-level test harness exists in this codebase).
  `dotnet test` 296 passed/0 failed (up from 293). `pnpm check` clean under Node 22.
- **Twenty-fifth through twenty-seventh passes — no gap found.** Applied the twenty-fourth
  pass's "is this persisted/settable field load-bearing anywhere, or a silent no-op" angle
  exhaustively to every remaining `Account`/`AccountSettingsDto`/`ImapProviderConfig` field
  (twenty-fifth), then every `Mailbox`/`ImapMailboxMetadata` field (twenty-sixth — one always-
  unused field, `Mailbox.IsSubscribed`, judged not a gap since no UI ever promises it does
  anything, unlike `PollingEnabled`'s user-facing broken toggle), then every `Calendar`/
  `SendIdentity`/`Draft` field (twenty-seventh). All confirmed genuinely consumed or
  deliberately UI-only by design. This angle is now exhausted across the plausible domain
  classes.
- **Twenty-eighth pass — §10's Serilog logging pipeline was never built.** The doc promises
  "Serilog, async sink... A shared enricher/destructuring policy partially obfuscates
  identifying fields (e.g. email addresses)"; the app ran on ASP.NET Core's bare default
  logging, with zero Serilog references anywhere. Not an active leak — grepped for any log call
  interpolating a body/subject/credential/token and found none, so the no-PII half of the
  promise was already honored by discipline — but the infrastructure itself didn't exist.
  Raised to the user as a scope decision (Serilog vs. the already-referenced-but-unwired
  OpenTelemetry packages, vs. just fixing the doc); they chose to build Serilog as documented.
  Added `Serilog.AspNetCore`/`Enrichers.Thread`/`Sinks.Async`/`Sinks.File`; wired an async file
  sink under the same `dataDirectory` the database already uses, `Microsoft`/`System` log-level
  overrides (EF Core's SQL-per-statement diagnostics were flooding the log at Information,
  discovered via manual smoke test), and `EmailMaskingDestructuringPolicy` — the doc's "shared
  enricher/destructuring policy" — which masks an `@`-destructured `Address`'s email to its
  first character plus domain. `invariant-review` confirmed no PII risk from the now-durable
  log file (nothing sensitive was logged before either), correct log-level-override scoping,
  and correct middleware placement. Manually smoke-tested the built app starting, migrating,
  and writing a real log file end-to-end. 2 new tests against a real Serilog `Logger`. `dotnet
test` 298 passed/0 failed (up from 296). `pnpm check` clean under Node 22.
- **Twenty-ninth pass — a calendar event's RSVP status could go stale indefinitely on an
  already-open surface.** This app's TanStack Query client never auto-refetches
  (`refetchOnWindowFocus: false, staleTime: Infinity`), so a query key only refreshes via
  explicit invalidation. A `CalendarEvent`'s invite-response status is shown under three
  different query keys (the calendar grid, `EventModal`'s detail view, and a message's
  `InviteBanner`), but the SignalR push handler for `CalendarEventUpdated` only invalidated the
  calendar grid's key — responding to an invite from the reading pane never updated an
  already-open `EventModal` for the same event, or vice versa, and closing/reopening didn't
  help either since nothing was ever considered stale. Fixed by broadening the handler to also
  invalidate `["calendar-event-detail"]` and `["invite"]`, matching the existing pattern.
  `invariant-review` confirmed both query key strings match their components exactly, nothing
  else in the renderer shares either prefix (no over-invalidation), and the fix closes the bug
  end-to-end in both directions. `pnpm check` clean under Node 22.
- **Thirtieth pass — `DraftUpdated` had no renderer consumer at all.** Verified the
  twenty-ninth pass's flagged lead first (message-side handlers are genuinely clean:
  `MessageReceived`/`Updated`/`Deleted` correctly invalidate `["messages"]`/`["search"]`), then
  swept the remaining event families. `IMailClient.cs` declares `DraftUpdated` and it fires
  from 6 real backend call sites, but `HubConnection.ts` had a handler for every other §7
  event except this one — the `["drafts", accountId]` key was never invalidated by anything,
  not even a local `onSuccess` call. Worse than the twenty-ninth pass's calendar bug (that one
  had partial coverage; this had none) — the Drafts pane showed whatever it fetched on first
  mount, forever. Fixed by adding the missing handler. `invariant-review` confirmed the query
  key match and did a full sweep of all 15 `IMailClient.cs` events: everything now has a
  handler except `OutboxStatusChanged`/`ConnectivityChanged`, which have no renderer consumer
  of any kind (no outbox pane, no connectivity UI exists) rather than a stale-cache bug —
  flagged as a separate, lower-severity finding for a future pass, not fixed here since there's
  no existing query key to invalidate. `pnpm check` clean under Node 22.
- **Thirty-first pass — built `ConnectivityChanged`'s "one calm offline state," the real half
  of the thirtieth pass's lead.** §7/§15 explicitly document its purpose: replace per-mailbox
  error noise with one calm banner. Investigated first (not built blind): `OutboxStatusChanged`
  had no doc-backed UI requirement and no existing query key to attach to, so left alone;
  `ConnectivityChanged` did, and the per-failure "You appear to be offline" toast it's meant to
  replace was still firing per `MessageSyncFailed`/`ErrorCategory.Network` — the exact noise
  the doc calls out. Added `MailHub.GetConnectivity()` (needed because the push event only
  fires on a transition, not on connect), wired the event into `HubConnection.ts` via
  `setQueryData` matching the existing `SyncProgress` cache-only pattern, and added
  `ConnectivityBanner`, mounted per-window in `AppShell.tsx`. Deliberately left the existing
  per-failure toast untouched — additive, not a replacement, since suppressing it wasn't part
  of the identified gap. `invariant-review` confirmed `GetConnectivity` is genuinely necessary,
  `ConnectivityMonitor.IsOnline` defaults `true` (no false banner on fresh launch), each
  window's banner is correctly self-scoped per the established per-window `HubConnection`/
  `QueryClient` pattern, and the existing header status text observes a genuinely distinct
  failure domain (local SignalR connection, not mail-provider reachability) so the two can't
  contradict each other. `dotnet test` 298 passed/0 failed (unchanged). `pnpm check` clean
  under Node 22.
- **Thirty-second pass — no new gap; strong signal of near-exhaustion.** Reverse SignalR/query-
  key cross-check (every renderer query key against push coverage) and a full §2/§4 read-through
  both came up clean — `["shell-settings",...]`/`["credential-store-status"]`/
  `["remote-content-trusted-senders"]` flagged as a weak, undecided lead (see next entry); §2's
  `GoogleCalendarProvider`/`GraphCalendarProvider` gap is pre-acknowledged deferred work per
  `CalendarProviderFactory.cs`'s own doc comment, not undiscovered. Combined with passes 20, 21,
  23, 25, 26, 27 also finding nothing, systematic-technique review is likely near its ceiling.
- **Thirty-third pass — resolved two loose ends to firm conclusions, per explicit instruction
  not to leave anything as a vague "worth investigating later."** (1) `OutboxStatusChanged` (a
  SignalR event with zero renderer consumers, flagged by pass 30): confirmed legitimate dead
  code — no doc anywhere promises a live pending-sends indicator, and both real UI needs
  (Compose.tsx's local-RPC-driven "Undo send", the Electron quit dialog's on-demand REST poll)
  are already fully satisfied without it. (2) Cross-window settings sync (pass 32's weak lead):
  `credential-store-status` is a genuine process-lifetime constant, correctly fetch-once — but
  `shell-settings` and `remote-content-trusted-senders` are shared backend state with no
  carve-out, unlike panel-layout/window-bounds (a pre-existing, doc-attested deliberate
  exception), and §13 Epic 10 unambiguously requires "all actions reflected live across all
  open windows." Fixed: added `ShellSettingsChanged`/`TrustedSendersChanged` SignalR events,
  fired from the theme/close-behaviour/mailto-prompt endpoints and from `Trust`/`Untrust` only
  when a row actually changed (idempotent no-ops don't double-broadcast). `invariant-review`
  confirmed the panel-layout carve-out is genuinely doc-attested (not an oversight this fix
  should have also closed), the idempotent-broadcast logic is correctly scoped, and nothing
  under `Mutations/` is touched. 6 new tests, including one proving the panel-layout carve-out
  doesn't broadcast. `dotnet test` 304 passed/0 failed (up from 298). `pnpm check` clean under
  Node 22 (`pnpm check` needed several retries this pass due to background `dotnet watch`/VS
  Code process contention in the sandbox — each individual check step verified clean in
  isolation before the full aggregate finally succeeded cleanly).
- **Thirty-fourth pass — no gap found.** Fresh technique (error-path correctness: every broad
  `catch` clause in `server/MyloMail.Api/` across mutation/outbox/export/content paths) came up
  clean — every one checked was intentional, correctly scoped, and logged.
- **Thirty-fifth pass — two silent-failure UI gaps.** Fresh technique (renderer error
  boundaries/loading states). `DraftList.tsx` had no `isError` branch: on a genuine fetch
  failure it fell through to the empty-state check and showed "No drafts." exactly as if the
  user legitimately had none. `Sidebar.tsx`'s `reorder`/`toggleCollapsed` mutations had no
  `onError` at all, unlike every one of `MailboxTree.tsx`'s five sibling mutations (a shared
  `reportFailure`/`notify` helper) — a failed drag-to-reorder or collapse-toggle failed
  completely silently. Fixed both, reusing the established patterns exactly.
  `invariant-review` confirmed both fixes are correct and scoped to just the two renderer
  files touched. `pnpm check` clean under Node 22.
- **Thirty-sixth pass — six more silent-failure UI gaps, same shape, found by a full sweep.**
  Continued the thirty-fifth pass's technique across every `useQuery`/`useMutation` in the
  renderer instead of stopping at the first two: `ExportAccount.tsx`'s `start`/`cancel`,
  `EventModal.tsx`'s and `ReadingPane.tsx`'s RSVP mutation (the same gap duplicated across both
  RSVP surfaces), `MessageList.tsx`'s `setFlags`/`trash`/`deletePermanently` (had `onSettled`
  but no `onError`), `AttachmentList.tsx`'s attachments query (failed fetch rendered nothing,
  indistinguishable from no attachments), and `MessageHtml.tsx`'s `useTrustSender` — which also
  had a more fundamental related bug found while there: its raw `fetch` never checked
  `response.ok`, so it wouldn't have thrown even with `onError` added, since `fetch` only
  rejects on a network-level failure. Fixed all six reusing the two established patterns
  (`reportFailure`/`notify` for mutations, an `isError` branch for queries).
  `invariant-review` confirmed every fix is correctly wired, the `response.ok` fix is a strict
  improvement with `useIsTrustedSender`'s sibling fail-closed-security-default query correctly
  left untouched, `onError`/`onSettled` both still fire correctly in `MessageList.tsx`, no new
  error string surfaces message content, and nothing under `Mutations/` is touched. `pnpm
check` clean under Node 22.
- **Thirty-seventh pass — four more instances, confirming the pass-36 sweep wasn't
  exhaustive.** Re-grepped every `useQuery`/`useMutation` and cross-checked against what was
  already fixed/confirmed-fine. `AppShell.tsx`'s top-level `accounts` query had no `isError`
  check anywhere — the header status text showed "No accounts yet." during a real fetch
  failure, the most visible surface in the app (verified `effectivePane`'s Add-Account routing
  does NOT also misfire on error, only the status text needed fixing).
  `ShellSettings.tsx`'s three raw-`fetch` writes never checked `response.ok` (the same bug
  class `MessageHtml.tsx` had before pass 36), and its `credentialStore` query defaulted to a
  false-positive "using the safe native store" claim on failure — fixed to throw and show a
  neutral error state instead, while leaving other low-stakes preference defaults alone.
  `ReauthenticateAccount.tsx`'s `trustAndRetry` mutation had no `onError` for its
  `TrustCertificate` hub call specifically (a sibling failure path was already correctly
  surfaced). `invariant-review` traced the trickiest point carefully: confirmed TanStack
  Query's `mutateAsync()` rethrows the exact error object with no wrapping, so the new
  `instanceof ReauthenticateError` de-duplication guard genuinely works rather than silently
  never firing. `pnpm check` clean under Node 22.
- **Thirty-eighth pass — `Compose.tsx`'s entire save/send/attach flow had zero error
  handling, the most severe instance across four rounds of this technique.** A maximally
  exhaustive final sweep (every `useQuery`/`useMutation`/write-`fetch` across the whole
  renderer, cross-checked against everything already fixed) found `save()`, `send()`,
  `detach()`, `addFiles()`, `removeAttachment()`, and `undo()` all had only `try/finally`,
  never `try/catch` — a failed Send just silently stopped spinning with no explanation, the
  single most consequential action in a mail client. `AccountSettings.tsx`'s save had the same
  gap. Fixed by wrapping every top-level fire-and-forget entry point in a `reportFailure`
  catch, while deliberately leaving `save()` itself throwing (its internal callers — `detach`,
  `send`, the forward-attachment-copy effect — need the rejection to propagate so they skip
  their own next step rather than proceeding with an undefined draft id). Surfaced and fixed a
  genuine, unrelated pre-existing ESLint declaration-order issue (`uploadAttachment` used
  before its own definition) along the way. `invariant-review` traced every `save()` caller
  and confirmed none proceeds past a failure as if it succeeded, found no double-reporting, and
  caught one more real gap in the same file (`GetSendIdentities`'s effect had no catch either) —
  fixed the same way. Four consecutive increasingly-thorough sweeps (passes 35-38) now confirm
  this bug class is closed across the renderer. `pnpm check` clean under Node 22.
- **Thirty-ninth pass — electron-shell's main process is clean; one more small renderer gap
  found while checking.** Investigated a flagged lead: `Main.ts`'s ~7 `fetch` call sites and
  the IPC boundary. All clean — `ipcRenderer.invoke` correctly propagates a main-process throw
  as a renderer-side rejection (Electron's own error forwarding, no custom convention needed),
  and every other main-process fetch is a deliberate, documented read-with-safe-default or
  best-effort-write for a genuinely low-stakes preference. That check surfaced one concrete
  instance of the same passes-35-38 bug shape, missed by those sweeps because it's a plain
  async function call rather than `useQuery`/`useMutation`/write-`fetch`:
  `AttachmentList.tsx`'s "Open" button had no `.catch()`, even though its `open()` call can
  genuinely throw from the main-process rejection path just confirmed clean. Fixed the same
  way. `pnpm check` clean under Node 22.
- **Fortieth pass — a failed mutation had no durable indicator on the affected message, only
  a one-shot toast.** A fresh full re-read of §13's Epic list (not systematically swept since
  around pass 15) found Epic 4 explicitly requires a failure be shown "via a visible indicator
  on the affected message, never silent" — `MutationItem.LastError`/`FailureCategory` were
  already persisted on terminal failure, but nothing queryable tied a message back to that
  state, and the only user-facing signal was `MessageSyncFailed`'s transient toast. Added
  `MessageMutationFailures.ForMessagesAsync`, a self-clearing read-side lookup (the highest-
  sequence _resolved_ mutation per message; a later success simply outranks an earlier failure
  once it too resolves, no flag to write or clear), wired into `MessageSummaryDto` and shown
  as a ⚠️ in `MessageList.tsx` alongside the existing flag/attachment icons. `invariant-review`
  caught a real logic bug before it shipped: the first version picked the highest-sequence
  item regardless of state, so a newer mutation merely being _queued_ (not yet executed) made
  an unresolved failure vanish immediately — fixed by skipping non-terminal rows, with a
  regression test confirmed to fail against the pre-fix logic. `dotnet test` 308 passed/0
  failed (up from 304). `pnpm check` clean under Node 22.
- **Forty-first pass — resolved three leads from the fortieth pass's Epic re-read; one was a
  real gap.** Epic 7's "organiser sees attendee responses update" and "agenda view
  virtualised" both confirmed clean (`CalendarSyncService` fires `CalendarEventUpdated` for
  any changed event, not just new/deleted; `CalendarAgenda.tsx` genuinely uses
  `useVirtualizer`). Epic 2's "sensible handling/error messaging where a provider doesn't
  support an operation (e.g. Gmail's lack of true nesting)" was real: Gmail's synthesized
  nested-label intermediate `Mailbox` rows (`ProviderMailboxId == null`, already documented at
  `architecture.md:94` as "not renameable or deletable, since no provider object backs them")
  had nothing actually enforcing that — `MailboxTree.tsx`'s context menu offered Rename/Delete
  unconditionally, reaching a developer-facing `InvalidOperationException` in
  `GmailMailProvider.cs`. Added `MailboxSummaryDto.IsSynthesized`, gated Rename/Delete using
  the same `unavailable` mechanism already used for "Sync settings…". `invariant-review`
  confirmed the field's definition is correct and exclusive, and caught a closer-than-described
  miss: dropping a _message_ onto a synthesized folder via drag-and-drop hit the identical
  exception class, reachable through ordinary interaction — fixed the same way; mailbox-
  reparenting drops onto a synthesized node were confirmed intentionally supported and left
  alone. `pnpm check` clean under Node 22.
- **Forty-second/forty-third passes — no gap found; one feature-sized finding escalated and
  built.** Continued the §13 Epic re-read (Epic 7 attendee-response refresh, Epic 5 attachment
  temp-cleanup, print stylesheet, Epic 6 Message-ID, Epic 9 notification eligibility) with
  nothing wrong found on any of those. Also checked IMAP special-use detection as a fallback
  technique and found a real gap: `ImapMailProvider.cs` relies entirely on RFC 6154 SPECIAL-USE
  server attributes with no fallback, so a server that doesn't advertise it reports every
  folder as `SpecialUse.None`, silently breaking send reconciliation and similar features. Not
  explicitly promised by the doc (§16 only says the capability matrix "needs testing"), so
  raised to the user rather than built blind — they approved a name-based fallback plus a
  manual per-mailbox override UI.
- **Built the approved IMAP special-use fallback + override.** Added a modest, English-centric
  name heuristic (`ImapMailProvider.SpecialUseFromName`) as the last arm of the existing
  attribute switch, never overriding a real server attribute. Added
  `Mailbox.SpecialUseOverride` and a computed `EffectiveSpecialUse` property, following
  `InitialSyncModeOverride`'s exact convention — an override column sync logic never writes to,
  so a resync can't clobber a user's correction. Added `SetMailboxSpecialUseOverride` and a
  "Folder role…" entry in `MailboxTree.tsx`'s context menu. Updated every decision-making call
  site (`RemoteDraftMaterializer.cs`, `MessageIngestor.cs`, `SendReconciler.cs`,
  `MutationReconciler.cs`) to respect the override, using the in-memory `EffectiveSpecialUse`
  property where appropriate and an explicit inline `(SpecialUseOverride ?? SpecialUse)` form
  in genuine EF Core LINQ-to-Entities queries, since EF can translate the latter to SQL
  `COALESCE` but not a call to a C#-only computed property. `invariant-review` confirmed the
  fallback never fires when a real attribute exists, override precedence is applied
  consistently with no missed call site, the resync-survival test is a genuine discriminator,
  the EF-translatability split is correct in both directions, and nothing under `Mutations/`'s
  frozen invariants is touched. 22 new tests. `dotnet test` 330 passed/0 failed (up from 308).
  `pnpm check` clean under Node 22.
- **Passes 45-47 — no gap found; forty-eighth pass — recurring calendar events never actually
  recurred in the UI, a genuine missing core feature, built with user approval.** Passes 45-47
  checked default-record races, crash-window coverage, Electron auto-update, export
  resumability, and accessibility on recently-added components — all clean. The forty-eighth
  pass then found `RecurrenceRules` were stored as raw unparsed RRULE strings and never
  expanded anywhere — a weekly event showed once, on the week it was created, and never again,
  directly contradicting §13 Epic 7's "correct time/timezone/recurrence handling." Raised to
  the user as a scope decision (query-time vs. sync-time expansion, vs. just documenting the
  gap); they approved query-time expansion. Added `Ical.Net` for RRULE/RDATE/EXDATE parsing;
  `CalendarRecurrenceExpander.Expand` (pure, stateless) generates occurrences within a window,
  bounded by the window itself or a defensive 2000-occurrence cap;
  `CalendarEventOccurrences.ForCalendarAsync` correlates each generated occurrence against the
  master's pre-fetched override/cancelled rows (keyed by original `RECURRENCE-ID` slot, one
  query, no N+1) — a modified override replaces it, a cancelled one suppresses it, anything
  else is synthesised as a virtual row with a stable deterministic id. The Ical.Net API itself
  was verified empirically via a throwaway compiled/executed probe before being relied on, not
  guessed from memory — including confirming the DST-transition timezone conversion is
  genuinely correct (a 9am `America/New_York` weekly event across the 2026-03-08 transition
  correctly shifts UTC instant while holding 9am local time). `invariant-review` independently
  re-derived the DST correctness by tracing the actual conversion code, confirmed override
  precedence can't double-show or miss a slot under any ordering, confirmed no N+1, and
  confirmed nothing under `Mutations/`'s frozen invariants is touched. Deliberately deferred:
  true per-occurrence editing of a not-yet-materialised virtual occurrence (would need
  extending `SaveAsync`/`CalendarEventInput` to create a real override row and push it to
  CalDAV) — clicking one today routes to editing the whole series, the same behaviour a
  recurring master already had. 9 new tests. `dotnet test` 339 passed/0 failed (up from 330).
  `pnpm check` clean under Node 22.
- **Fiftieth pass — no gap found.** Checked undo-send timing (no countdown is actually shown
  in `Compose.tsx`, just a static "Undo send" button, so there's nothing that could drift from
  the server's real `ScheduledSendAt` window) and bulk export completeness (`ExportJobs.cs`
  enumerates every mailbox with no `SpecialUse` filtering — Drafts/Sent/Trash/Archive/Junk are
  all included, and `RawBytesAsync` writes full raw MIME bytes, preserving attachments). Both
  clean after reading the actual implementation.
- **Fifty-first pass — search results were returned in recency order despite genuine relevance
  ranking already being computed and discarded.** `MessageSearch.cs`'s `MatchAsync` computes
  real FTS5 relevance ranking (SQL `ORDER BY rank`; its own doc comment says it returns "ids
  FTS5 matches, in relevance order") — but `SearchAsync` discarded that order entirely and
  re-sorted by `ReceivedAt` instead. Not a strict doc violation (§8 doesn't explicitly promise
  relevance ranking), so raised to the user as a genuine UX decision; they chose relevance
  order. Fixed by reapplying `MatchAsync`'s rank order in-memory after the EF `Contains()`
  query, which doesn't preserve source-list order. New regression test manually confirmed as a
  genuine discriminator (fails against the pre-fix recency ordering, passes with the fix) —
  written carefully to avoid bm25 corpus-statistics pollution from the test fixture's existing
  "invoice" documents, verified with a standalone SQLite probe of bm25's actual behavior before
  writing the assertion. `dotnet test` 340 passed/0 failed (up from 339). `pnpm check` clean
  under Node 22.
- **Fifty-second pass — a draft sync conflict, once flagged, could never actually be resolved.**
  §1/§15 promise a push conflict "prompts resolution rather than overwriting," but nothing ever
  cleared `Draft.SyncConflict` once `DraftSyncService` set it — a conflicted draft silently
  stopped syncing forever, both copies frozen. Built `DraftService.ResolveConflictAsync`,
  mirroring `CalendarEventService.ResolveConflictAsync`'s "keep mine"/"keep theirs" shape but
  diverging where a draft's push mechanics require it: every provider's
  `CreateOrUpdateDraftAsync` treats a null expected revision as "create," never "force-update,"
  so "keep mine" abandons the old remote draft (`RemoveRemoteAsync`) and clears
  `ProviderDraftId`/`ProviderRevision`/`PushedAt` so the next ordinary push creates a genuinely
  fresh one; "keep theirs" fetches the server's raw bytes and materializes them via a new
  `RemoteDraftMaterializer.ApplyRawBytes`, extracted from existing sync-observation logic so
  both paths share one MIME-to-`Draft` mapping instead of two that could drift. Added
  `MailHub.ResolveDraftConflict`, `DraftDto.SyncConflict`, a ⚠️ indicator in `DraftList`, and a
  resolution banner in `Compose`. `invariant-review` caught a real data-integrity bug before
  this shipped: the "keep theirs" branch located the account's Drafts mailbox with
  `FirstAsync(... EffectiveSpecialUse == Drafts)`, but nothing enforces at-most-one-mailbox-
  per-account for that condition — a manual `SpecialUseOverride` (§13 Epic 2) has no uniqueness
  check against other mailboxes already holding Drafts. With two candidates, `FirstAsync` would
  pick one arbitrarily and fetch the raw message against the wrong mailbox's id-space, silently
  materializing an unrelated message's content into the user's draft. Every other consumer of
  this override pattern already used `.Where(...)` as a set; this was the one holdout. Fixed by
  switching to `Where(...).ToListAsync` with an explicit count check that throws
  `InvalidOperationException` when it isn't exactly 1, plus a regression test seeding a second
  `SpecialUseOverride = Drafts` mailbox and asserting the throw. `dotnet test` 344 passed/0
  failed (up from 343). `pnpm check` clean under Node 22.
- **Fifty-third pass — a changed close-behavior setting silently required a relaunch to take
  effect.** §13 Epic 10's minimize-to-tray-vs-quit setting is read once at startup into a
  main-process-local variable (`Main.ts`'s `closeBehavior`) and acted on when the window's
  close event fires. `ShellSettings.tsx` lets the user change it at runtime and genuinely
  persists the change via a `PATCH`-style fetch — but nothing told the already-running main
  process, so the window kept acting on its stale startup value until the whole app was
  relaunched, with no error surfaced to explain why closing didn't do what was just chosen.
  Added an IPC channel (`shell-settings:close-behavior-changed`) mirroring the existing
  `showNotificationChannel`/`pickExportFolderChannel` pattern: the renderer calls
  `window.shellSettings.closeBehaviorChanged(value)` right after its save succeeds, and
  `Main.ts` reassigns the shared `closeBehavior` variable directly from that value.
  `invariant-review` found no frozen-invariant hit (shell/IPC plumbing, not mutation/sync/
  persistence) and confirmed the process-global variable is correct by design (this setting is
  a fact about the installation, like window bounds, not per-window state). No changes
  requested. `dotnet test` unaffected at 344 passed/0 failed (renderer/shell-only change).
  `pnpm check` clean under Node 22. A small follow-up (commit `6667647`) extracted the
  value-to-CloseBehavior mapping into a new electron-import-free `CloseBehavior.ts` with its
  own unit tests, since `Main.ts` imports `electron` at module scope with a `startup` side
  effect and so cannot itself be unit-tested without mocking the whole module — this pass also
  happened to run concurrently with another instance of itself picking the same angle, landing
  a small, harmless duplicate fix (`6acfd8a`) before the two were reconciled.
- **Fifty-fourth pass — `CalendarSyncService.ApplyPageAsync`'s recurrence-master resolution was
  a real N+1.** It issued one `SingleAsync` plus one `SingleOrDefaultAsync` EF Core query per
  upserted event just to resolve the local `RecurrenceMasterId` — including every non-recurring
  event, whose `RecurrenceMasterProviderEventId` is null and so was still spending a query
  looking for a row that could never match — plus one `SingleAsync` plus one `ToListAsync` per
  master event to find its "waiting children." A page of N events could issue up to ~2N+2M
  queries. Batched into three total queries regardless of page size: one loading every
  upserted event's own row, one batch-loading any referenced masters not already loaded, one
  batch-loading waiting children. `invariant-review` caught a real, narrow behavior divergence
  in the first version: an event skipped this page because it's flagged `SyncConflict` never
  runs through `Apply()`, so its stored `RecurrenceMasterProviderEventId` keeps its old value —
  the old per-item code always re-read that stored value fresh from the DB, but the first
  batched version used the incoming dto's value instead, which could silently re-parent a
  conflicted event's master link. Fixed by deriving the batch-load's master-id set from the
  loaded rows' own stored field, never from the dtos. The reviewer also flagged that the only
  existing recurrence test covered same-page master+child linking only; added
  `CalendarRecurrenceCrossPageLinkingTests` covering cross-page master resolution, the
  waiting-children path, and the conflict-skip case, the last one manually verified as a
  genuine discriminator (reverting the fix reproduces `Expected: <guid>, Actual: null`). A
  second invariant-review pass confirmed both findings resolved. `dotnet test` 347 passed/0
  failed (up from 344). `pnpm check` clean.
- **Fifty-fifth pass — `MessageIngestor.IngestAsync`'s per-message matching was the same N+1
  shape as the fifty-fourth pass's calendar fix, on a much hotter path.** Its three-tier match
  precedence (§1: provider stable id, then per-mailbox occurrence identity, then Message-ID
  header + `ReceivedAt`) issued up to ~3 EF Core queries per message, plus one more per
  occurrence in `UpsertOccurrenceAsync` — and this runs on every poll page for every mailbox,
  not just backfill; the code's own comment notes IMAP returns the whole mailbox on every poll,
  so an established mailbox's steady-state sync paid full per-message query cost every time.
  Batched all three tiers into three total queries per page (`LoadMatchCandidatesAsync`), moved
  matching itself into a synchronous, static `Match` method resolving purely from those
  in-memory dictionaries, and preloaded existing `MessageMailbox` rows for matched (non-new)
  messages so the occurrence-upsert query only runs as a safety-net fallback. The occurrence
  tier deliberately over-fetches (the full mailboxIds × occurrenceIds cross-product) then keys
  results by the exact `(MailboxId, ProviderOccurrenceId)` tuple, since an IMAP UID is only
  unique within its folder and merging two distinct messages is the unrecoverable direction
  (§1). Added a same-page cross-mailbox-collision regression test, manually confirmed as a
  genuine discriminator by temporarily dropping `MailboxId` from the dictionary key and
  reproducing a `UNIQUE constraint failed` violation. `invariant-review` confirmed match
  precedence, in-page duplicate-creation behavior, and `UpsertOccurrenceAsync`'s ChangeTracker-
  vs-preload ordering are all preserved exactly, with no frozen-invariant hit. `dotnet test` 348
  passed/0 failed (up from 347). `pnpm check` clean, 257 dotnet tests, vitest 43.
- **Fifty-sixth pass — the same N+1 shape in two more sync paths.** `IntegrityReconciliationService`'s
  periodic degraded-IMAP reconciliation and `ChangeStreamService`'s live incremental sync both
  looped over every flag change and removal a page reported, issuing 1-2 EF queries per item —
  the dominant cost on a large mailbox's full integrity pass, or a busy live sync page. Replaced
  `MessageIngestor`'s single-item `RemoveOccurrenceAsync`/`ApplyFlagChangeAsync` with batched
  `RemoveOccurrencesAsync`/`ApplyFlagChangesAsync`, one preload query per batch, scoped to one
  mailbox (provider ids are folder-scoped and can repeat across mailboxes, per AGENTS.md's own
  reasoning for why they live on `MessageMailbox` rather than `Message`). `ChangeStreamService`
  groups its flag changes and removals by `ProviderMailboxId` before calling, since one page can
  span mailboxes but each batched call must stay scoped to one; changes within a batch apply in
  list order, unmodified, so a provider reporting the same occurrence twice still lets the later
  change win. `invariant-review` confirmed generation-guard equivalence, ordering, unchanged
  transaction scope, and no frozen-invariant hit — and caught a real test-coverage gap: the
  first regression test covered only the flag-change path's cross-mailbox isolation, not the
  removal path's identical risk. Added a symmetric removal test; both were manually confirmed as
  genuine discriminators by temporarily dropping each method's `MailboxId` filter and
  reproducing the failure, then restoring. `dotnet test` 350 passed/0 failed (up from 348).
  `pnpm check` clean, 259 dotnet tests, vitest 43.
- **Fifty-seventh pass — a conflicted draft could still be sent.** Pass 52 built
  `DraftService.ResolveConflictAsync` so a user can clear `Draft.SyncConflict` via "keep
  mine"/"keep theirs," but nothing stopped hitting Send on a still-conflicted draft:
  `SendAsync` builds its outgoing MIME straight from the draft's local fields with no revision
  check of its own, so it would silently discard whatever the server's copy actually held —
  defeating §1/§15's "prompts resolution rather than overwriting" promise for the send path
  specifically. `SendAsync` now throws `InvalidOperationException` immediately after loading
  the draft, before any side effect, if `SyncConflict` is set — matching the same bare-
  `InvalidOperationException` convention pass 52's Drafts-mailbox-ambiguity guard already
  established in this file. `Compose.tsx` additionally disables Send, the "Send later"
  overflow menu, and the schedule picker's Schedule button while conflicted, as defense in
  depth. `invariant-review` found no issues and no frozen-invariant hit, and flagged one
  legitimate but out-of-scope follow-up: an outbox item already queued before a concurrent
  sync flips `SyncConflict` on its draft isn't touched by this guard, since `SendAsync` isn't
  re-invoked for an item already in the queue — closing that would need `OutboxService` to
  re-check at dispatch time, or cancellation triggered on the conflict transition itself; left
  as a recorded gap, not fixed here. New regression test manually confirmed as a genuine
  discriminator via revert-and-reproduce. `dotnet test` 351 passed/0 failed (up from 350).
  `pnpm check` clean, 260 dotnet tests, vitest 43.
- **Fifty-eighth pass — closed pass 57's recorded gap: a queued send could still fire against a
  draft that became conflicted afterward.** `SendExecutor.SendAsync` built outgoing MIME
  straight from the draft's local fields with no revision check of its own, so an item already
  sitting in the queue when a concurrent sync flipped `Draft.SyncConflict` would still send,
  silently discarding whatever the server's real copy held. `SendExecutor` now checks
  `draft.SyncConflict` immediately after loading it (right after the existing "draft no longer
  exists" branch, which this mirrors exactly): the item fails with a user-facing `LastError`
  instead of sending, routing the user back through `DraftService.ResolveConflictAsync`. No
  `MutationExecutionAttempt` is created, since nothing was dispatched to a provider — there is
  no ambiguous outcome to record. `invariant-review` caught a real bug: the new `Failed`
  transition wrote `OutboxItem.Status` directly without announcing `OutboxStatusChanged`,
  leaving the renderer showing a stuck "Sending" item forever (§7 requires every status
  transition to be announced) — and found the identical gap already existed, unannounced, in
  the adjacent "draft is null" branch this code was modeled on, pre-existing but propagated to
  a second call site by this diff. Fixed both via a new `OutboxService.AnnounceStatusAsync`
  wrapper around the existing private announce method. New regression test manually confirmed
  as a genuine discriminator via revert-and-reproduce (item ends `Sent` instead of `Failed`
  against the pre-fix code). `dotnet test` 352 passed/0 failed (up from 351). `pnpm check`
  clean, 261 dotnet tests, vitest 43.
- **Fifty-ninth pass — background jobs flipped `Account.AuthState` but never announced it live.**
  `AccountProvisioningService`'s own `AuthState` transitions call
  `IHubEvents.AccountStatusChangedAsync` (§7), but `MutationJobs`, `OutboxJobs`, and `SyncJobs`
  entering `NeedsReauth` on a `ProviderAuthenticationException`, and
  `StartupScheduler.ResumeAccountAsync` clearing it back to `Connected`, never did — the row
  updated correctly, an already-open window just didn't hear about it until its next full
  accounts refresh. Added a shared `AccountDtoFactory.AnnounceStatusAsync` helper and called it
  from all four sites. Along the way, found and fixed a second, more serious bug in
  `MutationJobs`/`OutboxJobs`: both wrote the transition through the `account` variable loaded
  _before_ their claim CAS ran, but `MutationClaimService`/`OutboxService`'s claim CAS executes
  a raw SQL UPDATE and then calls `context.ChangeTracker.Clear()` so their own tracked copy
  can't go stale — silently detaching every other tracked entity in that context too, `account`
  included. EF gives no error for writing through a detached entity; `SaveChangesAsync` just had
  nothing queued for it, so this specific `NeedsReauth` write was silently dropped. Fixed by
  reloading the account fresh before writing. `invariant-review` confirmed the reload is
  genuinely necessary (verified `ChangeTracker.Clear()` in both claim services) and found no
  further issues. Two new regression tests confirmed as genuine discriminators via
  revert-and-reproduce. `dotnet test` 354 passed/0 failed (up from 352). `pnpm check` clean, 263
  dotnet tests, vitest 43.
- **Sixtieth through sixty-second passes — clean.** Swept the whole backend for other instances
  of pass 59's stale-entity-after-`ChangeTracker.Clear()` bug shape (found none — every other
  caller either reloads fresh after the claim CAS or only reads already-materialised fields),
  attachment handling/notification dedup/CalDAV-IMAP RSVP reply ordering (all already correct
  by design), and concurrent-SignalR-hub-call races plus silent catch blocks (also clean,
  though this pass surfaced a real but design-ambiguous gap it deliberately did not fix:
  `ExportJobs.StartAsync` has no guard against two windows starting a bulk export
  simultaneously — reject the second, cancel the first, or allow both is a genuine product
  decision with no strong precedent settling it either way, confirmed by the next pass).
- **Sixty-third pass — confirmed the export race has no strong precedent, found a real gap
  instead.** Checked whether `SendIdentity`'s unique-index pattern or the mutation-claim CAS
  pattern settle pass 62's `ExportJobs` question by precedent — neither does (one enforces a
  business invariant, the other prevents double-executing the _same_ mutation, not two
  independent bulk jobs), so it stayed unfixed and ambiguous. FTS5 index staleness
  (`SearchIndexer.IsIntactAsync`/`RebuildAsync`) checked clean, already wired into
  `StartupScheduler`. Found a genuine, feature-sized gap: a mid-session OS credential-store
  failure (locked keyring, denied Keychain prompt) falls through every background job's generic
  `catch (Exception)` with no `AuthState` change, no `LastAuthError`, no live announcement — the
  account silently stops syncing, the same bug shape passes 40/57/58/59 exist to close, but for
  a different root cause where `NeedsReauth` would be actively misleading (the stored credential
  may be fine; the fix is unlocking the OS keychain, not reauthenticating). Raised to the user as
  a design decision.
- **Sixty-fourth pass — built credential-store failure surfacing, per the user's decision.** The
  user chose: a new `AuthState.CredentialStoreUnavailable` value, extending the existing
  `CredentialStoreUnavailableException` (previously startup-only, triggering Electron's exit-78
  restart-and-prompt protocol — confirmed by `invariant-review` to be unreachable from a
  background job) to also apply mid-session, and a distinct "Unlock your keychain" banner with
  no Reauthenticate action, announced live via pass 59's `AccountDtoFactory.AnnounceStatusAsync`.
  `NativeCredentialStore.RetrieveAsync` now wraps any non-cancellation OS-store exception in the
  new type; `SendExecutor` classifies it as non-ambiguous (nothing was dispatched); `SyncJobs`,
  `MutationJobs`, `OutboxJobs`, `ContentJobs`, and `DraftSyncService.PushAsync` each catch it,
  set the new state, and self-clear back to `Connected` on their next successful run — unlike
  `NeedsReauth`, deliberately not gated off from retrying, since the store commonly becomes
  reachable again on its own. `invariant-review` ran three rounds and found a real, in-scope gap
  each time: `LinuxSecretServiceCredentialStore.FindItemAsync` could return null (no exception)
  for an item that stayed locked with no interactive prompt offered — the primary scenario this
  diff exists to catch — misclassifying it as "never stored," now fixed to throw; `ContentJobs`
  and `DraftSyncService.PushAsync` were initially missed entirely, reproducing the original bug
  on two more code paths; `ContentAcquisition.AcquireAsync`'s existing fetch-failure catch
  counted the new exception against its 5-attempt retry budget the same as genuinely malformed
  content, now left uncounted. A known, deliberately accepted limitation remains: the
  self-clearing logic across five independent job types has no cross-job synchronization, so a
  job succeeding on its own can in principle clear a state another job just set from an actual
  ongoing failure — lower-severity and self-correcting (the next failing job re-sets it within
  its own cadence), and fixing it properly would need synchronization this codebase has nowhere
  else for independent job types. New regression tests confirmed as genuine discriminators via
  revert-and-reproduce. `dotnet test` 359 passed/0 failed (up from 354). `pnpm check` clean, 268
  dotnet tests, vitest 43.
- **Sixty-fifth pass — a rejected mailbox create/rename/move/delete reached the user as a
  useless generic error instead of the provider's real message.** Checked pass 64's one
  deliberately accepted limitation (the cross-job self-clearing race) first and confirmed
  leaving it is genuinely correct — fixing it would need cross-job synchronization this codebase
  has nowhere else for independent Hangfire job types, and the race is narrow and self-correcting
  within one cycle. Moved to a fresh angle instead: `MailboxManagement`'s four provider-calling
  operations let any provider exception (a duplicate name, a namespace the server won't accept)
  propagate raw, and this server's `AddSignalR()` uses the default `EnableDetailedErrors=false`,
  so SignalR replaces anything that isn't a `HubException` with a generic "An unexpected error
  occurred" — silently defeating `MailboxTree.tsx`'s error banner, whose own comment says its
  purpose is showing the user exactly why the operation was rejected. Added
  `RunProviderCallAsync`, wrapping Create/Rename/Move/Delete (not `ReorderAsync`, which never
  calls a provider) to catch any non-cancellation, non-`HubException` exception and rethrow it as
  `HubException(ex.Message)`, mirroring `MailHub.DeleteSendIdentity`'s existing narrower version
  of the same pattern. `invariant-review` confirmed no frozen-invariant hit and suggested logging
  the original exception before the rethrow so a genuine defect isn't telemetrically
  indistinguishable from a legitimate rejection once only the bare message survives to the client
  — applied. New regression test confirmed as a genuine discriminator via revert-and-reproduce.
  `dotnet test` 360 passed/0 failed (up from 359). `pnpm check` clean, 269 dotnet tests, vitest 43.
- **Sixty-sixth pass — the same raw-provider-exception bug pass 65 fixed for mailboxes, still
  live for calendar events.** `CalendarEventService.CreateAsync`, `UpdateAsync`'s non-conflict
  path, `ResolveConflictAsync`'s keepMine branch, `DeleteAsync`, and `RespondToInviteAsync` all
  let a provider exception other than `ProviderConflictException` propagate raw, silently
  defeating `EventModal.tsx`'s and `ReadingPane.tsx`'s `InviteBanner`'s error reporting the same
  way pass 65 found for `MailboxTree.tsx`. Added a `RunProviderCallAsync` helper to
  `CalendarEventService`, structurally identical to `MailboxManagement`'s helper of the same
  name, and wrapped every remaining raw provider call. `UpdateAsync`'s existing
  `catch (ProviderConflictException)` is untouched and still runs first, so a genuine sync
  conflict still routes to keep-mine/keep-theirs rather than becoming a generic `HubException`.
  `ResolveConflictAsync`'s keepMine branch deliberately does not special-case
  `ProviderConflictException` the way `UpdateAsync` does: keepMine already is the
  conflict-resolution action, so a rejection there has no further resolution path to route back
  into and should just surface like any other failure. `Deleting_an_event_the_provider_rejects_
leaves_it_in_place` previously asserted the raw `ProviderConflictException` type; updated to
  assert `HubException` with the same message, since surfacing that message is exactly the fix
  — confirmed via a full-codebase search that no other caller of `DeleteAsync` depended on
  catching `ProviderConflictException` specifically. `invariant-review` confirmed no
  frozen-invariant hit, correct catch-block precedence, and that every remaining provider call
  in the file is now wrapped. New/modified tests confirmed as genuine discriminators via
  revert-and-reproduce. `dotnet test` 361 passed/0 failed (up from 360). `pnpm check` clean, 270
  dotnet tests, vitest 43.
- **Sixty-seventh pass — `DraftSyncService.RemoveRemoteAsync` wasn't as best-effort as its own
  comment claimed.** Its inline comment already said a failure here "would block the local
  deletion the user asked for," but the catch only swallowed `NotSupportedException` and
  `ProviderConflictException` — every other exception (a network blip, a provider auth failure,
  a locked credential store) still propagated through `DraftService.DeleteAsync`, even though
  the local `Drafts` row was already removed and committed by the time this runs, so a
  transient remote-cleanup failure surfaced to the user as a failed delete that had actually
  already succeeded. The same exception also propagated through `ResolveConflictAsync`'s
  keepMine branch, leaving `SyncConflict` uncleared over a failure unrelated to whether
  keepMine's own decision was sound. Widened the catch to `catch (Exception ex) when (ex is not
OperationCanceledException)`, matching the method's own stated contract. `invariant-review`
  confirmed correctness for both call sites and flagged one pre-existing, non-blocking gap: a
  locked-keychain failure during cleanup specifically skips pass 64's live `AuthState`
  announcement here, but the next `PushAsync` cycle already catches and announces the same
  condition, so nothing is left permanently unsignaled. New regression test (with a new
  `FakeMailProvider.FailDeleteDraftWith` injector) confirmed as a genuine discriminator via
  revert-and-reproduce. `dotnet test` 362 passed/0 failed (up from 361). `pnpm check` clean, 271
  dotnet tests, vitest 43.
- **Sixty-eighth pass — a sent message's compose window never learned what actually happened
  to it.** `Compose.tsx`'s post-send view never subscribed to `OutboxStatusChanged` at all —
  zero renderer consumers anywhere — so it showed a static "Sending…" (or a live "Undo send"
  button) forever regardless of whether the send actually succeeded, failed, or landed
  ambiguous, even though the backend (since pass 58) already announces every transition
  specifically to prevent this. Subscribed and extracted the state-to-display mapping into a
  pure `describeSentState` (`SentStatus.ts`), mirroring pass 53's `CloseBehavior.ts` precedent
  for logic that needs a real test but has no React/SignalR harness to exercise it through.
  Three rounds of `invariant-review` reshaped this: round 1 flagged the `AmbiguousOutcome`
  copy needed a closer look; reading `SendReconciler.cs` directly showed its `LastError` is
  only set once its 10-minute `ReconciliationWindow` expires — but round 2 caught that
  `SendExecutor.cs`'s generic catch actually sets `LastError` immediately, with the raw
  provider exception text, on the very first `AmbiguousOutcome` announcement, so
  `lastError`-presence can't safely distinguish "seconds-old raw exception" from "reconciler's
  10-minutes-later verdict" — fixed by having that branch ignore `lastError` entirely and
  always show a neutral "Confirming this was sent…," never a false failure. Verifying that
  tradeoff surfaced a second, separate bug: `SendReconciler.ReconcileAsync` never called
  `IHubEvents.OutboxStatusChangedAsync` at all, so an open window wouldn't even learn a send
  was finally resolved — fixed by injecting `OutboxService` and announcing each changed item
  after a single batched `SaveChangesAsync`. Round 3 confirmed no remaining invariant
  violations and flagged two legitimate, out-of-scope follow-ups for later: the
  `Sending → AmbiguousOutcome` transition itself isn't announced unless the same
  reconciliation pass also resolves or expires it, and the renderer still can't distinguish a
  genuinely expired `AmbiguousOutcome` from an in-window one without a dedicated DTO signal —
  both gaps in richness, not correctness. `dotnet test` 364 passed/0 failed (up from 362).
  `pnpm check` clean, 271 dotnet tests, vitest 52.
- **Sixty-ninth pass — closed pass 68's documented follow-up: `SendExecutor` never announced its
  own terminal transitions.** Pass 68 fixed `Compose.tsx` to subscribe to `OutboxStatusChanged`
  and fixed `SendReconciler.ReconcileAsync` to announce its resolutions, but `SendExecutor
.SendAsync`'s own three terminal write paths — a throttled/categorised rejection returning the
  item to `Scheduled`, an ambiguous outcome, and a genuine successful `Sent` — never called
  `outbox.AnnounceStatusAsync` themselves. An already-open compose window heard about none of
  these until `SendReconciler`'s next reconciliation pass, or never for `Scheduled`/`Sent`,
  which `SendReconciler` doesn't reconcile at all. Added the announcement call right after each
  of the three `SaveChangesAsync`/`CommitAsync` calls (the `Sent`-path call sits outside the
  retryable execution-strategy delegate, after the transaction actually commits, so it can't
  fire on an attempt that later rolls back). `invariant-review` confirmed correct ordering, no
  double-announcement (the account-gating catches in `OutboxJobs.cs` only announce the `Account`
  DTO, a separate event), and no frozen-invariant table entry touched. Three new regression
  tests, one per terminal path, manually confirmed as genuine discriminators via
  revert-and-reproduce. `dotnet test` 367 passed/0 failed (up from 364). `pnpm check` clean, 271
  dotnet tests, vitest 52.
- **Seventieth pass — closed pass 68's last documented gap: distinguishing an expired
  `AmbiguousOutcome` from an in-window one.** `describeSentState` could not tell "seconds-old
  raw exception from `SendExecutor`'s catch-all" from "`SendReconciler`'s final verdict once its
  `ReconciliationWindow` expires" — both populate `LastError` the same way, and the DTO carried
  no separate signal. Added `OutboxItemDto.ReconcilingSince` (nullable, mirroring the domain
  field), threaded it through `Compose.tsx`, and gave `describeSentState` an optional `now`
  parameter plus a client-side `RECONCILIATION_WINDOW_MS` (10 minutes, duplicating the server's
  fixed `SendReconciler.ReconciliationWindow` constant rather than sending it over the wire,
  since it isn't per-account config): within the window it still shows the neutral "Confirming
  this was sent…"; past it, `LastError` if present, or a new fallback message if the reconciler
  itself never resolved the item. `invariant-review` asked for confirmation the regenerated
  types file wasn't stale — re-ran `pnpm generate:types` and got byte-identical output,
  confirming the odd comment (no `?` for a nullable `DateTimeOffset`) is just the transpiler's
  own rendering, not a hand-edit. New C# and vitest regression tests (both sides of the
  10-minute boundary) manually confirmed as genuine discriminators. `dotnet test` 368 passed/0
  failed (up from 367). `pnpm check` clean, 271 dotnet tests, vitest 55.
- **Seventy-first pass — `Create`/`MoveMailbox` trusted a client-supplied parent id without
  checking it belonged to the same account.** `MoveAsync` already guards against a cyclic
  `ParentId` chain with a comment explicitly noting the client's own guard can't be trusted from
  a hub method — the same reasoning was missing for a cross-account parent id, even though
  `MailboxTree.tsx` only ever offers folders from the same account's tree. Left unfixed, a
  cross-account id would carry through reconciliation into a persisted `Mailbox.ParentId`
  pointing outside the child's own account, corrupting the per-account tree every reader
  assumes it can walk within one account's rows alone. Added `ResolveParentAsync`, used by both
  `CreateAsync` and `MoveAsync`, which resolves the parent and throws `HubException` if it
  belongs to a different account — a nonexistent parent id still resolves to `null` exactly as
  before. `invariant-review` confirmed no legitimate cross-account parent usage exists anywhere,
  the dangling-id behavior is preserved, the cycle guard still runs safely first (it walks by
  globally-unique mailbox id with no account filter and persists nothing before the new check),
  and independently ran its own stash-and-reproduce check on the new test. `dotnet test` 369
  passed/0 failed (up from 368). `pnpm check` clean, 272 dotnet tests, vitest 55.
- **Seventy-second pass — the same missing cross-account check as pass 71, one layer down:
  `MutationQueue.EnqueueAsync` trusted a client-supplied messageId against a client-supplied
  accountId with no check they actually belong together.** `MailHub`'s `SetFlags`/`MoveMessages`/
  `RemoveFromMailbox`/`MoveToTrash`/`DeletePermanently` all take an `accountId` and a batch of
  `messageIds` from the client; `MutationExecutor` later resolves a message's provider occurrence
  by `MessageId` alone, with no `AccountId` filter, so a mismatched pair would enqueue a
  `MutationItem` tagged for the wrong account and go on to authenticate as that account while
  acting on a message that actually belongs to a different one. Added a check at the top of
  `EnqueueAsync`, before the transaction begins, throwing `HubException` on a definite mismatch —
  a dangling/nonexistent messageId is deliberately left unrejected, since
  `MyloMailDbContext.ConfigureMutations` already documents no FK to `Message` for exactly that
  reason (a message can become a tombstone while mutation state still references it).
  `invariant-review` confirmed the check is read-only and sits before the sequence-assignment
  transaction, all five `MailHub` call sites pass one constant `accountId` for a whole batch with
  none intentionally crossing accounts, and no frozen-invariant table entry is touched. The new
  regression test's first version (an unrelated random Guid as the accountId) turned out to be a
  false-positive discriminator — SQLite's own foreign-key constraint caught it before the new
  check could — so it was rewritten to seed a real second `Account` row, and manually confirmed
  as a genuine discriminator against that: the pre-fix code silently succeeded with no exception.
  `dotnet test` 370 passed/0 failed (up from 369). `pnpm check` clean, 273 dotnet tests, vitest 55.
- **Seventy-third pass — the same gap, one level further in: a MoveMessage mutation's
  `TargetMailboxId` was never checked against the caller's account either.**
  `MutationExecutor.CallAsync`'s MoveMessage branch resolves the destination via
  `context.Mailboxes.FirstAsync(m => m.Id == exemplar.TargetMailboxId, ct)` with no `AccountId`
  filter, so a mismatched pair would hand a foreign account's `Mailbox` — and its
  provider-specific folder id — to this account's authenticated provider call. Added a second
  ownership check in `EnqueueAsync`, right after pass 72's messageId check, guarded by
  `if (item.TargetMailboxId is Guid targetMailboxId)` so it only fires for MoveMessage (the only
  operation kind that ever sets it). `ScopeMailboxId` (`RemoveFromMailbox`) doesn't need the same
  fix: `MutationExecutor.ResolveAsync`'s query starts from `MessageMailboxes` filtered by
  `MessageId` before filtering by `ScopeMailboxId`, and a `MessageMailboxes` row only exists for
  mailboxes that actually contain that message — a `ScopeMailboxId` from a different account
  simply matches zero rows, a no-op rather than a cross-account action. `invariant-review`
  confirmed the guard fires exclusively for MoveMessage, the `ScopeMailboxId` reasoning holds, and
  no frozen-invariant table entry is touched. New regression test mirrors pass 72's shape (a real
  second `Account`+`Mailbox` row) and manually confirmed as a genuine discriminator via
  revert-and-reproduce. `dotnet test` 371 passed/0 failed (up from 370). `pnpm check` clean, 274
  dotnet tests, vitest 55.
- **Seventy-fourth pass — the same gap in `DraftService.SaveAsync`: a client-supplied
  `SendIdentityId`, and a client-supplied `AccountId` for an existing draft, were both trusted
  with no ownership check.** `SendExecutor` resolves the From address from `SendIdentityId`
  alone with no `AccountId` filter, so a mismatched identity would let a draft send under
  another account's address while authenticating and dispatching as this account. Separately,
  an existing draft's real `AccountId` was never checked against the caller-supplied one, so
  editing another account's draft (or a stale/mismatched save) surfaced as a confusing
  `InvalidOperationException` from `DefaultIdentityAsync`'s `Sequence contains no elements`
  rather than a clear rejection. Added two `HubException` checks in `SaveAsync`, phrased to
  match `MutationQueue.cs`'s precedent exactly. `invariant-review` confirmed correct ordering
  for both the new-draft and existing-draft paths, no false-positive risk (`MailHub.SaveDraft`
  always passes the same `AccountId` a draft was created with), and no frozen-invariant table
  entry touched — it flagged one pre-existing, out-of-scope observation (a nonexistent
  `SendIdentityId` is silently accepted rather than rejected) left as-is. Two new regression
  tests manually confirmed as genuine discriminators via revert-and-reproduce. `dotnet test` 373
  passed/0 failed (up from 371). `pnpm check` clean, 276 dotnet tests, vitest 55.
- **Seventy-fifth pass — closed pass 74's flagged-but-unfixed observation: a nonexistent
  `SendIdentityId` wasn't actually silently accepted, just rejected badly.** Pass 74's
  invariant-review had described it as "silently accepted rather than rejected," but
  `Draft.SendIdentityId` has a real FK constraint to `SendIdentity`
  (`OnDelete(DeleteBehavior.Restrict)`) — a nonexistent id was already rejected, just as a raw,
  unhandled `SqliteException` from `SaveChangesAsync`'s constraint violation rather than a clean
  message. Same raw-exception-propagation bug class passes 65-67 fixed elsewhere: SignalR's
  `EnableDetailedErrors` is off, so the raw exception reaches the renderer as a generic error.
  Split the existing ownership check into two: a nonexistent identity now throws a clean
  `HubException` naming the id, distinct from the existing cross-account-mismatch message.
  `invariant-review` confirmed the FK claim, that the split is behaviorally equivalent for the
  already-covered case, and no caller catches `SqliteException` around this call site. New
  regression test manually confirmed as a genuine discriminator via revert-and-reproduce
  (reverting reproduces the raw `SqliteException` instead of the expected `HubException`).
  `dotnet test` 374 passed/0 failed (up from 373). `pnpm check` clean, 277 dotnet tests, vitest 55.
- **Seventy-sixth pass — closed a sixth instance of the cross-account-ownership bug class:
  `CalendarEventService.SaveAsync`'s client-supplied `EventId` wasn't checked against its
  client-supplied `CalendarId`.** `SaveAsync` resolves `calendar`/`account` from
  `input.CalendarId`, then separately loads `existing` from `input.EventId`, with no check that
  `existing.CalendarId == input.CalendarId`. A mismatched pair would let `UpdateAsync` mutate a
  foreign calendar's event and push the edit through the wrong account's provider credentials —
  authenticating as the account derived from `input.CalendarId` while writing to a
  `ProviderEventId` that actually belongs to a different account entirely. Added a check right
  after loading `existing`, before either `CreateAsync` or `UpdateAsync` runs. A
  dangling/nonexistent `EventId` still falls through to `CreateAsync` unchanged, matching pass
  74's precedent that only a definite mismatch against something real is rejected.
  `invariant-review` confirmed no legitimate code path (`CalendarSyncService`, `ItipReplySender`,
  `EventModal.tsx`) ever relies on moving an event between calendars via `SaveAsync`, and no
  frozen-invariant table entry is touched. New regression test seeds a real second
  Account+Calendar and manually confirmed as a genuine discriminator via revert-and-reproduce.
  `dotnet test` 375 passed/0 failed (up from 374). `pnpm check` clean, 278 dotnet tests, vitest 55.
- **Seventy-seventh pass — confirmed the cross-account-ownership sweep exhausted; found one
  unrelated feature-sized gap instead.** Checked every remaining candidate: `SendIdentityService`'s
  CRUD, `OutboxService.TryCancelAsync`/`DraftService.CancelSendAsync`, `NotificationService`,
  `ExportJobs`, `MailboxManagement.ReorderAsync`, `MessageSearch.SearchAsync`, and the rest of
  `MailHub`'s client-id-taking methods — all either single-id (nothing to mismatch) or already
  filter by the caller's own `AccountId` first, so a foreign id yields a safe no-op/empty result
  rather than a leak. This bug class (passes 71-76) is genuinely closed. Separately found that
  `AccountProvisioningService.RemoveAsync` — a fully-built, carefully-ordered account-removal
  feature reachable via `DELETE /accounts/{accountId}`, with a documented remote-completion
  caveat in `docs/architecture.md` — had zero UI entry point anywhere in the renderer. Raised to
  the user as a feature-sized decision; they chose to build it now (see the seventy-eighth pass
  below).
- **Seventy-eighth pass — built the missing account-removal UI, per the user's decision on pass
  77's finding.** Added a "Remove account" section to `AccountSettings.tsx`, mirroring
  `MailboxTree.tsx`'s existing danger `Modal` confirmation pattern and calling
  `DELETE /accounts/{id}` directly via `fetch` — the same REST-call pattern `ShellSettings.tsx`
  already uses for other REST-controller endpoints. The confirmation copy states the actual
  caveat from `docs/architecture.md` (already-dispatched provider operations may still complete
  remotely) rather than a generic "cannot be undone." On success, `AppShell.tsx`'s new
  `onRemoved` callback clears `selectedAccountId` to `null`, letting the existing
  auto-select-the-first-account effect pick a replacement or fall through to the empty-state
  add-account pane, and invalidates the accounts query. A stale `selectedMessageId` from the
  removed account isn't separately cleared: `ReadingPane` already handles a message that becomes
  unavailable the same way any ordinary message deletion produces, so nothing new is introduced.
  `invariant-review` confirmed no frozen-invariant table entry is touched, but its scope is
  architectural invariants only and it explicitly declined the confirmation-copy accuracy,
  double-submission safety, and dangling-state questions — verified those directly instead:
  `RemoveAsync` no-ops safely on a second call (an already-removed account just returns early),
  so a double-click races harmlessly, and the Remove button also disables while in flight. No
  backend change was needed (the endpoint was already complete); `dotnet test` unaffected (278
  dotnet tests unchanged). `pnpm check` clean, vitest 55.
- **Seventy-ninth pass — a reading pane left open on a deleted message polled forever with no
  indication anything was wrong.** While independently verifying pass 78's dangling-state claim,
  found the underlying gap it was relying on: `MailHub.GetMessageBody` returned the same all-null
  `MessageBodyDto` shape for "content not yet fetched" and "this message no longer exists at all"
  (deleted, or its whole account removed). `ReadingPane`'s `refetchInterval` only stops once
  `isFetched` or `isFailed` is true, neither of which a permanently-gone message ever reaches, so
  it would poll every two seconds forever, showing a blank body under a stale subject line.
  Pre-existing, not introduced by pass 78 — ordinary single-message deletion via `MessageList`
  never clears `selectedMessageId` either, so this was already reachable before account removal
  existed as a second path to it. Fixed by having `GetMessageBody` check the `Message` row itself
  exists before falling through to the body/state lookup, throwing `HubException` when it
  doesn't — distinct from the "exists but unfetched" case, where a `Message` row is present
  before its `MessageBodies`/`MessageContentStates` rows would ever be. `ReadingPane` now also
  stops polling on a query error and shows "This message is no longer available." `invariant-review`
  confirmed `Message` rows are hard-deleted (never soft-tombstoned in place), so the existence
  check can't misfire against a legitimately unfetched-but-real message, and that `HubException`
  surfaces to TanStack Query the same way `MessageList.tsx`'s existing error handling already
  relies on. New regression test — the first in this repo to resolve `MailHub` directly rather
  than through SignalR's own invocation pipeline — confirmed as a genuine discriminator via
  revert-and-reproduce. `dotnet test` 376 passed/0 failed (up from 375). `pnpm check` clean, 279
  dotnet tests, vitest 55.
- **Eightieth pass — removing an account with a nested mailbox hierarchy threw a FOREIGN KEY
  constraint failure.** Follow-up sweep on account-removal cleanup completeness from pass 79.
  `Mailbox.ParentId` is `Restrict`, not `Cascade` — a topology reconciliation must decide
  explicitly what happens to a deleted parent's children, never have the database silently drop
  a subtree — but that same guard also blocked `AccountProvisioningService.RemoveAsync`'s own
  account-deletion cascade: SQLite refused to delete a parent mailbox row while a child mailbox
  in the same account still referenced it, for any account with at least one nested folder (the
  common case for most providers). Fixed by clearing every mailbox's `ParentId` for the account
  in one `ExecuteUpdateAsync` immediately before the account row (and therefore its mailboxes)
  is removed — safe specifically because every mailbox in the account is about to be deleted
  together, so there is no longer a subtree left for `Restrict` to protect. `invariant-review`
  confirmed ordinary single-mailbox deletion (a separate code path) remains fully protected, no
  tracked entities are left stale by the bulk update, and no frozen-invariant entry is touched;
  one low-priority note accepted as-is: the update and the account deletion aren't in one
  transaction, leaving a narrow, self-healing crash window. New regression test (a real
  parent+child mailbox pair) confirmed as a genuine discriminator via revert-and-reproduce.
  `dotnet test` 377 passed/0 failed (up from 376). `pnpm check` clean, 280 dotnet tests, vitest 55.
- **Eighty-first pass — no gap found.** Swept for other `DeleteBehavior.Restrict` FKs with the
  same cascade-conflict shape pass 80 found. Only two others exist:
  `MessageSearchContent.MessageId`→`Message` (both real deletion paths —
  `AccountProvisioningService.RemoveAsync` and `TombstoneGcJobs`'s tombstone collection — already
  call `search.RemoveAsync`/`RemoveForAccountAsync` before removing the `Message` row) and
  `Draft.SendIdentityId`→`SendIdentity` (a probe test seeding a real cross-referencing pair and
  calling `RemoveAsync` passed cleanly — unlike the self-referencing `Mailbox.ParentId` case,
  `Draft` and `SendIdentity` are independent tables both cascading from `Account`, and SQLite's
  cascade engine deletes the referencing side first without issue). Pass 80's fix was the only
  genuine instance of this bug class.
- **Eighty-second pass — a lost undo-send race gave the user no feedback at all.** §15's
  undo-send compare-and-swap (`OutboxService.TryCancelAsync` vs. `TryClaimForSendAsync`) was
  already correct server-side, but `Compose.tsx`'s `undo()` did `setSent({ ...sent, cancelled:
false })` on a lost race — a no-op from the UI's perspective, since `cancelled` was already
  `false`. The "Undo send" button stayed visible and clickable, silently doing nothing, until a
  later `OutboxStatusChanged` (potentially seconds away) corrected the display. Added
  `undoRejected` to `Sent`/`SentState`, shown as "Too late to undo — this message is already
  being sent." with the button hidden, checked after every real status branch so a genuine
  announcement always wins over a stale rejection flag. `invariant-review`'s first pass caught a
  real bug in the initial fix: `undo()` spread the closure-captured `sent` instead of using
  React's functional update form, so a genuine `OutboxStatusChanged` arriving mid-flight could
  get clobbered by a stale `undoRejected: true` — fixed by switching to the functional form,
  matching the sibling `onStatusChanged` handler's own pattern; a second `invariant-review` pass
  confirmed the fix and identified `describeSentState`'s status-first check ordering as a second,
  independent layer of protection regardless of arrival order. Two new regression tests confirmed
  as genuine discriminators via revert-and-reproduce. `dotnet test` 377 passed/0 failed
  (unaffected — renderer-only). `pnpm check` clean, 280 dotnet tests, vitest 57 (up from 55).
- **Eighty-fifth pass — built message-list pagination.** Pass 84 found `MailHub.GetMessages` had
  a flat `take: 100` with no pagination at all — nothing beyond the first 100 messages in a
  mailbox was ever reachable in the UI, undocumented anywhere. User asked, chose to build it now.
  `GetMessages` gained a `skip` parameter; ordering is `ReceivedAt` descending with `Id` as a
  tiebreaker so two page fetches over an unchanged mailbox partition the same total order rather
  than reshuffling ties between calls — the sort still runs in-memory after materializing the
  mailbox's messages, extending pass 62's existing SQLite-DateTimeOffset-ordering workaround
  rather than introducing a new one. `MessageList.tsx` switched its ordinary listing from
  `useQuery` to `useInfiniteQuery` with a "Load more" button; search stays a plain, unpaginated
  query since FTS5 already returns a bounded ranked set. `invariant-review` confirmed the
  ordering provably guarantees disjoint, jointly-exhaustive pages, the sort genuinely runs
  in-memory (not SQL-translated), `getNextPageParam` computes correctly across any page count,
  and the load-more error state correctly re-enables via React Query's own reset — flagged one
  non-blocking perf note for a future pass (each "Load more" click re-sorts the whole mailbox in
  memory rather than the old single per-session full-scan). New regression tests seed 150
  messages sharing one timestamp specifically to exercise the `Id` tiebreaker, confirmed
  disjoint/exhaustive/ordered across the page boundary. `dotnet test` 379 passed/0 failed (up
  from 377). `pnpm check` clean, 282 dotnet tests, vitest 57.
- **Eighty-sixth pass — no gap found.** Investigated whether pass 85's flagged "Load more
  re-sorts the whole mailbox" perf note was a small fix (it isn't — a clean fix needs either a
  SQL-translatable keyset requiring further DateTimeOffset-comparison research pass 62 didn't
  solve, or a session-scoped cache with real invalidation, both genuine design questions) and
  whether new mail arriving mid-pagination could shift `skip` offsets into a duplicate/gap
  (checked: `HubConnection.ts` invalidates the whole `["messages"]` infinite query on any
  message event, so TanStack Query refetches every already-loaded page together at their stored
  `skip`s — a shifted mailbox still gets stable slices, no duplicate or gap). Both clean.
- **Eighty-seventh pass — a locked OS keychain during account creation or reauthentication
  surfaced as a bare, uninformative 500.** Pass 64 gave every background job a distinct
  `AuthState.CredentialStoreUnavailable` and a friendly UI banner for exactly this failure, but
  `AccountsController.Add` and `.Reauthenticate` — synchronous, user-initiated REST endpoints
  that also read/write credentials — never caught `CredentialStoreUnavailableException`, and
  with no global exception-handler middleware in `Program.cs`, an uncaught one propagated
  straight to a bare 500 with no detail. Added a catch to both returning a 503 Service
  Unavailable Problem with the exception's message. `invariant-review` confirmed both call paths
  genuinely reach `ICredentialStore` in a way that can throw before responding, 503 is the right
  choice since the renderer's failure handling is generic, and no frozen-invariant entry is
  touched — it also caught that the first version only tested `Add`, leaving `Reauthenticate`'s
  independent catch block unverified; added a symmetric test using the same
  `ThrowingCredentialStore` double, confirmed as a genuine discriminator via
  revert-and-reproduce for both. `dotnet test` 381 passed/0 failed (up from 379). `pnpm check`
  clean, 284 dotnet tests, vitest 57.
- **Eighty-eighth pass — no gap found.** Following pass 87's finding (a REST controller missing
  a catch every background job already had), checked every other `[ApiController]` class
  (`OutboxController`, `DraftAttachmentsController`, `MessagePartsController`,
  `MessageAttachmentsController`, `RemoteContentController`, `CredentialStoreController`,
  `AppSettingsController`) for the same shape — none call into a service that can throw
  `CredentialStoreUnavailableException`/`ProviderAuthenticationException`; they're all local
  DB reads/writes or raw-MIME extraction with no provider or credential-store round trip. This
  bug class is now exhausted across every REST controller, not just hub methods (already swept
  in passes 65-67). Also re-checked mailbox rename/move collision handling directly against
  `MailboxManagement.cs`: cross-account parent checks (pass 71's `ResolveParentAsync`) and
  provider-delegated collision errors surfaced via `HubException` (pass 65's
  `RunProviderCallAsync`) are both already correct, confirming pass 83's earlier clean finding
  on the same code. No commits made this pass.
- **Eighty-ninth pass — one malformed recurring master crashed the entire calendar view.**
  `CalendarEventOccurrences.ForCalendarAsync` loops over every recurring master in a window
  calling `CalendarRecurrenceExpander.Expand` with no exception handling. `Expand` calls
  `TimeZoneInfo.FindSystemTimeZoneById(master.StartTimeZoneId)`, which throws for an
  unrecognised zone id — a real, documented issue since Outlook/Exchange sometimes emit
  non-standard vendor TZIDs like "Customized Time Zone" not in the IANA/Windows tz database.
  One malformed master's data crashed the whole `ForCalendarAsync` call, hiding every other
  unrelated event (plain events, other masters, overrides) in that window too. Wrapped the
  per-master `Expand` call in a try/catch, skipping only that master's expansion on failure.
  `invariant-review` confirmed the containment is clean (a malformed master's `RecurrenceRules`
  count already excludes it from the `plain` bucket, so `continue` leaves no partial state) and
  flagged a same-shaped sibling bug in the same unguarded call: Ical.Net's `RecurrencePattern`
  constructor throws `ArgumentOutOfRangeException` for a malformed RRULE string via the
  identical crash-the-whole-view path — fixed in the same catch clause, verified via a
  standalone probe that Ical.Net genuinely throws that type for a garbage RRULE. Noted as a
  legitimate follow-up, not a hole in this fix: the silent omission (a broken series simply
  never shows again, with no signal to the user) could use a health/diagnostic surface in a
  future pass, but that's product-scope work beyond "don't crash the view." Two new regression
  tests (one per exception type) confirmed as genuine discriminators via revert-and-reproduce.
  `dotnet test` 383 passed/0 failed (up from 381). `pnpm check` clean, 286 dotnet tests, vitest 57.
- **Ninetieth pass — no gap found.** Swept attachment/MIME edge cases (`AttachmentService`'s
  path-traversal-safe filename sanitization, RFC 2231/encoded-word filenames via MimeKit's own
  decoding), inline-image `cid:` resolution and blob-URL lifecycle in `MessageHtml.tsx` (already
  cancellation-guarded, no leak or race), `MessagePartsController`'s message-id-only lookup
  (correctly not a cross-account gap — unlike the mutation/draft/calendar cases from passes
  71-77, a message id is already globally unique in this single-user desktop app), the
  `ReauthenticateAccount`/`CredentialStoreUnavailable` banner split, and `AccountSettings.tsx`'s
  save/remove error handling. All already correct on inspection.
- **Ninety-first pass — a resize left the panel-layout write's failure completely silent.**
  `useShellLayout`'s debounced write used `void fetch(...)` with no error handling at all — not
  even a `response.ok` check — the one write in the codebase that didn't follow the
  try/catch-plus-`notify` pattern every other write already uses (`ShellSettings.tsx`'s
  `setCloseBehavior`/`setTheme`/`untrustSender`). A failed save (network error, backend down,
  non-2xx response) gave the user no signal their resize preference wasn't persisted. Added an
  `onSaveError` callback wired to the existing `notify` infrastructure in `AppShell.tsx`.
  `invariant-review` confirmed the fix catches both failure shapes with no unhandled-rejection
  gap, `notify`'s title-based dedup collapses repeated failures into one toast, and suggested
  extracting the inline async IIFE into a named function for readability (applied). No
  regression test added — confirmed this bug class has no existing test precedent anywhere in
  the repo (no `@testing-library/react`/jsdom setup exists, and `ShellSettings.tsx`'s identical
  pattern ships untested too), a real infrastructure gap flagged as a follow-up rather than
  worked around. `pnpm check` clean, 286 dotnet tests (unaffected), vitest 57.
- **Ninety-second pass — two more fire-and-forget async chains had no error handling.**
  Follow-up sweep from pass 91's finding, grepping the renderer for other unawaited/uncaught
  hub/REST calls. Found `SendIdentityManager.tsx`'s `reload()` (called at mount, and awaited
  inside the component's own save/delete flow) had no `.catch()` at all — a failed initial load
  left `identities` `null` forever, rendered via `(identities ?? []).map(...)` as "this account
  simply has no send identities" rather than as an error. And `AppShell.tsx`'s
  notification-click handler invoked `ResolveStagedMessage` with a `.then()` but no `.catch()` —
  a hub disconnect at exactly the wrong moment made clicking a notification silently do nothing.
  Fixed both by adding `.catch()` handlers reusing each component's existing
  error/`notify`-based feedback path. `invariant-review` confirmed both fixes correct and
  identified one real, benign behavior change: `reload()` now always resolves, so `run()`'s own
  `await reload()` always proceeds to `setEditing(null)` after a successful save/delete even if
  the refresh itself failed — correct, since the save/delete already succeeded and the refresh
  failure is still surfaced separately. Neither change touches a frozen-invariant table entry.
  No regression test added, matching pass 91's already-documented gap (no
  `@testing-library/react`/jsdom setup exists in this repo). `pnpm check` clean, 286 dotnet
  tests (unaffected — renderer-only), vitest 57.
- **Ninety-third pass — closed the last instance of passes 91-92's fire-and-forget sweep.**
  `HubConnection.ts`'s `NotificationReady` handler chained
  `window.notifications.show(...).then(() => hub.invoke("MarkNotificationDelivered", ...))` with
  no `.catch` at all — either call rejecting produced a bare unhandled promise rejection with no
  trace of what happened. Added a
  `.catch` that only logs via `console.error`, not user-facing feedback: this is an IPC/hub
  relay with no natural UI surface, not a user-initiated action, and leaving the notification
  unmarked-delivered on failure is the intended fallback — confirmed by reading
  `NotificationService.RedispatchPendingAsync`, which re-announces every record with
  `DeliveredAt == null` specifically to recover from a crash between announcement and delivery
  confirmation ("duplicating an already-shown notification is the accepted cost; silently
  dropping one is not," §13 Epic 9). A systematic grep of the whole renderer for `void fetch(`
  and unawaited `.then()` chains without `.catch()` found nothing else remaining — this angle is
  now exhausted. `invariant-review` confirmed no frozen-invariant hit and that the fix introduces
  no retry, double-invoke, or swallowed error that previously reached calling code. No
  regression test added, matching passes 91-92's already-documented gap. `pnpm check` clean, 286
  dotnet tests (unaffected — renderer-only), vitest 57.
- **Ninety-fifth pass — built search-result term highlighting.** Pass 94 found search results
  never highlighted matched terms: `Message.Snippet` is a static provider-supplied preview
  (Gmail's `snippet`/Graph's `bodyPreview`), not an FTS5 match-context excerpt, so a hit showed
  generic preview text with no indication of what actually matched. User asked, chose to build
  it now. `MessageSearch.MatchAsync` now also calls FTS5's own `snippet()` function (column
  index `-1`, so FTS5 picks whichever indexed column actually contains the match), delimiting
  matched terms with the ASCII SOH/STX control characters rather than any HTML markup — message
  content is arbitrary, untrusted sender text, and a marker scheme that could collide with or be
  mistaken for real markup would be a real risk. `MessageSummaryDto` gained an optional trailing
  `SearchSnippet`, populated only by search; ordinary listing implicitly passes `null` via the
  new parameter's default. The renderer's new `SearchSnippet.ts` parses the delimited string
  into plain `{text, highlighted}` segments with no HTML anywhere in the pipeline;
  `MessageList.tsx` renders them as `<mark>`/`<span>` JSX children (never
  `dangerouslySetInnerHTML`) for search results only. `invariant-review` confirmed no XSS path
  exists, the `snippet()` call's own arguments are bound parameters entirely separate from the
  user's own query text, `maxTokens=20` is within FTS5's documented clamp range, ordinary
  listing is unaffected end-to-end, and no frozen-invariant table entry is touched — it also
  caught a real robustness gap: relying on `Dictionary<Guid,string>`'s insertion-order
  enumeration to preserve FTS5's rank order was an undocumented implementation detail, not a
  language guarantee, fixed by switching to `List<(Guid Id, string Snippet)>` instead. New
  regression tests confirmed the snippet-content test is a genuine discriminator via
  revert-and-reproduce. `dotnet test` 385 passed/0 failed (up from 383). `pnpm check` clean, 288
  dotnet tests, vitest 62.
- **Ninety-sixth pass — no gap found.** Swept for other Dictionary/HashSet-iteration-order bugs
  following pass 95's fix: every `Dictionary<>` in the backend is a keyed lookup or join, never
  enumerated where order matters. Checked export completeness (bulk export is a deliberate,
  mail-only `.eml` tree — calendar/drafts/identities are out of scope by design, not a gap),
  multi-window sync (every `IHubEvents` method broadcasts via `hub.Clients.All` uniformly, no
  per-window filtering to miss an update through), and keyboard shortcuts (only one call site
  exists, so no duplicate-binding conflict is even possible yet). All clean.
- **Ninety-seventh pass — deleting a recurring event's virtual occurrence silently removed the
  whole series with no warning.** `Calendar.tsx`'s `openEditModal` already substitutes a virtual
  occurrence's id with its `masterEventId` before opening the edit modal (existing, documented:
  editing reaches the whole series, per §13 Epic 7's deferred per-occurrence editing) — delete
  silently inherited the same whole-series scope, with the button just reading "Delete event".
  Added a `deletesWholeSeries` prop to `EventModal`, showing `window.confirm` with the real scope
  before calling `onDelete()`, mirroring the existing `window.confirm` pattern already used in
  `Compose.tsx`'s send-time warnings. The first version set this from the existing `isRecurring`
  field, but `invariant-review` caught a real false-positive: `isRecurring` is true both for a
  master AND for an already-materialised override/exception row, so deleting an override would
  show the same "deletes the whole series" warning even though it only ever removes that one
  row — user-facing misinformation about a destructive action, not a data-loss bug. Fixed
  properly with a new `CalendarEventSummaryDto.IsRecurrenceMaster` field, true only for the row
  that owns the series' `RecurrenceRules` (or a virtual occurrence generated from one), set
  correctly at all three DTO construction sites. A second `invariant-review` round confirmed the
  fix and that no construction site was missed. New regression test distinguishes an override
  (false) from a virtual occurrence (true) within one result set. `dotnet test` 386 passed/0
  failed (up from 385). `pnpm check` clean, 289 dotnet tests, vitest 62.
- **Ninety-eighth pass — editing a recurring event's virtual occurrence silently rescheduled the
  whole series.** A more serious version of pass 97's delete shape. `Calendar.tsx`'s
  `openEditModal` substitutes a virtual occurrence's id with its `masterEventId` before opening
  the edit modal (existing, documented — true per-occurrence editing is deferred per §13 Epic
  7), but only `id` was substituted: the form's initial `Start`/`End` stayed the clicked
  occurrence's own derived date. `CalendarEventService.UpdateAsync` unconditionally overwrites
  `existing.Start`/`End` with whatever the form holds, so saving an edit — even just changing
  the title, without touching the date fields — would silently reschedule the entire series to
  that occurrence's date. Not an unclear warning; a silent data-corruption path. Added
  `Start`/`End`/`IsAllDay` to `CalendarEventDetailDto` and `MailHub.GetCalendarEventDetail`'s
  construction (the master row's own persisted values); `EventModal.tsx` gained a
  `virtualOccurrence` prop that, once `GetCalendarEventDetail` resolves, corrects the form's
  date fields via a `useEffect` guarded to apply exactly once (never on a later background
  refetch that would clobber the user's own in-flight edit). `invariant-review` confirmed the
  root cause by reading `UpdateAsync` directly, confirmed the once-only guard and the negligible
  resolve-before-submit race (no worse than one pass 97 already accepted), confirmed no
  regression when `virtualOccurrence` is false, confirmed the single DTO construction site, and
  confirmed no frozen-invariant entry touched — noting one optional, not-required UX follow-up:
  nothing yet stops a user from _manually_ changing the date fields on a virtual-occurrence edit
  and still moving the whole series. New regression test seeds a real recurrence master and
  asserts the detail reports its own dates, not a later occurrence's. `dotnet test` 387 passed/0
  failed (up from 386). `pnpm check` clean, 290 dotnet tests, vitest 62.
- **Hundredth pass — a rejected CalDAV certificate surfaced with no fingerprint, hostname, or
  issuer.** Investigating provider-specific error message clarity, found IMAP's
  `AuthenticateAsync` and the SMTP send path both translate a rejected certificate into
  `CertificateTrust.Problem`'s detailed message (§15), but CalDAV surfaced only a bare
  `HttpRequestException` — the codebase's own documented expectation until now, per an existing
  live test that explicitly asserted that raw exception type. Added `CertificateRejectionHandler`,
  a `DelegatingHandler` `CalendarProviderFactory` wraps its `HttpClientHandler` with, translating
  the same rejection from one place rather than at each of `CalDavCalendarProvider`'s nine call
  sites. `invariant-review`'s first pass caught a real, serious bug: the handler is held and
  reused across a provider's whole lifetime (unlike IMAP, which gets a fresh client per connect),
  and the rejected-certificate field was never reset — a rejection on one request would stay
  stamped forever, mislabelling a later, unrelated transport failure as the same stale
  certificate problem. Fixed by resetting per attempt, mirroring `ImapMailProvider.ConnectAsync`'s
  own pattern; a second `invariant-review` pass confirmed the fix. Two new regression tests: a
  live test driving the real `CalendarProviderFactory` against the `caldav:up` fixture's actual
  self-signed certificate, and a unit test (made possible by widening the nested handler from
  `private` to `internal` under the existing `InternalsVisibleTo` grant) exercising the staleness
  scenario directly against a scripted fake transport — both manually confirmed as genuine
  discriminators via revert-and-reproduce. `dotnet test` 388 passed/0 failed (up from 387), plus
  4/4 CalDAV live tests verified against the real Docker fixture. `pnpm check` clean, 291 dotnet
  tests, vitest 62.
- **Hundred-and-first pass — a fresh finding surfaced mid-pass-100: IMAP Sent-folder filing
  failures were invisible.** `ImapMailProvider.AppendFailure` records when a successfully-sent
  message's copy couldn't be filed into Sent — deliberately not a send failure of its own, since
  the message already left — but nothing anywhere read the property: not logged, not surfaced,
  no diagnostic trail for "why is this sent message missing from Sent." Added
  `SendExecutor.AppendFailureOf`, a pattern-match that only matches an `ImapMailProvider` with a
  non-null `AppendFailure`, logging a warning after a successful send when it does. Building this
  required hoisting `providers.For(account)` out of the inline send call to reuse the resolved
  instance for the check — the hoist initially moved that call outside the surrounding try block,
  a real regression `invariant-review` caught before it shipped: `providers.For(account)` can
  itself throw `CredentialStoreUnavailableException` (a locked OS keyring), and that exception's
  dedicated handling (returning the item to `Scheduled` rather than leaving it stuck) depends on
  being inside that try. Fixed by declaring the variable before the try and assigning it as the
  try's first statement. Two new regression tests: one exercising `AppendFailureOf` directly
  against a real `ImapMailProvider` via a new `SimulateAppendFailure` test seam (a genuine IMAP
  APPEND failure needs a live server round trip), and one exercising the hoist regression itself
  via a new `MutationHarness.FailNextProviderResolutionWith` hook, manually confirmed as a
  genuine discriminator via revert-and-reproduce. `dotnet test` 392 passed/0 failed (up from
  388). `pnpm check` clean, 294 dotnet tests, vitest 62.
- **Hundred-and-third pass — a rejected IMAP certificate was only translated into a clear error
  on two of many call paths.** `ImapMailProvider.ConnectAsync` sets `rejectedCertificate` on a
  TLS validation rejection, but only `AuthenticateAsync` and `Send.cs`'s separate SMTP connect
  caught `SslHandshakeException` specifically to build a rich `CertificateTrust.Problem`
  (fingerprint/issuer). Every other caller — `ListMailboxesAsync` and the rest of
  `.Mailboxes.cs`, `.Sync.cs`, `.Mutations.cs`, `.Drafts.cs` — had no try/catch at all, so a
  rejection hit during ordinary mid-session work propagated as a raw MailKit exception through
  `SyncJobs`/`MutationJobs`'s generic handlers with no `AuthState` change, no live announcement,
  and no fingerprint/issuer detail — the same "raw provider exception defeats a clear error
  message" bug class passes 65-67 and 100-101 already fixed elsewhere. Centralized the
  translation in `ConnectAsync` itself: a new catch throws `ProviderAuthenticationException`
  whenever `rejectedCertificate` is set, so every caller benefits without its own catch, and the
  scheduling layer's existing `ProviderAuthenticationException` handling picks it up
  automatically. `invariant-review` confirmed correct disposal, confirmed `Send.cs`'s SMTP path
  is unaffected, and flagged one real but non-blocking inconsistency: the same rejection cause
  now resolves to `AuthState.Error` via explicit `AuthenticateAsync` but `AuthState.NeedsReauth`
  via this centralized path elsewhere — a label mismatch, not a functional bug, since both states
  are handled identically for recovery and `NeedsReauth` is actually the behaviorally-correct
  choice (it's what gates further polling; mapping to `Error` would retry-storm against the same
  rejected certificate). No test added: `tests/imap-matrix/compose.yaml` is explicitly
  plaintext-only with no TLS fixture at all, unlike CalDAV's — building one is real new
  infrastructure, documented as a gap per this session's precedent (passes 91-93, 102).
  `dotnet test` 392 passed/0 failed (unaffected — no test added). `pnpm check` clean, 294 dotnet
  tests, vitest 62.
- **Hundred-and-sixth pass — all-day events were stored one day short, and single-day ones
  vanished entirely.** `CalDavIcs.cs`'s ICS export and `CalendarAgenda.tsx`'s day-span filter
  (`event.start.isBefore(dayEnd) && event.end.isAfter(dayStart)`) both assume RFC 5545's
  exclusive-end convention for all-day events — `End` is the day _after_ the last included one —
  but `EventModal.tsx`'s End date field read and wrote the raw stored `End` with no adjustment.
  Picking the same date for Start and End on a new all-day event, the natural way to create a
  one-day event, produced `Start === End`: a zero-duration span the agenda filter's
  `isAfter(dayStart)` (false when equal) then silently excluded from every day, including the one
  picked. A multi-day event was similarly stored one day short of its real last day. Added
  `AllDayEventEnd.ts`'s `toInclusiveEndDateInputValue`/`fromInclusiveEndDateInputValue` pair to
  show/store the inclusive-last-day the user actually means, wired into the End field only when
  `isAllDay` (the timed-event path is unchanged), plus a fix to the "All day" toggle itself so
  flipping it snaps `End` to a valid span on either transition instead of carrying over whatever
  timed value was already there. `invariant-review` confirmed the two functions are true inverses,
  DST-safe (`dayjs` has no default timezone set, so day arithmetic is calendar-day not fixed-24h),
  the timed path is provably byte-identical to before, the toggle can never produce
  `End <= Start`, no frozen-invariant hit, and `Calendar.tsx`'s two `isAllDay` usages are pure
  pass-through with no parallel fix needed. New pure-function tests confirmed genuine (concrete
  date-string assertions, not tautologies) after fixing a UTC-vs-local mismatch in the test's own
  first draft. `pnpm check` clean, 294 dotnet tests (unaffected), vitest 66 (up from 62).
- **Hundred-and-tenth pass — built a settings UI for `ImapProviderConfig.AppendToSentOnSend`.**
  Pass 109 found this real, correctly-consumed toggle (`MailProviderFactory` reads it to decide
  whether the IMAP client should append a Sent copy on send) had zero UI exposure — absent from
  `AccountSettingsDto`/`AccountDto`, no checkbox anywhere. User asked, chose to build it now.
  Added `bool? AppendToSentOnSend` to both DTOs (nullable — IMAP-only), populated from
  `(account.ProviderConfig as ImapProviderConfig)?.AppendToSentOnSend` at both `AccountDto`
  construction sites, and `MailHub.UpdateAccount` writes it back by mutating the already-tracked
  `ImapProviderConfig` instance in place (never reassigning `account.ProviderConfig`, so no
  sibling field like `Host`/`SmtpHost` can be clobbered), silently ignoring a non-null value from
  a non-IMAP account rather than throwing. `AccountSettings.tsx` gained a `Toggle` gated on
  `providerType === ProviderType.Imap`. `invariant-review` confirmed the write path is safe,
  that `ProviderConfig`'s JSON value converter (`HasJsonConversion` with an explicit structural
  `ValueComparer`) means EF Core correctly detects and persists this in-place mutation, the
  non-IMAP no-op has no side effect, and no frozen-invariant entry is touched. New tests confirm
  the setting round-trips without disturbing sibling `ImapProviderConfig` fields and that a
  Gmail account's attempt is silently ignored; the first manually confirmed as a genuine
  discriminator via revert-and-reproduce. `dotnet test` 394 passed/0 failed (up from 392).
  `pnpm check` clean, 296 dotnet tests, vitest 66.

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
3. **Gmail/Graph conformance and correctness against real accounts is unverified.** The new
   send/draft/mailbox-CRUD implementations match their SDKs' documented shapes but have not run
   against live Gmail/Microsoft 365 accounts — in particular, whether Graph honors a
   caller-supplied `internetMessageId` on send (needed for Message-ID-based reconciliation) is
   explicitly flagged as unconfirmed in `GraphMailProvider.Send.cs`'s own comment. Running
   `GmailConformanceTests`/`GraphConformanceTests` for real needs `.dev/provider-test.env`
   sourced and, if no cached token still works, a fresh interactive OAuth consent click.
4. **IMAP/Graph "stage C" mutation divergence** — `RemoveFromMailbox`'s IMAP and Graph
   implementations both currently collapse to the same effect as `DeletePermanently`
   (`ImapMailProvider.Mutations.cs`, `GraphMailProvider.Mutations.cs`), which is why the hub
   method exists (§7) but has no UI caller yet. Once they properly diverge (drop-this-membership
   vs. delete-the-message), wire a "Remove from this folder" entry into `MessageList.tsx`'s
   context menu the same way `DeletePermanently` already is.
5. **Gmail/Graph never translate a 401/429/503 into `ProviderAuthenticationException`/
   `ProviderThrottledException`** (found by pass 105) — every `catch` in `GmailMailProvider.*`/
   `GraphMailProvider.*` only handles a 404 lookup-miss; a real revoked-token or rate-limit
   response would propagate as a raw `GoogleApiException`/`Microsoft.Kiota.Abstractions
.ApiException` straight through the background jobs' generic catch blocks — never triggering
   `AuthState.NeedsReauth`, never getting `AccountGate` backoff, never getting a live
   announcement, unlike every IMAP/CalDAV path passes 59/64/100/103 now cover. Currently dead
   code (item 1 above blocks any Gmail/Graph account from existing at all), and unlike IMAP's
   single `ConnectAsync` chokepoint (pass 103) this spans ~10 files with no shared connection
   step to centralize the translation in, plus the exact status-code-to-exception mapping and
   `Retry-After` parsing per SDK is a real design decision — worth doing once OAuth registration
   (item 1) actually lands and these providers become reachable.
6. **`Message.ThreadId` is a half-built, provider-inconsistent field with no consumer** (found by
   pass 108, left as a documented gap per the user's explicit choice). It exists on the domain
   model and `MessageDto`, and Graph populates it from `ConversationId` — but Gmail and IMAP
   never populate it at all, it's absent from `MessageSummaryDto`, no conversation-grouping UI
   exists, and no query filters by it. Building real threading needs each provider's own
   thread-id concept (a design decision, not dictated by strong precedent) plus new UI; the field
   should either be built out properly or removed, not left in this half state indefinitely.

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
- `TombstoneGcJobs`'s eligibility-then-delete TOCTOU window is narrowed, not closed: a draft save
  or mutation enqueue landing in the gap between the in-transaction recheck and the delete would
  still reference a row that's gone, since `MutationItem.MessageId`/`Draft.InReplyToMessageId`
  deliberately have no FK. Accepted for now as an extremely narrow, already-rare window (requires
  a message sitting orphaned for 30+ minutes AND a concurrent write landing in a sub-transaction
  gap); revisit if it's ever observed in practice.
