# Invariant review

Use this checklist before completing any change to mutation chains, execution
attempts, sync state machines, reconciliation, or persistence.

The reviewer must read `AGENTS.md` and the relevant architecture section before
reviewing the diff. When the environment supports an independent reviewer, the
implementing agent must not perform this review itself.

Answer only this question: does the diff violate an architectural invariant?

For every violation:

1. Quote the changed line or identify the changed behaviour.
2. Name the principle or deliberate design decision it violates.
3. Explain the resulting crash-window, identity, or cursor-skip failure.

Passing ordinary tests is not evidence that these invariants hold. Do not review
style, naming, or feature completeness in this review.
