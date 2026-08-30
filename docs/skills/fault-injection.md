# Fault injection

Read `docs/architecture.md` §16. Fault injection is the primary correctness evidence for
changes to mutation, sync, or persistence: the failures are crash-window races that
ordinary tests do not expose.

The harness must start the backend, drive it to a named durable boundary, kill it hard,
restart it, and assert recovered observable state. A deterministic in-process kill hook
is acceptable; graceful shutdown is not.

Cover these scenarios: optimistic commit before enqueue; enqueue before `Dispatched`;
`Dispatched` before provider call; provider call before results persist; partial batch;
move after UID change; page before cursor commit; Gmail staged history before replay;
mailbox delete/recreate during sync; `Sending` outbox recovery; and startup reconciliation
for all of them.

Startup reconciliation is the sole recovery mechanism with in-memory job storage. It must
enumerate backfills, non-terminal mutation chains, dispatched attempts without results,
scheduled/sending outbox items, queued/fetching content, in-progress exports, and
undelivered notifications.

Arrange using `FakeMailProvider`, kill, restart, and assert the externally observable
outcome—not internal call order. Mark scenarios `FaultInjection` and `Deep` so they run
in `pnpm check:deep`, not the inner loop.

## Prove the scenario discriminates

**A fault-injection test is not done when it passes. It is done when it has been shown to
fail against the invariant it protects being removed.**

Break the invariant — defer the `Dispatched` write, commit the cursor ahead of its page,
stub the generation check to always pass — run the scenario, watch it fail, then restore.
Record in the handover what you broke and which scenarios failed. If nothing fails, the
test is decorative and the invariant is unprotected.

This is not hypothetical. Two scenarios in this repository passed against code containing
the exact bug they existed to catch:

- A cursor crash test written over a **single-page** walk. Its resume token is null whether
  or not the cursor was committed ahead of its data, so the correct and broken
  implementations were indistinguishable. Fixed by making the walk multi-page and crashing
  on the first page.
- A topology-generation test that exercised **only the path that was already guarded**,
  passing the stale generation in by hand. Removals, flag changes and staged replay had no
  check at all; an independent invariant review found it, not the test.

Both share one authoring error: **asserting that the mechanism ran, rather than that the
failure cannot happen.** The rule that catches it — every scenario must contain an
assertion whose value differs between the correct and broken implementations. If you cannot
construct an input where they differ, you have not written a test yet.

`pnpm check:deep` runs mutation testing over `Mutations/` and `Sync/` as the mechanical
backstop. A surviving mutant in a guard clause is exactly this failure, found without
anyone having to remember.

**Read the mutant statuses, not the score.** Stryker counts a timed-out mutant as killed,
so a run where everything times out reports an excellent mutation score. The first run here
scored 85.56% having killed nothing at all: all 326 mutants timed out. A run whose mutants
are mostly `Timeout` or `NoCoverage` has measured nothing, whatever number it prints.

The cause is `additional-timeout`, and it is set generously on purpose: every test in this
suite creates a real on-disk SQLite database, so a mutant run is slower than the baseline
Stryker derives its per-mutant timeout from. The conformance suite is **not** excluded — the
live provider subjects skip cleanly without credentials, and the five
`FakeProviderConformanceTests` shapes are in-memory and belong in the run. An earlier
version of this file claimed conformance caused the timeouts; measuring it showed
identical results with and without them (205/113/8 versus 203/113/10, same three minutes),
so that claim was wrong.

**Do not run it alongside a build or test run.** It instruments and rebuilds the project,
and a concurrent `pnpm check` will fail with unrelated-looking test failures that vanish on
a clean re-run. Both runs' results are worthless when that happens.

The `break` threshold is a **ratchet, not a target**: it sits just under the current score
so the suite cannot get weaker, and it should be raised as survivors are killed. Not every
survivor is worth killing — log strings, progress counters and orderings that carry no
meaning are noise, and some are *equivalent mutants* that cannot change behaviour at all
(an `&&` whose short-circuit operand is a default value, a guard clause whose removal falls
through to the same answer). A survivor in a guard clause, a cursor advance, or a state
revert is a real gap.

**Compare killed counts, not scores, when the scope changes.** Adding a directory to
`mutate` lowers the score even as the suite gets stronger, because new unkilled mutants
arrive with it. The run that added `Scheduling/` went from 205 killed to 241 while the score
fell from 55.91% to 50.40% — that is the suite improving, not regressing. Reset the ratchet
to the new baseline and say why.
