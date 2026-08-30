# Current handoff

## Completed

- Repository skeleton, shared checks, formatting gate, CI, and agent-neutral review and
  delegation guidance were committed in `35d8214`.
- The shared handoff protocol, invariant-review checklist, provider-research guide, and
  all six shared skill guides are ready in the current commit.

## Next task

- Await the next implementation task. The repository currently contains tooling and empty
  projects only; no mail, sync, mutation, provider, persistence, or UI feature work has
  begun.

## Read first

- `AGENTS.md`
- `docs/architecture.md` §16 and the section relevant to the next task
- The corresponding `docs/skills/<area>.md` guide
- Recent commits and the current diff

## Verification

- `pnpm check` passed after the current changes.

## Risks / decisions

- All six skills are shared guides in `docs/skills/` with Claude-discoverable wrappers in
  `.claude/skills/`. Codex reads the shared guide directly.
- `docs/handover.md` is a current-state note. Replace it at every handoff; do not append
  historical entries.
