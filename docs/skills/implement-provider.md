# Provider implementation

Read `docs/architecture.md` §§1–3. Do not read the whole design document.

One interface covers providers that genuinely differ on identity, hierarchy, deletion, and
change tracking. The interface absorbs those differences; callers never branch on the
provider. Differences that cannot be absorbed surface as capability negotiation.

## Non-negotiables

- **Graph:** every request carries `Prefer: IdType="ImmutableId"`; never construct a bare
  Graph client. Default IDs change on move and corrupt occurrence identity.
- **IMAP:** UIDs are folder-scoped and change on move. Store them on `MessageMailbox`, not
  `Message`; use the server’s full folder name and declared hierarchy delimiter.
- **Gmail:** history is account-scoped, labels are flat, and one message may have many
  labels. Derived label hierarchy is not provider topology.

## Mutations and conformance

`MessageOccurrenceRef` is an ephemeral execution DTO, constructed at execution time and
never persisted. Keep `RemoveFromMailbox`, `MoveToTrash`, and `DeletePermanently`
separate. `FlagUpdate` supports read and flagged only; do not add `IsAnswered`.

Batch results are per item and carry `OccurrenceChanges` so moves can report destination
identity.

The conformance suite is first-class: every provider must prove move identity, cursor
invalidation (`ProviderCursorInvalidException`), per-item batch results, honest
capability negotiation, and Graph immutable IDs. Run IMAP against QRESYNC,
CONDSTORE-only, and neither. Tag conformance tests `Conformance` and `Deep`.

Before finishing: all providers and tiers pass conformance; provider-specific branching
has not leaked to callers; complete invariant review.
