# Current handoff

## Completed

- Ambiguous mutation reconciliation and periodic degraded-IMAP integrity reconciliation are implemented and verified.
- The backend production credential store probes Windows DPAPI, macOS Keychain Services, or Linux Secret Service using a temporary native credential. It selects a usable native store rather than inferring availability from the OS.
- If no native store is usable, `MYLOMAIL_MASTER_PASSWORD` enables the encrypted SQLite fallback. It uses PBKDF2-SHA256 (600,000 iterations), a random salt, an AES-GCM verifier, and per-credential AES-GCM ciphertext. Passwords and provider credentials are never stored in plaintext.
- If neither native storage nor a master password is available (or the password is wrong), startup exits with code `78` before scheduling work. Electron can use that code to prompt, then restart the backend with the per-launch password.
- Independent invariant review of the credential fallback and persistence changes found no architectural violations.
- `startBackend` in the Electron shell implements the restart protocol and is unit-tested: it strips any inherited master password on the initial launch, issues a fresh launch token for each process, prompts only after code `78`, and passes the password only to the restarted process.

## Next task

1. **Wire `startBackend` into `Main.ts`** with the real setup/unlock dialog. The supervisor,
   the health probe and the backend contract all exist and are tested; nothing calls them
   from the actual Electron entry point yet.
2. **Supply the actual client ids.** The mechanism and its documentation exist (`AGENTS.md`,
   Provider client registration); no values are configured anywhere.
   Previously: `MailProviderFactory` now builds
   real providers, but nothing supplies `Providers:Gmail:ClientId`/`ClientSecret` or
   `Providers:Graph:ClientId`, and §5 records that distributing Gmail's secret in a desktop
   binary is unresolved. IMAP needs an `ImapProviderConfig` on the account plus a password
   stored under `MailProviderFactory.ImapPasswordFormat`.
3. **Continue Stage D** (§12, §13). The foundation is in: per-window store and query client,
   the hub wired to cache invalidation, a Carbon sidebar and message list. Still to build —
   HTML body rendering, TanStack Router/Virtual/Table, compose with
   Lexical, and the toast/context-menu/shortcut/error registries.
4. **The last §7 events without producers**: `ExportProgress`, `DraftUpdated`,
   `CalendarEventUpdated`, `CalendarConflictDetected` and `ConnectivityChanged` — each waiting
   on a feature that does not exist yet, rather than on wiring. `IMailClient` declares all of them so none is orphaned,
   but only `SyncProgress` has a producer wired. `IHubEvents` is the seam — services depend on
   it, not on SignalR, so they stay testable.
5. **Inline images do not render — unresolved.** `cid:` references survive sanitisation and
   should be rewritten to blob URLs by `resolveInlineImages`, but in the running app the
   rewrite never completes. What is ruled out: the rewrite logic (unit-tested with a stub
   fetch), the part endpoint (verified by hand — 200, correct content type and length, under
   both bearer and cookie auth), routing (a valid-but-unknown id returns the controller's own
   404), and the sanitiser stripping `cid:` (fixed, with a test). What is left: the fetch
   inside the component appears never to settle, and the failure is invisible because the
   shell forwards backend stderr but Playwright does not surface Electron output. Instrument
   the component to write its outcome into the DOM rather than the console — every probe
   through the console or a compound `page.evaluate` cost a full run and produced ambiguity.
   The e2e asserts the current behaviour (`src` stays `cid:`) so it keeps guarding the
   security properties instead of being disabled.
6. **Rich composition.** The compose body is a plain textarea escaped into HTML. §12
   specifies Lexical; a hand-rolled editor would have to be unbuilt, so the textarea is
   deliberately temporary while the send path underneath it is complete and tested.
7. **Server-side drafts.** Drafts are local only. `CreateOrUpdateDraftAsync` and
   `DeleteDraftAsync` still throw on IMAP, so a draft started here never appears on another
   device.

### Credential storage decision

The .NET backend owns `ICredentialStore` and accesses Windows DPAPI, macOS Keychain Services,
or Linux Secret Service/libsecret directly. Electron never accesses provider credentials; it
only presents master-password setup/unlock UI and supplies the password only to the restarted
backend launch when the encrypted SQLite fallback is active.

If no native store is usable, a backend launch without a master password exits with a dedicated
recoverable code. Electron catches it, prompts for setup/unlock, and restarts the backend with
the per-launch password. The backend derives an in-memory key, validates a persisted verifier,
and encrypts fallback credentials in SQLite; it never persists the password.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §§3, 6, 9 and 16
- `docs/skills/fault-injection.md`
- `docs/reviews/invariant-review.md`

## Verification

- `pnpm check` under Node 22: green — format, tsc, eslint, stylelint, build, tests(76), vitest(2).
- `pnpm check:deep` under Node 22: green — conformance(60), fault-injection(21), mutation testing.
- Mutation scope expanded with the new reconciliation services and integrity loop. The measured baseline is 47.23% (306 killed / 134 survived / 9 timeout), so `stryker-config.json` resets the ratchet to 47, just below that baseline. Raise it as survivors are killed.

## Live risks / decisions

- `MutationReconciler` observes full mailbox pages and uses provider stable id, prior occurrence id, then `(Message-ID, ReceivedAt)` matching. If desired state cannot be established, it requeues rather than guessing.
- Node 22 is required: `source "$HOME/.nvm/nvm.sh" && nvm use` before wrappers.
