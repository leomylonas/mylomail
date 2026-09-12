# MyloMail

MyloMail is a cross-platform desktop mail and calendar client: an Electron shell
starts a local .NET backend, which serves the React renderer and stores data in
SQLite. It supports IMAP today; Gmail and Microsoft 365 require deployment OAuth
registrations before their account flows can be enabled.

## Prerequisites

- Node.js 22 (the exact major version is pinned in [`.nvmrc`](.nvmrc))
- pnpm 10, supplied through Corepack
- .NET SDK 10, selected by [`global.json`](global.json)
- Docker Compose, only for the local IMAP matrix and end-to-end tests

On a new checkout:

```bash
nvm use
corepack enable
dotnet tool restore
pnpm install --frozen-lockfile
pnpm generate:types
```

The generated shared types are committed build output for the renderer. Run
`pnpm generate:types` after backend contract changes; do not edit files in
`packages/shared-types` by hand.

## Install a release package

GitHub release artifacts are intentionally unsigned. Download them only from the
project's release page and compare the artifact's SHA-256 digest with the release's
`SHA256SUMS` file before opening it:

- **Linux:** install the `.deb` or `.rpm` with the distribution's package manager,
  or mark the `.AppImage` executable (`chmod +x MyloMail-*.AppImage`) and run it.
- **Windows:** use either the `.exe` installer or `.msi`. SmartScreen reports
  **Unknown publisher** because no signing certificate is used; choose **More info**
  and **Run anyway** only after confirming the download came from this project.
- **macOS:** open the `.dmg` (or extract the `.zip`) and copy MyloMail to
  Applications. The app is neither signed nor notarized, so Gatekeeper blocks a
  normal first launch. In Finder, Control-click MyloMail, choose **Open**, then
  confirm **Open**. Do not disable Gatekeeper globally.

Updates are manual downloads. Replacing the application does not replace or remove
the OS-standard per-user data directory.

## Run the desktop app

Build the renderer and Electron shell:

```bash
pnpm build:renderer
pnpm build:shell
```

Then launch Electron with the backend project as its child process:

```bash
MYLOMAIL_BACKEND_ARGS="run --project server/MyloMail.Api" pnpm app
```

`pnpm app` runs both build commands before it launches, so the three commands above
are useful when you want to build explicitly; the launch command alone is sufficient
for the usual workflow.

On first launch, MyloMail uses the native credential store when one is available.
Otherwise, the shell prompts for a master password. The default data directory is
per-user (`~/.local/share/mylomail` on Linux, unless `XDG_DATA_HOME` is set); do
not place its SQLite database on a network filesystem.

For an IMAP account, use your own server or the local test account described in
[the IMAP matrix guide](tests/imap-matrix/README.md). Gmail and Microsoft 365
need these deployment configuration values and must not have credentials
committed to the repository:

```bash
Providers__Gmail__ClientId=...
Providers__Gmail__ClientSecret=...
Providers__Graph__ClientId=...
Providers__Graph__Authority=... # optional; defaults to the common authority
```

## Development loop

Start the watcher once in a separate terminal. It keeps TypeScript, ESLint, and
the backend build warm:

```bash
pnpm watch
```

After an edit, inspect its current result:

```bash
pnpm status
```

`STALE` or `DEAD` means the result is not verified yet. Do not invoke raw
`dotnet`, `tsc`, ESLint, or Vitest commands: the project wrappers deduplicate
and cap diagnostics.

## Checks and tests

```bash
pnpm check        # formatting, builds, unit tests, and invariant tests
pnpm check:fast   # formatting and build checks only; no watcher required
pnpm check:deep   # explicit, slow: conformance, fault injection, E2E, mutation tests
```

Run the real Electron end-to-end suite against the local IMAP capability matrix:

```bash
pnpm imap:up
pnpm build:renderer
pnpm build:shell
pnpm e2e
pnpm imap:down
```

`pnpm imap:down` discards the matrix's mail data and Docker volumes. See
[tests/imap-matrix/README.md](tests/imap-matrix/README.md) for the test account,
ports, capability tiers, and environment variables.

## Useful commands

```bash
pnpm format       # apply repository formatting
pnpm format:check # check formatting only
pnpm build:renderer
pnpm build:shell
pnpm package       # build this host's unsigned installers/packages
pnpm package:smoke # launch the unpacked package through Playwright
```

Packaging is host-native: Windows and macOS artifacts are produced on those
operating systems. Building the Linux RPM locally also requires `rpmbuild`
(`sudo apt-get install rpm` on Debian/Ubuntu); the release workflow installs it.

For architecture and contribution constraints, read [AGENTS.md](AGENTS.md) and
the relevant section of [docs/architecture.md](docs/architecture.md).
