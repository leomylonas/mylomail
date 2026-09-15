# Fifth architecture audit

Date: 2026-09-15

## Scope

Reviewed the implementation after fourth-pass remediation against `AGENTS.md` and `docs/architecture.md` §§1–10, 12–13, and 15–16. The pass covered mutation/sync/persistence recovery, provider and credential boundaries, backend API/error transport, Electron lifecycle and security, renderer state/reconnect/accessibility, and production packaging.

The user-deferred Gmail expired-history and native Gmail/Graph RSVP fixtures remain external verification gaps, not source findings.

## Findings

### High — minimize-to-tray bypasses the detached-compose save barrier

`apps/electron-shell/src/Main.ts` handles the last-window `MinimizeToTray` branch before its detached-compose close barrier. When a detached compose is the sole window, closing it hides the window and returns without asking the renderer to durably save or confirm failure.

This violates §13's detached-compose durability contract. The compose barrier must run first; only a re-entered, save-approved close may proceed to ordinary last-window handling.

### Medium — CalDAV forbidden responses escape the provider taxonomy

`CalDavCalendarProvider.SendAsync` translates 401 and 429 but leaves ordinary HTTP 403 responses for `EnsureSuccessStatusCode`, which throws a raw `HttpRequestException`. A valid account denied calendar access therefore looks like a transient network failure instead of a permanent, actionable provider rejection.

This violates the shared provider/error boundary. A non-cursor 403 must become a structured account-level provider rejection and pause background work with `AuthState.Error`.

### Medium — CalDAV sync treats every forbidden response as cursor expiry

`CalDavCalendarProvider.SyncCalendarAsync` maps every 403 received with a stored cursor to `ProviderCursorInvalidException`. RFC 6578 uses 403 specifically with the `DAV:valid-sync-token` precondition for an invalid token, but 403 also represents revoked collection access. The current code resets coverage for both and can repeatedly resynchronise an account whose authorization was actually denied.

Cursor invalidation must require the `DAV:valid-sync-token` response marker (while retaining the existing 409/412 compatibility path); any other 403 is an account-level provider rejection.

### Medium — contact configuration failures silently strand durable work

`ContactService.RefreshAsync`, `ExecuteAsync`, and `ReconcileAsync` catch `ProviderNotConfiguredException` and return without changing `Account.AuthState`, recording `LastAuthError`, or broadcasting `AccountStatusChanged`. Refresh silently stops; a pending operation remains `Pending` and is redispatched at startup/reauthentication with no actionable account state.

This violates the account error/manual-recovery lifecycle used by mutation, content, drafts, outbox, and exports. Contact configuration failure must persist and announce `AuthState.Error` while leaving unattempted intent pending.

### Medium — malformed stored MIME lacks a content-unavailable boundary

`MessagePartsController.Get` and `AttachmentService.ReadAsync` parse and decode provider-authored raw MIME without translating parser, safety-limit, or decode failures into a content-specific problem. Inline-part and attachment requests can therefore surface generic Unknown/Validation transport errors that the renderer cannot distinguish from an application failure.

This violates the shared `MutationProblemDetails` contract. Stored MIME parse/validation/decode failures must map to a structured `ProviderRejected`/content-unavailable problem without misclassifying cancellation or unrelated backend defects.

### Medium — an absent notification preload bridge throws in the receiver

`apps/renderer/src/Shell/Backend/HubConnection.ts` calls `.then` on the result of `window.notifications?.show(...)`. When the optional bridge is absent, optional chaining returns `undefined` and the subsequent `.then` throws. The pending notification remains unacknowledged, but the renderer also produces an avoidable event-handler failure on every replay.

The receiver must return cleanly when shell integration is unavailable, preserving the durable pending row for a later connected shell.

### Medium — panel layout teardown drops the final resize

`apps/renderer/src/Shell/Layout/UseShellLayout.ts` debounces persistence for 500 ms and clears the timer during unmount without flushing the captured layout. Closing or reloading a window immediately after resizing therefore causes the next window to inherit the previous default.

This violates §13's persisted global panel-default workflow. Teardown must flush the pending layout with a keepalive request while preserving live per-window independence.

## Reviewed areas with no new finding

- Mutation sequencing, execution-time provider identity, synchronous `Dispatched` writes, ambiguous reconciliation, outbox send classification, sync cursor/data transactions, topology generations, coverage, staging, content retry budgets, export manifests, startup reconstruction, SQLite/FTS, and tombstones remain aligned.
- Graph immutable-id clients, Gmail account-scoped history, IMAP folder-scoped identity, TLS pinning, credential authority, Google/Graph calendar and contact boundaries, throttling, and OAuth translation remain aligned outside the CalDAV/contact findings above.
- SignalR event inventory, durable notification records, launch-token protection, telemetry privacy, attachment temp-path security, backend supervision, CSP/navigation blocking, packaging/runtime paths, per-window stores/query clients, reconnect invalidation, optimistic mail projection, virtualized keyboard navigation, calendar accessibility, HTML isolation, and remote-content enforcement showed no additional source finding.

## Required remediation evidence

- Focused regressions must cover CalDAV 403 discrimination, contact account-state announcements, MIME content-error mapping, absent notification bridge handling, and layout teardown persistence.
- The detached-compose/minimize ordering and layout persistence must be exercised in the actual Electron surface.
- Any mutation/sync/persistence-adjacent changes require an independent invariant review; final acceptance is `pnpm check`.

## Remediation

- Detached compose windows now cross the durable-save close barrier before last-window tray handling. The approved close may then hide the saved window according to the configured policy, and tray interception consumes that approval so every later close crosses a fresh barrier.
- CalDAV preserves 403 responses long enough to distinguish the RFC 6578 `DAV:valid-sync-token` precondition from ordinary collection access denial. Only the former resets the cursor; the latter is a structured `ProviderRejected` account error.
- Contact refresh, execution, and reconciliation persist and announce `AuthState.Error` on missing provider configuration without marking unattempted intent dispatched or changing ambiguous intent.
- Attachment and inline-part MIME parsing, structural validation, and decoding now share a `MessageContentUnavailableException`, transported as `ProviderRejected` with HTTP 422.
- Notification relay returns cleanly when no native preload bridge exists and leaves the durable notification pending.
- Completed panel resizes are dispatched immediately through a serialized main-process IPC write chain. Normal quit drains the current tail and every tail appended while quit is prevented before stopping the backend; browser/attach renderers retain the authenticated HTTP fallback.

## UI testing findings

The actual Electron suite exposed one additional backend defect: `MailHub.GetDrafts` ordered `Draft.SavedAt` inside SQLite, whose EF provider cannot translate `DateTimeOffset` ordering. A detached compose therefore opened onto “This draft could not be found” even though the row was present. Drafts are now materialized for the account first and ordered in memory.

The displayless Linux harness can now select an available Wayland compositor through `WAYLAND_DISPLAY`; ordinary desktop and CI launch behavior is unchanged.

## Verification

- Independent invariant review found no violation in the CalDAV cursor discrimination or contact durable-operation state changes.
- A later review found two close-order defects: quit could miss a layout write appended while it waited, and tray interception left detached-compose close approval reusable. Both were corrected; a fresh reviewer found no remaining invariant violation.
- Final `pnpm check` passed: format, TypeScript, ESLint, Stylelint, build, 697 .NET tests, and 226 Vitest tests.
- Focused actual-Electron regressions passed for detached compose save-before-tray, save-after-restore, and final panel-layout persistence; the latter also passed three consecutive repetitions.
- The complete actual-Electron suite passed 26 workflows with one packaged-native attachment test skipped by its existing environment gate. It covered account setup, CalDAV calendar CRUD, SMTP compose/invites, crash/reconnect recovery, drafts, hostile HTML, locale rendering, mailbox mutations, mail flow, mailto, startup failure, minimum layout, window bounds, and immediate-close layout persistence.
