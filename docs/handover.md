# Current handoff

## Completed

**Stage B — provider conformance.** Gmail and Microsoft Graph now have thin live
providers, OAuth boundaries, and real conformance harnesses alongside the existing IMAP
matrix.

- `ICredentialStore` keeps provider-owned opaque token/cache payloads outside `Account` and
  ordinary SQLite. `GoogleCredentialDataStore` adapts it for Google; Graph persists its
  MSAL cache through the same boundary. `InMemoryCredentialStore` is explicitly
  transport-test-only.
- Gmail uses installed-app loopback OAuth with PKCE and `gmail.modify`; Graph uses MSAL
  interactive/silent public-client OAuth with `Mail.ReadWrite`, `Mail.Send`, and
  `offline_access`. Neither stores account passwords.
- `GmailMailProvider` and `GraphMailProvider` implement topology, initial/incremental sync,
  raw-message retrieval, supported mutations, and explicit `NotSupportedException`s for
  deferred operations. Gmail uses its native label batch operations. Graph batches
  mutations and applies `Prefer: IdType="ImmutableId"` both through its request pipeline
  and inside batch steps; this has a dedicated request-handler unit test.
- `IProviderMailboxResolver` is now the provider-neutral execution-time mapping from stable
  local mailbox identity to volatile provider mailbox identity. Architecture §2 documents
  why a provider must not cache it.
- The shared conformance suite now captures cursors before the mutations it expects to
  observe, so its partial-flag and bounded-sync assertions exercise the intended timeline.
  Gmail's expiry assertion is skipped because a fresh Gmail account cannot deterministically
  age a history cursor out of retention.
- Live conformance harnesses share a process-local credential cache and serialize their one
  authorization flow, eliminating the repeated OAuth windows caused by parallel xUnit
  harness construction.
- `scripts/check.ts` supports optional, diagnostic-only focused test execution via
  `MYLOMAIL_TEST_PROJECT`, `MYLOMAIL_CONFORMANCE_FILTER`, and
  `MYLOMAIL_TEST_RESULTS_DIRECTORY`. Normal `pnpm check*` behaviour is unchanged.

## Next task

Begin **Stage C**: persistence, sync state machines, mutation chains, reconciliation and
fault injection. Read the relevant architecture sections before making a change.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §§1–6 and §16
- `docs/reviews/invariant-review.md` before mutation, sync, reconciliation, or persistence
  work
- `server/MyloMail.Api/Providers/` contracts and doc comments
- `server/MyloMail.Api.Tests/Conformance/MailProviderConformanceTests.cs`

## Verification

- `pnpm format` and `pnpm check:fast` passed after the final changes.
- Focused Gmail failure-path conformance test passed:
  `.dev/test-results/gmail-failed-batch-valid-id/conformance_net8.0_20260830224045.trx`.
- The preceding full Gmail run passed every executed case, including the bounded cursor
  assertion; Gmail's only skip was non-deterministic expiry. Its last failure was the
  syntactic test fixture issue subsequently fixed and verified by the focused run.
- Six Graph mutation-conformance tests passed against the live Outlook.com account:
  move destination identity, canonical message identity, per-item batches, occurrence
  handling, categorized failure, and partial flag updates. Evidence:
  `.dev/test-results/graph-mutations/conformance_net8.0_20260830224332.trx`.
- The full live Graph suite ran. Ten of its eleven original cases passed, including the
  page-overflow cursor assertion; Graph reports a malformed delta token as HTTP 400 rather
  than 410, so the provider now maps either status (when following a cursor) to
  `ProviderCursorInvalidException`. The focused eleventh case then passed, giving coverage
  for all eleven Graph cases: `.dev/test-results/graph-expired-cursor/conformance_net8.0_20260830225139.trx`.
- Per the owner’s request, the long full-sync/page-overflow test was not rerun after the
  Gmail fixture fix; its earlier successful result remains the verification for that path.

## Risks / decisions

- A production OS-keychain/master-password `ICredentialStore` registration is intentionally
  still absent. Only the provider contract and test in-memory implementation belong to this
  Stage B work; do not register the in-memory store in production.
- Gmail history expiration cannot be created on demand on a newly created test account, so
  that one real-provider conformance case remains correctly skipped. Deterministic expiry
  belongs in the fault-injection/fake-provider layer.
- Graph topology currently enumerates top-level mail folders. Recursive folder hierarchy is
  a follow-up before nested-folder support is claimed.
- Real OAuth runs need a desktop browser reachable by the backend’s loopback listener. The
  diagnostic receiver writes the OAuth URL to the ignored path configured by
  `MYLOMAIL_OAUTH_DIAGNOSTICS_PATH`, but this is an aid only, not a remote-auth transport.
