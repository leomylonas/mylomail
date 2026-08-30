# Provider research

Use this guide for a specific factual question about IMAP, Microsoft Graph, Gmail,
iCalendar/iTIP, MIME, or RFC 5322. Research is isolated because source material is large
and the implementation task needs a short, verifiable answer.

## Scope

- **IMAP** — RFC 3501 and CONDSTORE/QRESYNC, UIDPLUS, MOVE, SPECIAL-USE, NAMESPACE, and
  LIST extensions.
- **Microsoft Graph** — mail and calendar endpoints, delta queries, immutable IDs, batch
  requests, throttling, upload sessions, permissions, and consent.
- **Gmail API** — messages, labels, history, drafts, batch modify, scopes, and quotas.
- **iCalendar / iTIP** — RFC 5545 and RFC 5546 recurrence, `RECURRENCE-ID`, and RSVP.
- **MIME / RFC 5322** — header semantics, structure, and encoding.

## Answer format

1. Search authoritative documentation or the relevant RFC first; provider details change.
2. Answer the specific question, not the surrounding topic.
3. Cite the documentation page or RFC section.
4. State uncertainty plainly when documentation does not answer it.
5. Name capability or version dependencies, especially for IMAP’s capability tiers.

Stay under 200 words unless the question genuinely needs more. Lead with the answer,
then cite it and state caveats. Do not propose a design or implementation; the caller
holds that context.
