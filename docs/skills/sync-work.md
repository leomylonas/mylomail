# Sync topology

Read `docs/architecture.md` §3 and the sync-state tables in §1.

Coverage and live change tracking are separate. Topology is account-scoped; coverage is
mailbox-scoped; live changes are account-scoped for Gmail and mailbox-scoped for Graph
and IMAP; integrity reconciliation is mailbox-scoped.

> Never persist a cursor past changes that have not been durably persisted.

Commit a cursor and the state it represents in the same transaction. Replaying an upsert
page is safe; skipping a page is silent data loss. Cursor state is structured and
versioned, not an opaque string.

Keep triggered resynchronisation (broken cursor) distinct from periodic integrity
reconciliation (degraded IMAP despite a valid cursor). Gmail captures a baseline, stages
history durably while backfill runs, then replays it after coverage; notifications still
derive from staged events. Bounds limit historical coverage, never subsequent live sync.

Graph bounded sync reduces materialisation, not delta enumeration. Folder delta moves are
membership removal/addition, possibly in either order. IMAP must support QRESYNC,
CONDSTORE-only, and neither; the last tier uses scheduled reconciliation.

Carry and check `Mailbox.TopologyGeneration` on pages, coverage jobs, and content fetches.
Derive notification eligibility from stream provenance, not row creation.

Before finishing: add a fault-injection scenario for every new cursor/page boundary and
complete invariant review.
