# CalDAV HTTPS matrix

One Radicale server over real HTTPS, for CalDAV provider work (§13 Calendar). The backend
requires HTTPS for CalDAV Basic auth (`904908f`), so an HTTP-only fixture cannot exercise the
real client at all — only unit tests against a fake transport could, until this existed.

```bash
pnpm caldav:up      # generate a self-signed cert (once) and start Radicale
pnpm caldav:down    # stop and discard its data
```

User `test@mylomail.local`, password `password`, on `https://127.0.0.1:15232`. No calendar
collection is pre-created — `CalDavLiveTests` creates its own via `MKCALENDAR` under a fresh,
randomly-named path per run, so one run's leftovers can never be mistaken for another's
baseline sync state.

## Certificate

`generate-cert.sh` writes a self-signed certificate to `certs/` (gitignored), valid for
`127.0.0.1`/`localhost`. It is presented to nothing but the test fixture, so a ten-year
validity period trades nothing away.

`CalDavLiveTests` does **not** exercise certificate trust at all: its own `HttpClient` accepts
any server certificate, constructed directly in the test rather than through the app's
DI-registered HTTP client pipeline. `AccountTrustedCertificate` / `Account.CertificateTrustMode`
are designed (§1, §9) but not wired to any provider's transport yet — that is a separate,
not-yet-built feature, not something this fixture can fake its way around.

## Environment variables

`CalDavLiveTests` reads these and **skips when they are absent**, so the suite is safe to run
with no container up:

```bash
TEST_CALDAV_HOST=127.0.0.1        TEST_CALDAV_PORT=15232
TEST_CALDAV_USER=test@mylomail.local  TEST_CALDAV_PASSWORD=password
```
