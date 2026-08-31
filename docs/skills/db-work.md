# Database work

Read `docs/architecture.md` §1, §8, and §9.

`app.db` is authoritative. Hangfire uses in-memory storage; jobs are non-authoritative and
startup reconciliation resolves disagreement. Do not introduce another jobs database.

Every connection sets `journal_mode=WAL`, `synchronous=FULL`, `busy_timeout=5000`, and
`foreign_keys=ON`. Only WAL persists on the file. `synchronous=FULL` is required by the
dispatch boundary; reject network-backed data directories because WAL assumes local shared
memory.

Run `Database.Migrate()` at startup. Back up an existing WAL database with `VACUUM INTO`
or the online backup API, never by copying its main file.

`Message.Id` is canonical; `MessageIdHeader` is nullable metadata. Provider IDs are on
`MessageMailbox`; server-known and desired state stay separate. Raw MIME is the one stored
message representation. Attachment rows are rebuilt after raw replacement, drafts are the
structured exception, and list queries project rather than include content.

FTS5 indexes flattened `MessageSearchContent` using an integer row ID, separate searchable
columns, extracted HTML text, and explicit same-transaction upserts. Scope by mailbox after
matching. Physical message deletion is tombstone GC, not mutation-worker work.

Before finishing: verify pragmas, migrate an existing database, add fault injection when
relevant, and complete invariant review.

## SQLite quirks that have already cost time

**`ORDER BY` on a `DateTimeOffset` throws.** EF's SQLite provider refuses to translate it —
`SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses`. Order
client-side after materialising, or order by something else. This has bitten twice, in the
mutation claim query and in send reconciliation.

**Comparing a `DateTimeOffset` in raw SQL does not work either.** It is stored as text with
variable-length fractional seconds, so it does not sort lexicographically. Where a
comparison has to happen inside a statement, compare-and-swap on a value already read
instead — see `MutationClaimService`.

**An FTS5 external-content index is updated by handing SQLite the OLD column values.** They
are how it finds the terms to remove. Passing the new ones deletes terms that were never
indexed, and the mismatch surfaces as `database disk image is malformed` on some later,
unrelated write — nowhere near the cause, and looking nothing like a logic error. Snapshot
the row before changing it. `INSERT INTO "MessageSearchIndex"("MessageSearchIndex") VALUES
('integrity-check')` asks FTS5 to verify itself, and `TestDatabase` runs it on disposal so a
mismatch fails the test that caused it.

**Deleting a `Message` is restricted while its search content exists.** Cascading would
remove the row the index mirrors and leave the terms behind — searchable, pointing at
nothing. Tombstone collection removes it from the index first (§6, §8).

**Pass a `Guid` as a `Guid`, never as a string.** The driver writes it upper-case, and
`Guid.ToString()` is lower-case, so a hand-built string parameter silently matches nothing.
