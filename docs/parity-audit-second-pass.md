# Architecture parity audit — second pass

**Audit date:** 2026-09-12  
**Baseline:** the 47 remediations and 13 verification items recorded in `docs/handover.md`, through commit `7eaabc1`

## Verdict

The first audit's 47 confirmed implementation gaps are closed. The remediation record, current source, focused runtime workflows, and the full repository check support those closures.

A fresh architecture-to-code review found **two new architecture-parity defects** that the first audit missed:

1. Microsoft Graph mailbox topology is neither recursive nor delta/cursor based. This is high severity because an incomplete response is treated as a complete snapshot.
2. Rich-text compose formatting is blocked in the real renderer by its CSP. Plain-text composition and delivery still work, but the HTML-compose requirement is not met.

The review also found one lower-severity distribution defect: the built Carbon stylesheet retains unresolved IBM Plex package URLs and ships no font files. This is not a direct architecture-contract violation, but it produces startup 404s and unintended fallback typography.

| Classification | Count | Status |
| --- | ---: | --- |
| Original confirmed gaps | 47 | Remediated |
| Original verification-only items | 9 | Resolved |
| Original verification-only items | 4 | Partially verified or externally blocked |
| New architecture-parity defects | 2 | Open |
| New build/distribution defects | 1 | Open |
| Additional portability checks | 2 | Native hosts/runners required |

## Method

Four high-reasoning reviews independently covered the product epics, provider/sync architecture, mutation/process/security architecture, and final finding adjudication. Every candidate was then checked against current source and recorded runtime evidence. Candidate gaps for missing-attachment detection, external-file drag/drop, themes, notification-baseline semantics, typed provider cursors, keyboard folder/message moves, and general additional-window offset were rejected because current code or focused runtime evidence implements them.

The audit treated a source path as implementation evidence and a successful command or observed native workflow as runtime evidence. It does not convert unavailable cloud credentials or unavailable Windows/macOS hosts into passing claims.

## New confirmed architecture gaps

### 1. Graph mailbox topology is an incomplete snapshot, not recursive delta sync

**Severity:** High  
**Architecture:** §1 `MailboxTopologySyncState`; §3 Sync Topology; Epic 2 nested folders

`GraphMailProvider.ListMailboxesAsync` in `server/MyloMail.Api/Providers/Graph/GraphMailProvider.cs` performs one `client.Me.MailFolders.GetAsync` call and consumes only `folders.Value`.

It does not:

- follow `OdataNextLink` for additional root pages;
- traverse `ChildFolders` recursively;
- call the `mailFolder` delta endpoints;
- return a delta cursor or continuation to the topology orchestrator.

`TopologySyncService.ReconcileAsync` in `server/MyloMail.Api/Sync/TopologySyncService.cs` treats the returned ids as a complete `seen` set and passes it to `RemoveVanishedAsync`. It updates `LastReconciledAt` and `LastError`, but never reads or writes `MailboxTopologySyncState.Cursor`.

Consequences:

- nested Graph folders are absent;
- root folders beyond the first provider page are absent;
- omitted folders can be treated as deleted because a partial response is reconciled as a full snapshot;
- the required restartable, account-scoped topology cursor is unused.

Required closure: add a provider-neutral paged topology result, recursively walk Graph root and child-folder deltas, persist the complete cursor set atomically with each safe reconciliation boundary, and prove pagination, nesting, deletion, parent change, and restart behavior. This changes a frozen provider contract and therefore needs an explicit design decision before implementation.

### 2. Rich-text composition fails under the production CSP

**Severity:** Medium  
**Architecture:** Epic 6 compose plain text or HTML; §12 strict CSP

The production renderer CSP in `apps/renderer/index.html` permits `style-src 'self'` and correctly rejects inline styles. The Lexical compose path in `apps/renderer/src/Components/Editor/Editor.tsx` triggers an inline-style CSP violation when formatting is applied. In the actual Electron workflow, clicking Bold or Italic logs:

```text
Applying inline style violates the following Content Security Policy directive 'style-src 'self''
```

The formatting action is blocked and does not survive. Sending and plain text remain functional.

`tests/renderer.e2e/Compose.e2e.ts` clicks Bold but only proves that a message with the expected subject reaches Mailpit; it does not assert editor semantics, generated HTML, or the received MIME body's formatting. That allowed the defect to pass.

Required closure: keep the strict CSP, make Lexical formatting class-based or otherwise CSP-compliant, and verify bold and italic in the received message body through the real Electron-to-SMTP path. Weakening `style-src` with `unsafe-inline` is not an acceptable fix because the renderer also displays remote-authored mail.

## New non-parity defect

### Carbon font assets are not bundled

**Severity:** Low  
**Classification:** Build/distribution quality, not an explicit architecture requirement

The generated renderer stylesheet under `apps/renderer/dist/assets/*.css` contains literal URLs such as:

```text
~@ibm/plex/IBM-Plex-Mono/fonts/split/woff2/IBMPlexMono-Regular-Latin1.woff2
```

The built distribution contains no `.woff`, `.woff2`, or `.ttf` files. The browser therefore cannot resolve Carbon's intended font resources and falls back to system fonts. The two renderer 404s repeatedly observed at startup are consistent with these unresolved resources, although the console message alone does not expose the requested URLs.

Required closure: make `electron-vite` resolve/copy the IBM Plex assets, or import the supported packaged font entry points so final CSS references emitted files. Verify a packaged renderer has no failed font requests.

## Original audit remediation status

| Original items | Area | Second-pass status |
| --- | --- | --- |
| 1–4 | Gmail/Graph/CalDAV onboarding | Closed in application UI and provider configuration; live cloud consent remains blocked below |
| 5–10 | Provider correctness and contact deletion policy | Closed; Google contact deletion exception is now explicit architecture |
| 11–17 | Sync, bounded coverage, fencing, availability | Closed; the newly found Graph **topology discovery** defect is separate from the original Graph bounded-message-sync finding |
| 18–24 | SignalR events, cross-window convergence, notifications | Closed in source and deterministic tests |
| 25–27 | Calendar reachability, recurrence/time zones, runtime CRUD/iTIP | Closed for UI and local CalDAV/iTIP; live Google/Graph calendar behavior remains blocked |
| 28–37 | Reading, actions, settings, locale, signatures, mailto, layout | Closed; rich-text formatting and font packaging are new findings |
| 38–47 | Error transport, offline scheduling, process/platform, tests, selected frontend stack | Closed in source and Linux/package workflows, subject to native portability checks below |

## Verification matrix

### Resolved original verification items

- **Three-tier IMAP conformance:** 42/42 live cases passed across QRESYNC, CONDSTORE, and Basic tiers.
- **OS attachment opening and cleanup:** live Linux Electron workflow reached an isolated native MIME handler, verified exact bytes and mode `0600`, and proved shutdown cleanup.
- **IMAP IDLE reconnect:** a real server-side connection kick was followed by session replacement and live arrival without polling.
- **Window bounds:** a real additional window inherited identical dimensions at a 24-pixel offset; a complete Electron restart restored the exact persisted rectangle.
- **Backend crash/restart and cached display:** the owned backend was hard-killed, cached mail remained rendered, and a fresh backend/Electron pair reopened the same SQLite state.
- **SQLite WAL/FULL durability contract:** production pragmas passed against an on-disk database and were checked against SQLite's official WAL documentation.
- **Hangfire.InMemory suitability:** version and behavior are current for its deliberately volatile executor role; SQLite remains the only durable source of work.
- **TypeContractor Zod output:** regeneration was byte-for-byte clean and a runtime schema smoke accepted and rejected the expected wire shapes.
- **Gmail CASA/deployment policy:** current public documentation supports BYOC until shared-client restricted-scope verification; the local-only data flow does not meet the published third-party-server CASA trigger, subject to Google's final determination.

### Partially verified or externally blocked original items

| Item | Evidence obtained | Missing evidence |
| --- | --- | --- |
| Gmail and Graph mail conformance | Real Gmail browser authorization URL reached; provider registrations load | Interactive user consent/token, then live mail conformance and mutation runs for both providers |
| Google and Graph calendars | Local CalDAV CRUD and iTIP request/reply workflows pass | Authorized Google/Graph accounts and live native calendar CRUD, recurrence, RSVP, delta, and conflict runs |
| Three-OS native keychains | Linux Secret Service live contract passes store/retrieve/replace/delete; it exposed and drove a production fix | Windows Credential Manager/DPAPI and macOS Keychain live runs, including locked/unavailable behavior and account-removal cleanup |
| Tray and last-window close | Headed Linux Electron proved `MinimizeToTray` intercepts last-window close, leaves one hidden window, and keeps the process alive | Safe observation/click of the native tray restoration and explicit Quit path in the available GNOME/Playwright session |

### Additional portability verification still required

These are not evidence of missing source behavior, but a cross-platform desktop release should not claim them from Linux-only execution:

- Windows and macOS attachment-handler invocation, private-file protections, and cleanup.
- Native Windows/macOS packaged-app smoke; local Linux produced AppImage and `.deb`, while RPM and both non-Linux targets currently rely on the release workflow definition rather than a recorded successful run.

## Documentation corrections made during this audit

- Updated the §2 provider-abstraction example to use the implemented typed `ProviderCursorState` plus non-durable continuation rather than its stale `string? cursor` signature.
- Replaced the stale §16 “verification blockers” list with dated dependency/deployment watchpoints. SQLite WAL/FULL, current Hangfire compatibility, and TypeContractor Zod output are now recorded as verified; TypeScript 7 remains deferred.

## Explicitly deferred or excluded

Unchanged from the architecture: server-side rules/filters, mail import, auto-update, internationalised strings, code signing/notarisation, storage eviction, exactly-once send, sending scheduled mail while the process is closed, CI policy for live-provider tests, and TypeScript 7 adoption.

## Bottom line

The first audit's implementation backlog is complete, but the application is not yet at architecture parity. Graph topology discovery can silently mis-model a real mailbox tree and is the highest-priority code defect. Rich-text compose formatting is the next user-visible parity defect. Cloud-provider, native-keychain, tray, and non-Linux package evidence remains blocked on credentials or native environments rather than local implementation work.
