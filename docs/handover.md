# Current handoff

## Completed

- Stage A remains **incomplete** — see Risks. Everything else below is stage B.
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
  conformance suite runs against all five shapes: 50 cases.
- `.editorconfig` added. C# had no formatter configuration, so `dotnet format` defaulted to
  spaces and contradicted the tab convention in AGENTS.md; the first indented C# file
  exposed it.

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
- `pnpm check:deep` green — 50 conformance cases pass.
- The conformance suite was mutation-tested rather than merely run: making the flag update
  absolute instead of partial, dropping the move's destination identity, and swallowing
  cursor invalidation each produced 5 failures (one per shape). The suite discriminates.

## Risks / decisions

- **Stage A is not complete, and cannot be closed by writing more of our own code.**
  `TypeContractor` is installed in `.config/dotnet-tools.json` but `scripts/generate-types.ts`
  never invokes it, so only the `tsrts` half of type generation has ever run. SETUP.md's
  original command was wrong (`--project` is not a flag; the tool requires `--assembly` and
  `--output`), which is likely why it was dropped.
  Attempting to wire it now fails on this machine: TypeContractor 0.18.0 cannot resolve
  `Microsoft.NETCore.App.Ref` on Linux. It finds the shared runtime at
  `/usr/lib/dotnet/shared/Microsoft.NETCore.App/8.0.28/` and then throws
  `FileNotFoundException` from `ReflectionContextHelper.GetResolver`, regardless of what
  `--packs-path` is given, though `/usr/lib/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.28/ref/net8.0/`
  exists and is populated. Tried: the real packs path with and without a trailing separator,
  its parent, a synthetic tree using a literal backslash separator, and a synthetic tree
  using an exact `8.0.0` version directory. §16 already flags TypeContractor's Zod support
  as a currency risk. This needs a decision — upgrade the tool, run it only on Windows/CI,
  or drop it and hand-maintain the DTO types — and it is a **blocker on stage A**, not on B.
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
- **An independent invariant review has not been run on this diff.** It touches the mutation
  and sync contract surface, so definition-of-done item 5 applies.
- `FakeMailProvider` implements `SendAsync`, draft methods and `MoveMailboxAsync` as no-ops.
  Adequate for the current suite; they need real behaviour before stage C exercises them.
