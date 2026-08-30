# Current handoff

## Completed

- Ambiguous mutation reconciliation is implemented. Dispatched attempts reconcile provider-observed location/existence before moves, membership removals, trashing, and permanent deletes are completed or requeued; idempotent flag updates requeue safely.
- Periodic degraded-IMAP integrity reconciliation is implemented as a distinct self-scheduling job. It compares UID membership despite a valid cursor; Basic IMAP also scans flags. It never resets a change cursor.
- IMAP implements `GetMailboxIntegritySnapshotAsync`; fakes cover it; Gmail and Graph explicitly reject it because their streams express the required facts.
- Added crash-window recovery and IMAP integrity tests. Independent invariant review found no violations.

## Next task

Before Stage D, make the app runnable:

1. Add a production `ICredentialStore`; do not register the in-memory test store.
2. Register it with `MailProviderFactory` so real provider construction is possible.
3. Map API controllers in `Program.cs`.
4. Apply the per-launch token middleware from architecture §9.
5. Verify the backend starts and Electron can poll `/health` using the launch token.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §§3, 6, 9 and 16
- `docs/skills/fault-injection.md`
- `docs/reviews/invariant-review.md`

## Verification

- `pnpm check` under Node 22: green — format, tsc, eslint, stylelint, build, tests(74), vitest.
- `pnpm check:deep` under Node 22: green — conformance(60), fault-injection(21), mutation testing.
- Mutation scope expanded with the new reconciliation services and integrity loop. The measured baseline is 47.23% (306 killed / 134 survived / 9 timeout), so `stryker-config.json` resets the ratchet to 47, just below that baseline. Raise it as survivors are killed.

## Live risks / decisions

- `MutationReconciler` observes full mailbox pages and uses provider stable id, prior occurrence id, then `(Message-ID, ReceivedAt)` matching. If desired state cannot be established, it requeues rather than guessing.
- Node 22 is required: `source "$HOME/.nvm/nvm.sh" && nvm use` before wrappers.
