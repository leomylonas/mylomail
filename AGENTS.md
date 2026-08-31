# AGENTS.md

Canonical agent instructions for **MyloMail**. Claude Code, Codex and any other
agent tool should treat this file as authoritative. `CLAUDE.md` points here.

Full design: `docs/architecture.md`. Read the section you need, not the
whole file.

---

## What this is

MyloMail — a cross-platform desktop mail and calendar client. .NET backend spawned as an Electron
child process, React renderer, SignalR between them, SQLite locally. Providers: IMAP,
Microsoft Graph, Gmail.

**The architecture is frozen.** It reached its current state through several rounds of
adversarial review. Do not make structural changes without asking.

---

## The three principles

Everything structural in this codebase follows from these. If a change would violate
one, stop and ask rather than proceeding.

1. **Stable local identity owns ordering and intent. Volatile provider identity is an
   input to individual execution attempts.** `Message.Id`, `Mailbox.Id` and mutation
   sequence numbers are local and permanent. IMAP UIDs, Graph ids and Gmail history
   positions are resolved at the moment of use and never persisted as queue keys.

2. **No local record of an attempt is evidence about what the server actually did.** A
   durable record that an operation was dispatched establishes only that its outcome
   _may_ be ambiguous. Absence of a persisted response never implies the operation
   failed remotely.

3. **Never persist a cursor past changes that have not been durably persisted.** Replay
   is always acceptable — every write is an upsert. Skipping is never acceptable, and it
   is silent.

---

## Do not change without asking

Each of these looks wrong. Each is deliberate, and each was arrived at by finding the
simpler version broken. **Tests will not catch you undoing them** — the failures are
crash-window races visible only under fault injection.

| Thing                                                     | Why it looks wrong                   | Why it is right                                                                                                             |
| --------------------------------------------------------- | ------------------------------------ | --------------------------------------------------------------------------------------------------------------------------- |
| Synchronous `Dispatched` write before every provider call | Gratuitous I/O on a hot path         | It is the entire crash-safety mechanism. Batching, deferring or making it async silently reopens the window                 |
| `Message.Id` as identity, not `Message-ID`                | The header looks like a natural key  | RFC 5322 says SHOULD, not MUST, and duplicates occur in practice                                                            |
| Provider ids on `MessageMailbox`, not `Message`           | Looks like misplaced denormalisation | IMAP UIDs are folder-scoped and change on move                                                                              |
| Two count fields per mailbox                              | Looks redundant                      | Locally computed counts are wrong under bounded sync                                                                        |
| In-memory Hangfire storage                                | Looks fragile                        | Job persistence was a second source of truth. Startup reconciliation is the real recovery mechanism                         |
| `[AutomaticRetry(Attempts = 0)]` everywhere               | Looks like retry is missing          | Auth must not retry at all; throttling must wait exactly `Retry-After`. Hangfire's curve would double-retry underneath both |
| Terminal failure re-evaluates a chain, not cancels it     | Looks inconsistent                   | Providers offer no transaction across operations. Cancelling turns one failure into several abandoned user intentions       |
| Separate `Availability` and `Coverage` UI state           | Looks like one enum would do         | A mailbox is fully usable while backfilling                                                                                 |
| Raw MIME as the single stored representation              | Looks wasteful next to parsed fields | Reconstructing `.eml` from parts loses DKIM signatures, original headers and encrypted parts                                |
| No `IsAnswered` in `FlagUpdate`                           | Looks like an omission               | Gmail has no `ANSWERED` label; Graph has no equivalent mutable property. Including it would overpromise uniformity          |
| `MessageOccurrenceRef` never persisted                    | Looks like a useful thing to store   | It carries volatile provider identity. Persisting it reintroduces the staleness the mutation chain exists to prevent        |

---

## Environment

**Node 22.** Pinned in `.nvmrc` and `engines`, and matched by CI. Node 18 is not
supported — Stylelint 17 and other tooling require 20+, and a version mismatch shows up as
a tool crashing rather than as a clear error.

```bash
nvm use            # reads .nvmrc
corepack enable    # provides the pnpm version pinned in packageManager
```

`.NET 8` per `global.json`. `dotnet tool restore` once, for the type generators.

### Provider client registration

Gmail and Graph need this application's own client registration before any account can
authenticate. It is deployment configuration, not a credential, so it comes from the
environment and **is never committed**:

```bash
Providers__Gmail__ClientId=...        # installed-app client
Providers__Gmail__ClientSecret=...    # §5: distributing this in a desktop binary is unresolved
Providers__Graph__ClientId=...
Providers__Graph__Authority=...       # optional; defaults to the common authority
```

Without them `MailProviderFactory` raises `ProviderNotConfiguredException`, which the API
reports as `501`, distinct from a rejected credential's `400`. A user can fix the second and
not the first.

The conformance suite's own names (`GMAIL_CLIENT_ID`, `GMAIL_CLIENT_SECRET`,
`GRAPH_CLIENT_ID`, `GRAPH_TENANT_ID`) are accepted as a fallback, so
`source .dev/provider-test.env` is enough to run the app locally. Configuration wins where
both are set.

IMAP needs no registration: an account carries its own `ImapProviderConfig` and its password
lives in the credential store.

---

## Checks

**Use the wrappers. Do not run `dotnet`, `tsc`, `eslint` or `vitest` directly** — the
wrappers deduplicate, cap and strip output, and raw invocations produce hundreds of
lines where the wrapper produces one.

| Command           | When                                                                                     | Cost               |
| ----------------- | ---------------------------------------------------------------------------------------- | ------------------ |
| `pnpm status`     | after each edit — reads the watcher's current state                                      | ~1 line            |
| `pnpm check`      | before declaring work done — full build, unit + invariant tests                          | ~1 line on success |
| `pnpm check:deep` | only when explicitly asked — fault injection, provider conformance across all IMAP tiers | expensive, slow    |

`pnpm e2e` drives the built app against the IMAP matrix and needs `pnpm imap:up`, plus
`pnpm build:renderer && pnpm build:shell` for the bundles it launches. It is the only test
that asks the mail server what happened rather than the app what it believes.

Start the watcher once per session with `pnpm watch` in a separate terminal. `pnpm status`
reports `STALE` if the watcher hasn't caught up, and `DEAD` if it isn't running — treat
either as "not yet verified", never as success.

If `pnpm status` is unavailable, `pnpm check:fast` runs the same checks directly.

---

## Agent handoffs

`docs/handover.md` is the committed, current-state handoff between agents. It is not a
history or log; Git commits provide that history.

At the start of a new session, after reading this file, read `docs/handover.md` when it
exists, inspect `git status` and recent commits, and verify the recorded state before
editing. The worktree and verification output are authoritative if they disagree with
the handoff.

Before handing off unfinished work, replace the contents of `docs/handover.md` with the
current state, update its verification result, and commit it with the related work. Keep
only the completed work, next task, required reading, verification, and live risks or
decisions.

---

## Build order

Work proceeds in this order. See §16 of the design doc for reasoning.

- **A** — contracts and skeleton (solution, workspace, CI, type generation)
- **B** — provider conformance: all three providers, thin, against a shared conformance suite
- **C** — backend core (sync state machines, mutation chains, reconciliation) + fault-injection harness
- **D** — frontend shell architecture (panels, per-window state, theme tokens, registries)
- **E onwards** — features, in any order

The epics in §13 of the design doc are **requirements, not a build sequence**.

---

## Conventions

**C#**

- Tab indentation.
- `MutationItem` must never carry a provider identifier field. There is an architecture
  test asserting this.
- Every Graph request goes through the pipeline that sets `Prefer: IdType="ImmutableId"`.
  Never construct a bare Graph client.
- Every SQLite connection sets `journal_mode=WAL`, `synchronous=FULL`, `busy_timeout=5000`,
  `foreign_keys=ON`. Only `journal_mode` persists on the file; the rest are per-connection.
- Serilog only. Never log message bodies, subjects, addresses or credentials — ids,
  operations and exceptions only.

**TypeScript / React**

- Tab indentation, except YAML which is 2 spaces.
- **`PascalCase` for every file and folder**, including generated output. `src` is the one
  exception, and it is package layout rather than part of any import path. The workspace
  package directories themselves (`electron-shell`, `shared-types`) are kebab-case package
  names and stay as they are.
- **A component owns a folder containing a `.tsx` of the same name** —
  `MessageList/MessageList.tsx`. Its CSS module, store, tests and any child components it
  alone uses sit beside it: `MessageList.module.css`, `MessageList.store.ts`,
  `MessageList.test.tsx`, `MessageRow/MessageRow.tsx`.
- **Layer-first directories** under `apps/renderer/src`: `Shell/`, `Components/`, `Hooks/`,
  `Stores/`, `Styles/`, `Lib/`, `Types/`. `Shell/` is the cross-cutting layer — panels,
  registries, theme, per-window state, error mapping.
- **No barrel files.** No `index.ts`, no re-export hubs. Import the module by its full
  path — `@mylomail/shared-types/Api/Contracts/HealthDto` — so a symbol is greppable to
  exactly one path and a re-export cannot quietly rename it.
- **Import via the mapped prefixes**, never a relative climb or a `src` segment:
  `@mylomail/shared-types/*`, `@mylomail/ui/*`, `@mylomail/renderer/*`,
  `@mylomail/electron-shell/*`. Defined in
  `tsconfig.json` `paths`.
- **Named exports only.** No default exports; they let the same module be imported under
  different names.
- Components are **function declarations**, not arrow constants, so stack traces and
  DevTools carry a name.
- Stores are **per-window**. Never module-level singletons — multi-window is an
  assumption made from the first line, not a feature added later.
- Types in `packages/shared-types` are **generated**. Do not hand-edit; run
  `pnpm generate:types`. Import from `@mylomail/shared-types`, never by path into its source.
- Styling is **CSS Modules** with camelCase class names, so they read as
  `styles.messageRow`. Carbon theme tokens before literal colours or spacing.
- Carbon (`@carbon/react`) components before hand-rolled ones.
- `ToastNotification` for passive feedback, `ActionableNotification` where there is an
  action — interactive content in a toast breaks WCAG.
- Accessibility rules (`jsx-a11y`) are **errors, not warnings**. Accessibility is
  continuous; it is not a pass at the end.

Naming, structure and accessibility are enforced by `pnpm check` (ESLint and Stylelint),
not by convention alone. `docs/skills/frontend-shell.md` carries the reasoning.

**Everywhere**

- Errors use `MutationProblemDetails` (RFC 7807 + `Category`). Never invent a new error
  shape; never let a provider exception reach the UI unmapped.
- English strings only, but formatting is locale-aware.

---

## Delegation

Where subagents are used:

- **Model equivalents** — use Claude Sonnet or Codex Terra for careful bounded
  implementation and invariant review; use Claude Haiku or Codex Luna for repository
  scanning, symbol hunting, file-set summaries, and mechanical work. These are role
  equivalents, not interchangeable authority to bypass the safety boundaries below.

- **Freely** — repo scanning, symbol hunting, "where is X used", file-set summarisation,
  mechanical rename. Cheap model.
- **With care** — bounded implementation in a disjoint file set, outside mutation and
  sync core. Read the relevant architecture section and the corresponding guide in
  `docs/skills/` when available.
- **Provider research** — use `docs/research/provider-research.md` for a bounded factual
  question about IMAP, Graph, Gmail, iCalendar/iTIP, MIME, or RFC 5322. A Claude
  `provider-research` wrapper is available; Codex may use a Terra subagent with the same
  guide.
- **Never** — mutation chains, execution attempts, sync state machines, reconciliation.
  These are where locally sensible code violates non-local invariants, and a delegate
  cannot see the invariant it is breaking.

---

## Definition of done

1. `pnpm check` is green.
2. Invariant tests pass — they encode the table above; a failure means an invariant was
   broken, not that a test is wrong. Do not edit an invariant test to make it pass.
3. New provider behaviour satisfies the conformance suite for **all three** providers and
   all three IMAP capability tiers.
4. Anything touching mutation, sync or persistence has a fault-injection scenario, **and
   the scenario has been shown to fail with the invariant removed.** A scenario that passes
   against the bug it exists to catch is worse than none: it is evidence of nothing that
   reads as evidence of something. Break the invariant, watch the test fail, restore it, and
   record in the handover what you broke. This has caught real false passes in this
   repository — see `docs/skills/fault-injection.md`.
5. Changes to mutation, sync, reconciliation, or persistence have an invariant review
   using `docs/reviews/invariant-review.md`.
6. `pnpm format:check` is green. Formatting is enforced by `pnpm check` and CI; editor
   hooks are optional conveniences, not the source of truth.
