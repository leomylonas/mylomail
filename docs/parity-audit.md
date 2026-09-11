# Architecture parity audit

## Verdict

The application is not yet feature-parity with `docs/architecture.md`.

Four independent high-reasoning SOL audits found substantial implemented coverage, but also confirmed missing behavior, partial implementations, structural deviations, and verification gaps.

| Audit | Implemented | Partial | Missing | Unverified | Deferred / out of scope |
| --- | ---: | ---: | ---: | ---: | ---: |
| Epics and user features | 67 | 15 | 2 | 9 | 0 |
| Backend, sync, mutation, events, search | 45 | 11 | 3 | 8 | 1 |
| Providers, credentials, OAuth, send | 27 | 12 | 2 | 3 | 1 |
| Platform, security, testing, packaging | 71 | 18 | 11 | 6 | 14 |

These audits overlap; the counts must not be added into a single percentage.

## Major confirmed feature gaps

### Account and provider onboarding

1. **Gmail account setup is unavailable.**
   - `apps/renderer/src/Components/AddAccount/AddAccount.tsx` disables Google and labels it “coming soon.”
   - Backend OAuth/provider implementations exist but are unreachable through the application.
2. **Microsoft 365 account setup is unavailable.**
   - The same disabled UI path applies.
   - Consequently Graph mail, calendar, and contact functionality cannot be reached by a new user.
3. **Gmail bring-your-own OAuth credentials are absent.**
   - The architecture requires BYOC as a supported mode.
   - Only deployment environment configuration exists.
   - Required guidance for wrong OAuth client type, disabled APIs, and seven-day testing tokens is absent.
4. **CalDAV cannot be configured through IMAP account setup.**
   - Calendar providers and renderer exist, but a user cannot supply independent CalDAV settings through the current onboarding UI.

### Provider correctness

5. **IMAP password authentication can use plaintext.**
   - `ImapMailProvider` uses `SecureSocketOptions.None` when `UseSsl=false` and still authenticates.
   - The architecture requires refusing password authentication without SSL/STARTTLS.
   - Separate IMAP/SMTP security modes and the SMTP authentication method are not represented as specified; OAuth2 is declared but not implemented.
6. **IMAP QRESYNC advertises expunge support but returns no removals.**
   - The provider capability says incremental expunges are supported, while the sync result does not populate them.
7. **IMAP “Move to Trash” currently expunges permanently.**
   - This violates the distinct `MoveToTrash` versus permanent-delete semantics.
8. **Native provider batching is incomplete.**
   - IMAP loops per UID rather than using UID sets.
   - Gmail trash and Graph permanent-delete paths issue per-message requests.
9. **Graph large-upload requests bypass the central Graph request pipeline.**
   - `GraphMailProvider.Send.cs` uses a bare `HttpClient` for upload-session PUTs.
   - This violates the architecture’s “every Graph request” invariant, although upload URLs may not themselves return item IDs.
10. **Google provider-backed contact deletion is disabled.**
    - This is currently the safe implementation because Google People provides no atomic revision precondition for DELETE.
    - It nevertheless conflicts with the architecture’s unqualified “Google contacts synchronise two-way” requirement. This is a design-contract gap requiring an explicit architecture exception, not an unsafe code change.

### Sync and topology

11. **Gmail synthetic folder hierarchy is missing.**
    - `TopologySyncService` does not split slash-delimited Gmail labels or create synthetic intermediate nodes as required.
12. **Gmail `LastNMessages` bound semantics are wrong.**
    - The provider applies the bound as a per-page maximum but continues following page tokens, eventually materialising the whole label.
13. **Graph bounded initial-sync semantics are wrong.**
    - It does not perform the required full delta enumeration while materialising only the selected recent subset.
    - Out-of-bound messages are materialised.
14. **Content fetches are not fenced by mailbox topology generation.**
    - Coverage and change-stream pages carry generation snapshots; `ContentAcquisition` does not.
    - Late content work can therefore survive mailbox deletion/recreation contrary to the architecture.
15. **An in-flight coverage page can commit after account disable/removal.**
    - Entry and successor-enqueue checks exist, but coverage does not recheck account enablement after the provider call and before committing the page.
16. **Mailbox-scoped cursor invalidation does not increment topology generation.**
    - Older content work may remain valid after the mailbox resynchronisation boundary.
17. **The mailbox API has no separate `Availability` projection.**
    - Coverage is exposed, but the required orthogonal `Usable`/`Degraded`/`Unavailable` state is absent.

### Events and cross-window consistency

18. **`MessageDeleted` has no producer.**
    - The SignalR contract exists, but no deletion path emits it.
19. **Confirmed local mutations do not emit `MessageUpdated`.**
20. **Content-index pages do not emit `SyncProgress`.**
21. **`MailboxUpdated` is not emitted at the end of every specified sync concern.**
22. **Account settings, account order, and account-collapse changes do not update all open windows.**
    - Several mutations persist successfully but emit no corresponding cross-window invalidation.
23. **Native notifications duplicate across windows.**
    - `NotificationReady` is broadcast to every renderer.
    - Every renderer invokes the Electron notification bridge, producing one OS notification per open window.
24. **Notification-click navigation is incomplete.**
    - It sets the message ID without reliably selecting or refetching the owning account, mailbox, subject, and sender context.

### Calendar

25. **Calendar access is effectively unavailable to newly configured users.**
    - OAuth account types are disabled and IMAP setup has no CalDAV configuration.
26. **Calendar editing lacks timezone and recurrence controls.**
    - The backend model handles these concepts, but `EventModal.tsx` exposes local start/end fields only.
27. **Calendar CRUD, invite round trips, and organiser-visible attendee updates are unverified.**
    - Code and unit-test evidence exists, but real provider/Electron verification does not.

### Mail reading and actions

28. **Remote-content policy is incomplete.**
    - Request-layer blocking and sender allowlisting exist.
    - Domain allowlists and explicit sender/domain blocklists required by the document do not.
29. **Message-list columns are incomplete.**
    - From, Subject, and Date are sortable.
    - Snippet and read/flag state are not full sortable/filterable columns.
30. **Optimistic mutation display is incomplete.**
    - Read/unread overlays desired state.
    - Flag and membership-changing actions do not provide the same immediate projection.
31. **Print output is incomplete.**
    - It omits required message headers.
    - It uses `window.print()` rather than the specified Electron print bridge.
32. **Existing-account settings cannot change initial-sync mode/bound.**
33. **Sidebar counts do not consistently show both unread and provider-total counts.**
34. **Changing the selected send identity does not replace or insert that identity’s signature.**
35. **Incoming `mailto:` activation is absent.**
    - The app registers itself as a handler, but no startup-argument, `open-url`, or second-instance path turns an incoming URI into a compose window.
36. **Small-window layout behavior is incomplete.**
    - No minimum size or adaptive/wrapping header behavior protects the three-panel layout.
37. **Locale-aware formatting is inconsistent.**
    - Several calendar surfaces hard-code English labels and twelve-hour formatting instead of the OS locale.

## Platform and architecture gaps

38. **Unified RFC 7807 error transport is not implemented.**
    - REST controllers frequently return plain `ProblemDetails`.
    - SignalR paths throw plain-text `HubException`.
    - The renderer lacks the required uniform category deserialization.
    - Therefore Network/Auth/RateLimit/Conflict behavior is not consistently driven from `MutationProblemDetails.Category`.
39. **Connectivity handling does not fully pause work while offline.**
    - Logging noise is suppressed, but some content/mutation paths immediately enqueue another run rather than waiting for connectivity recovery.
40. **Attach-mode Electron operation is missing.**
    - `ELECTRON_BACKEND_MODE=attach` is declared but not used.
    - `BACKEND_URL` is unused.
    - Corresponding isolated Playwright E2E mode is absent.
41. **Packaging is absent.**
    - No `.deb`, `.rpm`, AppImage, Windows installer, or macOS package/release pipeline exists.
    - Required SmartScreen and Gatekeeper guidance is absent.
    - “No code signing” is out of scope; producing unsigned packages is still in scope.
42. **macOS network-backed data-directory rejection is incomplete.**
    - Linux and Windows detection exist.
    - The macOS path relies on Linux `/proc/self/mounts`, so an NFS/SMB data directory can be accepted despite SQLite WAL restrictions.
43. **Migration failure is not visibly surfaced.**
    - The backend exits and the shell logs the failure, but no user-facing error dialog is presented.
44. **Explicit ICU/app-local globalization configuration is absent.**
45. **The fault-injection harness does not meet the documented process-level contract.**
    - It throws `SimulatedCrashException` and rebuilds dependency injection rather than killing and restarting the backend process.
    - The required mid-attachment-download fault point is absent.
46. **Integration-test project separation is incomplete.**
    - Real-provider tests live under the normal test project; the dedicated integration project is effectively empty.
47. **Required frontend stack pieces are absent or incomplete.**
    - `electron-vite`
    - TanStack Router
    - TanStack Form
    - `json-fetch-client`
    - Generated and consumed Zod schemas

These are architecture deviations even where replacement code currently supplies similar behavior.

## Verification-only gaps

The following implementations exist but cannot yet be treated as proven parity:

- Real Gmail and Graph mail conformance.
- Real Google Calendar and Graph Calendar behavior.
- Complete live three-tier IMAP conformance.
- Three-OS native keychain behavior.
- Actual OS attachment opening and cleanup.
- Live IMAP IDLE notification/reconnect behavior.
- Tray and last-window close behavior.
- Window-bounds restore/offset behavior.
- Full application crash/restart and immediate cached-render behavior.
- SQLite `FULL`-under-WAL power-loss guarantee.
- Current `Hangfire.InMemory` suitability.
- TypeContractor Zod output.
- Gmail CASA applicability and resulting deployment model.

## Explicitly deferred or excluded—not gaps

- Server-side rules and filters.
- Mail import.
- Auto-update.
- Internationalised strings.
- Code signing/notarisation.
- Storage eviction.
- Exactly-once send delivery.
- Sending scheduled mail while the application is closed.
- CI policy for live-provider integration tests.
- TypeScript 7 adoption.
- Gmail CASA decision pending confirmation from Google.

## Bottom line

The architecture’s central identity, mutation, persistence, threading, contact-conflict, raw-MIME, and much of the shell model are substantially implemented. The application is not architecture-complete. Most importantly, Gmail and Microsoft 365 cannot currently be added through the UI, several IMAP behaviors violate provider semantics, bounded-sync/topology edge cases remain, calendar onboarding is unavailable, event/error propagation is incomplete, and packaging does not exist.
