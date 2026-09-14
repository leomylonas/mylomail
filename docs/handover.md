# Current handoff

## Completed work

- Completed the third-pass architecture audit and remediation. The immutable baseline findings, per-area resolution summary, verification-gap disposition, and evidence are in `docs/architecture-audit-third-pass.md`.
- Closed every high-, medium-, and lower-severity source finding across Gmail/Graph/IMAP semantics, cursor and topology ordering, mutation/outbox crash recovery, persistence and credential cleanup, Electron lifecycle/security, renderer multi-window behavior, packaging, CI, and documentation.
- Added durable process-kill coverage for staged sync replay and tombstone/FTS deletion, mixed partial-batch mutation recovery, production startup dispatch coverage, and a real FTS rebuild migration test.
- Completed independent invariant and UI lifecycle reviews. Their follow-up findings were repaired, including startup lease ownership, the covered-IMAP baseline gate, Graph draft identity/revision recovery, ambiguous resend reuse, attachment/save close barriers, recurring conflict refresh, and tray-hidden relaunch.
- Fixed the Electron E2E disconnect exposed during final verification. `createMailHubConnection` copied TypedSignalR's enumerable private `connection` field onto SignalR's `HubConnection`, causing recursive `start()` calls. The adapter now copies generated invocation functions only.
- Vendored `Credentials/SecretService.Generated.cs` from Tmds.DBus.Generator 0.95.0 and retained `SecretService.DBus.xml` as its source schema. Stryker's Roslyn rebuild drops the generator's required `AdditionalFiles` metadata; removing the build-time generator restored deterministic mutation testing without changing the D-Bus protocol implementation.
- Completed headed GNOME notification/tray verification. Electron 44.3.0 restored the StatusNotifier tray; close-to-tray restore and native Quit passed. Notifications now use the `Mylo Mail` product identity, retain click handlers for notification-centre activation, route from minimized/maximized/hidden windows, leave Calendar for the target mail view, and synchronize the selected list row with the reading pane.
- Live IMAP notification verification exposed two source defects: live sync now schedules content acquisition after durable change application, and Linux startup now treats a locked-but-present Secret Service collection as native rather than silently selecting an empty master-password fallback.

## Verification

- `pnpm check`: format, TypeScript, ESLint, Stylelint, build, 674 .NET tests, and 225 Vitest tests passed.
- `pnpm e2e`: 24 actual Electron workflows passed; the packaged/native-platform-only case skipped by design.
- `pnpm package:smoke`: the unpacked production application launched the bundled self-contained backend and renderer.
- Deep conformance: 65/65 passed.
- Deep fault injection: 61/61 passed, including the hard-process staged-history and tombstone/FTS crash scenarios.
- Stryker mutation discrimination passed after removing the incompatible build-time D-Bus source generator.
- Focused live Gmail mail verification passed 12 cases when the expired-history case was correctly left conditional.
- The native credential-store path reached the unlocked GNOME Secret Service, retrieved the existing IMAP credential, cleared every stream error, and drained the pending mutation without falling back to SQLite.
- The final independent invariant review found no cursor, crash-recovery, stable-identity, or credential-authority violations in the live-content scheduling and locked-keyring changes.

## External verification still required

- **Gmail expired history:** provide `GMAIL_EXPIRED_HISTORY_ID` from an account with a genuinely expired history cursor. Gmail exposes no API to create one. Cursor `1` was probed and is not a valid deterministic expired fixture for the configured account.
- **Native Gmail and Graph RSVP:** configure invitations owned by second consenting Gmail and Microsoft 365 calendar accounts. The current live credentials cannot deterministically create an attendee-owned invitation for themselves.

## Next task

Complete the fourth-pass architecture review, then remediate and verify the two evidence-backed findings already isolated:

1. `MutationJobs.DrainAsync` hot-loops on `ProviderNotConfiguredException`. `MutationExecutor.ExecuteAsync` resolves `providers.For(account)` before creating an attempt, but the job's generic catch releases the untouched claim and immediately enqueues another drain. This was observed natively as hundreds of thousands of repeated missing-credential failures.
2. Pending notifications have no renderer-readiness replay. `StartupScheduler` calls `NotificationService.RedispatchPendingAsync` before a SignalR renderer can connect; `IHubEvents.NotificationReadyAsync` succeeds with zero clients, and nothing redispatches when a client later becomes ready. The durable row remains undelivered across restarts.

Continue the review for analogous provider-configuration, retry-ownership, credential recovery, and lifecycle gaps before editing. Add the fourth audit report, focused regression/fault-injection coverage required by the affected domains, an independent invariant review, and a green `pnpm check`.

## Required reading

- `AGENTS.md`
- `docs/architecture-audit-third-pass.md`
- `docs/reviews/invariant-review.md`
- The relevant guide under `docs/skills/` before changing persistence, providers, sync, mutations, fault injection, or the frontend shell.

## Fourth-pass review in progress

- Confirmed the two findings above from source and prior native runtime evidence.
- Reviewed mutation dispatch ordering, outbox provider acquisition, notification durability/replay, startup reconstruction, draft credential recovery, provider-id storage, retry annotations, sync transactions, Graph request construction, renderer import/export conventions, and structured logging.
- No fourth-pass source changes or verification claims have been made yet.

## Live risks / decisions

- Notification retention intentionally outlives delivery so a still-clickable OS notification can resolve through stable provider identity; GC detaches expired navigation links transactionally.
- Explicit application Quit removes Electron-owned GNOME notifications. Delivered records are not replayed on restart; minimize-to-tray is the supported path when notifications and background work should remain active.
- Gmail expired-history live coverage remains conditional rather than using a fake cursor that may be accepted or rejected nondeterministically.
- Native RSVP remains unclaimed until an invitation is owned by a different consenting account; ordinary calendar CRUD/recurrence live success is not evidence for attendee response actions.
- The D-Bus proxy is checked in. If `SecretService.DBus.xml` changes, regenerate `SecretService.Generated.cs` with Tmds.DBus.Generator 0.95.0 and verify an unlocked native round trip plus Stryker.
