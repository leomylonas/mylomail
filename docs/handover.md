# Current handoff

## Completed work

- Completed the fourth architecture audit and remediated all seven evidence-backed findings. The scope, findings, fixes, and verification record are in `docs/architecture-audit-fourth-pass.md`.
- Stopped missing provider configuration from hot-looping mutation workers; unattempted claims return to durable pending state and the account transitions to announced `AuthState.Error`.
- Moved pending-notification recovery to `MailHub.OnConnectedAsync`, so initial SignalR connection and reconnect replay durable undelivered rows only after a receiver exists.
- Separated account-access failures from message corruption: authentication, configuration, and credential-store failures preserve content retry budgets, propagate across stale-fetch fences, and reconstruct work through bounded retry or `ResumeAccountAsync`.
- Added draft-job account lifecycle handling and centralized Graph MSAL translation. Administrator-consent failures retain structured Validation data and `AuthState.Error`; ordinary token failures retain `NeedsReauth`.
- Classified missing provider configuration during send as definitely unsent while preserving the synchronous `Dispatched` write. The outbox item returns to `Scheduled`, the attempt closes durably, and the worker pauses without retry.
- Preserved export manifest position across credential/auth/config failures, resumed active exports after account recovery, and retained cached export availability while provider access is paused.
- The first independent invariant review found two defects in the initial remediation—suppressed stale-fetch account failures and an over-broad export auth guard. Both were corrected. A fresh invariant recheck found no patch-introduced architectural violation.

## Verification

- `pnpm check`: format, TypeScript, ESLint, Stylelint, build, 694 .NET tests, and 225 Vitest tests passed.
- Focused regressions cover mutation-loop suppression/lease release, definite send classification, content budget and stale-fence behavior, draft lifecycle handling, Graph administrator consent, export retry/resume/cache behavior, account announcements, and notification replay through the hub connection lifecycle.
- Deep scenarios were not rerun because repository policy reserves `pnpm check:deep` for explicit requests. The patch leaves synchronous `Dispatched` and cursor/data commit boundaries unchanged; the previously recorded 61/61 fault-injection result remains the evidence for those boundaries.

## External verification still required

- **Gmail expired history:** provide `GMAIL_EXPIRED_HISTORY_ID` from an account with a genuinely expired history cursor. Gmail exposes no API to create one; cursor `1` is not a deterministic substitute.
- **Native Gmail and Graph RSVP:** configure attendee-owned invitations from second consenting Gmail and Microsoft 365 calendar accounts. Ordinary calendar CRUD/recurrence does not prove attendee response actions.

## Next task

- No fourth-pass source remediation remains. Complete the two deferred live fixture checks when the required external accounts/cursor are available.

## Required reading

- `AGENTS.md`
- `docs/architecture-audit-fourth-pass.md`
- `docs/reviews/invariant-review.md`
- The relevant guide under `docs/skills/` before changing persistence, providers, sync, mutations, fault injection, or the frontend shell.

## Live risks / decisions

- `AuthState.Error` and `NeedsReauth` pause runtime jobs until explicit account recovery; `CredentialStoreUnavailable` remains retryable because successful provider access self-clears it.
- Notification retention intentionally outlives delivery so a still-clickable OS notification can resolve through stable identity. Client-connect replay may duplicate an already-shown but unacknowledged notification; durable id deduplication makes that the accepted at-least-once tradeoff.
- Explicit application Quit removes Electron-owned GNOME notifications. Delivered records are not replayed on restart; minimize-to-tray remains the supported path for background notifications and scheduled work.
- Gmail expired-history coverage remains conditional rather than using a nondeterministic fake cursor. Native RSVP remains unclaimed until an invitation is owned by a different consenting account.
- `SecretService.Generated.cs` remains vendored from Tmds.DBus.Generator 0.95.0. If `SecretService.DBus.xml` changes, regenerate it and verify an unlocked native round trip plus Stryker.
