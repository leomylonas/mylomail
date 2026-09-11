# Current handoff

## Completed work

- Completed the prior Contacts, message-threading, IMAP IDLE, recipient-suggestion, and send-reconciliation slice. See `docs/parity-audit.md` for the remaining architecture gaps.
- Parity remediation 1/47: enabled Google account onboarding in `AddAccount`. Account name and email are shared fields for OAuth providers; selecting Google now enables the interactive browser sign-in path instead of presenting a disabled “coming soon” option.
- Parity remediation 2/47: enabled Microsoft 365 onboarding through the same interactive browser OAuth path.
- Parity remediation 3/47: added per-account Google installed-app OAuth credentials. The non-secret client id persists in `GmailProviderConfig`; its client secret has a dedicated credential-store slot and is resolved by all Gmail mail, calendar, and contact factories. Add Account validates and gates the pair, explains Desktop-client/API/Testing requirements, and Google authentication now diagnoses wrong client types, expired Testing tokens, and a disabled Gmail API.
- Parity remediation 4/47: exposed optional CalDAV setup for IMAP accounts. The form accepts an absolute HTTPS endpoint, an optional distinct login name, and either reuses the IMAP password or collects an independent CalDAV password; account creation stays gated until the selected credential mode is complete.
- Parity remediation 5/47: replaced IMAP's shared `UseSsl` switch with explicit IMAP/SMTP transport-security and authentication modes. Password authentication is refused on plaintext transports; mandatory STARTTLS and OAuth2 are implemented; definite pre-send SMTP capability failures no longer become ambiguous sends. OAuth tokens retain their credential format during reauthentication, cannot be reused for CalDAV Basic authentication, and certificate bypass now carries an explicit account-wide warning. A data migration upgrades legacy settings to encrypted modes while preserving SMTP password authentication.
- Parity remediation 6/47: enabled RFC 5162 QRESYNC immediately after IMAP authentication and changed incremental mailbox sync to resume from the prior UIDVALIDITY/mod-sequence, collect and deduplicate `VANISHED` UIDs, and return them as occurrence removals. QRESYNC IDLE sessions now wake on `MessagesVanished`. Removal application and cursor advancement remain one transaction, with an apply-before-commit fault boundary and a mod-sequence-aware fake provider.
- Parity remediation 7/47: separated IMAP Move to Trash from permanent deletion. Each dispatch resolves and durably records one stable local Trash target before the provider boundary; IMAP moves into that target, stale UIDs fail without side effects, and Basic-tier partial COPY recovery deletes only the exact original source UID. Ambiguous recovery uses the attempt-time target despite later special-use override changes and never treats heuristic Message-ID duplicates as mutation members.
- Parity remediation 8/47: replaced per-message mutation requests with provider-native batches. IMAP now searches, changes flags, moves, and expunges UID sets per folder; Gmail trashes up to 100 messages per multipart batch; Graph permanently deletes up to 20 messages per JSON batch. Per-item outcomes remain correlated, Graph inner requests retain immutable-ID preference, and inner/outer throttling escalates the longest provider delay to the account gate.
- Parity remediation 9/47: removed the bare `HttpClient` from Graph large-attachment uploads. Upload-session slices now use raw Kiota requests through the same Graph request adapter, preserving exact byte ranges, immutable-ID middleware, error mapping, and `Retry-After` translation. Graph authentication is explicitly allowlisted to `graph.microsoft.com`, so the pipeline omits bearer tokens from pre-authenticated Outlook upload URLs.
- Parity remediation 10/47: resolved the Google contact deletion design conflict without weakening concurrency safety. The architecture now explicitly limits Google People two-way writes to creates and revision-checked updates, keeps remote deletion observations authoritative, permits local-contact deletion, and requires provider-backed contacts to be deleted in Google because `people.deleteContact` has no atomic revision precondition.

## Next task

- Build Gmail's synthetic slash-delimited label hierarchy.

## Required reading

- `AGENTS.md`
- `docs/architecture.md` §§1–5, 12–16 as relevant
- `docs/parity-audit.md`
- `docs/skills/implement-provider.md`, `docs/skills/sync-work.md`, `docs/skills/mutation-work.md`, `docs/skills/frontend-shell.md`, `docs/skills/fault-injection.md`, `docs/skills/db-work.md`

## Verification

- Baseline `pnpm check`: format, TypeScript, ESLint, Stylelint, build, 499 .NET tests, and 146 Vitest tests passed.
- `pnpm status` after the Google onboarding change: TypeScript, ESLint, and .NET clean.
- Actual Electron smoke: `pnpm e2e --grep "Google account setup"` passed. The Google provider can be selected, common identity fields can be completed, the interactive sign-in explanation is visible, and Create account becomes enabled.
- Actual Electron smoke: `pnpm e2e --grep "Microsoft 365 setup"` passed. The Microsoft 365 provider can be selected, identity fields can be completed, and Create account becomes enabled.
- `pnpm check` after Gmail BYOC: format, TypeScript, ESLint, Stylelint, build, 500 .NET tests, and 146 Vitest tests passed.
- Actual Electron smoke: `pnpm e2e --grep "account-owned OAuth"` passed. The BYOC toggle reveals the client fields and setup guidance, keeps Create account disabled until both credentials are present, then enables it.
- `pnpm check` after CalDAV onboarding: format, TypeScript, ESLint, Stylelint, build, 500 .NET tests, and 146 Vitest tests passed.
- Actual Electron smoke: `pnpm e2e --grep "independent CalDAV"` passed. Enabling CalDAV reveals its fields, endpoint and independent-password gates are enforced, and both credential modes can make the account ready.
- `pnpm imap:up`: all three Dovecot capability tiers healthy with mandatory STARTTLS.
- `pnpm check` after encrypted IMAP/SMTP authentication: format, TypeScript, ESLint, Stylelint, build, 507 .NET tests, and 146 Vitest tests passed.
- Actual Electron smoke: `pnpm e2e --grep "new user adds|plaintext IMAP"` passed 2/2. It exercised mandatory STARTTLS against the local server, surfaced the account-wide certificate-bypass warning, retried successfully, reached INBOX, and kept password authentication unavailable on plaintext IMAP/SMTP.
- Persistence compatibility: the legacy-config migration test upgrades `UseSsl=false` to mandatory STARTTLS and preserves password authentication. It failed when the migration incorrectly selected unauthenticated SMTP, then passed after restoring `SmtpAuthMethod.Password`, so the assertion discriminates the upgrade regression it protects. No cursor or multi-write durable boundary changed; EF's transactional migration and pre-migration backup path remain unchanged.
- Invariant review after remediation found no remaining violations. It specifically confirmed credential-format/rollback safety, definite pre-send failure classification, OAuth/CalDAV separation, the explicit TrustAll warning, and legacy SMTP-auth preservation.
- `pnpm check` after QRESYNC expunge support: format, TypeScript, ESLint, Stylelint, build, 508 .NET tests, and 146 Vitest tests passed.
- Fault discrimination: removing change-stream occurrence application made `A_crash_before_a_qresync_removal_commit_replays_the_vanished_uid` fail; restoring the invariant returned the full check to green. The scenario now crashes after removals and the new mod-sequence are applied but before their transaction commits.
- QRESYNC invariant review found no violations. It confirmed authentication-time enablement, pre-open `VANISHED` subscription, UIDVALIDITY handling, pagination/replay safety, atomic removal/cursor persistence, IDLE wakeups, and discriminating fake/live coverage.
- `pnpm check` after distinct IMAP Trash semantics: format, TypeScript, ESLint, Stylelint, build, 513 .NET tests, and 146 Vitest tests passed.
- Fault discrimination: replacing the persisted attempt-time Trash target with `MutationItem.TargetMailboxId` made all three Basic-tier recovery scenarios fail, including the override-change race. Earlier removals of unknown-destination waiting, partial cleanup, and exact-source scoping also failed their dedicated scenarios. Restoring each invariant returned the full check to green.
- IMAP Trash invariant review found no remaining violations. It confirmed stale-UID handling, UID-less destination reconciliation, partial-COPY continuation, heuristic-duplicate isolation, attempt-time target persistence, role-change safety, and conservative requeue of legacy attempts whose target cannot be known.
- `pnpm check` after native provider batching: format, TypeScript, ESLint, Stylelint, build, 518 .NET tests, and 146 Vitest tests passed.
- Batch discrimination: reducing Gmail and Graph batch chunk sizes to one made both HTTP request-count regressions fail; restoring the provider limits returned the suite to green. The throttle tests also exercise Gmail outer 429 wrapping, longest Gmail inner delay, and longest Graph inner delay.
- Provider invariant review found no remaining violations. It confirmed folder-scoped IMAP UID sets, Gmail/Graph batch limits and item correlation, Graph immutable-ID headers, and account-level escalation of outer and inner throttles.
- `pnpm check` after routing Graph upload-session PUTs through the central pipeline: format, TypeScript, ESLint, Stylelint, build, 520 .NET tests, and 146 Vitest tests passed.
- Graph upload transport tests exercised a two-slice attachment, exact `Content-Range`/length values, immutable-ID middleware, off-host bearer suppression with zero token requests, and 429 `Retry-After` translation.
- Graph upload invariant review found no remaining violations after restricting authentication to the Graph API host. It confirmed off-host pre-authenticated uploads retain the central middleware without leaking a bearer token.
- `pnpm check` after resolving the Google contact deletion contract: format, TypeScript, ESLint, Stylelint, build, 520 .NET tests, and 146 Vitest tests passed.
- Contact contract review matched the implemented boundary: Google creates/updates remain revision-aware and two-way; local deletes and remote deletion observations remain supported; only unsafe provider-backed Google deletion is deliberately unavailable.

## Live risks / decisions

- Live Google provider authentication remains unverified without real external credentials; the account-owned registration path is covered through factory resolution and the actual Electron setup surface.
- Existing `UseSsl=false` accounts are upgraded to mandatory STARTTLS rather than allowed to continue sending passwords in plaintext. Servers without STARTTLS now fail closed with a mapped account error.
- The real-Dovecot QRESYNC regression is committed under the existing `Deep`/`Conformance` suite but was not executed because `pnpm check:deep` was not requested; the normal fake-provider crash/replay scenario passed.
- The live-Dovecot Move-to-Trash regression covers all three IMAP capability tiers but remains under the existing `Deep`/`Conformance` suite; it was not executed because `pnpm check:deep` was not requested.
- Native batching against live Gmail/Graph and the three IMAP tiers remains covered by the existing `Deep`/`Conformance` suite; it was not executed because `pnpm check:deep` was not requested. Normal HTTP transport tests proved Gmail and Graph issue one outer request for two message mutations.
- Work continues item-by-item from `docs/parity-audit.md`; each completed item updates this handover and receives its own commit.
