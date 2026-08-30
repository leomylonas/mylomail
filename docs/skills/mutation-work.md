# Mutation model

Read `docs/architecture.md` §6 in full. Locally sensible changes here can silently lose
data.

> No local record of an attempt is evidence about what the server actually did.

Keep five concerns separate: total per-`(AccountId, MessageId)` ordering; atomic leased
ownership; stable local intent; provider identity resolved at execution; and remote
uncertainty represented by `MutationExecutionAttempt`. Ordering is by message, not
occurrence, because a move destroys occurrence identity.

## Dispatch boundary

1. Claim eligible items atomically.
2. Persist the attempt and membership.
3. Mark `Dispatched` synchronously and durably, immediately before the provider call.
4. Issue the provider request.
5. Persist per-item outcomes and terminal attempt state in one transaction.

Step 3 is the crash-safety mechanism. It must not be batched, deferred, asynchronous, or
removed. A crash after it but before the call is conservatively ambiguous; reconciliation
treats “never happened” as a normal result.

Recovery policy comes from operation type and capability: absolute flag sets and Gmail
label changes are `RetrySafe`; moves and permanent deletion reconcile then retry; sends
are ambiguous and never blindly replayed. Capability changes cost, never safety.

Terminal failure does not roll back a chain: re-evaluate later intent against current
server state. Sends use a stable pre-attempt `Message-ID`, compare-and-swap cancellation,
and reconciliation for expired `Sending` leases. Keep server-known state and
`MessagePendingChange` separate.

Before finishing: add a fault-injection scenario for each new boundary and complete the
mandatory invariant review.
