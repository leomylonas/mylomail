# MyloMail — Architecture Reference

## Overview

A .NET backend (spawned as an Electron child process) providing a unified sync/mutation API across IMAP, Gmail, and Microsoft 365/Graph, exposed to an Electron/React frontend via SignalR. Local storage in SQLite (via EF Core). Background sync and mutation processing via Hangfire with in-memory job storage; all durable state lives in the application's own tables.

**Terminology note**: this document uses "Account" for a connected mail account and "Mailbox" for a folder within it (IMAP folder / Gmail label / Graph mail folder). In conversation, "account" and "mailbox"/"folder" are sometimes used interchangeably for the same two concepts — treat any reference to a "mailbox" or "folder" as this document's `Mailbox`, and any reference to an "account" (including occasional "mailbox" used to mean the whole connected account) as this document's `Account`.

## Rationale — why this is being built

No existing Linux desktop mail client satisfied the combination of requirements sought:
- All accounts' mailboxes visible in a single persistent sidebar, without "switching" between accounts that hides mail arriving on the others.
- Full IMAP/SMTP support alongside native Gmail (mail + calendar) and Microsoft 365/Exchange (mail + calendar) support.
- Native Linux desktop application (not a web wrapper of limited fidelity).
- One-time cost acceptable; ongoing subscriptions explicitly not wanted.
- No bridge/relay tools (e.g. DavMail) required to reach Office 365.

Evolution and Thunderbird were the closest existing options but were considered unreliable and dated in feel. Commercial alternatives (eM Client, Mailspring, BlueMail, Kanmail, Front) were each ruled out on Linux support, licensing (subscription-only), architecture (unified-inbox-only), or fit for a personal desktop client. No suitable open-source "sync engine" foundation was found either (Mailspring's Mailsync had licensing ambiguity; EmailEngine/RustMailer are source-available commercial middleware, not free to run). Given the gap, building a bespoke client was judged the practical path forward, with an explicit design goal of reusing well-maintained libraries and standards wherever possible rather than reinventing protocol handling.

---

## 1. Resource Model

### Account
| Field | Type | Notes |
|---|---|---|
| `Id` | Guid | |
| `DisplayName` | string | |
| `ProviderType` | enum | `Imap`, `Gmail`, `Microsoft365` |
| `AuthState` | enum | `Connected`, `NeedsReauth`, `Error` — **authentication state only**; sync progress lives in the sync-state tables (below), not here |
| `IsEnabled` | bool | Soft-disable. Set `false` before account removal so running jobs stop cleanly (see §3); disabled accounts are excluded from scheduling and hidden from the sidebar |
| `LastAuthError` | string? | |
| `Color` | string | Hex, sidebar display |
| `SortOrder` | int | |
| `ProviderConfig` | polymorphic | Non-secret config only (host/port, tenant ID, IMAP `AuthMethod`) — never raw credentials |
| `PollIntervalSeconds` | int | Applies to all mailboxes under the account |
| `PollingEnabled` | bool | Account-level pause switch |
| `InitialSyncMode` | enum | `LastNMonths`, `LastNMessages`, `Full` — default for child mailboxes |
| `InitialSyncBoundValue` | int? | Null if `Full` |
| `UndoSendDelaySeconds` | int | Per-account undo-send window; `0` = instant send |
| `NotificationsEnabled` | bool | Per-account desktop notification toggle |
| `AttachmentSizeLimitOverride` | int? | Fallback when the provider can't report a limit |
| `CertificateTrustMode` | enum | `Default` \| `TrustAll` |

Credentials (passwords, OAuth tokens) are **never** stored on `Account` — see §4. Signatures and send-as addresses live on `SendIdentity` (§15), not on `Account`.

### SendIdentity (child of Account)
| Field | Type | Notes |
|---|---|---|
| `Id` | Guid | |
| `AccountId` | Guid | |
| `DisplayName` | string | |
| `EmailAddress` | string | The send-as address (alias or primary) |
| `SignatureHtml` | string? | |
| `IsDefault` | bool | Exactly one default per account |

`SendIdentity.EmailAddress` on the default identity is the **authoritative address for the account** — `Account` deliberately has no `EmailAddress` column, to avoid two sources of truth.

### AccountTrustedCertificate (child of Account)
`AccountId`, `Thumbprint` — multiple entries supported (certificate rotation). See §15.

### Mailbox
| Field | Type | Notes |
|---|---|---|
| `Id` | Guid | Local canonical identity |
| `AccountId` | Guid | |
| `ProviderMailboxId` | string? | **The provider's identifier for this mailbox.** Gmail label id, Graph folder id, IMAP full folder name. This is what provider calls use — never `Name` or a derived path. **Null only for synthesised intermediate nodes** in a derived Gmail label hierarchy (see below), which have no backing provider object; any other null is a bug |
| `ParentId` | Guid? | Self-referencing FK — relational nesting for sidebar tree |
| `Name` | string | Display name (leaf name, not a path) |
| `SpecialUse` | enum | `Inbox`, `Sent`, `Drafts`, `Trash`, `Junk`, `Archive`, `None` |
| `IsSubscribed` | bool | |
| `LocalSortOrder` | int | **Local sidebar ordering only.** No provider supports arbitrary folder ordering, so this is never pushed upstream (Epic 2) |
| `InitialSyncModeOverride` | enum? | Null = inherit account default |
| `InitialSyncBoundValueOverride` | int? | |
| `ProviderTotalCount` | int? | Provider-reported message count, where available |
| `ProviderUnreadCount` | int? | Provider-reported unread count, where available |

### ImapMailboxMetadata (1:1, IMAP accounts only)
| Field | Type | Notes |
|---|---|---|
| `MailboxId` | Guid | PK/FK |
| `FullName` | string | Server's full hierarchical name, e.g. `INBOX.Work.Clients` |
| `HierarchyDelimiter` | char | Server-declared, `.` or `/` — varies by server, must not be assumed |
| `NamespacePrefix` | string? | From the IMAP `NAMESPACE` capability |

IMAP hierarchy is a server-specific string convention, not a portable path. Keeping `FullName` and the delimiter as **provider metadata** rather than deriving identity from a user-facing path avoids breaking on servers with unusual delimiters or namespace prefixes.

**Gmail labels are presented flat, despite slash-delimited names.** Gmail supports hierarchy-looking labels via `/` in the label name (`Projects/Client`), but the API exposes labels as a flat collection with no parent ids — any tree is derived, not given. The sidebar therefore **derives** Gmail nesting by splitting label names on `/` and synthesising intermediate `Mailbox` rows where needed, with `ProviderMailboxId` set only on rows that correspond to a real label. Synthesised intermediates are not renameable or deletable, since no provider object backs them.

**Counts are two different numbers, and both are needed.** The locally computed count (via the `MessageMailbox` join) reflects only what has been downloaded. Under a bounded initial sync — "last 3 months" against a 10,000-message Inbox — that number is simply wrong as a mailbox total, yet Epic 2 requires accurate live counts. So:
- `ProviderTotalCount`/`ProviderUnreadCount` are refreshed on each sync and are what the sidebar displays.
- The locally computed count is used only where it is the correct answer, such as "messages currently shown in this view".
- Where a provider cannot report a count, the local count is displayed with an indication that older mail is not held locally.

### Sync state — four separate concerns

`InitialSyncStatus == Completed` was hiding materially different states, and a single per-mailbox cursor is wrong for Gmail. **Historical coverage and live change tracking are separate dimensions**, and topology discovery and integrity reconciliation are separate again. Four state machines, some of which a given provider collapses internally.

#### MailboxTopologySyncState (1:1 with Account)
| Field | Notes |
|---|---|
| `AccountId` | PK/FK |
| `Cursor` | Provider-specific, where the provider offers one |
| `LastReconciledAt`, `LastError` | |

**Mailbox work carries a topology generation.** `Mailbox.TopologyGeneration` is incremented whenever a mailbox is deleted, recreated or replaced by topology reconciliation. Sync pages, coverage jobs and content fetches carry the generation they were issued under and are discarded if it no longer matches — otherwise a late page from an in-flight sync can resurrect or mutate state belonging to a mailbox that has since been deleted and recreated (an IMAP folder deleted and recreated with the same name, a Graph folder replaced). This is the topology-domain equivalent of the message-mutation staleness protection in §6.

**Graph topology is recursive, not a flat list.** Graph exposes both mailbox-folder and child-folder delta forms, and the root delta does not return the entire hierarchy in one response — the implementation maintains child folders and parent relationships by traversing, not by assuming a flat result.

Folder discovery is **not** a by-product of message sync. Gmail's `history.list` is a message-history stream and does not report labels created, renamed or deleted elsewhere. Each provider reconciles topology separately: Gmail `labels.list`, IMAP `LIST`/`LSUB`, Graph `mailFolder` delta. This is what produces `MailboxTreeChanged`.

#### MailboxCoverageState (1:1 with Mailbox)
| Field | Notes |
|---|---|
| `MailboxId` | PK/FK |
| `Status` | `NotStarted`, `Backfilling`, `Covered`, `Failed` |
| `MessagesFetched`, `EstimatedTotal` | |
| `ResumeToken` | Persisted every page — resumability |
| `StartedAt`, `LastError` | |

How much of the user's requested history has been materialised locally. **Bounds are coverage targets, not membership limits**: "last N messages" means actively enumerate at least the newest N for that mailbox. Messages discovered through another mailbox or the account change stream may also appear there — necessarily so under Gmail's canonical label model, where one message legitimately belongs to several mailboxes at once.

#### ChangeStreamState — scope varies by provider
| Field | Notes |
|---|---|
| `AccountId` | |
| `MailboxId` | **Null for Gmail** — the stream is account-scoped |
| `CursorKind` | Discriminator: `GmailHistory`, `GraphDelta`, `ImapUid` |
| `CursorState` | **Structured and versioned per provider**, not an opaque string — see below |
| `BaselineEstablishedAt`, `LastSyncedAt`, `LastError` | |

Gmail provides one account-wide history sequence; per-label cursors would be fiction, would consume the same change stream repeatedly, and would race between label jobs. Graph and IMAP are genuinely per-mailbox.

**Cursor state is structured, because a single string would pretend these are equivalent when they are not:**

| Provider | State |
|---|---|
| Gmail | `HistoryId` |
| Graph | `DeltaLink` |
| IMAP | `UidValidity`, `HighestKnownUid`, `HighestModSeq?`, and reconciliation state (last known-UID-set baseline, last reconciliation time) |

Each is versioned so its shape can change without a migration guessing at the meaning of an old opaque token.

### Cursor advancement is transactional

> **Never persist a provider cursor past changes that have not been durably persisted.**

The cursor and the state it represents commit in the same transaction, always: Gmail's staging cursor with its staged history, Graph's delta progress with the applied page, IMAP's UID/MODSEQ progress with the observations it covers. Replaying a page is acceptable — every write is an upsert. Skipping one is not, and it is silent.

#### IntegrityReconciliationState (1:1 with Mailbox)
| Field | Notes |
|---|---|
| `MailboxId` | PK/FK |
| `LastReconciledAt`, `LastError` | |

Universal infrastructure, not an IMAP workaround — but **two distinct triggers with different purposes**:
- **Triggered resynchronisation** — recovery from a broken cursor: Gmail `historyId` expiry, Graph `410 Gone`, IMAP `UIDVALIDITY` change. Something is wrong.
- **Periodic integrity reconciliation** — routine work required *despite* a valid cursor, because the cursor cannot express what is needed. Only applies to degraded IMAP (below). Nothing is wrong.

Shared machinery, different cadence and meaning. Framing this as IMAP-specific would mean building it twice.

#### Derived UI state — two orthogonal dimensions

The sidebar must not reason across four state machines, or every surface will reimplement it and they will disagree. But the projection must be **two values, not one**, because a mailbox can be entirely usable while still backfilling:

- **`Availability`** — `Usable` | `Degraded` | `Unavailable`
- **`Coverage`** — status plus progress (fetched / provider-reported total)

Collapsing these into a single enum containing `Backfilling` would eventually have different screens disagreeing about whether backfilling means unusable. It does not.

Provider-reported counts give honest "showing 2,400 of 18,900" messaging while coverage is incomplete. For Gmail the projection reads coverage per-mailbox and change-stream state per-account; for Graph and IMAP both are per-mailbox.

### Message
| Field | Type | Notes |
|---|---|---|
| `Id` | Guid | **The canonical identity.** Local, permanent, never derived from provider or header data |
| `AccountId` | Guid | Mailbox membership is via `MessageMailbox` (below), not a column here |
| `ProviderStableId` | string? | Gmail message id; Graph **immutable** id (see §2). `null` for IMAP, which has no account-wide stable message identifier |
| `MessageIdHeader` | string? | RFC 5322 `Message-ID`. **Nullable metadata, not identity** — RFC 5322 states a message SHOULD have one, not MUST, and duplicates occur in practice. Used for reconciliation, reply construction and future threading |
| `InReplyToHeader` | string? | Required to construct correct replies (RFC 5322 identification fields) |
| `ReferencesHeader` | string? | As above; also the basis for any future threading |
| `ReplyToAddresses` | Address[] | **Reply routing uses this, not `From`**, when present |
| `SenderAddress` | Address? | RFC 5322 `Sender`, where it differs from `From` |
| `ThreadId` | string? | Provider-native thread id where offered; not used in the current UI |
| `From`/`To`/`Cc`/`Bcc` | Address[] | `{Name, Email}` |
| `Subject`, `Snippet` | string | |
| `ReceivedAt` | DateTimeOffset | |
| `IsRead`/`IsFlagged`/`IsDraft` | bool | **Server-known state**, mutable. Locally desired changes live in `MessagePendingChange` (§6), not here |
| `IsAnswered` | bool | **Read-only**, provider-derived where available. Not portably mutable — see §2 |
| `HasNonInlineAttachments` | bool | Deliberately *not* `HasAttachments`: inline signature images are MIME attachments, so a naive flag would show a paperclip on almost every corporate email |
| `SizeEstimate` | long? | |
| `RawFetched` | bool | Whether `MessageRaw` has been populated (§1, storage model) |

**Message identity.** `Message.Id` is canonical and local. Nothing about a message's identity is derived from provider ids or headers, because none of them are dependable across all three providers:
- **Gmail**: `ProviderStableId` is genuinely stable and account-wide.
- **Graph**: stable *only* under immutable-id mode (§2); otherwise ids change on move.
- **IMAP**: has no account-wide message id at all. Identity there is per-folder and volatile.

**Matching an incoming provider object to an existing local message** (deduplication and reconciliation) uses, in order of preference: `ProviderStableId` where the provider supplies one; otherwise the per-mailbox occurrence identity in `MessageMailbox`; otherwise `(AccountId, MessageIdHeader, ReceivedAt)` as a heuristic where a `Message-ID` exists. Where no match can be established, a new local message is created — duplicating a message is recoverable, whereas merging two distinct messages is not.

### MessageMailbox (occurrence — message ↔ mailbox membership)
| Field | Type | Notes |
|---|---|---|
| `Id` | Guid | **Local** occurrence identity only. Durable mutations never reference it — execution resolves the occurrence from stable intent (§6) |
| `MessageId` | Guid | FK |
| `MailboxId` | Guid | FK |
| `ProviderOccurrenceId` | string | The provider's id for **this message in this mailbox**: IMAP UID, Gmail message id, Graph immutable id |
| `ImapModSeq` | long? | Per-message mod-sequence, IMAP CONDSTORE only (§3) |

**Why the provider id lives here.** IMAP UIDs are scoped to a folder and **change when a message moves** — UID 42 in `INBOX` becomes a different UID in the destination. A single id on `Message` therefore cannot address a message on the server once folders are decoupled from messages. For Gmail and Graph this column repeats the stable id on each row, which keeps one code path.

**A message belongs to zero or more mailboxes.** This models Gmail correctly, where a message has one canonical existence and a *set of labels* (`INBOX`, `SENT`, `STARRED` and every user label are labels on the same message) — a message labelled both `Inbox` and `Receipts` genuinely appears in both, and Gmail's archive is simply removing the `INBOX` label. For IMAP and Graph there is always exactly one row, so the model is correct for all three rather than correct for one and bent for the others.

**Provider operations address resolved occurrences where required; durable mutation intent is scoped per operation and is never keyed by volatile occurrence identity** (§6). `MessageOccurrenceRef` carries `ProviderOccurrenceId` and is an **ephemeral execution DTO only** — it is constructed at execution time and must never be persisted into a mutation record.

### Content storage — one row per concern

Content lives in tables separate from `Message` so that list views never materialise large columns. SQLite stores oversized values on overflow pages and will not read a column a query doesn't reference, so the split is less about page-level I/O than about **preventing EF Core from materialising large columns by accident** and making lazy loading explicit.

| Table | Contents | Read when |
|---|---|---|
| `Message` | metadata, flags, addresses | constantly (list views) |
| `MessageHeaders` | full parsed header set, as received | detail view, diagnostics |
| `MessageBody` | `TextBody`, `HtmlBody` | on open |
| `MessageSearchContent` | flattened plain text + header text | FTS5 only (§8) |
| `MessageRaw` | original RFC 5322 bytes | export, signature verification, attachment extraction |

**`MessageRaw` is the single stored representation of content.** There is no `AttachmentBlob` table: attachments are extracted from the raw message on demand via MailKit. Raw MIME already contains every attachment base64-encoded, so storing decoded copies as well would duplicate the same bytes with roughly 33% inflation on the raw copy, and introduce a cache-coherency problem between the two. Parsing a stored message to pull one part is fast enough for an on-open operation.

This also makes `.eml` export faithful — the original bytes are returned verbatim, preserving headers, DKIM signatures, S/MIME and PGP parts, nested `message/rfc822`, calendar parts and transfer encodings. Reconstructing MIME from parsed text, as previously designed, produced a *plausible* message rather than *the* message.

**Attachment metadata** is parsed from raw at ingest and stored for listing without re-parsing:

| Field | Type | Notes |
|---|---|---|
| `Id`, `MessageId` | Guid | |
| `PartSpecifier` | string | MIME part path within `MessageRaw`, valid only for the current `MessageContentState.RawVersion` |
| `RawVersion` | int | The raw version this locator applies to |
| `Filename`, `MimeType`, `Size` | | `Filename` is untrusted input — see §9 for sanitisation before any filesystem use |
| `ContentId` | string? | For inline images referenced by `cid:` |
| `IsInline` | bool | |

**Drafts are the exception to raw-MIME-canonical.** A compose draft is a mutable authoring document — recipients, editor content, attachments added and removed, identity selection — and treating every keystroke as a mutation of a MIME document would be perverse and would make conflict handling far worse. `Draft` stays structured (below); MIME is generated at save-to-server and send time, and remote drafts are parsed into the structure.

**Fetching content is one operation, not several.** Gmail (`format=RAW`), Graph (`$value`) and IMAP can each return a full raw message in a single call, so background indexing, body extraction, header parsing and raw storage are all satisfied by one fetch per message. Parse once at ingest, populate all of the above together.

### MessageContentState (1:1 with Message)
| Field | Type | Notes |
|---|---|---|
| `MessageId` | Guid | PK/FK |
| `Status` | enum | `NotFetched`, `Queued`, `Fetching`, `Indexed`, `Failed` |
| `RawVersion` | int | Incremented whenever `MessageRaw` is replaced |
| `Attempts`, `LastError` | | |

**Content acquisition is message state, not mailbox state.** Under Gmail's canonical model one message belongs to several labels, so mailbox-scoped index progress would fetch the same message repeatedly through different labels, and "65% indexed in Inbox" is ill-defined when the same message is also in Receipts and Important. Mailbox and account progress are aggregate views over this table.

Bodies are **not** fetched only on open. Search must cover mail the user has never opened (Epic 5), which lazy fetching cannot deliver. After coverage completes, a throttled background job fetches raw content per message, at lower priority than metadata sync so an account becomes usable first.

`RawVersion` pairs with `Attachment.PartSpecifier`: a MIME part path is a locator within a *particular* stored raw blob, not an eternal identity. Attachment rows are rebuilt whenever raw content is replaced.

### Draft / OutboxItem
- `Draft`: `Id`, `AccountId`, `SendIdentityId`, `InReplyToMessageId?`, `To/Cc/Bcc`, `Subject`, `BodyHtml`, `Attachments[]`, `SavedAt`, `ProviderDraftId?`, `ProviderRevision?` (ETag/revision, required for the conflict detection below)
- `OutboxItem`: `Status` (`Draft`/`Scheduled`/`Pending`/`Sending`/`Sent`/`Failed`/`AmbiguousOutcome`/`Cancelled`), `ScheduledSendAt`, `StableMessageId` (generated before the first attempt, §15), `Attempts`, `LastError`. Status transitions are compare-and-swap, not checks — see §15.

**Drafts are structured authoring state, not raw MIME.** Unlike received and sent messages (§1), a draft is a mutable document — recipients, editor content, attachments added and removed, identity selection. MIME is generated at save-to-server and send time; remote drafts are parsed into this structure.

**Draft sync**: drafts are not local-only. All three providers have server-side Drafts folders, and a draft started on another device should appear here. Local drafts are pushed via the mutation queue; `ProviderDraftId` records the server-side id once created. Remote drafts arrive through sync of the `Drafts` special-use mailbox.

**A synced remote draft materialises as a `Draft`, not as both a `Draft` and a `Message`** — otherwise the same draft appears twice locally. The Drafts mailbox is excluded from normal message materialisation for this reason. If another client sends or deletes a draft, the local `Draft` is removed on the next sync.

**Conflict detection requires `ProviderRevision`.** `CreateOrUpdateDraftAsync` takes an expected revision and the provider passes it as a precondition where supported; a precondition failure surfaces as `ErrorCategory.Conflict` and prompts resolution rather than overwriting. Without stored revision state the detect-don't-merge guarantee would be unenforceable.

### Calendar / CalendarEvent

`Calendar`: `Id`, `AccountId`, `ProviderCalendarId`, `Name`, `Colour`, `IsDefault`.

`CalendarEvent`:

| Field | Notes |
|---|---|
| `Id` | Local canonical identity |
| `CalendarId`, `ProviderEventId` | |
| `ICalUid` | iCalendar `UID` — the cross-system identity used to match invites against events |
| `ProviderRevision` | ETag / `@odata.etag`, drives conflict detection |
| `Sequence` | iTIP `SEQUENCE`, for invite update ordering |
| `Title`, `Location`, `Description` | |
| `Start`, `End` | With **separate** `StartTimeZoneId` and `EndTimeZoneId` — they legitimately differ |
| `IsAllDay` | |
| `Organizer`, `Attendees[]` | `{Name, Email, Role, ResponseStatus}` |
| `Status` | `Confirmed`, `Tentative`, `Cancelled` |
| `Reminders[]` | |
| `SyncConflict` | Set on precondition failure (§15) |

**Recurrence is a set, not a single rule.** iCalendar recurrence comprises `RRULE` plus `RDATE` and `EXDATE`, with modified and cancelled instances existing as separate objects. Normalising to one `RRULE` string would lose information.

| Field | Notes |
|---|---|
| `RecurrenceRules[]`, `RecurrenceDates[]`, `ExceptionDates[]` | The recurrence set |
| `RecurrenceMasterId` | Null on the master; set on modified/cancelled instances |
| `RecurrenceId` | iCalendar `RECURRENCE-ID` — which occurrence this overrides |

Graph's `recurrence` object is translated into this model on ingest; CalDAV supplies it natively.

**Invites** carry `.ics`/`text/calendar` parts that parse into this model. RSVP on Graph and Google Calendar uses native APIs; the CalDAV/IMAP path generates an iTIP `REPLY` and sends it via the mail provider — a distinct piece of work from calendar CRUD.

**Provider paths**: `GoogleCalendarProvider` uses the Google Calendar API (better delta sync, consistent with using the Gmail API for mail); `GraphCalendarProvider` for Microsoft 365; `CalDavCalendarProvider` is genuinely generic and **may be configured independently against a plain IMAP account**, which is what makes self-hosted setups workable.

---

## 2. Provider Abstraction

### Architectural invariant — Graph immutable IDs

**`GraphMailProvider` sends `Prefer: IdType="ImmutableId"` on every request, without exception.** By default, Microsoft Graph item ids change when an item moves between folders, which would silently corrupt the occurrence identity model in §1. The header is scoped to the individual request, so it must be present on every call that returns or accepts an id: delta queries, message reads, folder listings, draft creation, moves, sends and batch operations alike. A single call that omits it can introduce an id from the other namespace into the database.

This is enforced centrally, in the Graph client's request pipeline rather than at individual call sites, and covered by a test that asserts the header on every outbound request.

```csharp
public interface IMailProvider
{
    ProviderType Type { get; }
    Task<AuthResult> AuthenticateAsync(Account account, CancellationToken ct);
    Task<IReadOnlyList<MailboxDto>> ListMailboxesAsync(Account account, CancellationToken ct);
    Task<int> EstimateMailboxCountAsync(Account account, Mailbox mailbox, CancellationToken ct);
    Task<InitialSyncPage> InitialSyncMailboxAsync(Account account, Mailbox mailbox, string? resumeToken, InitialSyncMode mode, int? bound, int pageSize, CancellationToken ct);
    Task<SyncResult> SyncMailboxAsync(Account account, Mailbox mailbox, string? cursor, CancellationToken ct);
    // Content acquisition is ONE raw fetch per message (§1). Body, headers, attachment
    // metadata and search content are all parsed from this; there is no separate body
    // or attachment fetch. Gmail format=RAW, Graph $value, IMAP BODY.PEEK[].
    Task<RawMessageResult> FetchRawMessageAsync(Account account, MessageOccurrenceRef occurrence, CancellationToken ct);

    Task<AttachmentConstraints> GetAttachmentConstraintsAsync(Account account, CancellationToken ct);

    // Mutations — operate on occurrences and stable intent, never on captured provider ids.
    // Provider identifiers are resolved by the caller at execution time (§6).
    Task<BatchResult> SetFlagsAsync(Account account, IReadOnlyList<MessageOccurrenceRef> refs, FlagUpdate update, CancellationToken ct);
    Task<BatchResult> MoveMessagesAsync(Account account, IReadOnlyList<MessageOccurrenceRef> refs, Mailbox target, CancellationToken ct);
    Task<BatchResult> RemoveFromMailboxAsync(Account account, IReadOnlyList<MessageOccurrenceRef> refs, CancellationToken ct);
    Task<BatchResult> MoveToTrashAsync(Account account, IReadOnlyList<MessageOccurrenceRef> refs, CancellationToken ct);
    Task<BatchResult> DeletePermanentlyAsync(Account account, IReadOnlyList<MessageOccurrenceRef> refs, CancellationToken ct);
    Task SendAsync(Account account, Draft draft, string stableMessageId, CancellationToken ct);

    // Capability negotiation — drives recovery policy and IMAP sync tier (§3, §6)
    ProviderCapabilities Capabilities { get; }

    // Server-side drafts
    // expectedRevision drives conflict detection (§1); null when creating.
    Task<DraftResult> CreateOrUpdateDraftAsync(Account account, Draft draft, string? expectedRevision, CancellationToken ct);
    Task DeleteDraftAsync(Account account, string providerDraftId, CancellationToken ct);

    // Mailbox (folder) management
    Task<MailboxDto> CreateMailboxAsync(Account account, string name, Mailbox? parent, CancellationToken ct);
    Task RenameMailboxAsync(Account account, Mailbox mailbox, string newName, CancellationToken ct);
    Task MoveMailboxAsync(Account account, Mailbox mailbox, Mailbox? newParent, CancellationToken ct);
    Task DeleteMailboxAsync(Account account, Mailbox mailbox, CancellationToken ct);
}
```

**Batch mutations are the default shape.** Multi-select routinely spans hundreds of messages, and one round-trip per message would be unusable. All three providers batch natively — IMAP `UID STORE`/`UID COPY` over UID sets, Gmail `batchModify`, Graph `$batch`. Single operations pass a one-element list.

```csharp
public record MessageOccurrenceRef(Guid MessageId, Guid MailboxId, string ProviderOccurrenceId);
public record FlagUpdate(bool? IsRead, bool? IsFlagged); // null = leave unchanged
public record BatchResult(IReadOnlyList<BatchItemResult> Items);
public record BatchItemResult(
    Guid MessageId,
    bool Succeeded,
    MutationProblemDetails? Problem,
    IReadOnlyList<OccurrenceChange> OccurrenceChanges); // created/removed memberships
public record OccurrenceChange(Guid MailboxId, string? NewProviderOccurrenceId, bool Removed);
```

`OccurrenceChanges` is what allows a move to report its destination identity — an IMAP move changes the UID, and with UIDPLUS/`MOVE` the server returns it. Where the server does not, the result flags that destination reconciliation is required rather than guessing.

**Flag updates are partial, not absolute-per-message.** A mixed multi-select must not clobber flags the user did not touch, hence `bool?` per field.

**`IsAnswered` is deliberately absent from `FlagUpdate`.** IMAP has `\Answered`, but Gmail's mutation model is label-based with no `ANSWERED` system label, and Graph exposes no equivalent first-class mutable property. Including it would overpromise provider uniformity. `Message.IsAnswered` remains as **read-only provider-derived metadata** where available; Epic 6 requires only read/unread and flag/unflag.

Absolute-value semantics are also what make flag mutations `RetrySafe` under §6 — a toggle-based model would be unsafe to replay.

**Mailbox deletion is provider-divergent and must be surfaced.** Deleting an IMAP folder or Graph mail folder deletes the messages in it; deleting a Gmail label removes only the label, leaving messages in All Mail. The UI shows a provider-specific confirmation stating the actual outcome, never a generic "are you sure".

**Attachment upload** is a single interface method (`SendAsync`) regardless of provider mechanics: Graph requires a chunked upload-session flow above ~3MB, Gmail and IMAP do not. This is entirely absorbed inside `GraphMailProvider` — the interface signature and every caller stay identical across providers.

Implementations: `ImapMailProvider` (MailKit), `GmailMailProvider` (`Google.Apis.Gmail.v1`), `GraphMailProvider` (`Microsoft.Graph`). Each translates its native incremental-sync mechanism (UID ranges / `historyId` / `deltaLink`) into the common `SyncResult` shape.

```csharp
public interface ICalendarProvider
{
    ProviderType Type { get; }

    Task<IReadOnlyList<CalendarDto>> ListCalendarsAsync(Account account, CancellationToken ct);
    Task<CalendarSyncResult> SyncCalendarAsync(Account account, Calendar calendar, string? cursor, CancellationToken ct);

    Task<string> CreateEventAsync(Account account, Calendar calendar, CalendarEventDto ev, CancellationToken ct);
    Task UpdateEventAsync(Account account, CalendarEvent ev, string? expectedETag, CancellationToken ct);
    Task DeleteEventAsync(Account account, CalendarEvent ev, CancellationToken ct);

    // Meeting invites (iTIP). Graph/Gmail handle RSVP natively; the IMAP path
    // generates an iCalendar REPLY and sends it via SendAsync on the mail provider.
    Task RespondToInviteAsync(Account account, CalendarEvent ev, InviteResponse response, string? comment, CancellationToken ct);
}

public record CalendarSyncResult(
    string NewCursor,
    IReadOnlyList<CalendarEventDto> Upserted,
    IReadOnlyList<string> DeletedProviderEventIds,
    bool HasMore);
```

`expectedETag` on `UpdateEventAsync` is what drives the detect-don't-merge conflict handling (§15) — the provider passes it through as an `If-Match` precondition where supported (CalDAV `ETag`, Graph `@odata.etag`), and a precondition failure surfaces as `ErrorCategory.Conflict` rather than an overwrite.

Implementations: `GoogleCalendarProvider` (Google Calendar API — better delta sync, and consistent with using the Gmail API for mail), `GraphCalendarProvider` (Microsoft 365), and `CalDavCalendarProvider`, which is genuinely generic and **may be configured independently against a plain IMAP account**. An IMAP account has no calendar unless the user supplies CalDAV details.

A parallel `IMailProviderFactory` resolves the correct implementation by `ProviderType`; the orchestrator and hub layers never contain provider-specific logic.

---

## 3. Sync Topology

| Concern | Scope | Gmail | Graph | IMAP |
|---|---|---|---|---|
| Topology | account | `labels.list` | `mailFolder` delta | `LIST`/`LSUB` |
| Coverage/backfill | mailbox | `messages.list` per label, soft bounds | bounded materialisation over full enumeration | UID range |
| Live change | **varies** | **account** `historyId` | mailbox `deltaLink` | mailbox UID/MODSEQ |
| Integrity reconciliation | mailbox | on `historyId` expiry | on `410 Gone` | on capability gap or cadence |

### Gmail — baseline and backfill

The account-wide history stream and per-mailbox backfill can observe the same message in different states, so ordering must be architectural rather than a matter of job timing. Consider: baseline `H0` captured, backfill lists message A in Inbox, another client archives A, history records the `INBOX` removal, backfill then writes its earlier view. Applied naively, the stale backfill write resurrects the Inbox membership.

**Chosen approach**: capture `H0`, then drain history **durably but unapplied** while backfill runs, applying the queued history only once baseline completes. This prevents `historyId` expiry during a long full sync without admitting concurrent writers to the canonical model.

**Notifications are processed from staged events, not deferred behind canonical replay.** Otherwise live mail would go unnotified for the entire backfill — potentially hours on a large account, and worst on a *triggered resync* of an established mailbox, where the user has every reason to expect normal behaviour. So: the staging queue is scanned for arrivals and notification records are created from it, before canonical application. If the user activates such a notification before replay has materialised the message, that message is fetched on demand rather than the navigation failing. (Initial setup of a new account is not affected either way — the notification epoch already suppresses everything predating setup.)

The alternative — concurrent application with a per-message `historyId` version, refusing older observations — gives better staleness behaviour but needs version state for deletions too, or a stale backfill page recreates something history already deleted. Noted as the upgrade path; it is cheaper than it appears now that tombstones exist for other reasons.

### Bound semantics

**Initial sync bounds limit historical backfill, not future synchronisation.** Any message affected by a post-baseline change may enter the local store even if its original date lies outside the bound. A bulk relabel of 50,000 old messages will therefore pull them in. This is coherent and consistent with the no-eviction decision; the alternative is a local store that knowingly misrepresents subsequent server changes.

### Graph — bounded sync does not reduce enumeration cost

A count bound is not a stable Graph delta predicate. Stopping the initial delta enumeration after N messages yields a `nextLink`, not the `deltaLink` required for subsequent incremental sync. A received-date filter can express `LastNMonths`, but it then becomes part of the delta query state rather than merely a backfill limit.

So for Graph: populate the UI from the requested recent subset, run the complete delta bootstrap in the background, decline to materialise messages outside the bound while walking it, persist the final unfiltered `deltaLink`, and only then transition to normal delta sync.

**Bounded sync on Graph reduces local materialisation and time-to-useful UI, not provider enumeration cost.** This must surface in the UI, since "last 3 months" otherwise implies a speed benefit Graph will not deliver.

### Graph — folder streams disagree about moves

Graph delta is folder-scoped, so a move surfaces independently as a removal in the source and an addition in the destination, in either order. Consequently: source removal removes the occurrence and never the canonical `Message`; a canonical message may transiently have zero memberships; destination addition reattaches the same canonical row; and GC must wait long enough that a late destination delta cannot find its row already collected.

### IMAP — three capability tiers

| Tier | New mail | Flag changes | Expunges |
|---|---|---|---|
| **QRESYNC** | increasing UIDs | mod-sequences | `VANISHED` |
| **CONDSTORE only** | increasing UIDs | `CHANGEDSINCE` | **UID-set reconciliation required** |
| **Neither** | increasing UIDs | **flag scan over known UIDs** | **UID-set reconciliation required** |

In the third tier, server-side read state reflects back on a **cadence**, not incrementally. If the flag scan runs every 30 minutes, an externally read message stays stale for up to 30 minutes. That is acceptable if stated honestly, which requires two distinct job types: a fast incremental poll and a slower integrity reconciliation.

Cheap signals (`UIDNEXT`, `EXISTS`) can trigger reconciliation early but never replace the periodic run — equal numbers of additions and deletions, or compensating flag changes, leave aggregates unchanged.

### Scheduling and control

- One Hangfire job per concern per scope, at the account's `PollIntervalSeconds`. Hangfire's recurring scheduler is minute-granular, so sub-minute polling uses self-scheduling delayed jobs.
- **Retry control is manual.** Jobs are registered `[AutomaticRetry(Attempts = 0)]` and reschedule explicitly, since auth failures must not retry at all and throttling must wait exactly `Retry-After`.
- **Account-level throttle gate**: a provider `429`/`Retry-After` sets `NextAllowedRequestAt` for the whole account, so thirty mailbox jobs do not all wake together and get throttled again.
- **Connectivity**: network-class failures suppress retry noise until connectivity returns, discovered via OS connectivity events plus a low-frequency probe — never an indefinite sleep.
- **Auth failures** set `AuthState = NeedsReauth` and pause the account's jobs. `ReauthenticateAccount` validates, clears the state, and **immediately requeues blocked work**: pending mutation chains, failed draft pushes, scheduled sends and sync jobs.
- **Account removal** sets `IsEnabled = false` first; jobs check it at entry and exit, so a returning worker cannot write data for a removed account, and removal need not prove no worker is running. But this does **not** make every job harmless: a worker already inside `SendAsync`, a permanent delete or a move may still complete its remote side effect after removal. Removal prevents further local commits and enqueues and attempts cancellation of in-flight work; **already-dispatched provider operations may still complete remotely.** For send in particular, that is worth stating to the user rather than implying the account was cleanly severed.

### Cursor invalidation

Every provider can return an invalid cursor, and all need the same response: discard it, mark the mailbox for **triggered resynchronisation**, and re-establish a baseline.

- **IMAP**: `UIDVALIDITY` changes on server-side reindex — any stored UID-based token is meaningless.
- **Gmail**: `historyId` expires (Google documents roughly a week); a `404` on `users.history.list` means the cursor is too old. Entirely plausible if the app is unopened for a fortnight.
- **Graph**: a `deltaLink` can be invalidated server-side, returning `410 Gone` with a resync indication.

A `ProviderCursorInvalidException` from any provider triggers the same path, handled once rather than three times. Note this is **triggered resynchronisation** (something is wrong), distinct from **periodic integrity reconciliation** (nothing is wrong, but the cursor cannot express what degraded IMAP needs) — same machinery, different trigger, cadence and meaning.

A mailbox becomes usable once coverage reaches the requested bound; it does not wait for the change-stream baseline, which is a separate dimension (§1).

---

## 4. Credential Storage

```csharp
public interface ICredentialStore
{
    Task StoreAsync(Guid accountId, CredentialPayload payload, CancellationToken ct);
    Task<CredentialPayload?> RetrieveAsync(Guid accountId, CancellationToken ct);
    Task DeleteAsync(Guid accountId, CancellationToken ct);
}
```

- Never stored on `Account` or in plain SQLite columns.
- Per-OS keychain implementations: Linux (`libsecret`/Secret Service, graceful fallback if unavailable), Windows (DPAPI), macOS (Keychain Services).
- `CredentialStoreResolver` probes platform availability at startup and selects accordingly.
- **Fallback: master-password store.** Where no OS keychain is available (a Linux system with no Secret Service provider running, most commonly), credentials are encrypted in SQLite under a key the user controls:
  - The user sets a master password; the encryption key is **derived** from it via Argon2id (or PBKDF2 with a high iteration count) using a stored random salt — the password is never the key directly, so a weak password isn't directly a weak key.
  - A verifier blob (a known value encrypted under the derived key) is stored so correctness can be checked at unlock without decrypting everything.
  - The derived key is held **in memory only**, zeroed on shutdown; the user re-enters the master password each launch.
  - **Behavioural consequence worth stating plainly**: with this store active, the app starts *locked* and **no background sync, notification, or scheduled send can run until the user unlocks it**. This is a real, visible difference from keychain-backed operation and must be communicated in the UI, not discovered.
  - The user is told at setup that this path is weaker and less convenient than an OS keychain, with guidance on enabling one.
- Retrieved only at point of use (auth/token refresh), never eagerly loaded with `Account`.
- **`ImapProviderConfig` must carry SMTP settings.** An IMAP account cannot send over IMAP, and this was missing entirely: `ImapHost`, `ImapPort`, `ImapSecurity`, `SmtpHost`, `SmtpPort`, `SmtpSecurity`, `SmtpUsername` (which may differ from the IMAP username), and `SmtpAuthMethod`. SMTP credentials may be separate from IMAP credentials and are stored as such.
- **IMAP basic auth**: `ImapProviderConfig.AuthMethod` (`Password`/`OAuth2`) is explicit, not inferred. Password auth refuses to authenticate over an unencrypted connection (SSL/StartTLS only) — hard client-side rule regardless of server capability.
- Account deletion explicitly calls `DeleteAsync` — keychain entries live outside the SQLite file and aren't cleaned up by cascading DB deletes.

---

## 5. OAuth (Gmail / Microsoft 365)

Use each provider's SDK loopback flow rather than hand-rolling a listener: `Google.Apis.Auth.OAuth2` (`LocalServerCodeReceiver`) and MSAL (`Microsoft.Identity.Client`), both of which handle PKCE, loopback redirect and token exchange.

**SDK token caching must be wired to `ICredentialStore` (§4).** MSAL's cache serialisation and Google's credential `DataStore` both persist tokens somewhere by default; left implicit, tokens end up outside the chosen keychain or master-password store. Cache serialisation/deserialisation is part of the credential-store implementation, not an SDK default.

### Gmail — credential distribution is unresolved

Restricted scopes require verification. Every Gmail scope usable by a real mail client is **restricted** (`gmail.modify` included; only send-only `gmail.send` avoids the tier), so verification is unavoidable for a shipped OAuth client.

The annual CASA security assessment, however, is conditioned specifically on server involvement — Google's wording is that apps accessing restricted data *from or through a third-party server* must undergo it, and documented exceptions include personal use and internal use. **This architecture has no third-party server**: the backend is a loopback child process and mail never reaches infrastructure the project controls.

Whether CASA applies to a wholly local desktop client is therefore genuinely unclear from the published wording and **must be confirmed with Google directly, not inferred**. Two paths:
- **Shipped OAuth client** — better onboarding; requires verification, and possibly annual assessment.
- **Bring-your-own-credentials** — the user registers their own Cloud project and Desktop OAuth client. No verification burden, materially worse onboarding.

BYOC is built as a supported mode regardless, since some users prefer it. Whether it is *mandatory* depends on the answer above.

BYOC setup needs a written guide and specific error handling for the predictable failures, which will dominate support: wrong client type (Web instead of Desktop) breaks the loopback redirect; APIs not enabled produces an opaque `403`; a project left in *testing* status expires refresh tokens after roughly seven days, causing weekly re-authentication.

### Microsoft 365

Entra public-client registration is lighter and can ship with the app. But **tenants can restrict or disable ordinary user consent**, so an organisational account may legitimately fail setup until an administrator approves the application. This surfaces as a distinct `TenantAdminConsentRequired` outcome with instructions — not as an authentication failure, which would send the user to re-enter working credentials.

---

## 6. Mutation Model

The governing invariant, which every part of this section follows from:

> **Stable local identity owns ordering and intent. Volatile provider identity is an input to individual execution attempts. No local record of an attempt is evidence about what the server actually did.**

Five deliberately separate concerns. Earlier drafts kept collapsing two or more into the same field, which is what produced the races.

### 1. Ordering

Message mutations are totally ordered per `(AccountId, MessageId)`. Each receives a monotonically increasing `Sequence`, assigned in the same transaction as its insert. Only the **lowest non-terminal sequence** for a message is eligible for execution.

Ordering is keyed on `MessageId`, not on occurrence, because occurrence identity is exactly what a move invalidates — the chain key would be destroyed by the operation it exists to sequence. `MessageId` is stable by construction (§1) and also covers Gmail label add/remove pairs, which are message-scoped rather than occurrence-scoped.

Different messages proceed concurrently, and eligible heads from different messages may be provider-batched where operation semantics allow. A chain is not "blocked" merely because its head is awaiting execution.

### 2. Ownership

Claiming an eligible mutation is an **atomic leased transition**, not read-then-decide — otherwise two workers both conclude a chain is clear and claim consecutive operations. The lease carries an expiry; a sweep reclaims expired leases.

**An expired lease does not imply the operation did not happen.** Recovery is governed by concern 5.

### 3. Intent

Mutations store **stable local intent**, never durable provider identifiers:

| Operation | Scope | Notes |
|---|---|---|
| `SetFlags(MessageId, FlagUpdate)` | message | Read and flagged only. Absolute (`bool?` per flag), not a toggle — which is what makes it safe to replay |
| `MoveMessage(MessageId, TargetMailboxId)` | message | Source is execution-time state |
| `RemoveFromMailbox(MessageId, MailboxId)` | **membership** | That specific membership is part of the intent; if it no longer exists the operation is unsatisfiable, never silently broadened to another occurrence |
| `MoveToTrash(MessageId)` | message | |
| `DeletePermanently(MessageId)` | message | Destructive; stricter reconciliation |

This taxonomy also resolves the earlier ambiguity in a single `DeleteMessages` operation, which was hiding label removal, trashing and permanent deletion behind one method with genuinely different consequences per provider.

Mailbox references follow the same rule: **mutations reference local `Mailbox.Id`; provider mailbox identifiers are execution-time state and must not be treated as durable queue identity.** A useful consequence is that a folder rename invalidates no queued mutations at all — only deletion does.

### 4. Execution identity

Provider message and mailbox identifiers are **resolved or revalidated at execution**, after preceding mutations settle. Capturing `ProviderOccurrenceId` at enqueue time would preserve precisely the staleness the chain exists to prevent, leaving the sequencing decorative.

### 5. Remote uncertainty

Leases answer "which worker owns this?". They do not answer "what may already have happened on the server?". That needs a durable, separate record.

| Entity | Fields |
|---|---|
| `MutationExecutionAttempt` | `Id`, `AccountId`, `Provider`, `OperationKind`, `State` (`Prepared`/`Dispatched`/`Completed`/`Ambiguous`), `CreatedAt`, `DispatchedAt?`, `ResultPersistedAt?` |
| `MutationExecutionAttemptItem` | `AttemptId`, `MutationItemId` |

Sequence:
1. Atomically claim eligible mutation items.
2. Persist the attempt and its membership.
3. **Mark the attempt `Dispatched` immediately before the irreversible provider call.**
4. Issue the provider request.
5. Persist per-item outcomes **and** the attempt's terminal state in a single transaction.
6. — (5 closes the attempt; an attempt is never observable as `Completed` with unpersisted results, nor results observable without the attempt closed.)

**`Dispatched` is deliberately conservative.** It means the provider *may* have seen the request. A crash between step 3 and step 4 produces a false positive, and that is correct: a false positive costs one unnecessary reconciliation, a false negative silently loses an outcome or duplicates an externally visible side effect. Locally, nothing can distinguish the two cases. Reconciliation therefore treats "the operation never happened" as a normal outcome, not an anomaly.

**Step 3 must not be optimised away.** It is a synchronous durable write on the hot path of every mutation, and it is exactly what a later performance pass would be tempted to batch, defer or make asynchronous — silently reintroducing the window it exists to close. The failure it prevents is invisible in testing and severe in the field.

**Ambiguity is per attempt, not per operation type.** An item is ambiguous because it belonged to a `Dispatched` attempt with no persisted result. The operation type determines *how* to reconcile; membership in an unresolved attempt is what makes reconciliation necessary. The recovery sweep therefore queries from attempts, not from item states.

**Batching does not narrow the dispatch boundary.** Fifty items in one Graph `$batch`, Gmail batch request or IMAP UID-set command share a single dispatch boundary however they are leased. Per-item leasing is the right *ownership* granularity but does not reduce *outcome* ambiguity to one item at a time.

**Send gets its own attempt, always, with exactly one item.** Its ambiguous outcome is externally visible in a way no other operation's is. This falls out of the model rather than being bolted on.

### Recovery policy

Resolved from operation type **and** the negotiated capabilities of that account's provider:

- **`RetrySafe`** — absolute flag sets, Gmail label add/remove. Idempotent; replay freely.
- **`ReconcileThenRetry`** — moves, permanent deletion. Re-establish current location or existence first.
- **`AmbiguousOutcome`** — send. Never blindly replayed; routes to reconciliation against the Sent mailbox using the pre-generated `Message-ID`, with a bounded retry window for eventual consistency (Graph does not guarantee the Sent copy is immediately available).

Capability is a **cost** input, not a safety one. IMAP UIDPLUS returns the destination UID and so improves the normal path, but it does not make post-crash recovery safe: after a crash the returned `COPYUID` is gone, and IMAP offers no way to ask what it previously returned. Nothing moves from `ReconcileThenRetry` to `RetrySafe` on the strength of a capability.

### Terminal failure — ordering is not transactionality

A chain establishes ordering. It does **not** establish a transaction, and the provider offers no transactional semantics across these operations.

After a terminal failure, later mutations are **re-evaluated against current server-known state**. Intentions that remain independently satisfiable continue; those whose prerequisites are no longer satisfiable are cancelled, carrying the originating failure. Cancelling a whole chain would turn one provider failure into several abandoned user intentions.

Most of this derives from intent semantics and execution-time resolution without explicit dependency metadata:
- `Move to X` fails, then `SetFlags` — message-scoped, resolves, proceeds. Correct.
- `Move to X` fails, then `RemoveFromMailbox(X)` — membership-scoped, no membership in X resolves, cancelled. Correct.
- `Move to X` fails, then `Move to Y` — source resolves wherever the message actually is, proceeds. Correct.

Explicit dependency metadata (`RequiresPriorSuccess`, `DependsOnMutationItemId`) is reserved for genuine causal dependency that resolution cannot infer — not something every mutation declares.

### Optimistic state and rollback

Local state splits into **server-known** (`Message.IsRead` and so on) and **desired** (`MessagePendingChange`, per message per field, referencing the owning mutation item). A single `SyncPending` boolean cannot represent three concurrent pending operations, and the first to complete would clear it while two remain outstanding.

Terminal failure reverts desired state deterministically. Incoming sync merges against pending desired state rather than overwriting it.

### Tombstones and garbage collection

A local `Message` row may become a tombstone after server deletion and **persists until mutation state, reconciliation state and other references permit collection**. Physical deletion is a garbage-collection decision based on references, not something a mutation worker performs when a chain goes terminal. Referencing subsystems include mutation items, execution attempts, notification navigation, draft reply linkage and the FTS external-content row — the last of which must be removed in the same operation, or search returns hits pointing at nothing.

### Separate coordination domains

`(AccountId, MessageId)` governs message mutation ordering only. Mailbox and account lifecycle operations — folder deletion, folder move, account removal — form a separate coordination domain with their own invalidation rules. The message chain must not become a general-purpose lock manager.

### Job execution

Hangfire supplies the worker pool, delayed execution and the dashboard, backed by `Hangfire.InMemory`. It supplies nothing else: retry is disabled (`[AutomaticRetry(Attempts = 0)]`, §3), recurring scheduling is replaced by self-scheduling delayed jobs, and persistence is provided by the tables above.

**Startup reconciliation is the sole recovery mechanism**, not a safety net behind a durable queue. It must enumerate every source of outstanding work, and it warrants direct tests rather than being assumed correct:

| Source | Condition |
|---|---|
| `MailboxCoverageState` | `Backfilling` |
| `MutationItem` chains | any non-terminal sequence |
| `MutationExecutionAttempt` | `Dispatched` without persisted results → reconciliation, never blind retry |
| `OutboxItem` | `Scheduled`, or `Sending` (→ ambiguous-outcome reconciliation) |
| `MessageContentState` | `Queued` or `Fetching` |
| `ExportJob` | in progress |
| `NotificationRecord` | created but undelivered |

**Job state is bounded by memory**, so work is enqueued incrementally rather than fanned out up front. The self-scheduling pattern already required for sub-minute polling and paged backfill satisfies this: each job enqueues its successor as it completes.

---

## 7. SignalR Hub Events

**Server → client** — every event with its producing mechanism, so none is orphaned:

| Event | Produced by |
|---|---|
| `AccountStatusChanged` | Auth failure/success in any provider call; account enable/disable |
| `MailboxUpdated` | End of each sync job — recomputed unread/total counts |
| `MailboxTreeChanged` | Folder CRUD mutation jobs; discovery of new/removed folders during sync |
| `MessageReceived` | Steady-state sync only — **never** initial sync (§13, Epic 9: prevents backlog notification floods) |
| `MessageUpdated` / `MessageDeleted` | Sync (remote change) or mutation job completion (local change confirmed) |
| `SyncProgress` | Coverage/backfill and content-index jobs, after each page — carries fetched/estimated counts |
| `ExportProgress` | Bulk-export job (§15), after each batch of messages written; also signals completion/cancellation |
| `MessageSyncFailed` | A mutation item reaching terminal failure (§6) |
| `DraftUpdated` | Draft sync (remote draft changed) or local draft save confirmation |
| `OutboxStatusChanged` | `OutboxItem.Status` transitions — scheduled, sending, sent, failed, cancelled |
| `CalendarEventUpdated` | Calendar sync job, or a calendar mutation job completing |
| `CalendarConflictDetected` | `UpdateEventAsync` returning `ErrorCategory.Conflict` on an `If-Match` precondition failure (§2, §15) |
| `ConnectivityChanged` | The connectivity-aware pause logic (§15) — emitted when network-class failures begin or when connectivity returns, so the UI can show one calm offline state rather than per-mailbox errors |

**Reconnect is a full resynchronisation, not just a pending-mutation check.** While disconnected a renderer misses `MessageReceived`, `MessageUpdated`, `MessageDeleted`, `MailboxTreeChanged` and the rest — pending mutations alone cannot repair a stale TanStack Query cache. On reconnect the client invalidates and refetches all active account and mailbox queries, in addition to calling `GetPendingSyncState`.

**Client → hub**:
- *Reads*: `GetMailboxes`, `GetMessages`, `GetMessageBody`, `GetAttachmentMetadata`, `GetPendingSyncState`, `GetCalendars`, `GetCalendarEvents`, `Search`
- *Message mutations (all batch-capable)*: `SetFlags`, `MoveMessages`, `RemoveFromMailbox`, `MoveToTrash`, `DeletePermanently`
- *Compose/send*: `SaveDraft`, `DeleteDraft`, `SendDraft`, `CancelScheduledSend`
- *Folder management*: `CreateMailbox`, `RenameMailbox`, `MoveMailbox`, `DeleteMailbox`
- *Calendar*: `CreateEvent`, `UpdateEvent`, `DeleteEvent`, `RespondToInvite`, `ResolveEventConflict`
- *Export*: `SaveMessageAsEml`, `StartBulkExport`, `CancelBulkExport`
- *Settings/accounts*: `AddAccount`, `UpdateAccount`, `RemoveAccount`, `ReauthenticateAccount`, `TrustCertificate`, `UpdateAppSettings`, `GetAppSettings`

---

## 8. Full-Text Search

- **A flattened `MessageSearchContent` row, 1:1 with `Message`, is the FTS external-content source** — not an index conceptually spanning several EF entities. Columns: `Subject`, `BodyText`, `FromAddresses`, `ToAddresses`, `CcAddresses`. Separate columns, not one blob, so FTS5's column-filter syntax supports field-scoped search (`from:alice`, subject-only, body-only).
- **It carries an INTEGER primary key**, not the Guid used elsewhere. FTS5 external-content tables require a stable integer rowid, which Guid keys cannot supply.
- **HTML is indexed as extracted plain text, never markup** — stripped server-side at ingest (AngleSharp or HtmlAgilityPack) — so tags, attributes and inline CSS can never match or appear in results. `MessageBody.HtmlBody` is retained unchanged for rendering.
- **Maintained by explicit upsert in the same transaction** as the content write, not by SQL triggers, which are fiddly against external-content tables and invisible to EF Core. Insert, update, delete and full-rebuild semantics are defined explicitly; the delete path is part of tombstone garbage collection (§6), or search returns hits pointing at nothing.
- **Mailbox scoping is applied after the FTS match**, as a join onto `MessageMailbox`. Membership is not indexed, so a message moving folders never requires reindexing.

---

## 9. Process Lifecycle & Local Security

- Electron spawns the backend as a child process; backend binds to `127.0.0.1` on an OS-assigned port (`0`), reports the port back to Electron (stdout or a well-known file).
- Electron polls a `/health` endpoint before connecting the renderer's SignalR client.
- Graceful shutdown on `before-quit` (dedicated shutdown signal/endpoint), not a hard kill — allows in-flight jobs to finish and SQLite to close cleanly.
- Electron detects unexpected backend process exit and offers restart.
- A per-launch random token (passed via env var at spawn) is required on all API/SignalR connections — prevents any other local process from connecting to the backend. The renderer supplies it via SignalR's **access token factory** (`accessTokenFactory` on `HubConnectionBuilder`), and as a bearer header for the few REST calls; the backend validates it in middleware and rejects mismatches with `401` before any hub/endpoint logic runs.

### AppSettings (single-row table in the app DB)
| Field | Type | Default | Notes |
|---|---|---|---|
| `CloseBehavior` | enum | `QuitApp` | `QuitApp` \| `MinimizeToTray` |
| `TelemetryEnabled` | bool | `false` | Opt-in only |
| `OtelEndpoint` | string? | `null` | User-supplied collector endpoint |
| `MailtoPromptDismissed` | bool | `false` | "Don't ask again" for the default-handler prompt |
| `PanelLayout` | string? | `null` | Global default panel sizes; live per-window state is not stored here (§13, Epic 11) |
| `WindowBoundsJson` | string? | `null` | Last-known size/position, inherited by newly opened windows |
| `Theme` | enum | `System` | `System` \| `Light` \| `Dark` |
| `AttachmentTempCleanupOnStartup` | bool | `true` | Sweeps orphaned temp attachments left by a crash |

Sensible defaults throughout — no initialisation file is required, and a missing row is equivalent to all-defaults. `DataDirectoryOverride` is deliberately **not** here; it lives in the bootstrap file (§15) because it must be resolved before this table can be located.

### SQLite configuration

**One database file: `app.db`, `PRAGMA synchronous=FULL`.** It holds everything authoritative — messages, content, mutation intent, execution attempts, outbox, notification records.

There is no separate job database. The design passed through two intermediate positions worth recording, because both were wrong for instructive reasons:

- **Single file shared with Hangfire's SQLite storage.** WAL allows concurrent readers, but SQLite still permits only one writer, and Hangfire writes constantly. Ingest, FTS updates and job bookkeeping would all serialise.
- **Two files with differing durability** (`app.db` at `NORMAL`, `jobs.db` at `FULL`). This introduced a cross-file atomicity boundary — in WAL mode an `ATTACH`ed multi-database transaction is atomic per file, not across both — so optimistic projections and durable intent could diverge on power loss. The `NORMAL`/`FULL` split it existed to serve rested on an unexamined assumption that `FULL` would be too costly for bulk ingest. Ingest is network-bound; at ~200 messages per commit that is one fsync per batch against seconds of provider round-trip.

Once job storage was correctly identified as **non-authoritative** — startup reconciliation rebuilds all outstanding work from the app tables, and the app tables win any disagreement — persisting it stopped serving any purpose. A file whose contents are discarded on conflict and rebuilt on startup is strictly worse than no file: same recovery model, plus disk writes, plus connection configuration outside our control, plus a dependency of uncertain maintenance status.

**Job storage is therefore in-memory** (`Hangfire.InMemory`, the official HangfireIO package), and the contention question disappears rather than being relocated.

**Durability, defined**: for the dispatch boundary (§6), *durable* means committed such that it survives both process termination and OS-level power loss. In WAL mode that requires `synchronous=FULL`; the commonly recommended `synchronous=NORMAL` explicitly trades power-loss durability for speed, which is unacceptable for the record whose entire purpose is surviving a crash. **Verify the exact `FULL`-under-WAL semantics against SQLite's own documentation before relying on this guarantee.**

Other pragmas, on **every** connection (only `journal_mode` persists on the file; the rest are per-connection):
- `PRAGMA journal_mode=WAL` — readers never block writers and vice versa. Applied once.
- `PRAGMA busy_timeout=5000` — wait on writer collision rather than throwing `SQLITE_BUSY`.
- `PRAGMA foreign_keys=ON` — off by default in SQLite; the occurrence and content models depend on it.

WAL leaves `-wal` and `-shm` sidecars beside each database. **`DataDirectoryOverride` must reject network-backed locations** (NFS, SMB): WAL coordinates via shared memory and assumes local single-machine access.

### Database migration safety
- `context.Database.Migrate()` runs on backend startup.
- **If a database already exists, a consistent backup is taken via `VACUUM INTO` (or the SQLite online backup API) immediately before migrations run** — **not** a filesystem copy. In WAL mode the `-wal` file is part of the persistent database state; copying the main file alone can lose committed transactions or produce a corrupt copy.
- The backup is deleted only after migrations complete successfully, so a failed migration always leaves a restorable copy. Failure is surfaced to the user rather than silently retried.

### Attachment temp-file handling
Attachments opened via the OS default handler (§13, Epic 5) are written to an **app-private** directory, never the shared system temp:
- Location: `<DataDirectory>/tmp/attachments/<guid>/<original-filename>` — a random GUID subdirectory per attachment prevents collisions and makes paths unguessable, while preserving the real filename for the OS handler.
- Permissions set **at creation** (via `FileStreamOptions.UnixFileMode`, avoiding a window where the file exists world-readable): `0700` on directories, `0600` on files. On Windows the location inherits the user-profile ACL.
- The executable bit is never set, regardless of file type.
- **MIME filenames are untrusted input and are sanitised before any filesystem use**: path separators and traversal sequences stripped, control characters removed, Windows reserved device names rejected, trailing dots and spaces trimmed — and the resolved path verified to still lie beneath the GUID directory.
- **Clearing the executable bit does not make opening safe.** `shell.openPath` invokes the OS-registered handler, which will happily execute `.exe`, `.msi`, `.bat`, `.cmd`, `.ps1`, `.desktop` and script files. Dangerous extensions get an explicit "this attachment may run code" confirmation before opening.
- Cleanup on graceful shutdown, plus a full sweep of `tmp/attachments/` at startup — nothing in it needs to survive a restart.

### Time zone handling
- **Store IANA identifiers canonically.** Graph frequently returns Windows time zone IDs (`"AUS Eastern Standard Time"`); CalDAV/iCalendar return IANA (`"Australia/Brisbane"`). Normalise Windows → IANA on ingest with `TimeZoneConverter` (`TZConvert.WindowsToIana`) so `CalendarEvent.TimeZoneId` has one representation regardless of source.
- **Enable ICU app-wide** (`InvariantGlobalization=false`, app-local ICU where needed) so `TimeZoneInfo.FindSystemTimeZoneById` accepts IANA IDs on Windows as well as Linux/macOS — removing the platform divergence at the source rather than converting at every call site.
- Recurrence expansion always operates in the event's stored IANA zone, never UTC-only, so DST transitions expand correctly.

---

## 10. Logging

Serilog, async sink. Never log message bodies, subjects, or credentials — `MessageId`/`AccountId`/operation/exception only. A shared enricher/destructuring policy partially obfuscates identifying fields (e.g. email addresses) where logging them is still useful for diagnostics.

---

## 11. Testing Strategy

- **Framework**: xUnit throughout.
- **Unit tests**: full coverage of every service/interface (sync orchestrator, job logic, hub mutation flow, **startup reconciliation**, credential store resolution) against fakes/mocks — no real provider dependencies. `FakeMailProvider` (canned `SyncResult`s) is a first-class test double, built alongside the real providers rather than as an afterthought.
- **Integration tests**: separate project/category, run against real IMAP/Gmail/Graph accounts. Credentials supplied via environment variables (`TEST_IMAP_HOST`, `GMAIL_*`, `GRAPH_*`, etc.); tests **skip** (not fail) when the relevant variables are absent, so the same suite runs safely in any environment with only the credentials that happen to be available.
- Dedicated throwaway test mailboxes per provider — not personal accounts — since integration tests genuinely send/move/delete mail.

**Crash and fault injection matter more here than interface coverage.** Blanket "every service tested" is less valuable than deliberately killing the process at each boundary the design depends on: after local optimistic commit; after enqueue; after attempt `Dispatched` but before the provider call; after the provider call but before results persist; on a partial batch result; after a move changes the UID; after cursor persist; mid attachment download; mid draft creation; and after send acceptance. Those are precisely where this system will develop its worst bugs, and every one is invisible to conventional unit tests.

**IMAP needs a capability matrix, not one test account.** Servers differ sharply on `MOVE`, `UIDPLUS`, `CONDSTORE`, `QRESYNC`, namespaces, special-use folders and hierarchy delimiters, and the provider abstraction assumes more uniformity than exists. Cover at minimum: a modern server with everything, CONDSTORE without QRESYNC, and basic IMAP only.
- CI inclusion of integration tests (vs. local/manual-only) to be decided once CI infrastructure is in place.

---

## Open items — backend
- Contacts (explicitly out of scope)
- Message threading (schema reserved via nullable `ThreadId`, not implemented)
- **Local storage growth is deliberately unbounded** — no eviction or cap policy. With `MessageRaw` stored for every indexed message, a full-history mailbox consumes roughly the size of the mailbox itself, and that is accepted.
- IMAP IDLE as a polling optimisation (poll-based sync is the baseline)

---

## 12. Frontend Architecture

### Stack
- **Bundler**: `electron-vite` (Vite-based, correctly separates main/preload/renderer build targets for Electron)
- **Framework**: React 19, TypeScript 6.x (TypeScript 7's Go-native compiler is GA but lacks a stable programmatic compiler API until 7.1 — `typescript-eslint` and other tooling aren't yet compatible; revisit once 7.1 lands)
- **Linting/formatting**: Prettier, ESLint, Stylelint, CSS Modules
- **Data/state**:
  - **TanStack Query** — server-state layer for request/response reads (`GetMailboxes`, `GetMessages`, `GetMessageBody`, etc.); SignalR events drive cache invalidation/updates (`queryClient.invalidateQueries`/`setQueryData`) rather than running a parallel state system
  - **TanStack Router** — type-safe routing, search-params-as-state for things like selected message ID
  - **TanStack Virtual** — virtualised long lists (message list, calendar agenda view)
  - **TanStack Table** — Outlook-style sortable/filterable message list columns
  - **TanStack Form** — compose/reply, account setup, settings forms, paired with Zod validation
  - **Zod** — schema validation, paired with TanStack Form and generated DTO schemas
  - **dayjs** + `utc`, `timezone`, `relativeTime`, `calendar` plugins (+ `isBetween`/`isSameOrBefore`/`isSameOrAfter` if client-side calendar date-range logic is needed)
  - **react-granular-store** — cross-cutting client-only UI state (sidebar selection, compose-window state, persisted global preferences such as panel layout); explicitly not Redux/MobX
  - **json-fetch-client** (`leomylonas/json-fetch-client`) — typed `fetch` wrapper (Zod validation, structured `ProblemDetails` error parsing) for the rare non-SignalR call (OAuth kickoff, health check, attachment download by URL)
  - **@carbon/react (IBM Carbon Design System)** — UI component library. WCAG 2.1 AA contrast, keyboard navigation, and ARIA patterns built into components by default, directly serving the project's continuous accessibility requirement rather than requiring it to be built from scratch. `ToastNotification` (auto-dismissing, non-interactive) and `ActionableNotification` (persistent, supports actions e.g. "Undo") cover the in-app feedback requirement (§13, Epic standing conventions) — `ActionableNotification` is used wherever a toast needs an action or link, per Carbon's own guidance that interactive content inside `ToastNotification` breaks WCAG compliance.
  - **Lexical** (Meta's editor framework) — rich-text HTML compose editor. Chosen over TipTap: fully MIT-licensed with no paywalled tier (TipTap's collaboration/Cloud/AI features sit behind a paid tier, irrelevant to a single-user desktop app but a licensing complication worth avoiding), and explicit WCAG/ARIA/keyboard-navigation support aligning directly with the project's continuous accessibility requirement. Lower-level than TipTap (more toolbar/UI assembly required), acceptable given compose needs a bounded formatting set, not a full document editor. HTML serialization via Lexical's `$generateHtmlFromNodes`/`$generateNodesFromDOM`, feeding `Draft.BodyHtml` directly — native Lexical JSON state is not used, consistent with the plain-text/HTML-only API boundary (§5, §12).

### Process structure
- **`apps/electron-shell`** — Electron main process + preload script. Owns: window creation, spawning/managing the .NET backend child process (health-check, graceful shutdown, crash-restart), OS-level integration (native notifications, system tray, `shell.openPath` for attachments), the preload bridge exposing a whitelisted API to the renderer, per-launch random auth token generation.
  - **Spawn mode** (default/production/full E2E): shell spawns the backend itself.
  - **Attach mode** (dev workflow, isolated E2E): shell connects to an already-running backend (e.g. started independently from an IDE with a debugger attached), toggled via env var (`ELECTRON_BACKEND_MODE=attach`, `BACKEND_URL=...`).
  - Single backend process shared across all open windows, regardless of window count.
- **`apps/renderer`** — the React SPA itself. No direct Node/OS access (Node integration disabled, standard Electron security practice); talks to the backend via SignalR/HTTP through the loopback connection using the shell-issued token.

### Type generation (backend DTOs → frontend types)
- **`TypedSignalR.Client.TypeScript`** — analyses C# hub/receiver interfaces directly and generates a fully typed TypeScript SignalR client (typed method calls and event handlers), replacing string-keyed/`any`-typed hub calls.
- **`TypeContractor`** — converts C# classes/enums to TypeScript interfaces, with an experimental `--build-zod-schemas` flag generating matching Zod schemas. Structural only (no validation refinements yet) — trial early against real DTO shapes; hand-maintained Zod schemas remain the fallback if it doesn't hold up.
- Both are `dotnet tool`s invoked via a root pnpm script (e.g. `pnpm run generate:types`), feeding `packages/shared-types`.

### Repository layout
```
/repo
  /apps
    /electron-shell       # main process + preload
    /renderer              # React SPA
  /packages
    /shared-types           # generated TS interfaces + Zod schemas
    /ui                       # shared component library (if warranted)
  /tests
    /renderer.unit             # Vitest
    /renderer.e2e                # Playwright (TypeScript), spawn-mode + attach-mode
  /server
    /MyloMail.Api                   # .NET solution
    /MyloMail.Api.Tests               # xUnit unit tests
    /MyloMail.Api.IntegrationTests      # xUnit, env-var-gated, skip-if-absent
  pnpm-workspace.yaml
  package.json
```

### Testing
- **Server-side**: xUnit. Unit tests mock every interface (`IMailProvider`, `ICredentialStore`, repositories) via a `FakeMailProvider`-style test double, built alongside real providers. Integration tests run against real IMAP/Gmail/Graph test accounts, gated by environment variables, **skipped (not failed)** when absent.
- **Client-side**: Vitest for unit tests. Playwright (TypeScript) for E2E, in two tiers:
  - **Full E2E** (spawn mode): `_electron.launch()` on the real app, which spawns the real .NET backend (against fakes or real test-provider accounts) — closest to production behaviour.
  - **Isolated E2E** (attach mode): `_electron.launch()` against a backend already running in a known state (real backend + `FakeMailProvider`, or a lightweight mock) — faster, more deterministic, no child-process spawn/lifecycle in the test itself.

---

## 13. Requirements — Epics & Stories

No MVP/phased cut — built continuously, kanban-style, epic by epic.

### Epic 1 — Account Management
- Add IMAP account (host/port/security/username/password), connects successfully.
- Add Gmail account via OAuth, connects successfully.
- Add Microsoft 365 account via OAuth, connects successfully.
- View per-account connection status. Note `AuthState` is `Connected`/`NeedsReauth`/`Error` only; "Syncing" is a **derived presentation state** computed from the sync-state tables (§1), not an auth state.
- Clear re-authentication flow when needed, without re-entering everything.
- Remove an account, with credentials/cached data cleaned up.
- Reorder accounts; assign a colour per account.
- Set sync polling interval per account.

### Epic 2 — Sidebar & Navigation
- All accounts' mailboxes visible in one persistent sidebar tree — no switching that hides other accounts.
- Correct nested folder structure (parent/child).
- Live unread/total counts per mailbox, updating regardless of which mailbox is being viewed.
- Select any mailbox without other accounts disappearing from view.
- Collapse/expand account sections; expand/collapse state persists per folder across restarts.
- **Folder management**: create, rename, delete (with confirmation), reorder, reparent/nest via drag and drop.
- Drag a message onto a sidebar folder to move it.
- Sensible handling/error messaging where a provider doesn't support an operation (e.g. Gmail's lack of true nesting).

### Epic 3 — Initial Sync
- Choose bounded (last N months/messages) or full-history initial sync per account/mailbox.
- Visible progress (fetched/estimated total).
- Resumable across app restarts — continues from last position, not from scratch.
- Already-synced mailboxes usable while others are still completing initial sync.

### Epic 4 — Steady-State Mail Sync
- New mail appears automatically, no manual refresh.
- Server-side state changes (e.g. read via another client) reflect back into the app.
- Failed sync/mutation shown via a visible indicator on the affected message, never silent.

### Epic 5 — Reading Mail
- Open a message, read plain text or rendered HTML body.
- Inline images render correctly via `cid:` resolution.
- **Attachments open via the OS default file handler** (`shell.openPath` on a temp-written copy, with sensible temp-file cleanup) — not an in-app viewer.
- **HTML isolation is a hard security requirement**, and the most security-sensitive surface in the app — the renderer also holds the capability to invoke mail mutations. Required: `contextIsolation` on, Electron sandboxing, a strict CSP, navigation and window-open interception, scripts/forms/iframes stripped, DOMPurify sanitisation, and message content rendered in an isolated document context.
- **Remote content blocked by default**, with a clear prompt to load and a per-sender/domain allow/block list. **Enforced at the request layer, not merely by removing `<img src>`** — CSS `url()`, `srcset`, SVG and `<style>@import` all trigger requests too.
- **Inline images are fetched through authenticated client code and rewritten to `blob:` URLs.** An `<img src>` request cannot carry a bearer token via SignalR's access token factory, and the launch token must never appear in a query string.
- Outlook-style sortable/filterable message list columns (sender, subject, snippet, date, read/flag state).
- Virtualised scrolling for long lists.
- Print a message (rendered HTML/plain text, sensibly formatted — headers, body, no app chrome) via Electron's `webContents.print()`/`printToPDF`, with a dedicated print stylesheet.
- **Search**: full body text and headers (To/From/Cc/Subject), with optional field-scoped search (e.g. `from:`); HTML messages searched on extracted plain text only — tags/attributes never affect matching or appear in results. Requires a derived plain-text column feeding the FTS5 index alongside the stored `HtmlBody`.

### Epic 6 — Mail Actions
- Mark read/unread, flag/unflag.
- Move between folders (including via drag and drop, per Epic 2).
- Delete.
- Optimistic updates — actions feel instant, server confirmation follows.
- **Multi-select**: select multiple messages (shift/ctrl-click, standard convention) and apply an action (move, delete, mark read/unread, flag) to the whole selection at once, including via the right-click context menu.
- Compose (plain text or HTML), To/Cc/Bcc, subject, body, attachments.
- Reply/Reply All/Forward.
- Save and resume drafts.
- Duplicate-resistant send: a stable `Message-ID` generated before the first attempt, ambiguous-outcome reconciliation against the Sent mailbox after a crash, and clear failure feedback. Exactly-once delivery is **not** claimed — no local transaction can span an external SMTP/Graph/Gmail send.
- **Missing-attachment detection**: on send, if the body contains attachment-indicating language (e.g. "attached," "see attachment") but no file is attached, show a dismissable warning before sending. Client-side heuristic only, no backend involvement.

### Epic 7 — Calendar
- View calendars from all accounts.
- Correct time/timezone/recurrence handling.
- Create/edit/delete events.
- Attendee response status visible to organisers.
- **Unified view** across all calendars/accounts, colour-distinguished.
- **Calendar (grid) and list/agenda views**, agenda view virtualised.
- **Meeting invites**: Accept/Decline/Tentative from message or calendar; correct iTIP/iCalendar `REPLY` generation for IMAP-based invites (Graph/Gmail handle this via native APIs); organiser sees attendee responses update. Requires `.ics`/`text/calendar` MIME parsing feeding the `CalendarEvent`/response-status model — a distinct piece of work from calendar CRUD.

### Epic 8 — Settings
- Per-account settings (polling interval, initial sync mode/bound, notifications enabled).
- Theme (light/dark) — scope TBD.
- Visibility into credential storage security state (e.g. warn if only the weaker SQLite fallback is available on this OS).
- Close behaviour: **quit app** vs **minimise to tray** (global app setting).

### Epic 9 — Notifications
- Native OS desktop notification on new mail.
- Per-account `NotificationsEnabled` flag; no in-app quiet hours, deferring entirely to OS-level do-not-disturb.
- Clicking a notification opens the app and navigates to the message.
- Dispatch owned by `electron-shell`; the renderer relays via the preload bridge.

**Eligibility comes from change-stream provenance, not row creation.** An earlier formulation — "notify when a canonical message row is created by a live change event" — is unsafe under the Gmail baseline strategy: baseline `H0` captured, backfill enumerates newly arrived message A and creates its row, queued history then reports A. The message genuinely arrived in the live interval, but history replay does not create the row, so the notification is wrongly suppressed.

> A notification is emitted once when the live change stream establishes that a message first arrived after the **notification baseline**. Backfill and reconciliation may materialise the row first; they do not consume notification eligibility.

This also gets Graph right: a destination-occurrence addition for an already-known canonical message is a move, not an arrival, and does not notify.

**Two separate things, because a single per-account provider baseline does not exist across all providers** — Gmail's change stream is account-scoped, Graph's and IMAP's are per-mailbox:

- **Notification epoch** — account-level policy: do not notify for anything predating account setup or a resynchronisation. Purely local.
- **Stream baseline token** — captured at whatever scope the provider's change stream uses: account for Gmail, mailbox for Graph and IMAP.

On cursor invalidation the **new stream baseline is captured before resynchronisation begins**, never advanced afterwards. Advancing after would classify mail arriving during the resync window as old and silently drop those notifications.

**Delivery is at-least-once, by explicit policy.** The SQLite transaction covers the notification record; it cannot cover the OS call. So: transactionally create one durable record keyed `(AccountId, MessageId, NotificationKind)`, dispatch pending records, mark delivered afterwards. A crash can therefore duplicate a notification. That is the chosen direction — an occasional duplicate is a minor annoyance, a silently missed new-mail notification defeats the feature.

### Epic 10 — Multi-Window Support
- Open a message in its own window.
- Open an additional independent main window.
- Detach compose into its own window (pop-out), surviving window close without discarding (autosave semantics).
- All actions reflected live across all open windows (via existing `Clients.All` SignalR broadcast design).
- Single shared backend connection/process regardless of window count.
- Configurable close behaviour (Epic 8) determines whether closing the last window quits the app or minimises to tray; tray icon always offers an explicit "Quit" action when minimise-to-tray is active.

### Epic 11 — Resizable/Configurable Layout
- Drag-resize sidebar, message list, reading pane/compose panel (e.g. via `react-resizable-panels`).
- Collapse reading pane or sidebar for more space.
- Layout remains usable across window sizes.
- **Global persisted default** panel layout (via react-granular-store + local persistence through the preload bridge) — read once as the initial layout for any newly opened window.
- **Live per-window state** is independent — resizing one window updates only that window's in-memory layout and writes back to the shared default for future windows; it does not live-sync to other currently-open windows.

### Standing conventions (not epics — applied continuously)
- **Keyboard shortcuts**: follow standard Outlook/Gmail conventions, wired up as each relevant feature is built (not a separate backlog item).
- **Reliability & performance, from day one** (not a deferred phase): fast startup with immediate cached-data display and background sync continuation; graceful backend crash recovery; no data loss/duplication across restarts.
- **Empty states**: no dedicated onboarding flow, but every list/collection view (no accounts yet, empty mailbox, no calendar events in range) must have a deliberate, non-broken-looking empty state.
- **Window bounds persistence**: standard convention (as in most multi-window desktop apps) — a newly opened window inherits the primary/last-active window's last-known size and position, offset slightly, rather than the app remembering fully independent bounds per window identity.
- **`mailto:` default-handler registration**: on startup, check whether this app is already the OS default `mailto:` handler; if not, prompt to make it default, with a "don't ask again" checkbox suppressing future prompts regardless of the user's choice. Implemented cross-platform via Electron's `app.setAsDefaultProtocolClient('mailto')` (registers the OS-level association — Windows registry, macOS `LSSetDefaultHandlerForURLScheme`, Linux `xdg-mime`/desktop-entry `MimeType` — Electron abstracts the per-OS mechanism); the "don't ask again" state is a simple `AppSettings` flag (`MailtoPromptDismissed`, bool).
- Multi-select (per Epic 6) and standard, expected context-menu options per item type (message: reply/reply-all/forward/mark read-unread/flag/move/delete/save-as-.eml/print; folder: rename/delete/new-subfolder). No further design required — implemented as conventional right-click menus matching Outlook/Gmail norms.
- Drag-and-drop of external files into compose as attachments — standard OS drag-and-drop onto the compose window, no further design required.

---

## 14. Open Questions / Deferred Topics

Explicitly out of scope — no further design required:
- **Contacts**
- **Message threading** (schema reserves a nullable `ThreadId` for potential future use, but no implementation is planned)

---

## 15. Resolved Decisions (formerly open questions)

### Deployment & data location
- No auto-update. Manual download/overwrite from GitHub releases.
- App binaries and app data are **never co-located** — an overwrite must never risk the database.
- Default data directory is OS-standard: Linux `$XDG_DATA_HOME/mylomail` (fallback `~/.local/share/mylomail`), Windows `%APPDATA%\MyloMail`, macOS `~/Library/Application Support/MyloMail`.
- `BootstrapConfig.DataDirectoryOverride` (string?) — user-configurable. Read from a small bootstrap config file at a fixed, non-overridable location (separate from the SQLite DB itself, since the override must be resolved before the DB path is known).

### Telemetry
- Standard OpenTelemetry (`OpenTelemetry.Extensions.Hosting`, ASP.NET Core/HttpClient auto-instrumentation).
- `AppSettings.TelemetryEnabled` (bool), **default off** — opt-in, user-facing setting.
- `AppSettings.OtelEndpoint` (string?) — user-defined exporter endpoint; no baked-in default destination.
- Same no-PII discipline as application logging (§10) applies to span/metric attributes — no addresses, subjects, or bodies.

### Configuration storage — bootstrap file vs AppSettings
- **Bootstrap file**: always at the fixed, OS-conventional config location (e.g. `~/.config/mylomail/bootstrap.json` on Linux) — never itself overridable. Contains **only** `DataDirectoryOverride` (string?), since this is the one setting that must be resolved before the data directory (and therefore the DB) can be located.
- **`AppSettings`**: everything else (`TelemetryEnabled`, `OtelEndpoint`, `CloseBehavior`, `PanelLayout`, etc.) lives as a table/row in the main SQLite DB at the resolved data directory. Sensible defaults, no separate init file required.

### Provider rate-limiting
- **Gmail/Graph**: honour the provider's explicit signal — `429`/`Retry-After` header (Gmail) or Graph's throttling response — as the exact retry delay, not generic backoff.
- **IMAP**: no standard rate-limit signalling exists; falls back to the existing exponential backoff, since there's no explicit signal to honour.
- `IMailProvider` implementations throw a `ProviderThrottledException(TimeSpan retryAfter)` where the provider gives an explicit signal; the mutation/sync job's failure handling checks for this specifically and schedules the retry at exactly that delay, falling back to standard exponential backoff only when no explicit signal is available.

### TLS certificate trust
- Default: standard OS trust-store validation (`SslStream`/`ImapClient.ServerCertificateValidationCallback`, OS store by default).
- On failure: clear "certificate untrusted" error surfaced to the user, showing the certificate's thumbprint/issuer — never silently failing or silently accepting.
- `AccountTrustedCertificate` (child table: `AccountId`, `Sha256Fingerprint`, `ExpectedHostname`) — supports **multiple** pinned certificates per account, for rotation on a self-hosted server.
- **The rule, stated precisely** (the earlier "on top of, not instead of" phrasing was self-defeating — the only reason to pin is that normal validation failed): validate normally first; **if and only if** normal validation fails, permit the connection when the presented certificate's SHA-256 fingerprint exactly matches a pinned entry for the expected hostname. A legitimately-signed certificate still validates normally and needs no pin.
- A SHA-256 fingerprint is used rather than an ambiguously-defined "thumbprint".
- `Account.CertificateTrustMode` (enum: `Default` | `TrustAll`) — `TrustAll` bypasses validation entirely for that account. A clear in-app warning is shown when enabling this mode, since it is a genuine security downgrade.

### Attachment size limits
The earlier claim that Graph exposes a maximum via `mailboxSettings` was **wrong** — that resource carries time zone, locale, working hours and automatic replies, not message size limits. Exchange message-size limits are separately configurable per tenant and are not reliably discoverable.

`IMailProvider.GetAttachmentConstraintsAsync(Account)` therefore returns a nuanced result rather than a single number:

| Field | Notes |
|---|---|
| `ApiPerFileLimit` | Graph upload sessions support up to ~150MB; Gmail's limit is a documented constant |
| `KnownMessageSizeLimit` | Where discoverable; frequently `null` |
| `ConfiguredOverride` | `Account.AttachmentSizeLimitOverride`, user-set |
| `IsUnknown` | Explicit, rather than a false precise number |

Checks account for **base64 overhead on total message size**, not just per-file raw size, and are applied before send is attempted. Where the limit is unknown, the user is warned rather than blocked.

**Large uploads are streamed**, and Graph's chunked upload-session flow above ~3MB is absorbed entirely inside `GraphMailProvider` — the interface signature and every caller stay identical across providers.

### Calendar conflict resolution
- Polling-based sync means true real-time conflict avoidance isn't possible — the strategy is **detect, don't silently merge**.
- Compare the server's `LastModified`/`ETag` (Graph) or CalDAV `ETag` against the value the local edit was based on, at the point the edit's mutation job runs.
- On mismatch: do **not** overwrite. Set `CalendarEvent.SyncConflict = true`, retain both the local pending edit and the server's version, and surface a "this event changed elsewhere — keep mine / keep theirs" resolution prompt rather than resolving automatically.

### Error taxonomy — RFC 7807 `ProblemDetails`, used consistently across REST and SignalR
```csharp
public class MutationProblemDetails : ProblemDetails
{
    public ErrorCategory Category { get; set; } // Network, Auth, RateLimit, Validation, ProviderRejected, Conflict, Unknown
    public string? ProviderCode { get; set; }    // raw provider error code — diagnostics only, not necessarily shown to the user
}
```
- Standard `ProblemDetails` fields (`Type`/`Title`/`Status`/`Detail`) carry the human-readable, safe-to-show message; `Category`/`ProviderCode` are the extension members.
- REST-adjacent calls (health check, OAuth kickoff, attachment download via `json-fetch-client`) get this for free — `json-fetch-client` already parses `ProblemDetails`/`ValidationProblemDetails` natively.
- SignalR hub methods that fail throw a `HubException` carrying the serialized `MutationProblemDetails`; the generated typed client (`TypedSignalR.Client.TypeScript`) deserializes it the same way on the renderer side — **one error shape across both transports**.
- `Category` drives UI behaviour uniformly: `Network` → calm "offline, will retry"; `Auth` → re-auth prompt; `RateLimit` → silent/no alarm; `Validation`/`ProviderRejected` → show `Detail` directly (e.g. attachment-too-large, certificate-untrusted); `Conflict` → keep-mine/keep-theirs prompt; `Unknown` → generic failure indicator, logged for diagnosis.

### Accessibility
- Continuous requirement, not a phase or epic — applies throughout implementation.
- Colour contrast (including per-account sidebar colours — needs a contrast floor, not arbitrary user-picked colour) must meet accessible standards.
- Full keyboard operability (ties into the standing keyboard-shortcut convention).
- Semantic/ARIA-correct markup required on custom components (message list, calendar grid, resizable panels) since TanStack Table/Virtual are headless and provide no accessibility scaffolding by default.

### Offline behaviour
- Reading cached/synced data must always work with no network.
- Sync errors use the same `MutationProblemDetails.Category` taxonomy as every other error in the system (see "Error taxonomy" below) — categorised, never flat strings — so the UI can show one calm "offline — will resume" state instead of per-mailbox error noise multiplying every poll cycle.
- Connectivity-aware pause: network-class failures suppress normal per-job retry noise/logging until connectivity returns, rather than surfacing every offline poll attempt as a fresh failure.

### Undo send & delayed send
- True message recall isn't possible across providers. "Undo send" is a client-side delay before dispatch — the same mechanism serves both undo-send and scheduled send.
- `OutboxItem.ScheduledSendAt` — `now + delaySeconds` for a normal send, or an arbitrary future time for a scheduled one.
- `Account.UndoSendDelaySeconds`, per account; `0` disables the window.
- **A stable RFC 5322 `Message-ID` is generated before the first send attempt** and reused across retries, which is what makes reconciliation against the Sent mailbox possible after an ambiguous outcome (§6).
- **Cancellation is a compare-and-swap race, not a recheck.** Cancel attempts `Scheduled`/`Pending` → `Cancelled`; the worker attempts `Scheduled`/`Pending` → `Sending`. Exactly one wins. Once `Sending`, cancellation reports "too late" and must not revert to `Draft`. A recheck immediately before dispatch narrows the window but does not close it.
- **Graph uses the draft-then-send path** in preference to direct send: creating a draft with an immutable id and sending by that id makes post-crash reconciliation deterministic. Microsoft does not guarantee the Sent copy is immediately available, so reconciliation uses a bounded retry window before declaring `AmbiguousOutcome`.

**Scheduled sends fire only while the app is running.** The scheduler requires the process, and with in-memory job storage a pending send exists as a Hangfire job only for the current session — it is re-enqueued from `OutboxItem` by startup reconciliation, which is what makes it survive a restart at all. A message scheduled for 09:00 will not send if the app is closed at 09:00. Minimise-to-tray covers the realistic case, since the app can run windowless. If the master-password credential store is active the app also starts **locked**, so scheduled sends wait for unlock as well as for launch. Both are stated in the UI, and quitting with pending scheduled messages prompts for confirmation.

### Sent-mail handling after send
Provider behaviour genuinely diverges here and must not be assumed uniform:
- **Gmail and Graph** place a copy in Sent automatically as part of the send operation — appending one manually would create a duplicate.
- **IMAP/SMTP** does not: SMTP simply relays the message, and it is the client's responsibility to `APPEND` a copy to the Sent mailbox afterwards. Some servers (notably Gmail-over-IMAP) do it server-side anyway, which is exactly why this can't be hardcoded.

`ImapProviderConfig.AppendToSentOnSend` (bool, **default `true`**) — applies to IMAP accounts only. Enabled by default because most plain IMAP/SMTP servers require it; users on a server that files sent mail automatically can disable it to avoid duplicates.

### Signatures
- Signatures live on `SendIdentity` (§1), not on `Account` — pre-populated into compose below quoted reply text (standard convention), using the identity selected for that message.
- **Multiple send-as identities per account**: in scope. `SendIdentity { Id, AccountId, DisplayName, EmailAddress, SignatureHtml, IsDefault }` — a child collection on `Account`, since a real send-as scenario (e.g. an alias address on the same account) needs its own display name, address, and signature, selectable at compose time. Each account has exactly one default identity, used unless the user picks another.

### Rules/filters
- Explicitly out of scope.

### Export
- Per-message **"Save As .eml"** — the stored `MessageRaw` bytes written verbatim (§1). Faithful: headers, DKIM signatures, S/MIME/PGP parts, nested `message/rfc822`, calendar parts and transfer encodings are all preserved. Where raw content has not yet been fetched, it is retrieved on demand first.
- **Bulk export** — a backend job walking the `Account` → `Mailbox` tree, recreating the folder hierarchy on disk from `ParentId`/`Name`, writing each message as `.eml`.
  - **Gmail**: a message in five labels is written into five folders. This is the correct behaviour for a folder-oriented export, and is stated so users aren't surprised by the total size.
  - **Progress is persisted**, not merely broadcast: an `ExportJob` entity (`Id`, `AccountId`, `DestinationPath`, `Status`, `TotalCount`, `WrittenCount`, `ResumeToken`, `LastError`) mirrors the coverage-state pattern, so an export survives restart and can be cancelled and resumed. `ExportProgress` events are derived from it rather than being the state itself.
- **Import is explicitly out of scope.**

### Packaging & code signing
- **Confirmed: no code signing**, on any platform.
  - **Linux** (primary target): unsigned `.deb`/`.rpm`/`.AppImage` — standard, unremarkable for OSS.
  - **Windows**: unsigned `.exe`/`.msi` — SmartScreen "unknown publisher" warning accepted as a documented click-through.
  - **macOS**: **in scope**. Built via GitHub Actions' hosted `macos-latest` runner (electron-builder does not cross-compile — Windows/Linux runners cannot produce a macOS build — but GitHub provides genuine macOS CI runners, so no locally-owned Mac is required for the build pipeline itself). Unsigned/non-notarized, consistent with the no-code-signing decision — blocked by Gatekeeper by default, with a documented right-click-to-open workaround for users. A personally-owned Mac remains useful for manual pre-release testing on real hardware, but is not a CI dependency.

### Localisation
- **English-only strings.** No i18n scaffolding, no string externalisation.
- **Locale-aware formatting is still required.** Dates, times and numbers follow the OS/user locale — hard-coded US or ISO display formatting would be conspicuous in a mail and calendar client. `dayjs` handles this; only the strings are fixed to English.

---

## 16. Status, Governing Principles, and Getting Started

**The architecture is frozen.** It reached this state through several rounds of adversarial review, and the sections above encode conclusions that are not obvious from the outside. Before changing anything structural here, read this section — several apparently redundant or over-engineered mechanisms exist because a simpler version was tried and found broken.

### The three principles everything else follows from

1. **Stable local identity owns ordering and intent. Volatile provider identity is an input to individual execution attempts.** `Message.Id`, `Mailbox.Id` and mutation sequence numbers are local and permanent. IMAP UIDs, Graph ids and Gmail history positions are resolved at the moment of use and never persisted as queue keys.

2. **No local record of an attempt is evidence about what the server actually did.** A durable record that an operation was dispatched establishes only that its outcome *may* be ambiguous. The absence of a persisted response never implies the operation failed remotely. This is why `MutationExecutionAttempt` exists, why `Dispatched` is written before the provider call, and why "worker crashed" never means "nothing happened".

3. **Never persist a cursor past changes that have not been durably persisted.** Replay is always acceptable — every write is an upsert. Skipping is never acceptable, and it is silent.

### Things that look wrong but are deliberate

Each of these was arrived at by finding the simpler version broken:

- **Writing `Dispatched` synchronously before every provider call.** Looks like gratuitous I/O on the hot path. It is the entire crash-safety mechanism. Batching, deferring or making it async silently reintroduces the window it closes, and the resulting bug is invisible in testing.
- **`Message.Id` rather than `Message-ID` as identity.** RFC 5322 says a message SHOULD have a `Message-ID`, not MUST, and duplicates occur in practice.
- **Provider ids on `MessageMailbox`, not `Message`.** IMAP UIDs are folder-scoped and change on move.
- **Two count fields per mailbox.** Locally computed counts are wrong under bounded sync; provider-reported counts drive the sidebar.
- **In-memory Hangfire storage.** Job persistence was a second source of truth that could diverge from the app tables. Startup reconciliation was always the real recovery mechanism.
- **Ordering that is not transactionality.** After a terminal failure, later mutations in a chain are re-evaluated, not cancelled wholesale — providers offer no transaction across these operations, and cancelling turns one failure into several abandoned user intentions.
- **Separate `Availability` and `Coverage` UI dimensions.** A mailbox is fully usable while backfilling.
- **Raw MIME as the single stored representation.** Reconstructing `.eml` from parsed parts produces a plausible message, not the message — losing DKIM signatures, original headers and encrypted parts.

### Build order

The epics in §13 are **requirements, not a build sequence**. There is no obligation to ship any particular functionality at any point, so the build order optimises for one thing only: **discover every constraint before code accumulates on top of it.**

Ordering by user-facing feature would be wrong here. Several structural decisions are cheap to make early and expensive to retrofit, and provider divergence is the single largest rework risk in the design.

#### A — Contracts and skeleton
.NET solution and pnpm workspace per §12; CI matrix across `ubuntu-latest`, `windows-latest`, `macos-latest`; `TypedSignalR.Client.TypeScript` and `TypeContractor` running end to end. Small, but everything downstream inherits it, and a broken type-generation pipeline found later is painful to unpick.

#### B — Provider conformance: all three, thin

**The highest-value stage, and the one most likely to be got wrong by deferring it.**

Implement `IMailProvider` and `ICalendarProvider` for IMAP, Graph and Gmail to a deliberately shallow depth — authentication, topology listing, one read path, one mutation — all driven by a **shared conformance suite** every provider must pass.

The purpose is not features. It is to establish whether the abstraction actually holds against all three providers while nothing is built on top of it. Building IMAP fully, then Graph, then Gmail means discovering Gmail's account-scoped change stream after months of code assumes mailbox-scoped cursors. That is a rewrite; finding it here is a day.

Include the three IMAP capability tiers (QRESYNC / CONDSTORE-only / neither) in the matrix from the start, since they are as divergent from one another as the providers are.

**The conformance suite is a first-class artefact, not merely tests.** One set of assertions every provider must satisfy: occurrence identity survives a move; cursor invalidation is detected and surfaced; batch results are genuinely per-item; capability negotiation reports honestly; immutable ids are used throughout on Graph. It is what makes this stage meaningful rather than three ad-hoc integrations, and it remains the regression net permanently.

#### C — Backend core
Sync state machines (§3), mutation chains, leases, execution attempts, reconciliation and startup recovery (§6). Built against `FakeMailProvider` plus the three thin providers from B.

**The fault-injection harness lands here** and runs against everything thereafter. It needs the ability to kill the backend at a named point and restart it. Minimum scenarios:

| Kill point | Expected outcome |
|---|---|
| After optimistic local commit, before enqueue | Desired state either applied or reverted; never orphaned |
| After enqueue, before `Dispatched` | Mutation re-executes cleanly |
| **After `Dispatched`, before provider call** | Treated as ambiguous; reconciled; "never happened" handled as a normal result |
| After provider call, before results persist | Reconciled, not blindly retried |
| Mid-batch, partial results | Only unresolved items reconciled |
| After a move changes the UID | Occurrence identity re-established |
| Mid page, before cursor commit | Page replayed, nothing skipped |
| Gmail staged history, before canonical replay | Staged events survive; notifications still fire |
| Mailbox deleted and recreated mid-sync | Late page discarded on topology generation mismatch |
| `Sending` outbox item | Never auto-retried; reconciled against Sent by stable `Message-ID` |
| Any of the above | **Startup reconciliation restores every outstanding work item** |

#### D — Frontend shell architecture

Independent of A–C and may run in parallel, but **must precede any real view**. Everything here is cheap now and expensive to retrofit, because every subsequent view is built inside it:

- Resizable panels (§13, Epic 11) — every view renders within this layout
- **Per-window state architecture** — the most expensive retrofit in the frontend. Multi-window is not a feature added later; it is an assumption made or not made. Stores are per-window and the SignalR connection is per-window from the first line, even while only one window ships
- Theme tokens — retrofitting means touching every stylesheet
- Window bounds persistence
- Cross-cutting registries: toasts (Carbon `ToastNotification`/`ActionableNotification`), context menus, keyboard shortcuts, `ErrorCategory` → UI mapping, empty-state pattern
- Accessibility baseline in the shell primitives, not added afterwards

Building the error-category mapping here specifically prevents every later feature growing its own ad-hoc error handling.

#### E onwards — features, largely in any order

Everything they depend on is now proven, so sequencing is a matter of preference rather than risk:

- **Message list and reading pane** — needs D's virtualisation (TanStack Virtual + Table is a specific integration, not a wrapper added later) and the hardened HTML rendering pipeline (§13, Epic 5: isolation, CSP and sandboxing are architectural; the remote-content allow/block list is additive on top). Pending-state rendering must derive from server-known state plus `MessagePendingChange` from the outset
- **Mutations against real providers** — the machinery already exists from C; this wires it up
- **Content, background indexing, FTS, attachments** — needs raw fetch from B
- **Compose, drafts, outbox, identities, signatures**
- **Calendar** — genuinely independent; shares the shell and little else
- **Export, print**
- **Polish**: OS notification dispatch, `mailto` registration, tray behaviour, remote-content lists, telemetry

**Notifications are deliberately split across stages.** Eligibility derivation is sync semantics and belongs with each provider as it is built — it is baked into the Gmail staged-history design (§3). Only OS dispatch is late polish. Treating notifications as a single late epic would mean revisiting every provider.

**No ordering constraint at all**, so they can be slotted opportunistically: export, print, `mailto` registration, tray behaviour, telemetry, remote-content allow/block lists, and the entire calendar branch.

### Verification blockers — resolve before relying on these

- **`PRAGMA synchronous=FULL` under WAL.** Confirm the exact durability semantics against SQLite's own documentation. The dispatch boundary depends on this guarantee; do not delete this note during cleanup.
- **Gmail CASA applicability.** Restricted-scope verification is unavoidable for a shipped client, but CASA is conditioned on third-party server involvement and this architecture has none. Confirm with Google before deciding whether BYOC is mandatory or merely offered. See §5.
- **`Hangfire.InMemory` version currency**, `TypeContractor`'s experimental Zod generation, and current `typescript-eslint` support for TypeScript 7 — all move faster than this document.

### Where the design deliberately stops

Out of scope, decided rather than overlooked: contacts; message threading (`ThreadId` reserved, unimplemented); server-side rules and filters; mail import; auto-update; i18n; code signing; storage eviction (growth is unbounded by choice). Scheduled sends fire only while the application is running. Exactly-once send delivery is not claimed and is not achievable.
