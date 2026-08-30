# Current handoff

## Completed

**Stage A — contracts and skeleton.** Complete. Both halves of type generation run end to
end and are idempotent: `TypedSignalR.Client.TypeScript` and `TypeContractor`, wired into
`scripts/generate-types.ts`. The `/health` endpoint (§9) is the pipeline's payload —
TypeContractor generates from controllers returning `ActionResult<T>`, so without one it has
nothing to carry.

**Stage B — provider conformance.** In progress.

- Provider contracts (`server/MyloMail.Api/Providers/`): `IMailProvider`,
  `ICalendarProvider`, their factories, the DTOs, structured per-provider cursor state,
  `ProviderCapabilities`, `ProviderCursorInvalidException`.
- Shared error taxonomy (`Errors/`): `MutationProblemDetails`, `ErrorCategory`.
- Domain POCOs (`Domain/`) for what the provider surface needs. No EF configuration or
  DbContext yet — that is `db-work`.
- The shared conformance suite (`Tests/Conformance/`): twelve assertions covering move
  identity, cursor advancement, cursor invalidation, per-item batch results, occurrence
  correlation, partial flag semantics and honest capability negotiation. Cases skip rather
  than fail when a subject is unreachable (§11).
- `FakeMailProvider` (§11) takes explicit capabilities, so it can wear any provider shape.
  `ProviderShapes` declares those shapes: Gmail, Graph and each IMAP tier.
- The IMAP capability matrix (`tests/imap-matrix/`, `pnpm imap:up` / `pnpm imap:down`):
  three Dovecot containers on 11143/12143/13143 advertising QRESYNC+CONDSTORE+UIDPLUS+MOVE,
  CONDSTORE-only, and none of them. Tier 2 uses a `.` delimiter behind an `INBOX.` prefix,
  so a provider that hardcodes `/` fails one tier and passes the others.
- `ImapMailProvider` (`Providers/Imap/`, MailKit), thin per §16: authentication with
  capability negotiation, topology listing, initial and incremental sync with `UIDVALIDITY`
  invalidation, raw fetch via `BODY.PEEK[]`, flags, move, and the deletion shapes. Passes
  conformance at all three tiers. Send, drafts and mailbox management throw
  `NotSupportedException` rather than returning a plausible empty result.

**Tooling and conventions.** Node 22 pinned in `.nvmrc` and `engines`, CI reads
`node-version-file`. `pnpm check` runs format, tsc, ESLint, Stylelint, build, tests, vitest.
Frontend conventions are in `AGENTS.md` with reasoning in `docs/skills/frontend-shell.md`,
and are lint-enforced: PascalCase files and folders except `src`, a component owning a
folder holding a `.tsx` of the same name, no barrel files, full-path imports through the
`@mylomail/*` prefixes, named exports, CSS Modules with camelCase classes, `jsx-a11y` as
errors.

## Next task

`GmailMailProvider` and `GraphMailProvider`, thin — authentication, topology listing, one
read path, one mutation each — with each supplying an `IConformanceHarness` so it inherits
the shared suite.

**Both are blocked on throwaway provider accounts, which do not exist yet.** Until they do,
stage B's central question — whether the abstraction holds across three genuinely different
providers — is only partly answered. The IMAP matrix covers tier divergence; provider
divergence is untested, and §16 expects that to be where the expensive surprise is.

`GraphMailProvider` additionally needs a test asserting `Prefer: IdType="ImmutableId"` on
every outbound request (§2). That is about the request pipeline rather than the interface,
so it is deliberately not in the shared suite and does not exist yet.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §§1–3, and §16 for build-order reasoning
- `docs/skills/implement-provider.md`
- `server/MyloMail.Api/Providers/` — the contracts and their doc comments
- `server/MyloMail.Api.Tests/Conformance/MailProviderConformanceTests.cs`
- `tests/imap-matrix/README.md` before touching IMAP

## Verification

- `pnpm check` green.
- `pnpm check:deep` green — 60 conformance cases with no IMAP servers running; 96 with
  `pnpm imap:up` and the `TEST_IMAP_*` variables set.
- `pnpm generate:types` clean and idempotent; generated output is committed.
- The conformance suite is mutation-tested, not merely run. Breaking partial flag semantics,
  dropping a move's destination identity, swallowing cursor invalidation, advancing a cursor
  mid-walk, and dropping the mailbox from batch correlation each fail the expected cases.
- The IMAP suite was verified in both directions: with no `TEST_IMAP_*` variables its 36
  cases skip; with the variables set and the containers stopped all 36 fail. The second is
  what proves they connect rather than passing vacuously.

## Risks / decisions

**Two gaps in the provider contract, both surfaced by the first real provider.** Neither is
fixed, because either fix amends frozen architecture. Both need a decision.

1. *IMAP capabilities are per-connection, not per-type.* §2 declares `Capabilities` as a
   provider property and `IMailProviderFactory` resolves by `ProviderType`, which assumes
   capabilities belong to the type. A QRESYNC server and a basic server are different
   providers as far as recovery policy goes. Handled by instantiating `ImapMailProvider` per
   account and renegotiating on every connect; it reports the weakest tier until it has, so
   a caller skips no reconciliation it needs. The factory cannot be a simple type switch.
2. *`MessageOccurrenceRef` cannot be addressed by an IMAP provider alone.* It carries a
   local `MailboxId` and a `ProviderOccurrenceId`, but a UID is meaningless without its
   folder, so four methods receive a reference the provider cannot resolve. Gmail and Graph
   are unaffected. Worked around with `IImapMailboxResolver`, injected by the caller —
   arguably the right home, since the orchestrator already owns local identity. The
   alternative adds a provider mailbox id to a record §2 writes verbatim.

**One deliberate departure from §2 as written**, to satisfy an invariant stated elsewhere in
the same document: `SyncMailboxAsync` takes `ProviderCursorState?` rather than `string?`,
because §1 requires cursor state to be structured and versioned per provider.
`SyncResult`/`CalendarSyncResult` additionally split a committable cursor from a walk
continuation, which §2 does not describe at all.

**`docs/architecture.md` §2 was amended with the owner's approval:** `BatchItemResult`
carries `MailboxId`, so results correlate on `(MessageId, MailboxId)`. Callers must not
submit duplicate pairs in one batch. Doc and code were changed together deliberately — a
divergence would have the next agent "correct" the code back.

**IMAP is thin in ways that matter later.** `MoveToTrashAsync` and `DeletePermanentlyAsync`
currently do what `RemoveFromMailboxAsync` does (flag `\Deleted`, expunge); they stay
separate methods because the difference is user-visible (§2, §6). Expunge detection is
absent — below QRESYNC it is UID-set reconciliation, which belongs with the reconciliation
machinery — so `SyncResult.Removed` is always empty for IMAP.

**The IMAP tiers are advertisement-only.** They are produced by overriding Dovecot's
`imap_capability`, not by servers genuinely lacking the features. That exercises our
capability negotiation, which is the thing under test, but proves nothing about a server
whose implementation is absent or subtly broken.

**TypeContractor carries a workaround for an upstream bug** in
`scripts/generate-types.ts`. `ReflectionContextHelper.GetNetCorePack` builds its pack lookup
with a hardcoded backslash, so on any non-Windows platform it aborts before reading
anything. Present in 0.18.0, 0.22.1 and 1.0.0, so upgrading will not fix it; `packsPathFor`
hands it symlinks whose names embed the literal backslash. Worth filing upstream. The tool
is pinned to `--casing Pascal` because its default casing changed between versions and would
silently rename every generated file.

**Smaller live notes.**

- `ImapMailboxMetadataDto.HierarchyDelimiter` is a `string` while the domain entity keeps
  `char` (§1). `char` has no TypeScript equivalent and TypeContractor emits broken output for
  it — a `MailboxDto` importing a file it did not generate.
- The `tsconfig.json` `paths` mapping resolves but nothing imports it yet, so `pnpm check`
  does not exercise it. Stage D is its first real use, and Vite and Vitest will need the
  same aliases — `vite-tsconfig-paths` avoids stating them twice.
- `local/component-folder` in `eslint.config.js` is an inline rule, not a package. If more
  local rules appear they should move to a real plugin.
- ESLint pins `react: { version: "19.0" }` rather than `"detect"`, because React is not yet
  a dependency and detection throws when absent. Revisit in stage D.
- `TypedSignalR.Client.TypeScript` writes a fixed `TypedSignalR.Client/index.ts` — the one
  generated path that can follow neither the PascalCase nor the no-barrel rule.
- `FakeMailProvider` implements send, drafts and mailbox move as no-ops. Adequate for the
  current suite; they need real behaviour before stage C exercises them.
- The `.claude/agents` definitions were not registered in the session that wrote this, so
  `invariant-review` ran as a general-purpose agent instead of on its declared model. A
  restart should pick them up; worth confirming with `/agents`.
