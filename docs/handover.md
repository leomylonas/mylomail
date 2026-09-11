# Current handoff

## Completed work

- Completed the Contacts feature end to end: account-scoped cached contacts, crash-safe ordered provider operations, explicit ambiguity/conflict handling, provider tombstones, Graph nested-folder coverage, Google People incremental sync with durable cursors and resource-name rebinding, safe Google deletion capability handling, and mounted renderer UI.
- Completed message conversations: RFC reference ingestion/serialization, fallback threading and rethread propagation, durable backfill, complete mailbox/search thread counts, on-demand expansion beyond the current page, per-window/account state, and one accessible disclosure control per conversation.
- Completed IMAP IDLE lifecycle handling: effective-Inbox selection, all active mailbox sessions within the cap, reconnect selection replay, authentication/offline handling, awaited shutdown, obsolete-session retirement, and coalesced wake jobs with failed-enqueue release.
- Added account-scoped recipient suggestions from valid observed message addresses and frozen successful-send recipients. Suggestion upserts are SQLite conflict-safe and remain atomic with message ingestion or send completion. Compose tokenizes To/Cc/Bcc autocomplete and surfaces query failures.
- Reconciled sends now remove the associated draft in the same transaction as the Sent state, attempt completion, and recipient suggestions.
- Regenerated SignalR TypeScript contracts and added migrations for contact suggestions, provider-missing tombstones, outbox recipient snapshots, and Google contact sync cursors.

## Next task

- None for this slice.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §§1–3, 7–9, 13–16
- `docs/skills/mutation-work.md`, `docs/skills/sync-work.md`, `docs/skills/frontend-shell.md`, `docs/skills/fault-injection.md`
- `docs/reviews/invariant-review.md`

## Verification

- Final `pnpm check` passed: format, TypeScript, ESLint, Stylelint, build, 499 .NET tests, and 146 Vitest tests.
- Current cursor and send crash-window scenarios passed when temporarily included in the normal wrapper: 511 .NET tests and 146 Vitest tests. Their exact `Deep` gates were restored afterward.
- The new contact cursor scenario was discriminated: deliberately persisting the cursor before contact observations made only `Contact_cursor_never_commits_past_uncommitted_observations` fail; the invariant was restored and the scenario passed.
- Earlier suggestion/cursor and frozen-send-recipient scenarios were likewise discriminated by temporarily breaking their atomic cursor ordering and recipient snapshot use, producing their exact intended failures before restoration.
- Actual Electron/IMAP smoke passed for autocomplete token selection and clearing. A referenced two-message conversation rendered one collapsed row and one disclosure control, expanded to two rows with `aria-expanded=true`, then collapsed to one row; the throwaway Playwright spec was removed and the IMAP matrix stopped.
- Final high-reasoning SOL invariant review (`agent://SolFinalRemediationGate`) returned `Zero findings` with 0.98 confidence.

## Live risks / decisions

- Provider-backed Google contact deletion remains disabled and explained because Google People offers no atomic revision precondition for deletion. Never-dispatched local Google contacts remain deletable.
- Real Google People and Microsoft Graph calls were not exercised because provider credentials were unavailable. `pnpm check:deep` and full provider conformance were not run because they were not requested.
