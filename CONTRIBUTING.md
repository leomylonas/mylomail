# Contributing to Mylo Mail

Thanks for helping. Bug reports, provider quirks you have run into, documentation fixes
and code are all welcome. Please read the [Code of Conduct](CODE_OF_CONDUCT.md) first.

## Reporting a bug

Open an [issue](https://github.com/leomylonas/mylomail/issues/new/choose) with the version
or commit, your operating system, the account type (IMAP, Gmail, Microsoft 365) and, for
IMAP, the server software. Steps to reproduce matter more than anything else.

Logs are in the data directory's `logs` folder (`~/.local/share/mylomail/logs` on Linux).
The app never logs message bodies, subjects, addresses or credentials, but check a log
before attaching it anyway. Do not post credentials or message content.

For anything security-sensitive, follow [SECURITY.md](SECURITY.md) instead of opening an issue.

## Proposing a change

For anything larger than a fix, open an issue first and say what you want to change and why.
The architecture is deliberately frozen: it reached its current state through several rounds
of adversarial review, and changes to sync, mutations, persistence or reconciliation are
reviewed against three principles:

1. Stable local identity owns ordering and intent; volatile provider identity is an input to
   individual execution attempts.
2. No local record of an attempt is evidence about what the server actually did.
3. Never persist a cursor past changes that have not been durably persisted.

[AGENTS.md](AGENTS.md) lists them in full, along with a table of things that look wrong and
are deliberate. Read it before touching `Mutations`, `Sync`, `Outbox` or `Persistence`: the
failures there are crash-window races that ordinary tests do not catch.

## Setting up

[DEVELOPMENT.md](DEVELOPMENT.md) covers prerequisites, running the app and the test suites.
In short: Node 24, pnpm via Corepack, .NET 10, and Docker only for the IMAP matrix and
end-to-end tests.

## Before you open a pull request

- `pnpm check` passes. Use the wrapper scripts rather than raw `dotnet`, `tsc`, `eslint` or `vitest`.
- Formatting is applied (`pnpm format`); CI enforces `pnpm format:check`.
- New behaviour has a test that would catch a plausible regression. Do not edit an invariant
  test to make it pass: a failure there means an invariant was broken.
- Provider behaviour satisfies the conformance suite for all three providers and all three
  IMAP capability tiers (`pnpm imap:up` starts the local servers).
- Anything touching mutation, sync or persistence has a fault-injection scenario, and the
  scenario has been shown to fail with the invariant removed. See
  [docs/skills/fault-injection.md](docs/skills/fault-injection.md).
- Generated types under `packages/shared-types` are produced by `pnpm generate:types`, never
  edited by hand.
- User-facing strings are English only; dates, times and numbers are formatted for the user's locale.
- Documentation that describes the behaviour you changed is updated.

Keep pull requests focused, with commits that each make sense on their own. Describe what
changed and how you verified it.

## Code conventions

C# and TypeScript use tabs. The renderer uses PascalCase files and folders, a folder per
component, named exports, no barrel files, CSS Modules and Carbon components, and
accessibility lint rules are errors. [AGENTS.md](AGENTS.md#conventions) is the authority;
`pnpm check` enforces most of it.

## Licence

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
