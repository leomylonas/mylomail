# Architecture audit — third pass

**Audit date:** 2026-09-12  
**Baseline:** `7db9c86` (`main`, clean and synchronized with `origin/main`)

## Verdict

The frozen architecture remains coherent, but the implementation is not yet production-safe. The dominant risks are silent synchronization skips, crash-window recovery gaps, and operations that cross multiple durable or provider boundaries without sufficient state.

No evidence was found that the 47 gaps closed by the previous parity audits have reopened. The findings below are deeper transition, concurrency, provider-semantic, restart, packaging, and native-verification defects that story-parity checks did not expose.

The review was read-only. No build or test suite was rerun. The last recorded baseline was green at 614 .NET tests and 218 Vitest tests, but those checks do not prove the crash and ordering invariants below.

## Method

Eight independent high-reasoning reviews covered persistence, providers, sync, mutations and outbox, security, frontend behavior, release operations, and cross-component requirement parity. Candidates were deduplicated and checked against the frozen architecture, prior audits, current handover, and current source at the baseline commit.

A source path establishes an implementation observation. It does not replace runtime proof for crash windows, native desktop integration, or live provider behavior.

## High-severity findings

### 1. Gmail history advances past missing or incorrectly ordered changes

**Locations:**

- `server/MyloMail.Api/Providers/Gmail/GmailMailProvider.cs:277-322`
- `server/MyloMail.Api/Sync/ChangeStreamService.cs:351-410`

`History.messagesDeleted` is ignored, so a permanent Gmail deletion can remain locally forever while the history cursor advances. Gmail also collapses changed ids to current hydrated messages while retaining earlier label-removal records independently. If a label is removed and re-added in one history page, ingestion adds the current membership and then applies the earlier removal, leaving the wrong final state.

Both paths violate the invariant that the cursor never advances past unapplied changes. Gmail advertises incremental expunges and has no periodic integrity path that repairs the skipped deletion.

### 2. Durable Gmail staged history has no startup owner

**Locations:**

- `server/MyloMail.Api/Mutations/StartupReconciliation.cs:15-41`
- `server/MyloMail.Api/Scheduling/StartupScheduler.cs:73-126`
- `server/MyloMail.Api.Tests/Sync/SyncCrashWindowTests.cs:253-276`

A crash after Gmail history is staged and its cursor advanced, but before canonical replay, leaves durable staged events that startup never inventories or schedules. Polling resumes from the advanced cursor, so the events can remain unapplied permanently. The current crash test manually invokes replay after rebuilding services and bypasses the production startup path.

### 3. IMAP coverage pagination can silently skip messages

**Location:** `server/MyloMail.Api/Providers/Imap/ImapMailProvider.Sync.cs:25-51`

The resume token is an ordinal offset into a freshly searched and sorted UID list. If an already-consumed UID is expunged before the next page, the list shifts and `Skip(offset)` omits an unfetched message. Coverage can still become `Covered`; incremental synchronization cannot discover the missing historical addition. The token must use a stable UID boundary.

### 4. Graph move tombstones are not fenced against delayed destination streams

**Locations:**

- `server/MyloMail.Api/Scheduling/TombstoneGcJobs.cs:36-104`
- `server/MyloMail.Api/Sync/MessageIngestor.cs:668-690`

Graph reports move removal and destination addition through independent streams. The fixed 30-minute grace period is unrelated to whether the destination stream has caught up. After a longer offline interval, GC can delete the canonical `Message` before the destination delta arrives. The late addition then creates a new `Message.Id`, breaking stable local identity and references to the original row.

### 5. Tombstone collection uses stale eligibility

**Locations:**

- `server/MyloMail.Api/Scheduling/TombstoneGcJobs.cs:78-123`
- `server/MyloMail.Api/Scheduling/TombstoneGcJobs.cs:182-214`

Zero membership and `OrphanedAt` are checked while selecting candidates, outside the later deletion transaction. `IsCollectibleAsync` does not recheck either condition. A destination-add delta can commit between selection and deletion, after which GC deletes the live message and cascades the new occurrence. `MarkOrphanedAsync` has the same race and can preserve an obsolete grace timestamp after membership returns.

### 6. Topology-generation fencing is incomplete

**Locations:**

- `server/MyloMail.Api/Sync/GenerationSnapshot.cs:43-60`
- `server/MyloMail.Api/Sync/IntegrityReconciliationService.cs:46-76`
- `server/MyloMail.Api/Sync/ChangeStreamService.cs:520-580`
- `server/MyloMail.Api/Sync/TopologySyncService.cs:314-389`

Successful integrity and replay paths compare against previously loaded mailbox objects rather than current rows inside the apply transaction. Deleting and recreating a mailbox also resets its generation to zero. A page issued for an obsolete mailbox incarnation can therefore mutate a replacement incarnation.

### 7. Mutation recovery can bind the wrong remote message

**Locations:**

- `server/MyloMail.Api/Mutations/MutationReconciler.cs:299-327`
- `server/MyloMail.Api/Persistence/MyloMailDbContext.cs:183-190`

When stable provider identity is unavailable, recovery matches `Message-ID` plus `ReceivedAt`. Both are non-unique, and multiple matches in one mailbox overwrite one dictionary slot. Recovery can persist another message's UID onto the original local `Message.Id`; a later mutation can then modify or delete the wrong remote message.

### 8. Account removal can irrecoverably strand credentials

**Locations:**

- `server/MyloMail.Api/Accounts/AccountProvisioningService.cs:276-281`
- `server/MyloMail.Api/Accounts/AccountProvisioningService.cs:330-345`
- `server/MyloMail.Api/Credentials/ICredentialStore.cs:8-15`

The Account row is committed as deleted before four independent credential deletions. A crash or credential-store exception leaves secrets behind, while retrying removal returns immediately because the Account no longer exists. There is no durable credential-deletion tombstone or credential enumeration contract.

### 9. Linux WAL locality checks can be bypassed by a symlink

**Locations:**

- `server/MyloMail.Api/Persistence/DataDirectory.cs:65-71`
- `server/MyloMail.Api/Persistence/DataDirectory.cs:103-119`
- `server/MyloMail.Api/Persistence/DataDirectory.cs:159-196`

Linux classifies the lexical override path against `/proc/self/mounts` without resolving symlinks. A local-looking path symlinked into NFS or SMB is accepted, allowing the database, WAL, and shared-memory files onto network storage. The existing link-resolution helper is used only by the macOS branch.

### 10. Closing a detached compose window loses recent edits

**Locations:**

- `apps/renderer/src/Components/Compose/Compose.tsx:314-354`
- `apps/renderer/src/Shell/Windows/ComposeWindow/ComposeWindow.tsx:44-50`
- `apps/electron-shell/src/Main.ts:652-673`

Autosave is debounced by two seconds, and effect cleanup cancels the pending timer. Native window close destroys the renderer without a save handshake. Closing within the debounce window silently loses recipients, subject, or body changes despite the requirement that detached compose survive window close.

### 11. Backend launch secrets are transported through process environment

**Locations:**

- `apps/electron-shell/src/BackendSupervisor.ts:62-82`
- `server/MyloMail.Api/Security/LaunchTokenMiddleware.cs:66-77`
- `server/MyloMail.Api/Credentials/CredentialStoreSelector.cs:20-28`

`MYLOMAIL_LAUNCH_TOKEN` and the fallback `MYLOMAIL_MASTER_PASSWORD` are placed in the child environment. On systems allowing same-user process environment inspection, another process can steal the REST/SignalR capability token and, during fallback startup, the credential-vault password. Clearing a managed environment value after initialization does not remove the process-creation observation window.

### 12. Common executable attachment types bypass the warning

**Locations:**

- `apps/electron-shell/src/DangerousAttachment.ts:9-40`
- `apps/electron-shell/src/Main.ts:166-201`
- `server/MyloMail.Api/Content/AttachmentService.cs:82-130`

Danger detection is extension-only and omits `.py` and `.pyw`. On Windows with normal Python associations, opening such a file executes it through `shell.openPath` without the required code-execution warning.

### 13. Graph send has an unrecorded provider-side boundary

**Locations:**

- `server/MyloMail.Api/Outbox/SendExecutor.cs:106-133`
- `server/MyloMail.Api/Providers/Graph/GraphMailProvider.Send.cs:38-53`
- `server/MyloMail.Api/Outbox/SendReconciler.cs:130-150`

Graph send creates a draft and then sends it inside one opaque provider call. The immutable draft id exists only in memory. A crash after creation but before submission leaves an ambiguous outer attempt that reconciliation cannot distinguish or resume; it can also strand a remote draft.

## Medium-severity findings

### Synchronization and scheduling

1. A null IMAP cursor searches all UIDs while the change stream bypasses the coverage gate, allowing bounded accounts to download historical backlog as live mail and emit it as `MessageReceived`: `ImapMailProvider.Sync.cs:104-148`, `SyncJobs.cs:347-418`.
2. Startup, periodic topology, and each coverage page can enqueue the same mailbox without a single owner, permitting duplicate page application, double-counted progress, and multiplied successor chains: `SyncJobs.cs:111-181`, `StartupScheduler.cs:126-138`, `CoverageService.cs:96-158`.
3. Jobs encountering an active account throttle stop instead of scheduling after `gate.Delay`; natural expiry has no callback to restart topology, coverage, change, calendar, or integrity work: `SyncJobs.cs:97-102,423-427,510-517,755-776`.
4. Provider 5xx responses and temporary credential-store failures can stop sync permanently because automatic retry is disabled and generic branches do not schedule successors: `SyncJobs.cs:149-181,303-318,827-841`, `GraphThrottleAwareRequests.cs:24-49`, `GmailThrottleAwareRequests.cs:68-107`.
5. Successful staged Gmail pages do not consistently clear account-wide stream health or announce recovery for every mailbox: `ChangeStreamService.cs:468-493`.

### Mutation, outbox, and persistence

1. Throttle, authentication, and credential failures strand later leased mutation batches; only the network-error path releases all unattempted claims: `MutationJobs.cs:70-179`.
2. A credential-store outage returns an outbox item to `Scheduled` but schedules no successor job: `SendExecutor.cs:149-169`, `OutboxJobs.cs:190-209`.
3. Delivered native notifications stop retaining their message immediately even though the OS notification can remain clickable: `TombstoneGcJobs.cs:150-157`, `MailHub.cs:1442-1518`.
4. Default send-identity promotion uses two commits and can leave an account with zero defaults: `SendIdentityService.cs:82-113`.
5. Search-row deletion and Account deletion use separate commits, allowing surviving mail to disappear from search without a startup repair path: `AccountProvisioningService.cs:299-332`, `SearchIndexer.cs:102-113`.
6. HTML search extraction uses a bracket scanner rather than an HTML parser, allowing attributes and entity spellings to become searchable text: `SearchIndexer.cs:185-237`.

### Provider correctness

1. Microsoft reauthentication becomes interactive only for `ConsentRequired`; other `MsalUiRequiredException` states repeat the failing silent path: `GraphOAuthAuthenticator.cs:29-82`.
2. Gmail authoritative mailbox totals are read from `labels.list`, which does not provide the required totals; bounded mailboxes can show incomplete local counts: `GmailMailProvider.cs:82-103`.
3. Gmail and Graph Move-to-Trash outcomes omit the destination occurrence: `GmailMailProvider.Mutations.cs:79-139`, `GraphMailProvider.Mutations.cs:162-206`.
4. Graph batch subresponse 429s are terminalized for flags and moves because the outer HTTP response is 200 and inner `Retry-After` is not translated: `GraphMailProvider.Mutations.cs:62-129,180-195,251-298`.
5. Gmail bulk mutations perform serial per-message GET preflights before the nominal batch request: `GmailMailProvider.Mutations.cs:144-242`.
6. Graph resolves the chosen From identity but does not map it onto outgoing messages or drafts: `GraphMailProvider.Send.cs:129-160`, `GraphMailProvider.Drafts.cs:108-121`.
7. Graph server-side drafts omit all attachments: `GraphMailProvider.Drafts.cs:15-42,108-121`.
8. SMTP and CalDAV certificate failures flatten the structured hostname and fingerprint into generic authentication prose, preventing safe pinning: `CertificateTrust.cs:50-69`, `ImapMailProvider.Send.cs:91-103`, `CalendarProviderFactory.cs:110-123`.

### Frontend contracts and multi-window state

1. “Keep theirs” changes React state but does not remount the Lexical editor; visible and sent bodies can disagree: `Compose.tsx:429-454,785-866`.
2. “Discard” closes the compose surface without deleting a persisted draft: `Compose.tsx:980-1001`.
3. Cc-only and Bcc-only messages are disabled and excluded from empty-draft autosave detection even though the backend accepts them: `Compose.tsx:346-354,918-928`.
4. Message-size checks count attachment estimates rather than the complete encoded MIME message, and body-only oversized messages bypass the check: `Compose.tsx:476-500`, `DraftService.cs:342-373`.
5. Durable mutation intent is not broadcast when enqueued, so peer windows remain stale until provider settlement: `MutationQueue.cs:174-206`, `MessageList.tsx:359-506`.
6. Calendar additions, removals, and renames emit no collection event, leaving infinite-stale queries with missing or phantom calendars: `CalendarSyncService.cs:156-203`, `HubConnection.ts:311-325`.
7. Send-identity mutations are not broadcast, leaving peer compose windows with stale aliases, signatures, and defaults: `MailHub.cs:618-683`.
8. Account removal does not clear peer-window selections or message/body caches: `HubConnection.ts:167-233`, `AppShell.tsx:164-174,625-636`.
9. Mailbox summaries omit durable fetched/estimated progress and the tree does not render Availability: `MailDtos.cs:29-47`, `MailboxSummaryDtoFactory.cs:112-159`, `MailboxTree.tsx:404-430,899-919`.
10. Persisted panel layout loads after the uncontrolled panel group mounts, allowing the hard-coded layout to win: `UseShellLayout.ts:20-63`, `AppShell.tsx:469-485`.
11. Rate limits generate error toasts despite the frozen silent mapping: `ErrorPresentation.ts:92-101`.
12. Ordinary message links navigate the sandboxed iframe instead of opening in the external browser: `MessageHtml.tsx:286-300`, `Main.ts:636-650`.
13. The generated typed SignalR proxy and receiver are not consumed; the renderer invokes and registers raw string names: `TypedSignalR.Client/MyloMail.Api.Hubs.ts`, `HubConnection.ts:133-169`.

### Lifecycle and release

1. A shell startup failure can orphan its owned backend because the outer catch cannot reach the child and shutdown handlers are installed late: `Main.ts:86-124,343-367,849-858`.
2. The release job lacks `needs: package`, so it can download artifacts before the packaging matrix finishes: `.github/workflows/release.yml:14-103`.

## Lower-priority implementation findings

1. Tombstone GC's fixed `Take(50)` can repeatedly revisit permanently referenced rows and starve later eligible tombstones: `TombstoneGcJobs.cs:77-177`.
2. Remote-authored Content-IDs can enter request logs, telemetry URL paths, and forwarded renderer console messages: `SanitiseMessageHtml.ts:129-159`, `Program.cs:84-107`.
3. Context-menu `unavailable` explanations are discarded instead of exposed accessibly: `MessageContextMenu.tsx:31-55`.
4. Scheduled-send input hard-codes `mm/dd/yyyy`: `Compose.tsx:947-963`.
5. Calendar glyph navigation buttons lack accessible names: `Calendar.tsx:283-301`.
6. `package.json` declares open-ended `node >=22` instead of the documented Node 22 major pin.
7. `pnpm app` uses POSIX inline environment syntax and fails under the default Windows shell.
8. `README.md` references removed `build:renderer` and `build:shell` scripts.

## Verification gaps

1. Sync cursor/page, staged-history, and topology-generation boundaries lack hard-process-kill coverage. Existing sync tests dispose services and manually invoke recovery.
2. Tombstone FTS deletion lacks a process-kill scenario and deliberate broken-invariant proof.
3. Mutation recovery lacks a mixed partial batch containing successful and unresolved siblings under one durable attempt.
4. Mutation and send restart tests inventory work directly rather than proving `StartupScheduler` dispatches it.
5. The FTS table-rebuild migration test migrates fully before seeding, then performs a no-op migration.
6. Packaged-mode E2E overrides `MYLOMAIL_RENDERER_PATH`, bypassing the renderer under `process.resourcesPath/Renderer`.
7. CI regenerates TypeScript contracts but never fails on a dirty generated diff.
8. Normal CI does not prove the actual electron-vite bundle, while deep E2E can consume missing or stale ignored `dist` output.
9. Graph and Gmail native RSVP remain unverified against live providers.
10. Gmail expired-history translation remains skipped in the live provider matrix.
11. Actual native notification display/click and Linux tray restoration/Quit remain unverified on the OS surface.

## Architecture areas that held

The audit found no competing architecture or broad structural drift in these areas:

- stable local `Message.Id` and `Mailbox.Id` ownership;
- provider occurrence identity placement;
- raw MIME as authoritative storage;
- attachment rebuild tied to raw version;
- SQLite WAL/FULL/busy-timeout/foreign-key configuration and pre-migration backup ordering;
- Graph immutable-id request routing;
- credential-backed Google and MSAL token caches;
- remote-content default denial, iframe sandboxing, authenticated CID resolution, and launch-token omission from the renderer bridge;
- per-window stores/query clients and reconnect-wide invalidation;
- the recently passing live mail, calendar, and contact provider matrix.

The remaining architectural weakness is transition ownership: each cursor page, provider sub-operation, lease, staged event, external secret deletion, native window close, and retry gate needs exactly one durable owner through failure and restart.

## Remediation status — 2026-09-14

All source-code findings above are remediated in the current worktree. The original text is retained as the baseline record; this section records the implemented disposition rather than rewriting the audit after the fact.

### High-severity findings

| # | Resolution |
|---|---|
| 1 | Gmail history now applies permanent deletions and derives each message's final label state before advancing the cursor. |
| 2 | Startup inventory owns durable staged history and schedules canonical replay even while polling is paused. |
| 3 | IMAP coverage resumes from a stable UID high-water mark rather than a mutable ordinal. |
| 4 | Graph tombstones are fenced by destination-stream progress before collection. |
| 5 | Tombstone orphaning and deletion recheck membership and age inside their write transactions. |
| 6 | Replay and integrity apply current topology generations; replacement mailbox generations remain monotonic. |
| 7 | Ambiguous mutation reconciliation refuses non-unique remote candidates instead of binding an arbitrary one. |
| 8 | Account deletion persists credential-cleanup intent and startup resumes every unfinished credential slot. |
| 9 | Linux locality checks resolve symlinks before mount classification. |
| 10 | Detached compose close now waits for the latest save, attachment mutations, and any send already in flight. |
| 11 | Backend launch capabilities and fallback vault secrets use inherited file descriptors rather than child-environment values. |
| 12 | Executable attachment classification includes Python scripts and the other common platform execution forms. |
| 13 | Graph send persists and re-resolves the immutable remote draft boundary; ambiguous resend reuses the durable local item with a new stable Message-ID. |

### Other implementation findings

- **Synchronization and scheduling:** initial IMAP baseline is separated from live ingestion; coverage, topology, retry, throttle, staged-health, and recovery work each have explicit ownership and successor scheduling.
- **Mutation, outbox, and persistence:** every unattempted lease is released on recoverable failure; credential-blocked sends reschedule; notification retention survives OS clickability; default-identity, account/search deletion, and HTML indexing are transactional and deterministic.
- **Providers:** Microsoft reauthentication covers every interactive-required result; Gmail obtains authoritative counts and batches preflights; Graph/Gmail trash outcomes carry destination occurrences; inner Graph throttles remain retryable; Graph honors From and draft attachments; certificate failures preserve pinning metadata.
- **Frontend and multi-window contracts:** compose conflict/discard/recipient/size behavior is server-consistent; durable intent, calendar, identity, account-removal, mailbox-health, and progress changes broadcast across windows; persisted layout wins before mount; rate limits remain silent; links leave the sandbox through Electron; generated SignalR contracts now own invocation and receiver names.
- **Lifecycle, release, and lower-priority findings:** backend startup failure cannot orphan its child; release depends on packaging; tombstone pagination cannot starve; Content-IDs are redacted; inaccessible context actions explain themselves; dates are locale-aware; calendar controls are named; Node is pinned to major 22; development scripts are cross-platform; stale README commands are removed.

### Verification-gap disposition

| Original gap | Status |
|---|---|
| Sync crash boundaries | Added hard-process staged-page rollback/restart proof; the fault suite also covers cursor advancement and topology-generation rejection. |
| Tombstone/FTS crash atomicity | Added a real process kill between deletion and commit, followed by restart and FTS integrity verification. |
| Mixed partial mutation batch | Added a batch containing a successful member and an unreported sibling; only the unreported item remains ambiguous. |
| Production startup dispatch | StartupScheduler now has observable mutation, outbox, staged-history, and content recovery dispatch coverage. |
| FTS rebuild migration | The migration test now seeds before the rebuilding migration and verifies both search results and FTS integrity afterward. |
| Packaged renderer bypass | Packaged smoke no longer overrides `MYLOMAIL_RENDERER_PATH`; it loads `resources/Renderer`. |
| Generated-contract drift | CI regenerates contracts and fails on a dirty generated diff. |
| Electron bundle coverage | normal and deep checks rebuild the current electron-vite graph before exercising it. |
| Native provider RSVP | **Externally blocked:** deterministic proof requires an invitation owned by a second consenting Gmail and Microsoft 365 calendar account. |
| Gmail expired history | **Externally blocked:** the provider does not expose a way to manufacture an expired history cursor; the live test runs when `GMAIL_EXPIRED_HISTORY_ID` names a known-expired cursor. |
| Native notification/tray | **Host-interaction blocked:** headed Electron proved minimize-to-tray keeps the process and hidden window alive, but this session cannot safely click the GNOME notification or tray menu. No mock was retained as misleading native proof. |

### Verification evidence

- `pnpm check`: format, TypeScript, ESLint, Stylelint, build, 673 .NET tests, and 225 Vitest tests passed.
- `pnpm e2e`: 24 Electron workflows passed; one packaged/native-platform-only case skipped by design.
- `pnpm package:smoke`: the packaged shell launched its bundled self-contained backend and renderer.
- Deep conformance passed 65/65 and fault injection passed 61/61, including the new process-kill scenarios.
- Stryker mutation discrimination passed after the Secret Service proxy was vendored from Tmds.DBus.Generator 0.95.0; this avoids Stryker dropping required `AdditionalFiles` metadata during its Roslyn rebuild.
- Two independent invariant reviews found no remaining mutation, sync, reconciliation, lease, cursor, retry, or UI lifecycle violation after their findings were repaired.
