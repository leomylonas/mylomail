# Current handoff

## Completed

- Ambiguous mutation reconciliation and periodic degraded-IMAP integrity reconciliation are implemented and verified.
- The backend production credential store probes Windows DPAPI, macOS Keychain Services, or Linux Secret Service using a temporary native credential. It selects a usable native store rather than inferring availability from the OS.
- If no native store is usable, `MYLOMAIL_MASTER_PASSWORD` enables the encrypted SQLite fallback. It uses PBKDF2-SHA256 (600,000 iterations), a random salt, an AES-GCM verifier, and per-credential AES-GCM ciphertext. Passwords and provider credentials are never stored in plaintext.
- If neither native storage nor a master password is available (or the password is wrong), startup exits with code `78` before scheduling work. Electron can use that code to prompt, then restart the backend with the per-launch password.
- Independent invariant review of the credential fallback and persistence changes found no architectural violations.

## Next task

Before Stage D, complete the remaining runnable-app wiring:

1. Register provider-specific OAuth/client configuration so `MailProviderFactory` can construct real providers.
2. Implement Electron child-process startup: initial normal launch, code-78 handling, password prompt, and restarted launch with `MYLOMAIL_MASTER_PASSWORD`.
3. Verify Electron polls `/health` with its per-launch bearer token in both native-store and fallback starts.

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

- `pnpm check` under Node 22: green — format, tsc, eslint, stylelint, build, tests(76), vitest.
- `pnpm check:deep` under Node 22: green — conformance(60), fault-injection(21), mutation testing.
- Mutation scope expanded with the new reconciliation services and integrity loop. The measured baseline is 47.23% (306 killed / 134 survived / 9 timeout), so `stryker-config.json` resets the ratchet to 47, just below that baseline. Raise it as survivors are killed.

## Live risks / decisions

- `MutationReconciler` observes full mailbox pages and uses provider stable id, prior occurrence id, then `(Message-ID, ReceivedAt)` matching. If desired state cannot be established, it requeues rather than guessing.
- Node 22 is required: `source "$HOME/.nvm/nvm.sh" && nvm use` before wrappers.
