# IMAP capability matrix

Three Dovecot servers for local provider work, because "IMAP needs a capability matrix,
not one test account" (§11). Servers differ sharply on `MOVE`, `UIDPLUS`, `CONDSTORE`,
`QRESYNC`, namespaces and hierarchy delimiters, and the provider abstraction assumes more
uniformity than exists.

```bash
pnpm imap:up      # start all three
pnpm imap:down    # stop, discarding all mail
```

| Tier        | Port  | Advertises                        | Delimiter | Prefix   |
| ----------- | ----- | --------------------------------- | --------- | -------- |
| `qresync`   | 11143 | CONDSTORE, QRESYNC, UIDPLUS, MOVE | `/`       | —        |
| `condstore` | 12143 | CONDSTORE, UIDPLUS, MOVE          | `.`       | `INBOX.` |
| `basic`     | 13143 | none of the above                 | `/`       | —        |

User `test@mylomail.local`, password `password`, on `127.0.0.1`. No TLS.

The `.` delimiter and `INBOX.` prefix on tier 2 are deliberate: the delimiter is
server-declared and must never be assumed (§1), so a provider that hardcodes `/` passes
two tiers and fails one. That is what the matrix is for.

Tier 3 advertises neither `UIDPLUS` nor `MOVE`, so a move cannot report its destination
UID and the provider must report `ReportsDestinationIdOnMove = false` and flag destination
reconciliation (§2).

## Environment variables

Integration and conformance runs read these and **skip when they are absent** (§11), so
the suite is safe to run with no containers up:

```bash
TEST_IMAP_QRESYNC_HOST=127.0.0.1    TEST_IMAP_QRESYNC_PORT=11143
TEST_IMAP_CONDSTORE_HOST=127.0.0.1  TEST_IMAP_CONDSTORE_PORT=12143
TEST_IMAP_BASIC_HOST=127.0.0.1      TEST_IMAP_BASIC_PORT=13143
TEST_IMAP_USER=test@mylomail.local  TEST_IMAP_PASSWORD=password
```

## Caveat

Tiers 2 and 3 are produced by overriding Dovecot's advertised `imap_capability`, not by
building a server that genuinely lacks the features. A client negotiates on what is
advertised, so this faithfully exercises **our** capability negotiation and tier
selection — which is the thing under test. It does not prove behaviour against a server
whose implementation is actually absent or subtly broken, and a real-world server may
diverge in ways an advertisement cannot express. Treat green here as necessary, not
sufficient; the throwaway-account matrix in §11 still matters before shipping.

Mail lives in a tmpfs, so every restart is a clean server and no state survives
`imap:down`.
