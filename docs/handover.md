# Current handoff

## Completed

- Ambiguous mutation reconciliation and periodic degraded-IMAP integrity reconciliation are implemented and verified.
- The backend production credential store probes Windows DPAPI, macOS Keychain Services, or Linux Secret Service using a temporary native credential. It selects a usable native store rather than inferring availability from the OS.
- If no native store is usable, `MYLOMAIL_MASTER_PASSWORD` enables the encrypted SQLite fallback. It uses PBKDF2-SHA256 (600,000 iterations), a random salt, an AES-GCM verifier, and per-credential AES-GCM ciphertext. Passwords and provider credentials are never stored in plaintext.
- If neither native storage nor a master password is available (or the password is wrong), startup exits with code `78` before scheduling work. Electron can use that code to prompt, then restart the backend with the per-launch password.
- Independent invariant review of the credential fallback and persistence changes found no architectural violations.
- Compose is a Lexical rich-text editor. **HTML is the stored form, never Lexical's own JSON
  state**: a draft's body is HTML and a sent message is MIME, so persisting editor state would
  tie the mail format to an editor version. The e2e asserts the formatting survives to the SMTP
  server, not merely to the editor.
- Server-side drafts are implemented for IMAP, which removed the last provider stub. An update
  is an append followed by an expunge of the old copy, in that order — the reverse loses the
  draft if the append fails.
- **Folder management works from the sidebar** — create, rename, delete, with the tree nested
  by parent. Three bugs were behind it, none visible to the unit suite:
  - `window.prompt` **throws** in Electron ("prompt() is not supported"), so create and rename
    did nothing at all in the packaged app while working in a browser. Both now use a Carbon
    modal. The folder mutations also had no error path, so a failure was indistinguishable
    from a click being ignored; failures now surface as notifications.
  - `TopologySyncService` loaded existing mailboxes **without** their `ImapMetadata`, so every
    reconciliation after the first attached a second metadata row and failed on the unique
    key. That is every folder operation on an IMAP account, and every topology poll after the
    first.
  - A rename **destroyed local mailbox identity**: an IMAP folder's provider id is its full
    path, so reconciliation saw the old id vanish and created a new row. §6 requires a rename
    to leave queued work valid, and only deletion to invalidate it. `RenameMailboxAsync` and
    `MoveMailboxAsync` now return the folder as the server names it, and `MailboxManagement`
    carries that onto the existing row — and onto descendants, whose paths begin with their
    ancestor's.
- The delete confirmation states what the provider actually does, read from
  `GetAccountCapabilities` rather than assumed: IMAP and Graph destroy the messages, Gmail
  leaves them in All Mail.
- **Message identity is now scoped to a mailbox.** `MessageIngestor` matched occurrences on
  `ProviderOccurrenceId` alone; an IMAP UID is unique only within a folder, so a draft holding
  UID 2 merged with the unrelated inbox message holding UID 2 — the unrecoverable direction of
  §1. The Drafts exclusion also ran after matching, letting a draft overwrite a real message,
  and skipping only drafts occurrences would double-materialise a Gmail draft that carries
  DRAFT alongside another label (found by invariant review). `MessageIdentityTests` covers all
  three, each checked to fail with its half of the fix removed.
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
   TanStack Router/Virtual/Table. HTML body rendering, compose and send, server-side drafts,
   the Lexical editor and the toast/context-menu/shortcut/error registries are done.
4. **The last §7 events without producers**: `ExportProgress`, `DraftUpdated`,
   `CalendarEventUpdated`, `CalendarConflictDetected` and `ConnectivityChanged` — each waiting
   on a feature that does not exist yet, rather than on wiring. `IMailClient` declares all of them so none is orphaned,
   but only `SyncProgress` has a producer wired. `IHubEvents` is the seam — services depend on
   it, not on SignalR, so they stay testable.
5. **Attachments are not implemented — the next task, and untouched.** `Attachment` rows are
   parsed at ingest and `HasNonInlineAttachments` reaches the message list, and that is the
   whole of it: no way to see, open or save one, and no way to add one to a message being
   composed. §9 specifies the receiving half in detail — extraction to
   `<DataDirectory>/tmp/attachments/<guid>/<filename>`, `0700`/`0600` set at creation, the
   executable bit never set, MIME filenames sanitised as untrusted input before any
   filesystem use, an explicit confirmation before opening anything that can run code, and a
   sweep of that directory at startup. The sending half needs somewhere to hold a draft's
   attachment bytes before send: `DraftAttachment` carries metadata only, and §1's
   no-`AttachmentBlob` rule is about received mail, where raw MIME already holds the bytes.
6. **Inline images do not render — unresolved.** `cid:` references survive sanitisation and
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
7. **Remote drafts are not materialised locally.** The outbound half works — a draft saved
   here reaches the server — but a draft started on another device does not become a local
   `Draft`. The Drafts mailbox is correctly excluded from message materialisation, so it does
   not appear as mail either; it simply is not shown.

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

- `pnpm check` under Node 22: green — format, tsc, eslint, stylelint, build, tests(125), vitest(35).
- Playwright e2e: the four earlier specs and the new `Mailboxes` spec pass individually; the
  full suite in one run has not been re-run since the mailbox work landed.
- `ImapLiveMailboxTests` drives folder lifecycle against Dovecot. Both fixes above were
  checked to fail with that fix reverted. It is `Category=Conformance,Deep`, so it needs
  `pnpm imap:up` and `TEST_IMAP_QRESYNC_HOST`/`PORT`.
  The identity bug above was found by that suite and by nothing else: it appeared only when
  specs ran in order, because it needed a leftover draft on the server to collide with.
- `pnpm check:deep` under Node 22: green — conformance(60), fault-injection(21), mutation testing.
- Mutation scope expanded with the new reconciliation services and integrity loop. The measured baseline is 47.23% (306 killed / 134 survived / 9 timeout), so `stryker-config.json` resets the ratchet to 47, just below that baseline. Raise it as survivors are killed.

## Live risks / decisions

- `MutationReconciler` observes full mailbox pages and uses provider stable id, prior occurrence id, then `(Message-ID, ReceivedAt)` matching. If desired state cannot be established, it requeues rather than guessing.
- Node 22 is required: `source "$HOME/.nvm/nvm.sh" && nvm use` before wrappers.
