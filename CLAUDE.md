# CLAUDE.md

**Read `AGENTS.md` first.** It is the canonical instruction file for this repository and
applies to every agent tool. This file holds only Claude-specific additions.

---

## The short version

MyloMail — a desktop mail client. .NET backend as an Electron child process, React renderer, SignalR
between, SQLite locally. Providers: IMAP, Graph, Gmail.

The architecture is **frozen**. `AGENTS.md` has a "do not change without asking" table —
those entries look like mistakes and are not. Tests will not catch you undoing them.

Checks: `pnpm status` after each edit, `pnpm check` before finishing. Never invoke
`dotnet`, `tsc`, `eslint` or `vitest` directly.

---

## Skills

Load the skill for the area you are working in rather than reading the whole design doc
(~1,100 lines). Each skill points into the specific sections it needs.

| Skill | Covers | Design doc |
|---|---|---|
| `implement-provider` | `IMailProvider`/`ICalendarProvider`, per-provider divergence, conformance suite | §1, §2, §3 |
| `mutation-work` | Ordering, ownership, intent, execution identity, remote uncertainty | §6 |
| `sync-work` | Four sync state machines, cursor rules, per-provider topology | §3 |
| `frontend-shell` | Panels, per-window state, Carbon, accessibility, registries | §12, §13 |
| `fault-injection` | Kill points, harness, adding scenarios | §16 |
| `db-work` | EF Core conventions, pragmas, migrations, FTS5 | §1, §8, §9 |

Invoke by name (`/mutation-work`) when you know the area; otherwise they load on
relevance.

---

## Subagents

- `invariant-review` — fresh context, given a diff and the principles, answers one
  question: does this violate anything in the do-not-change table? Isolation is the
  point; it cannot be talked round by the reasoning that produced the diff.
- `provider-research` — reads IMAP RFCs and provider documentation. Large input, small
  output; ideal for isolation.

Do not delegate whole-system reasoning. This design is interconnected enough that a
subagent lacking the invariants will produce locally sensible, globally wrong code.

---

## Working style here

- **Read the section, not the file.** The design doc is long by necessity. Skills exist
  so you load §6 without loading §13.
- **Batch edits, then check once.** The watcher recompiles continuously; checking after
  every single edit wastes turns.
- **When something looks wrong, check the table before fixing it.** Roughly a dozen
  decisions in this codebase are deliberate and counter-intuitive.
- **If an invariant test fails, the code is wrong.** Not the test.
