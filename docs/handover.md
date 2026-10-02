# Current handoff

## Completed work

- **Renderer rework (Outlook-style).** Three root causes of the broken look were fixed: Carbon publishes no `--cds-spacing-*` custom properties (defined in `Styles/Carbon.scss`), `GlobalTheme` only feeds React context so no colour tokens existed (`ThemeProvider` now puts `cds--white`/`cds--g100` on `<html>`, also set in `index.html`), and Carbon's reset left body `line-height: 1`. Settings are a modal `<dialog>` (`SettingsDialog`), not a reading-pane view. Command bar, rail, folder tree with role icons, avatar message rows with hover flag, compact reading pane with attachment chips, status bar, Drafts as an ordinary folder (`DraftList` in the list column), "Empty Trash/Spam", "View source", invert-colours viewing mode (not persisted), auto-select of the next message when the open one leaves the list.
- **Message rendering.** Frame styles are re-applied through the CSSOM (`ApplyMessageStyles`) because the frame inherits the app's `style-src 'self'`; dark theme inverts messages that declare no background (`DarkModeStyle`); printing uses a shadow-root copy so tall messages paginate, always on a light theme. The shell drops `Cross-Origin-Resource-Policy` from remote **image** responses only (`RemoteImagePolicy`), otherwise hosts such as claude.ai are blocked with `ERR_BLOCKED_BY_RESPONSE.NotSameOrigin` even after the user allowed remote content.
- **Keychain.** `LinuxSecretServiceCredentialStore` runs the desktop unlock prompt (`SecretPrompt`, `SecretUnlockGate` – one prompt at a time, 5 minute cooldown for background callers) and never calls Lock. Keyring operations are serialised, and `NativeCredentialStore` caches the encoded credential for the process lifetime. `POST /credential-store/unlock` backs the banner's "Unlock keychain" button. gnome-keyring-daemon has been crashing (SIGABRT, `coredumpctl list gnome-keyring-d`) when a client vanishes mid-request, and it restarts locked; trigger not proven. E2E runs now drop `DBUS_SESSION_BUS_ADDRESS` so they never touch the developer's keyring.
- **Content download pipeline.** One content chain per account (`SingleFlight`); newest-first ordering over the whole backlog; `ContentPriority` lets opened/visible/retried messages jump the queue (`PrioritiseContent`); failed downloads are retried at startup with a fresh attempt budget (they are not "problems" until they fail again); `GetContentQueue` feeds the status bar; `GetProblems`/`RetryFailedDownloads` feed the Problems dialog. Messages above the account's cap fail immediately with an explanation and are never fetched.
- **Per-account download cap.** `Account.MaxMessageDownloadMegabytes` (default 128, clamped 8–1024), migration `AddMaxMessageDownloadMegabytes` (existing rows get 128; its `Down` uses raw `DROP COLUMN`). Raising the cap requeues messages that were only too large.
- **Other fixes.** Mailbox unread/total counts are announced when topology reconciliation changes them; new-mail notifications skip mail sent by the account's own identities (Sent copies).

## Verification

- `pnpm check`: format, tsc, eslint, stylelint, build, 733 .NET tests, 245 Vitest passed. The .NET test host intermittently dies with "Internal CLR error (0x80131506)"; a rerun passes. Not investigated.
- Renderer e2e passed except `Locale` and the window-bounds restore test in `WindowBounds`, which fail identically on the tree before this work. Throwaway Electron scripts exercised: invert mode, Empty Trash, auto-advance after Delete and Move, the real Anthropic email with a trusted domain, print-to-PDF of a real multi-page email.
- Not run: `pnpm check:deep`; the new migration against a copy of the real database; the real GNOME unlock prompt.

## External verification still required

- **Gmail expired history:** provide `GMAIL_EXPIRED_HISTORY_ID` from an account with a genuinely expired history cursor. Gmail exposes no API to create one; cursor `1` is not a deterministic substitute.
- **Native Gmail and Graph RSVP:** configure attendee-owned invitations from second consenting Gmail and Microsoft 365 calendar accounts.

## Next task

- Watch `coredumpctl list gnome-keyring-d` after the credential cache and serialisation; if crashes continue, move to one long-lived D-Bus connection closed cleanly on shutdown.
- Possible notification flood after switching an account to full history (older mail was notified): confirm and, if real, gate notifications on message age/baseline.
- Date-group headers ("Last week", "This month") in the message list are not done.
- Do not restructure the frozen per-window shell/store/query boundaries.

## Required reading

- `AGENTS.md`
- `docs/architecture.md` §6, §12 and §13
- `docs/skills/frontend-shell.md`, `docs/skills/db-work.md`
- The relevant guide under `docs/skills/` before changing persistence, providers, sync, mutations, fault injection, or the frontend shell.

## Live risks / decisions

- Hub additions (`GetProblems`, `GetContentQueue`, `PrioritiseContent`, `RetryFailedDownloads`, `EmptyMailbox`) enqueue or read only; `EmptyMailbox` goes through the existing `DeletePermanently` mutation per message and refuses any folder whose effective role is not Trash or Junk.
- `ContentPriority` and the credential cache are in-memory by design (hints, not recovery state); the durable queue is `MessageContentState`.
- The legacy migration tests seed through the current model; they add and drop newer `Accounts` columns around the seed (`AddNewerAccountColumnsAsync`) and must be extended when another Account column is added.
- Gmail expired-history and native RSVP coverage remain conditional on external fixtures.
