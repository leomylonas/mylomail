# Frontend shell

Read `docs/architecture.md` §12 and the relevant epic in §13.

## Process split

- **`apps/electron-shell`** — main process and preload. Owns window creation, spawning and
  supervising the .NET backend, OS integration (native notifications, tray,
  `shell.openPath`), the preload bridge, and the per-launch auth token.
- **`apps/renderer`** — the React SPA. No Node access. Talks to the backend over
  SignalR/HTTP on loopback with the shell-issued token.

Two backend modes: **spawn** (production, full E2E) and **attach** (development,
isolated E2E, `ELECTRON_BACKEND_MODE=attach`). One backend regardless of window count.

## Per-window state

**Stores are per-window. Never module-level singletons.** Multi-window is an assumption
made from the first line, not a feature added later. Each window holds its own SignalR
connection.

Panel sizing has two tiers: a **global persisted default** read once when a window opens,
and **live per-window state** that does not sync to other open windows. Resizing one
window writes back the default for future windows and leaves current ones alone.

## Code style

Enforced by `pnpm check` (ESLint and Stylelint), not by convention alone. The rules below
are the reasoning; the checkable summary is in `AGENTS.md`.

**`PascalCase` for every file and folder, including generated output.** One rule for the
whole tree: no per-file-type exception to remember, and no judgement about whether a folder
is a layer or a component. `TypeContractor` is pinned to `--casing Pascal` to match.

`src` is excluded, because it is package layout rather than part of an import path — see
imports below. `TypedSignalR.Client.TypeScript` is the one thing that cannot comply: it
writes a fixed `TypedSignalR.Client/index.ts` with no naming option. It is generated, and
`packages/shared-types/src` is excluded from linting.

## Imports

**No barrel files.** A barrel re-exports a symbol under a second path and often a second
name, so a symbol stops being greppable to one location, and deleting the original leaves
the barrel compiling. Import the module by its full path instead:

```ts
import type { HealthDto } from "@mylomail/shared-types/Api/Contracts/HealthDto";
import { MessageRow } from "@mylomail/renderer/Components/MessageList/MessageRow/MessageRow";
```

**`src` never appears in an import.** `tsconfig.json` maps `@mylomail/shared-types/*`,
`@mylomail/ui/*`, `@mylomail/renderer/*` and `@mylomail/electron-shell/*` onto each
package's `src`, so the physical
layout stays conventional while specifiers stay clean. Generated types are stripped of
their C# namespace prefix (`--strip MyloMail.Api`) for the same reason — otherwise every
import would carry a redundant `MyloMail/Api/`.

Resolution is `moduleResolution: "bundler"`. `NodeNext` requires an explicit file extension
on every specifier, which is wrong for code electron-vite bundles and would put `.js` or
`.ts` into every import.

**A component owns its directory.** The folder is `PascalCase` and holds a `.tsx` of the
same name, with everything belonging to that component beside it:

```
Components/MessageList/
	MessageList.tsx           the component
	MessageList.module.css    its styles
	MessageList.store.ts      its per-window store
	MessageList.test.tsx      its tests
	MessageRow/               a child component only MessageList uses
		MessageRow.tsx
```

Colocation is the point: a component's styles, state and children live with it rather than
scattered across four sibling trees, and a child component is promoted by moving one folder
up rather than by unpicking four files. A `.tsx` whose name does not match its folder is an
error — `local/component-folder` in `eslint.config.js`, a local rule because the convention
is a *relationship* between file and folder, and a PascalCase folder plus a PascalCase file
checked independently would happily accept `MessageList/ReadingPane.tsx`.

The workspace package directories (`electron-shell`, `shared-types`) are kebab-case package
names, not source folders, and are excluded.

**Directories are layer-first** under `apps/renderer/src`:

```
Shell/        panels, registries, theme, per-window state, ErrorCategory mapping
Components/   presentational and composite components
Hooks/        shared hooks
Stores/       per-window stores (never module-level)
Styles/       global styles and theme tokens
Lib/          SignalR client, query client, fetch client
Types/        hand-written types only; generated types live in packages/shared-types
```

`shell/` is deliberately separate rather than being one layer among many: it is the Stage D
architecture every later view is built inside, and keeping it distinct makes it obvious when
a feature is reaching into it rather than building on it.

**Named exports only.** A default export lets the same module be imported under different
names, so one component acquires several names across the codebase and grep stops finding
it. Components are function declarations rather than arrow constants, so stack traces and
React DevTools carry a name.

**Styling is CSS Modules with camelCase class names**, so they read as `styles.messageRow`
from TypeScript without bracket access. Reach for Carbon's theme tokens before literal
colours or spacing — a token survives the theme work in Stage D, a hex value does not.

**Accessibility rules are errors, not warnings.** §15 makes accessibility continuous rather
than a late pass, and §16 puts the accessibility baseline in the shell primitives
specifically because retrofitting it is expensive. Carbon supplies accessible components,
but TanStack Table and Virtual are headless and provide no scaffolding, so the markup around
them is where this is actually won or lost.

## Stack

- TanStack **Query** (server state; SignalR events drive invalidation), **Router**,
  **Virtual** (message list, agenda), **Table** (Outlook-style columns), **Form** (+ Zod)
- **Carbon** (`@carbon/react`) before hand-rolled components
- **Lexical** for compose; serialise via `$generateHtmlFromNodes` to `Draft.BodyHtml`,
  never Lexical's native JSON
- `react-granular-store` for cross-cutting UI state; not Redux or MobX
- `dayjs` with `utc`, `timezone`, `relativeTime`, and `calendar`

Types in `packages/shared-types` are **generated**. Do not hand-edit; run
`pnpm generate:types`.

## Toasts and errors

Use `ToastNotification` for passive feedback. Use `ActionableNotification` wherever an
action or link is available—interactive content inside a toast breaks WCAG.

Every failure arrives as `MutationProblemDetails` (RFC 7807 + `Category`). Branch on
`Category`, never on a message string, and use the central mapping. A feature growing its
own error handling is a bug.

| Category | UI |
|---|---|
| `Network` | One calm “offline, will resume” state; never per-mailbox noise |
| `Auth` | Re-authentication prompt |
| `RateLimit` | Silent |
| `Validation` / `ProviderRejected` | Show `Detail` |
| `Conflict` | Keep-mine / keep-theirs choice |
| `Unknown` | Generic indicator, logged |

## Message rendering

The renderer holds the capability to invoke mail mutations, so untrusted HTML is the
most sensitive surface. Require `contextIsolation`, sandboxing, strict CSP, navigation
and window-open interception, scripts/forms/iframes stripped, DOMPurify, and an isolated
document context.

Block remote content at the request layer, not by removing `<img src>`: CSS `url()`,
`srcset`, SVG, and `<style>@import` also make requests. Fetch inline images through
authenticated client code and rewrite them to `blob:` URLs. The launch token must never
appear in a query string.

## UI state, reconnect, and accessibility

The message list derives display from server-known state plus `MessagePendingChange`.
Build this in from the start.

Keep `Availability` (`Usable` / `Degraded` / `Unavailable`) separate from `Coverage`
(status and progress). Provider-reported counts drive UI counts; locally derived counts
are wrong under bounded sync.

On reconnect, invalidate and refetch all active account and mailbox queries. Checking
pending mutations alone cannot repair a stale Query cache.

Accessibility is continuous. Carbon supplies accessible primitives, but TanStack Table
and Virtual are headless: semantic markup, ARIA, and full keyboard operation remain the
application’s responsibility. English strings only; formatting is locale-aware.
