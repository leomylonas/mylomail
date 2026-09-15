# Current handoff

## Completed work

- Completed the fifth architecture audit and remediated all seven findings. The scope, findings, fixes, and verification record are in `docs/architecture-audit-fifth-pass.md`.
- Moved the detached-compose durable-save barrier ahead of last-window minimize-to-tray handling. Tray interception now consumes close approval, so a restored compose must save again on its next close.
- Distinguished RFC 6578 `DAV:valid-sync-token` rejection from ordinary CalDAV 403 access denial. Only the marker resets the cursor; ordinary denial is a structured `ProviderRejected` account error.
- Persisted and announced `AuthState.Error` for missing contact-provider configuration across refresh, execution, and reconciliation while preserving Pending/Ambiguous intent semantics.
- Added a content-unavailable boundary for malformed stored MIME in attachment and inline-part reads, transported as `ProviderRejected`/HTTP 422.
- Made notification relay safe when the optional native bridge is absent; the durable notification remains pending for a later shell connection.
- Removed the redundant panel-layout debounce. Completed resizes now enter a serialized main-process IPC write chain, and normal quit drains every observed tail before stopping the backend.
- Actual Electron testing exposed and fixed `MailHub.GetDrafts` ordering `DateTimeOffset` inside SQLite. Drafts now materialize per account and sort client-side, so detached compose windows can load saved drafts.
- Added displayless Linux Electron support when a Wayland compositor is explicitly supplied through `WAYLAND_DISPLAY`; normal desktop and existing CI launch behavior is unchanged.

## Verification

- Final `pnpm check`: format, TypeScript, ESLint, Stylelint, build, 697 .NET tests, and 226 Vitest tests passed.
- Complete actual-Electron suite: 26 workflows passed; the packaged-native attachment workflow remained skipped by its existing environment gate.
- Focused Electron regressions passed for save-before-tray, save-after-restore, and immediate-close panel-layout persistence. Panel-layout persistence also passed three consecutive repetitions.
- Initial invariant review found no violation in CalDAV/contact changes. A later review found two close-order gaps in layout draining and reusable compose approval; both were corrected, and a fresh invariant review found no remaining violation.
- Deep scenarios were not rerun because repository policy reserves `pnpm check:deep` for explicit requests. No synchronous mutation dispatch or cursor/data commit boundary was changed.

## External verification still required

- **Gmail expired history:** provide `GMAIL_EXPIRED_HISTORY_ID` from an account with a genuinely expired history cursor. Gmail exposes no API to create one; cursor `1` is not a deterministic substitute.
- **Native Gmail and Graph RSVP:** configure attendee-owned invitations from second consenting Gmail and Microsoft 365 calendar accounts. Ordinary calendar CRUD/recurrence does not prove attendee response actions.

## Next task

- Architecture remediation is complete. Continue targeted visual and exploratory UI testing beyond the now-green end-to-end workflow suite, prioritizing responsive layout, keyboard-only flows, focus restoration, error presentation, and multi-window behavior.

## Required reading

- `AGENTS.md`
- `docs/architecture-audit-fifth-pass.md`
- `docs/reviews/invariant-review.md`
- The relevant guide under `docs/skills/` before changing persistence, providers, sync, mutations, fault injection, or the frontend shell.

## Live risks / decisions

- `AuthState.Error` and `NeedsReauth` pause runtime jobs until explicit account recovery; `CredentialStoreUnavailable` remains retryable because successful provider access self-clears it.
- Notification retention intentionally outlives delivery. An absent native bridge or failed display leaves the row pending; at-least-once replay and durable-id deduplication remain deliberate.
- Panel-layout persistence is global-default state. Existing windows keep independent live layouts; only the next window inherits the latest completed resize.
- Explicit application Quit removes Electron-owned GNOME notifications. Delivered records are not replayed on restart; minimize-to-tray remains the supported path for background notifications and scheduled work.
- Gmail expired-history and native RSVP coverage remain conditional on external fixtures rather than nondeterministic substitutes.
