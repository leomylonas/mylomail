# Current handoff

## Completed

- **Stage A is now complete.** Both halves of type generation run end to end:
  `TypedSignalR.Client.TypeScript` and `TypeContractor`, wired into
  `scripts/generate-types.ts` and verified idempotent (a second run produces no diff).
- `/health` endpoint (§9) and `HealthDto`, which Electron polls before connecting the
  renderer's SignalR client. It is also the type-generation pipeline's first real payload —
  TypeContractor generates from controllers returning `ActionResult<T>`, so before this the
  pipeline had nothing to carry.
- Provider contracts for mail and calendar are in place (`server/MyloMail.Api/Providers/`),
  translated from architecture §§1–3: `IMailProvider`, `ICalendarProvider`, their factories,
  the DTOs, structured per-provider cursor state, `ProviderCapabilities` and
  `ProviderCursorInvalidException`.
- Shared error taxonomy in `server/MyloMail.Api/Errors/`: `MutationProblemDetails` and
  `ErrorCategory`.
- Domain entities the provider surface needs (`server/MyloMail.Api/Domain/`): `Account` and
  its provider configs, `Mailbox` with `ImapMailboxMetadata`, `Draft`, `Calendar`,
  `CalendarEvent`, `Address`. These are plain POCOs — no EF configuration or DbContext yet.
- The shared conformance suite (`server/MyloMail.Api.Tests/Conformance/`): ten assertions
  covering move identity, cursor invalidation, per-item batch results, partial flag
  semantics, and honest capability negotiation.
- `FakeMailProvider` (`server/MyloMail.Api.Tests/Fakes/`) as a first-class test double per
  §11, constructed with explicit capabilities so it can wear any provider shape.
- `ProviderShapes` declares the capability set for Gmail, Graph and each IMAP tier. The
  conformance suite runs against all five shapes: 60 cases.
- `.editorconfig` added. C# had no formatter configuration, so `dotnet format` defaulted to
  spaces and contradicted the tab convention in AGENTS.md; the first indented C# file
  exposed it.

- Frontend code style is now defined and enforced. PascalCase file names throughout
  (including generated output — `TypeContractor` pinned to `--casing Pascal`), layer-first
  directories under `apps/renderer/src`, named exports only, CSS Modules with camelCase
  class names. ESLint gained `react`, `react-hooks`, `jsx-a11y` and `check-file`, all as
  errors; Stylelint is installed and wired into `pnpm check`. PascalCase for every file and
  folder except `src`; a component owns a folder holding a `.tsx` of the same name, with its
  CSS module, store, tests and private child components beside it. Rules in `AGENTS.md`,
  reasoning in `docs/skills/frontend-shell.md`.
- **No barrel files, and `src` never appears in an import.** `tsconfig.json` maps
  `@mylomail/shared-types/*`, `@mylomail/ui/*`, `@mylomail/renderer/*` and
  `@mylomail/electron-shell/*` onto each
  package's `src`. Resolution moved from `NodeNext` to `bundler`, which is what
  electron-vite-bundled code needs — `NodeNext` requires an explicit extension on every
  specifier. Generated types are stripped of the `MyloMail.Api` namespace prefix so imports
  do not carry a redundant `MyloMail/Api/`.
- **Node 22 is now the supported version**, pinned in `.nvmrc` and `engines`, with CI
  reading `node-version-file: .nvmrc` so there is one source of truth. pnpm comes from
  `corepack enable`, which honours the `packageManager` pin. Stylelint is back on 17.
- `scripts/watch.ts` no longer dies when a status write fails. It recreates `.dev/` each
  flush and logs rather than throwing, so a transient failure degrades to a stale heartbeat
  — which `pnpm status` already reports as NOT VERIFIED — instead of killing the watcher.

- **The IMAP capability matrix runs locally** (`tests/imap-matrix/`, `pnpm imap:up` /
  `pnpm imap:down`): three Dovecot containers on 11143/12143/13143 advertising
  QRESYNC+CONDSTORE+UIDPLUS+MOVE, CONDSTORE-only, and none of them. Tier 2 uses a `.`
  delimiter and an `INBOX.` prefix, so a provider that hardcodes `/` fails one tier and
  passes the others. Verified by probing post-login `CAPABILITY` on each.

## Next task

Stage B continues: implement the three real providers, thin — authentication, topology
listing, one read path, one mutation each — with each supplying an `IConformanceHarness`
so it inherits the shared suite. `ImapMailProvider` should be run against all three
capability tiers.

`GraphMailProvider` additionally needs the test asserting `Prefer: IdType="ImmutableId"`
on every outbound request (§2). That assertion is about the request pipeline, not the
provider interface, so it is deliberately not in the shared suite and does not yet exist.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §§1–3, and §16 for build-order reasoning
- `docs/skills/implement-provider.md`
- `server/MyloMail.Api/Providers/` — the contracts and their doc comments
- `server/MyloMail.Api.Tests/Conformance/MailProviderConformanceTests.cs`

## Verification

- `pnpm check` green.
- `pnpm check:deep` green — 60 conformance cases pass.
- `pnpm generate:types` runs clean and is idempotent; generated output is committed.
- The conformance suite was mutation-tested rather than merely run: making the flag update
  absolute instead of partial, dropping the move's destination identity, and swallowing
  cursor invalidation each produced 5 failures (one per shape). The suite discriminates.
- The three assertions added since were mutation-tested the same way: advancing a cursor
  mid-walk regardless of capability fails the two shapes that cannot (Gmail, Graph), and
  dropping the mailbox from batch correlation fails 6 cases.

## Risks / decisions

- **No throwaway provider accounts exist yet.** Gmail and Graph cannot be exercised at all
  until they do, so stage B's real value — finding out whether the abstraction survives
  contact with three providers — is only partly available. The IMAP matrix covers the tier
  divergence locally; the provider divergence still needs real accounts.
- The IMAP tiers are produced by overriding Dovecot's advertised `imap_capability`, not by
  servers that genuinely lack the features. That faithfully exercises our capability
  negotiation, which is the thing under test, but proves nothing about a server whose
  implementation is absent or subtly broken. Green here is necessary, not sufficient.

- The `tsconfig.json` `paths` mapping is verified to resolve
  (`@mylomail/shared-types/Api/Contracts/HealthDto` typechecks) but nothing in the tree
  imports it yet, so it is not exercised by `pnpm check`. Stage D will be the first real
  use; if it has broken by then, that is where it will show.
- Vite and Vitest will each need the same aliases as `tsconfig.json` `paths` when the
  renderer is built in stage D — `vite-tsconfig-paths` is the usual way to avoid stating
  them twice.
- `local/component-folder` in `eslint.config.js` is a rule defined inline in the flat
  config, not a package. It relates a file's name to its folder's, which no off-the-shelf
  rule does. If more local rules appear, they should move to a real plugin package.

- **TypeContractor carries a workaround for an upstream bug, in `scripts/generate-types.ts`.**
  `ReflectionContextHelper.GetNetCorePack` builds its pack lookup as
  `$"{packPath}\\{packName}"` with a hardcoded backslash, so on any non-Windows platform the
  directory never exists and the tool aborts with `FileNotFoundException` before reading
  anything. Every path below that first join uses `Path.Combine` and is fine. Confirmed
  present in 0.18.0, 0.22.1 and 1.0.0 — this is not a version problem and upgrading will not
  fix it. `packsPathFor` hands the tool a directory of symlinks whose names embed the literal
  backslash. Delete it once upstream joins paths portably; the code comment links the source.
  Worth filing upstream.
- The tool was upgraded 0.18.0 → 1.0.0 (latest). Its default file-name casing changed to
  `Pascal` between 0.22.1 and 1.0.0, which would silently rename every generated file on a
  future upgrade, so `--casing Kebab` is now pinned explicitly.
- `ImapMailboxMetadataDto.HierarchyDelimiter` is a `string`, while the domain entity keeps
  `char` per §1. `char` has no TypeScript equivalent: TypeContractor fails on it and emits a
  `MailboxDto` importing a file it did not generate — broken output, not merely a warning.
- SETUP.md's original `generate:types` command was wrong: `--project` is not a TypeContractor
  flag, the tool requires `--assembly` and `--output`. Worth reporting to whoever wrote it.
- The `MessageDto`/`SyncResult`/`InitialSyncPage`/`AttachmentConstraints` shapes are not
  given in the design doc; they are derived from what §§1–3 require and may need adjusting
  as the real providers land. The doc-specified shapes (`IMailProvider`,
  `MessageOccurrenceRef`, `FlagUpdate`, `BatchResult`, `BatchItemResult`,
  `OccurrenceChange`, `CalendarSyncResult`) are reproduced faithfully, with two additions
  noted below.
- Two deliberate departures from the interface as written in §2, both to satisfy invariants
  stated elsewhere in the same document:
  - `SyncMailboxAsync` takes `ProviderCursorState?`, not `string?`. §1 requires cursor state
    to be structured and versioned per provider rather than an opaque string.
  - `OccurrenceChange` carries `RequiresDestinationReconciliation`. §2 requires a provider
    that cannot report a move's destination id to flag that reconciliation is needed rather
    than guess; the record as written has no way to express it.
- **An invariant review of `bfc5183` was run and its findings applied** (see the commit
  after it). One violation was found and fixed: `SyncResult.NewCursor` was non-nullable
  beside a `HasMore` flag, so a provider mid-walk had to return *some* cursor, and the only
  available choices either covered undelivered pages or smuggled a Graph `nextLink` into a
  delta-cursor field. Either way the caller commits a cursor past data it has not received
  and those pages are skipped silently — principle 3.
- **`docs/architecture.md` §2 has been amended, with the owner's approval.**
  `BatchItemResult` now carries `MailboxId`, so results correlate on
  `(MessageId, MailboxId)` rather than on the message alone. `RemoveFromMailbox` is
  membership-scoped (§6) and a message holds several occurrences under Gmail's label model
  (§1), so a batch can carry two refs sharing a `MessageId` whose results would otherwise be
  indistinguishable. §6's ordering rule means the mutation worker never batches two
  operations for one message, so this was latent rather than live; it was fixed anyway
  because "per item" should mean an item is correlatable from the provider contract itself,
  rather than by virtue of a scheduling invariant enforced elsewhere.
  **The design document and the code were changed in the same commit deliberately.** Leaving
  §2 stating the old record would be worse than either option: the next agent would read the
  frozen doc, see the code disagree, and "correct" it back.
  Callers must not submit duplicate `(MessageId, MailboxId)` pairs in one batch.
- `FakeMailProvider` implements `SendAsync`, draft methods and `MoveMailboxAsync` as no-ops.
  Adequate for the current suite; they need real behaviour before stage C exercises them.
- Two generated paths cannot take PascalCase names: `TypedSignalR.Client.TypeScript` writes
  a fixed `TypedSignalR.Client/index.ts`, and package entry points stay `index.ts` because
  module resolution requires that exact name.
- ESLint's React settings pin `react: { version: "19.0" }` rather than `"detect"`, because
  React is not a dependency yet and detection throws when it is absent. Revisit when the
  renderer actually takes its React 19 dependency in stage D.
