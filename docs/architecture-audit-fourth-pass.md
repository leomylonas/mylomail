# Fourth architecture audit

Date: 2026-09-15

## Scope

Reviewed the current implementation against `AGENTS.md` and `docs/architecture.md` §§1–4, 6–10, 12–13, 15–16 after the third-pass remediation. The pass covered mutation ordering and dispatch, startup reconstruction, content/draft/outbox recovery, provider and credential boundaries, notification delivery, Electron/renderer lifecycle, SQLite/FTS/migrations, packaging, CI, and logging.

The two live fixture gaps remain deliberately deferred and are not source findings: deterministic Gmail expired-history recovery needs a genuinely expired cursor, and native Gmail/Graph RSVP needs attendee-owned invitations from second consenting accounts.

## Findings

### High — provider configuration can hot-loop the mutation drain

`MutationExecutor.ExecuteAsync` resolves `providers.For(account)` before creating an execution attempt. `MutationJobs.DrainAsync` handles `ProviderNotConfiguredException` only through its generic batch catch: it releases the untouched claim, reaches the unconditional successor enqueue, immediately reclaims the same item, and repeats. This was observed in the native application as hundreds of thousands of repeated missing-credential failures.

This violates §3 manual retry control and §6 bounded self-scheduling. A definite pre-dispatch configuration failure must leave every unattempted intent pending, transition and announce the account error, and stop until startup or explicit reauthentication reconstructs work.

### High — pending notifications have no client-readiness recovery

`StartupScheduler.ScheduleAsync` calls `NotificationService.RedispatchPendingAsync` before `app.StartAsync`; the backend is not listening and no SignalR renderer can exist. `IHubEvents.NotificationReadyAsync` succeeds with zero clients, and neither initial renderer connection nor SignalR reconnection requests another replay. The durable record therefore remains undelivered through every restart.

This violates §6 startup recovery and §13's at-least-once notification policy. Redispatch must happen when a SignalR client is connected, including reconnects; duplicate relays remain safe because Electron deduplicates by durable notification id and delivery is confirmed only after the OS notification is shown.

### High — account-access failures consume message-content retry budgets

`ContentAcquisition.AcquireAsync` special-cases throttling, credential-store unavailability, and network failure, but treats `ProviderAuthenticationException` and `ProviderNotConfiguredException` as unreadable-message failures. They consume `MessageContentState.Attempts`; `ContentJobs.FetchNextAsync` then self-schedules until readable content becomes permanently `Failed`. Its credential-store catch also returns without scheduling the recovery attempt its own comment promises.

This violates §3 auth/manual-retry behavior and §6 durable content recovery. Failures that occur before content can be evaluated must not consume the content corruption budget. Authentication pauses the account, missing configuration records an account error, and a temporarily unavailable credential store schedules a bounded retry.

### Medium — draft authentication failures bypass account lifecycle handling

`DraftSyncService.PushAsync` deliberately clears the initial-create claim and rethrows `ProviderAuthenticationException`, but `DraftJobs.PushAsync` catches only throttling and network failures. Because Hangfire retry is disabled, the durable dirty draft remains but the account is not transitioned or announced as requiring intervention.

This violates §3's auth pause and §7's `AccountStatusChanged` contract. The draft job must persist and announce the exception's account state while leaving the draft dirty for `ResumeAccountAsync`.

### Medium — Graph administrator-consent failures escape the provider boundary

Graph mail, calendar, and contact token acquisition catch `MsalException` only when it is not `AADSTS90094`. The administrator-consent form therefore escapes as a raw SDK exception. Background workers miss their provider-auth control path and apply generic retry/failure behavior, while interactive authentication correctly represents the same condition as structured Validation plus `AuthState.Error`.

This violates §§2, 5, and 15's provider abstraction and shared error taxonomy. Token-failure translation must be shared across Graph surfaces, preserve `adminConsentRequired`, and carry `AuthState.Error`; ordinary MSAL failures continue to carry `NeedsReauth`.

### Medium — missing provider configuration becomes an ambiguous send

`SendExecutor.SendAsync` writes `Dispatched` before resolving the provider. Its definite-failure catch covers throttling, authentication, and credential-store access but omits `ProviderNotConfiguredException`, so a factory failure is recorded as `AmbiguousOutcome` even though no send call occurred.

This violates §6's remote-uncertainty model and §15's outbox UX. The item must return to `Scheduled`, the local attempt must close as a definite pre-send failure, and the account worker must stop with an announced configuration error.

### Medium — transient account access permanently fails resumable exports

A cache miss during `ExportJobs.RunBatchAsync` calls `ContentAcquisition.AcquireAsync`. Credential-store, authentication, or provider-configuration failures fall into the export's generic catch and set the durable job to `Failed`, discarding automatic continuation from its frozen manifest. `ResumeAccountAsync` does not re-enqueue running exports.

This conflicts with §15's persisted, restart-resumable export contract and the shared credential/auth recovery model. Credential-store failure should retain `Running` and schedule; authentication/configuration should retain `Running` until reauthentication or startup re-enqueues it.

## Reviewed areas with no new finding

- Mutation sequence assignment, leased CAS ownership, execution-time provider identity, synchronous `Dispatched` persistence, ambiguous-attempt reconciliation, partial-batch handling, and tombstone references remain aligned with §6.
- Sync topology generations, coverage/live-stream separation, Gmail staging, Graph delta completion, IMAP capability tiers, and cursor/data transaction boundaries remain aligned with §§1 and 3.
- Graph clients use the immutable-id pipeline; provider mailbox and occurrence identities remain in the correct persistence locations.
- Credential authority, native/fallback selection, TLS pinning, OAuth secret transport, and no-PII logging remain aligned with §§4, 9, 10, and 15.
- Electron spawn/attach cleanup, launch-token secrecy, CSP/navigation/request blocking, tray/quit behavior, native notification deduplication, compose close barriers, per-window state, and reconnect cache invalidation remain aligned with §§7, 9, and 12.
- SQLite connection pragmas, migration backup, local-filesystem enforcement, FTS repair/tombstone coupling, bundled runtime paths, artifact verification, release matrix, telemetry, and generated-type drift checks remain aligned with §§8, 9, 12, 15, and 16.

## Required remediation evidence

- Focused tests must distinguish account-access failures from provider/content failures and prove no immediate mutation successor is scheduled for missing configuration.
- Existing dispatch-boundary and startup-recovery fault-injection scenarios must remain green; notification readiness needs an observable connected-client smoke path.
- Changes to mutation, sync/recovery, or persistence require the independent invariant review in `docs/reviews/invariant-review.md`.
- Final acceptance is `pnpm check`; the two user-deferred live fixtures remain explicitly unclaimed.

## Remediation completed

- `MutationJobs.DrainAsync` now treats missing provider configuration as an account gate in both preflight and claimed-batch paths. It persists and announces `AuthState.Error`, releases every unattempted claim, and returns without enqueuing another drain.
- Pending notifications now replay from `MailHub.OnConnectedAsync`, covering first connection and reconnect after a receiver exists. Startup no longer broadcasts into an empty SignalR client set. Durable ids preserve Electron-side deduplication.
- Authentication, configuration, and credential-store failures no longer consume `MessageContentState.Attempts`. Account-level failures still propagate when the issued occurrence becomes stale; credential-store failure schedules a bounded 30-second content retry. `ResumeAccountAsync` explicitly reconstructs content work.
- `DraftJobs` now persists and announces authentication/configuration account state. The dirty draft remains the recovery source, and initial-create claims retain the existing definite-versus-ambiguous handling.
- Graph mail, calendar, and contact token acquisition share one MSAL translation. `AADSTS90094` preserves the structured administrator-consent Validation problem and `AuthState.Error`; ordinary token failures carry `NeedsReauth`. Background account handlers now honor the translated state.
- Missing provider configuration during send is closed as a definite unsent attempt: the outbox item returns to `Scheduled`, `ResultPersistedAt` closes the attempt, and `OutboxJobs` records the account error without retrying. The synchronous pre-call `Dispatched` write remains unchanged.
- Export cache misses retain `Running` and the exact manifest position across credential, authentication, and configuration failures. Credential-store failure schedules a bounded retry; reauthentication reconstructs active exports. Cached exports remain available while provider access is paused.
- Runtime schedulers stop retrying accounts in `NeedsReauth` or `Error`; `CredentialStoreUnavailable` remains retryable and self-clears after successful provider access.

## Verification

- `pnpm check`: format, TypeScript, ESLint, Stylelint, build, 694 .NET tests, and 225 Vitest tests passed.
- Focused regressions cover mutation-loop suppression and lease release, definite send classification, account-state announcements, content retry-budget preservation including stale fetches, draft recovery, Graph administrator-consent translation, export position/retry/resume behavior, cached export availability, and pending-notification replay through `MailHub.OnConnectedAsync`.
- The first independent invariant review identified two violations in the initial remediation: stale content fencing could suppress an account failure, and an export-level auth guard blocked fully cached export. Both were corrected and covered by regression tests. A fresh invariant recheck found no patch-introduced cursor, dispatch, identity, lease, ambiguity, startup-recovery, cached-data, or notification-delivery violation.
- Existing deep dispatch-boundary and process-crash scenarios were not rerun: repository policy reserves `pnpm check:deep` for an explicit request. This patch does not move the synchronous `Dispatched` boundary or cursor/data commits; the previously recorded 61/61 deep fault-injection result remains the evidence for those unchanged boundaries.
- Live Gmail expired-history recovery and native Gmail/Graph RSVP remain unclaimed and explicitly deferred pending the external fixtures described above.
