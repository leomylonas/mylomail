# Current handoff

## Completed

**Stage C, first slice — persistence.** The schema, its pragmas, and migration safety
(§1, §8, §9). Nothing in the backend touched EF Core before this.

- `MyloMailDbContext` is the single authoritative `app.db`. Entities added for messages and
  occurrences, the four content tables, the flattened FTS source, attachments, the four sync
  state machines, send identities, trusted certificates, and single-row `AppSettings`.
- Provider identity is on `MessageMailbox`, never on `Message`; a message may hold several
  occurrences at once. Both are asserted by `SchemaInvariantTests`.
- `SqlitePragmaInterceptor` applies `journal_mode=WAL`, `synchronous=FULL`,
  `busy_timeout=5000` and `foreign_keys=ON` to **every** connection. Only `journal_mode`
  persists on the file; the rest are per-connection, so a startup-only application would be
  silently wrong.
- `DatabaseBootstrapper` backs an existing database up with `VACUUM INTO` before
  `Migrate()`, deletes the backup only on success, and refuses to start over a leftover
  backup — a leftover backup is the only copy predating a failed migration.
- `DataDirectory` resolves the OS-standard location (`$XDG_DATA_HOME/mylomail`,
  `%APPDATA%\MyloMail`, `~/Library/Application Support/MyloMail`) and rejects network-backed
  locations: UNC paths and mapped drives on Windows, network mount types via
  `/proc/self/mounts` elsewhere. `BootstrapConfig` reads the one pre-database setting.
- Address lists, `ProviderConfig` and `ProviderCursorState` are JSON columns with explicit
  polymorphic discriminators and value comparers. Cursor state round-trips with its provider
  shape and version.
- The FTS5 virtual table is created in the initial migration as external-content over
  `MessageSearchContents`, with an INTEGER rowid and no triggers — upserts are explicit and
  in the same transaction as the content write (§8).
- `Program.cs` now resolves the data directory, registers persistence, and migrates at
  startup. It still does not map controllers or apply the per-launch token middleware (§9);
  that was already true and is unchanged.

## Next task

Continue **Stage C**: the mutation model (§6) — ordering, ownership, intent, execution
identity — and the sync state machines (§3), then the fault-injection harness. The mutation
and sync tables are deliberately not in this migration; they land with the code that
maintains them.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §§3 and §6, and §16 for the harness
- `docs/reviews/invariant-review.md` before mutation, sync, reconciliation, or persistence
  work
- `server/MyloMail.Api/Persistence/` — the context, interceptor and bootstrapper

## Verification

- `pnpm check` green: `format · tsc · eslint · stylelint · build · tests(19) · vitest`.
- The one fault-injection scenario applicable to this slice passes: a migration that fails
  part-way leaves a `VACUUM INTO` backup that still holds data committed but not yet
  checkpointed out of the WAL (`MigrationCrashWindowTests`, `Category=FaultInjection`).
- The independent invariant review ran against the full diff and found no violation, and
  none of the ten checklist red flags. Its one non-blocking observation — that
  `SendIdentity.IsDefault` was documented as "exactly one default per account" but not
  enforced — has been closed with a filtered unique index and a test.

## Risks / decisions

- The previous handoff's verification section overstated three results. Corrected here:
  Graph has **twelve** conformance cases, not eleven — `graph-full` was 11 passed, 1 failed
  (expired cursor), with the focused `graph-expired-cursor` run covering the twelfth. And no
  full Gmail run passed every executed case: the last one, `gmail-full-valid-id`, failed
  `Cursor_is_not_advanced_past_undelivered_changes` on a Gmail send rate limit. That path's
  last pass was the earlier `gmail-full-final` run. It is a flake, not a provider defect,
  but the bounded-cursor path is not currently backed by a green run.
- Mutation, execution-attempt and outbox tables are absent by design. They are the next
  slice and arrive with their invariants, not ahead of them.
- The migration backup is deleted after a successful migration, so a database is unprotected
  between migrations. That is the intended scope — this guards migration, not user error.
- A production OS-keychain/master-password `ICredentialStore` registration is still
  intentionally absent; do not register the in-memory store in production.
- Graph topology currently enumerates top-level mail folders. Recursive folder hierarchy is
  a follow-up before nested-folder support is claimed.
- Real OAuth runs need a desktop browser reachable by the backend's loopback listener.
