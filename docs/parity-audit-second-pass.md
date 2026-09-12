# Architecture parity audit — second pass

**Audit date:** 2026-09-12  
**Baseline:** the 47 remediations and 13 verification items recorded in `docs/handover.md`, through the rich-text and packaged-font closures

## Verdict

The first audit's 47 confirmed implementation gaps are closed. The remediation record, current source, focused runtime workflows, and the full repository check support those closures.

A fresh architecture-to-code review found **two new architecture-parity defects** that the first audit missed. Both were remediated:

1. Microsoft Graph mailbox topology was neither recursive nor delta/cursor based. It now walks every root and child delta stream and commits its versioned cursor with the exact topology changes it covers.
2. Rich-text compose formatting was blocked in the real renderer by its CSP. The strict CSP is retained, and semantic bold/italic formatting now survives the complete Electron-to-SMTP path.

The review also found one lower-severity distribution defect: the built Carbon stylesheet retained unresolved IBM Plex package URLs and shipped no font files. This was remediated immediately after the audit by explicitly bundling the required local font faces.

| Classification | Count | Status |
| --- | ---: | --- |
| Original confirmed gaps | 47 | Remediated |
| Original verification-only items | 9 | Resolved |
| Original verification-only items | 4 | Partially verified or externally blocked |
| New architecture-parity defects | 2 | Remediated |
| New build/distribution defects | 1 | Remediated |
| Additional portability checks | 2 | Native hosts/runners required |

## Method

Four high-reasoning reviews independently covered the product epics, provider/sync architecture, mutation/process/security architecture, and final finding adjudication. Every candidate was then checked against current source and recorded runtime evidence. Candidate gaps for missing-attachment detection, external-file drag/drop, themes, notification-baseline semantics, typed provider cursors, keyboard folder/message moves, and general additional-window offset were rejected because current code or focused runtime evidence implements them.

The audit treated a source path as implementation evidence and a successful command or observed native workflow as runtime evidence. It does not convert unavailable cloud credentials or unavailable Windows/macOS hosts into passing claims.

## New architecture findings

### 1. Graph mailbox topology is recursive, paged, and cursor-safe — remediated

**Severity:** High  
**Architecture:** §1 `MailboxTopologySyncState`; §2 provider abstraction; §3 Sync Topology; Epic 2 nested folders  
**Status:** Closed

The approved provider-neutral contract is `MailboxTopologyResult`: upserts, explicit provider-id removals, an opaque versioned cursor, and a full-snapshot/delta discriminator. Gmail and IMAP return complete snapshots without topology cursors. The reconciler may delete every unseen mailbox only for a complete snapshot; a delta removes only explicit ids, so unchanged Graph folders omitted from a delta remain intact.

`GraphMailProvider.SyncMailboxTopologyAsync` in `server/MyloMail.Api/Providers/Graph/GraphMailProvider.Topology.cs` now:

- follows every root `OdataNextLink`;
- establishes and resumes a child-folder delta stream for every folder, including empty folders, and recursively discovers all descendants;
- stores root and per-folder `deltaLink`s plus parent relationships in a versioned provider cursor;
- merges repeated occurrences in feed order within one stream, preserves an existing child stream cursor when that child is discovered beneath a new parent, and point-reads a removed immutable id before classifying it as deleted rather than moved;
- prunes only confirmed removed subtrees, baselines streams for genuinely new folders, and treats an upsert as authoritative when independent parent streams report both sides of a move;
- sends initial, continuation, delta-link, and removal-resolution requests through the central immutable-ID Graph pipeline.

`TopologySyncService.ReconcileAsync` now passes the durable topology cursor to the provider, distinguishes snapshots from deltas, applies root moves as authoritative null parents, and writes the replacement cursor in the same transaction as mailbox upserts, parent changes, removals, and topology health. Provider cursor invalidation immediately performs a complete recursive rebaseline; a failed baseline cannot replace the prior cursor.

`GraphTopologyTests.Topology_walks_every_page_and_child_delta_then_resumes_each_stream` proves root pagination, recursive nesting, incremental resume, repeated-occurrence ordering, point-read deletion proof, split parent change, existing child-cursor preservation, new child-stream creation, removed-stream suppression, and immutable-ID middleware. `SyncTests` proves snapshot/delta deletion semantics, cursor handoff, root reparenting, invalid-cursor rebaseline, restart behavior, and cursor/data rollback at the apply-before-commit fault boundary.

### 2. Rich-text composition failed under the production CSP — remediated

**Severity:** Medium  
**Architecture:** Epic 6 compose plain text or HTML; §12 strict CSP  
**Status:** Closed

At audit time, the production renderer CSP in `apps/renderer/index.html` permitted `style-src 'self'` and correctly rejected inline styles. Lexical's default HTML exporter assigned `white-space: pre-wrap` to every temporary text element; merely assigning that inline style violated the live renderer's CSP. Clicking Bold or Italic logged:

```text
Applying inline style violates the following Content Security Policy directive 'style-src 'self''
```

`Editor.tsx` now overrides only Lexical's text-node HTML export with semantic elements and no inline style. Its live theme uses CSS Module classes; the strict CSP remains unchanged.

`tests/renderer.e2e/Compose.e2e.ts` now proves the consumer-visible contract through the actual Electron-to-SMTP path: the received Mailpit HTML contains semantic `<strong>` and `<em>` content for the entered text, and applying both formats emits no inline-style CSP violation.

## New non-parity finding

### Carbon font assets were not bundled — remediated

**Severity:** Low  
**Classification:** Build/distribution quality, not an explicit architecture requirement  
**Status:** Closed

At audit time, the generated renderer stylesheet contained literal `~@ibm/plex/...` URLs while the distribution contained no font files. The renderer therefore requested nonexistent resources, logged startup 404s, and fell back to system typography.

The renderer now has a direct `@ibm/plex` asset dependency. `Styles/Carbon.scss` disables Carbon's broken default font-face emission and declares the exact local Sans and Mono faces used by the application. `electron-vite` fingerprints and emits those five WOFF2 assets, and the final CSS references only relative packaged asset URLs.

`tests/renderer.e2e/PackagedMode.e2e.ts` loads all five faces through the browser Font Loading API and requires five distinct packaged WOFF2 resource entries. `pnpm package:smoke` passed against the unpacked production application without the prior startup 404s.

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
- Added the provider-neutral `MailboxTopologyResult` contract and documented Graph's versioned root/child topology cursor plus snapshot/delta deletion semantics.
- Replaced the stale §16 “verification blockers” list with dated dependency/deployment watchpoints. SQLite WAL/FULL, current Hangfire compatibility, and TypeContractor Zod output are now recorded as verified; TypeScript 7 remains deferred.

## Explicitly deferred or excluded

Unchanged from the architecture: server-side rules/filters, mail import, auto-update, internationalised strings, code signing/notarisation, storage eviction, exactly-once send, sending scheduled mail while the process is closed, CI policy for live-provider tests, and TypeScript 7 adoption.

## Bottom line

The first audit's implementation backlog and all three second-pass defects are closed. The audited source now matches the architecture for every locally actionable item. Live cloud-provider behavior, Windows/macOS keychains and packages, complete native tray interaction, and non-Linux attachment handling remain evidence gaps that require credentials or native environments; they are not known source omissions.
