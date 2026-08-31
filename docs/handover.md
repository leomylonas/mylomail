# Current handoff

This is a snapshot of what's true now, not a log — see git history for how it got here.

## State

**Backend core (Stage C) is complete and verified**: persistence, mutation chains, sync state
machines, jobs, send/outbox, credential storage. IMAP has no remaining stubs — folder
lifecycle, server-side drafts, and send are all implemented.

**Credential storage**: the .NET backend owns `ICredentialStore` and accesses Windows DPAPI,
macOS Keychain Services, or Linux Secret Service/libsecret directly, probed with a temporary
native credential rather than inferred from the OS. If no native store is usable,
`MYLOMAIL_MASTER_PASSWORD` enables an encrypted SQLite fallback (PBKDF2-SHA256 600k
iterations, AES-GCM verifier and per-credential ciphertext; nothing is ever stored in
plaintext). If neither is available, or the password is wrong, startup exits with code `78`
before scheduling work — this is what Electron watches for. Electron never accesses provider
credentials; it only presents setup/unlock UI and passes the password to the restart.
`startBackend` implements this restart protocol and is unit-tested, but **is not yet wired
into `Main.ts`** — the real entry point still needs the setup/unlock dialog calling it.

**Stage D (renderer) is mostly built**: per-window store and query client, the hub wired to
cache invalidation, sidebar with nested folder management, message list, HTML rendering,
compose and send via a Lexical editor (HTML is the stored form — never Lexical's own JSON
state, since a sent message is MIME and persisting editor state would tie the mail format to
an editor version), server-side drafts, and attachments. Received attachments are listed and
can be saved or opened; the latter decodes the stored raw MIME into a private temp copy and
uses a native Electron confirmation for dangerous file types. Compose accepts file-picker and
drag/drop attachments; draft attachment bytes are structured authoring state and IMAP emits
them as MIME attachments. Not built: TanStack Router/Virtual/Table, remote-draft
materialisation, inline image rendering.

**No provider client ids are configured.** The mechanism exists (`AGENTS.md`, "Provider
client registration"); nothing supplies `Providers:Gmail:ClientId`/`ClientSecret` or
`Providers:Graph:ClientId`. §5 records that distributing Gmail's secret in a desktop binary
is unresolved. IMAP additionally needs an `ImapProviderConfig` on the account plus a password
stored under `MailProviderFactory.ImapPasswordFormat`.

**Recent invariant fix worth knowing about**: `MessageIngestor` used to match provider
occurrences on `ProviderOccurrenceId` alone. An IMAP UID is unique only within its folder, so
two unrelated messages sharing a UID in different folders could merge into one local row —
the unrecoverable direction of §1. Matching is now scoped to the mailbox, the Drafts exclusion
runs before matching (not after), and it excludes on any drafts occurrence rather than all
occurrences (a Gmail draft carries `DRAFT` alongside other labels). `MessageIdentityTests`
covers all three; each was checked to fail with its half of the fix reverted. If you touch
`MessageIngestor` again, re-run that discrimination check rather than trusting the test alone.

## Next task

1. **Wire `startBackend` into `Main.ts`** with the real setup/unlock dialog.
2. **Supply real provider client ids** (see above).
3. **`cid:` inline images don't render.** Rewrite logic, the part endpoint, routing, and the
   sanitiser are all individually verified correct; what's left is that the fetch inside the
   rendering component never settles, and the failure is invisible because Playwright doesn't
   surface Electron's renderer console. Instrument the component to write its outcome into the
   DOM rather than console.log before debugging further — every console-based probe so far
   cost a full run and produced ambiguity. The e2e currently asserts the safe fallback
   behaviour (`src` stays `cid:`), so it isn't regressing anything while this is open.
4. **Remote drafts aren't materialised locally.** Outbound works (a draft saved here reaches
   the server); a draft started elsewhere doesn't become a local `Draft`. It's correctly
   excluded from message materialisation, so it also doesn't wrongly appear as mail — it's
   just invisible.
5. **§7 events without producers**: `ExportProgress`, `DraftUpdated`, `CalendarEventUpdated`,
   `CalendarConflictDetected`, `ConnectivityChanged`. `IMailClient` declares all of them so
   none is orphaned; only `SyncProgress` currently fires. Each is blocked on the feature that
   would produce it, not on wiring — `DraftUpdated` and `ConnectivityChanged` are probably
   closest now that drafts and folder management exist.
6. **Calendar is unstarted.** `ICalendarProvider` is an interface with no implementation, no
   domain model, no sync, no UI — effectively a second Stage C.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §§1, 2, 3, 6, 9, 16
- `docs/skills/fault-injection.md` — and specifically the discrimination-check discipline:
  three tests in this repo have passed against the exact bug they existed to catch. Never
  trust a fault-injection or identity test without reverting the fix and watching it fail.
- `docs/reviews/invariant-review.md`

## Verification

- `pnpm check` under Node 22: green — format, tsc, eslint, stylelint, build, tests(126),
  vitest(35).
- All five Playwright e2e specs pass together in one run against the local Dovecot matrix and
  Mailpit sink (`pnpm imap:up` first). If Playwright says `Process failed to launch!` before
  every spec, remove `ELECTRON_RUN_AS_NODE` for that invocation; it turns Electron into Node.
- `pnpm check:deep` under Node 22: green — conformance(60), fault-injection(21), mutation
  testing. Mutation ratchet is 47 (measured baseline 47.23%, 306 killed / 134 survived / 9
  timeout) — raise it as survivors are killed, don't lower it to make a run pass.
- `ImapLiveMailboxTests` and `ImapLiveSyncTests` need `pnpm imap:up` plus
  `TEST_IMAP_QRESYNC_HOST`/`PORT` env vars; they're `Category=Conformance,Deep` and skipped by
  default. The message-identity bug above was found only by the full e2e suite running specs
  in order against a real server — nothing in the unit suite could see it.

## Live risks / decisions

- `MutationReconciler` observes full mailbox pages and matches on provider stable id, then
  prior occurrence id, then `(Message-ID, ReceivedAt)`. If desired state can't be established
  it requeues rather than guessing.
- Node 22 is required: `source "$HOME/.nvm/nvm.sh" && nvm use` before any wrapper script.
- Never invoke `dotnet`, `tsc`, `eslint`, or `vitest` directly — use `pnpm status`/`pnpm check`.
- `AttachmentTempDirectory` uses a native Unix `open(..., 0600)` path because the pinned .NET
  8 runtime cannot create an asynchronous `FileStream` with Unix permissions atomically. Do
  not replace it with create-then-chmod; that creates the world-readable window §9 forbids.
