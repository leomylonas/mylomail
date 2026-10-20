# MyloMail

MyloMail is a cross-platform desktop mail and calendar client: an Electron shell
starts a local .NET backend, which serves the React renderer and stores data in
SQLite. It supports IMAP today; Gmail and Microsoft 365 require deployment OAuth
registrations before their account flows can be enabled.

## Prerequisites

- Node.js 24 (the exact major version is pinned in [`.nvmrc`](.nvmrc))
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

### Linux: Electron sandbox

Electron's `chrome-sandbox` helper must be root-owned with mode 4755, which a package
install cannot set. Without it, Chromium aborts at startup with "The SUID sandbox helper
binary was found, but is not configured correctly". Ubuntu 24.04 and later also block the
unprivileged user-namespace fallback. Fix the helper once, and again after `pnpm install`
changes the Electron version, because the path includes it:

```bash
sudo chown root:root node_modules/.pnpm/electron@*/node_modules/electron/dist/chrome-sandbox
sudo chmod 4755 node_modules/.pnpm/electron@*/node_modules/electron/dist/chrome-sandbox
```

If the helper is not configured, `pnpm app` prints a warning and launches Electron with
`--no-sandbox` so development still works. That fallback lives in the development
launcher only; packaged builds do not use it.

`pnpm app` builds the backend before launching Electron, so a cold compile does not count
against the shell's 30 second health timeout. If you override `MYLOMAIL_BACKEND_ARGS`,
that build is skipped and the first launch can still hit the timeout; run
`dotnet build server/MyloMail.Api` beforehand.

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

Launch the development shell and its backend:

```bash
pnpm app
```

`pnpm app` builds the current Electron bundles, starts the backend project as its
child process, and launches Electron through a cross-platform Node script. The
launch command alone is sufficient for the usual workflow.

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

A Microsoft 365 shared mailbox is added as a Microsoft 365 account with "This is a
shared mailbox" switched on: you sign in as yourself (Full Access to the mailbox, and
Send As or Send on Behalf to send from it). The application registration must have the
delegated `Mail.ReadWrite.Shared`, `Mail.Send.Shared` and `Calendars.ReadWrite.Shared`
permissions consented. They are available to work or school accounts only, and a
shared mailbox has no personal contacts.

## Public website

The `website/` pnpm workspace is the static Next.js website at
**https://mylo.software**, with the home page and `/privacy/`. It needs Node 24 and
pnpm, but no .NET backend, database, or account credentials.

```bash
pnpm website:dev    # http://127.0.0.1:3000
pnpm website:build  # generates website/out/
```

Deploy the contents of `website/out/` to any static HTTPS host. Preserve the
directory structure: `/privacy/` is served by `privacy/index.html`. Configure the
host to use `404.html` for unknown paths. `next start` is not used for static
exports. The build also emits `robots.txt`, `sitemap.xml`, canonical metadata, and
icons for the public domain. The origin is defined in
`website/src/Lib/Site.ts`.

The home page covers the free, subscription-free and ad-free model, account and
calendar features, privacy defaults, desktop platforms, and public development.
Its FAQ uses native HTML disclosures rather than client-side state. Source and
issue links share `sourceRepository` in `website/src/Lib/Site.ts`.
Availability copy describes active development, not a published release; keep
the FAQ and packaging descriptions aligned with actual releases.

The source repository is public, but no project licence is declared yet. The
site describes public development without promising specific reuse rights;
choose and add a licence before making formal open-source licensing claims.

The workspace reuses the root TypeScript options, ESLint/React/accessibility
rules, Prettier, Stylelint, and the app's React and IBM Plex versions. Next.js has
its own TypeScript compilation to keep its framework-specific Node globals out
of the desktop app and tooling. Both compilers are covered by `pnpm watch` and
`pnpm check:fast`; `pnpm check` also builds the static export. Only Next-required
route/config filenames and default exports are exceptions to the normal naming
rules.

The website build copies approved logos from `docs/branding/logos`, native icons
from `assets/icons`, and the installed IBM Plex font files and licence into the
static output. Those generated public assets are ignored by Git; update the
original artwork rather than editing copies. Fonts are self-hosted and Next.js
CLI telemetry is disabled by the build/development wrapper.

The home page uses screenshots of the running desktop app in both themes.
To refresh the committed images in `website/public/Screenshots/`:

```bash
pnpm website:screenshots
```

Regeneration requires Linux, .NET 10, OpenSSL, and a local Docker engine. It
builds the app and uses the desktop E2E fixture with a temporary profile, dedicated
Dovecot and Mailpit containers, and synthetic accounts at reserved `.example`
addresses. It does not use an existing app profile or the shared IMAP matrix.
The command removes its temporary profile and containers when it finishes.
Normal website builds use the committed PNGs and need none of these additional
runtime prerequisites.

There are no analytics, tracking cookies, signup forms, or server-side contact
handlers. The contact email is assembled in the browser after a button press,
not embedded in the initial HTML. This deters simple address harvesters; it does
not prevent JavaScript-capable scrapers or recovery from the open-source code.
Provision the displayed project mailbox before publication, and review the
privacy policy against the chosen host's access-log handling and any telemetry
enabled by a distributor.

## Development loop

Start the watcher once in a separate terminal. It keeps desktop and website
TypeScript, ESLint, and the backend build warm:

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
pnpm build:electron
pnpm package       # build this host's unsigned installers/packages
pnpm package:smoke # launch the unpacked package through Playwright
```

Packaging is host-native: Windows and macOS artifacts are produced on those
operating systems. Building the Linux RPM locally also requires `rpmbuild`
(`sudo apt-get install rpm` on Debian/Ubuntu); the release workflow installs it.

For architecture and contribution constraints, read [AGENTS.md](AGENTS.md) and
the relevant section of [docs/architecture.md](docs/architecture.md).
