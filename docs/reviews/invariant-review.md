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

## What to be most suspicious of

Changes that make code faster, simpler, or more idiomatic in the immediate vicinity.
These invariants are non-local: code that violates one often looks better than code that
satisfies it. Specifically watch for:

- A synchronous `Dispatched` write being batched, deferred, made asynchronous, or removed.
- A provider identifier persisted into a mutation record.
- `Message-ID` used as a key or assumed non-null.
- Provider identifiers moved from `MessageMailbox` onto `Message`.
- Counts computed from local rows rather than provider-reported values.
- `[AutomaticRetry]` with a non-zero attempt count.
- A cursor advanced separately from the data it covers.
- `Availability` and `Coverage` collapsed into one value.
- Terminal failure cancelling a whole mutation chain.
- A Graph client constructed outside the immutable-id pipeline.
