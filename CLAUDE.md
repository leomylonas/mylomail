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

When repository skills are available, load the one for the area you are working in rather
than reading the whole design doc (~1,100 lines). Until the skills are added, read the
listed architecture sections directly.

| Skill | Status | Covers | Design doc |
|---|---|---|---|
| `implement-provider` | Available | `IMailProvider`/`ICalendarProvider`, per-provider divergence, conformance suite | §1, §2, §3 |
| `mutation-work` | Available | Ordering, ownership, intent, execution identity, remote uncertainty | §6 |
| `sync-work` | Available | Four sync state machines, cursor rules, per-provider topology | §3 |
| `frontend-shell` | Available | Panels, per-window state, Carbon, accessibility, registries | §12, §13 |
| `fault-injection` | Available | Kill points, harness, adding scenarios | §16 |
| `db-work` | Available | EF Core conventions, pragmas, migrations, FTS5 | §1, §8, §9 |

All listed skills are installed as Claude-discoverable wrappers over shared guides in
`docs/skills/`. Codex should read the same shared guide for the relevant area.

---

## Subagents

- `invariant-review` — a Claude convenience wrapper for the shared
  `docs/reviews/invariant-review.md` checklist. Isolation is the point; it cannot be
  talked round by the reasoning that produced the diff.
- `provider-research` — a Claude convenience wrapper for the shared
  `docs/research/provider-research.md` guide. It reads provider source material and
  returns a short, cited factual answer.

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
