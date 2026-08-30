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
