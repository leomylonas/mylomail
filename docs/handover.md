# Current handoff

## Completed work

- Completed the prior Contacts, message-threading, IMAP IDLE, recipient-suggestion, and send-reconciliation slice. See `docs/parity-audit.md` for the remaining architecture gaps.
- Parity remediation 1/47: enabled Google account onboarding in `AddAccount`. Account name and email are shared fields for OAuth providers; selecting Google now enables the interactive browser sign-in path instead of presenting a disabled “coming soon” option.
- Parity remediation 2/47: enabled Microsoft 365 onboarding through the same interactive browser OAuth path.
- Parity remediation 3/47: added per-account Google installed-app OAuth credentials. The non-secret client id persists in `GmailProviderConfig`; its client secret has a dedicated credential-store slot and is resolved by all Gmail mail, calendar, and contact factories. Add Account validates and gates the pair, explains Desktop-client/API/Testing requirements, and Google authentication now diagnoses wrong client types, expired Testing tokens, and a disabled Gmail API.

## Next task

- Add CalDAV configuration to IMAP account setup.

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

## Live risks / decisions

- Live Google provider authentication remains unverified without real external credentials; the account-owned registration path is covered through factory resolution and the actual Electron setup surface.
- Provider-backed Google contact deletion remains disabled because Google People offers no atomic revision precondition. The architecture contract must be made explicit rather than weakening conflict safety.
- Work continues item-by-item from `docs/parity-audit.md`; each completed item updates this handover and receives its own commit.
