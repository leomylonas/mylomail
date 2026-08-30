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
