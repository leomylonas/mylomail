using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using Tapper;

namespace MyloMail.Api.Contracts;

/// <summary>A mailbox as the sidebar renders it (§1).</summary>
/// <remarks>
/// <paramref name="ProviderTotalCount"/> and <paramref name="ProviderUnreadCount"/> are what
/// the sidebar displays. A locally computed count is wrong under bounded sync — "last 3
/// months" of a 10,000-message inbox holds far fewer than the mailbox contains — so
/// <paramref name="LocalCount"/> is carried separately and used only where it is the right
/// answer, such as how many messages this view is showing.
/// </remarks>
/// <param name="InitialSyncModeOverride">
/// Null when this mailbox uses the account's own initial-sync choice (§3, §13 Epic 3).
/// </param>
[TranspilationSource]
public record MailboxSummaryDto(
	Guid Id,
	Guid AccountId,
	Guid? ParentId,
	string Name,
	SpecialUse SpecialUse,
	int? ProviderTotalCount,
	int? ProviderUnreadCount,
	int LocalCount,
	CoverageStatus Coverage,
	bool IsCollapsed,
	InitialSyncMode? InitialSyncModeOverride,
	int? InitialSyncBoundValueOverride
);

/// <summary>A message as the list renders it. Content is fetched separately (§1).</summary>
[TranspilationSource]
public record MessageSummaryDto(
	Guid Id,
	Guid AccountId,
	string Subject,
	string Snippet,
	IReadOnlyList<Address> From,
	DateTimeOffset ReceivedAt,
	bool IsRead,
	bool IsFlagged,
	bool HasNonInlineAttachments
);

/// <summary>
/// Backfill progress for one mailbox (§1).
/// </summary>
/// <remarks>
/// Availability and coverage are separate values on purpose: a mailbox is fully usable while
/// still backfilling, and collapsing them into one state would eventually have two screens
/// disagreeing about whether it can be opened.
/// </remarks>
[TranspilationSource]
public record SyncProgressDto(
	Guid MailboxId,
	CoverageStatus Status,
	int MessagesFetched,
	int? EstimatedTotal
);

/// <summary>
/// A message's readable content (§1).
/// </summary>
/// <remarks>
/// <paramref name="Html"/> is the original markup, kept unchanged for rendering — the
/// stripped text that search indexes is a different thing for a different purpose (§8).
/// <paramref name="IsFetched"/> is explicit because "no body yet" and "a message with no
/// body" look identical otherwise, and only one of them is worth waiting for.
/// </remarks>
/// <param name="IsFailed">
/// Content acquisition gave up after repeated attempts (§15) — distinct from
/// <paramref name="IsFetched"/> being false while a fetch is still in progress or queued, so
/// the reading pane can stop waiting instead of polling forever for content that will never
/// arrive.
/// </param>
[TranspilationSource]
public record MessageBodyDto(Guid MessageId, string? Text, string? Html, bool IsFetched, bool IsFailed);

/// <summary>Received attachment metadata; bytes remain solely in the raw MIME (§1).</summary>
[TranspilationSource]
public record AttachmentDto(
	Guid Id,
	Guid MessageId,
	string Filename,
	string MimeType,
	long Size,
	bool IsInline
);

/// <summary>
/// Enough of a message to build a reply or forward from (§13) — the address/subject/date
/// fields a compose window needs to prefill recipients and quote/forward headers, kept
/// separate from <see cref="MessageBodyDto"/> since building a reply also needs the body,
/// fetched independently through the existing content path rather than duplicated here.
/// </summary>
/// <param name="ReplyTo">Reply routing prefers this over <paramref name="From"/> when present (§1).</param>
[TranspilationSource]
public record MessageReplyContextDto(
	Guid MessageId,
	IReadOnlyList<Address> From,
	IReadOnlyList<Address> To,
	IReadOnlyList<Address> Cc,
	IReadOnlyList<Address> ReplyTo,
	string Subject,
	DateTimeOffset ReceivedAt
);

/// <summary>
/// A change the user asked for that will not happen (§6, §7).
/// </summary>
/// <remarks>
/// Carries the category, not just a message. The category is the whole point of the error
/// taxonomy: it decides whether the UI offers a retry, a sign-in, a conflict resolution or
/// nothing at all, and a bare string would leave every screen guessing from prose.
/// </remarks>
/// <summary>A draft as the compose window holds it (§1).</summary>
/// <param name="InReplyToMessageId">
/// Round-tripped so re-saving an existing reply draft keeps pointing at the message it replies
/// to — previously discarded on every save by the compose window always sending <c>null</c>
/// back, silently un-threading any reply draft the moment it autosaved (§13).
/// </param>
[TranspilationSource]
public record DraftDto(
	Guid Id,
	Guid AccountId,
	Guid SendIdentityId,
	Guid? InReplyToMessageId,
	IReadOnlyList<Address> To,
	IReadOnlyList<Address> Cc,
	IReadOnlyList<Address> Bcc,
	string Subject,
	string BodyHtml,
	IReadOnlyList<DraftAttachmentDto> Attachments
);

[TranspilationSource]
public record DraftAttachmentDto(Guid Id, string Filename, string MimeType, long Size, bool IsInline);

/// <summary>
/// What the compose window sends when it saves or sends.
/// </summary>
/// <param name="SendIdentityId">
/// Null lets the server pick the account's default identity — the case for a brand-new draft
/// that has never had one explicitly chosen. Once a draft exists, the compose window always
/// round-trips whatever identity `DraftDto.SendIdentityId` reported, the same way
/// <paramref name="InReplyToMessageId"/> is round-tripped, so switching identities sticks
/// across autosaves (§15).
/// </param>
[TranspilationSource]
public record SaveDraftRequest(
	Guid? DraftId,
	Guid AccountId,
	Guid? SendIdentityId,
	Guid? InReplyToMessageId,
	IReadOnlyList<Address> To,
	IReadOnlyList<Address> Cc,
	IReadOnlyList<Address> Bcc,
	string Subject,
	string BodyHtml
);

/// <summary>A send-as identity, as compose's identity picker needs it (§1, §15).</summary>
[TranspilationSource]
public record SendIdentityDto(
	Guid Id,
	Guid AccountId,
	string DisplayName,
	string EmailAddress,
	string? SignatureHtml,
	bool IsDefault
);

/// <summary>
/// What this account's provider actually does, where the UI has to say so before acting.
/// </summary>
/// <remarks>
/// Capabilities, not settings: nothing here is the user's to change. It is separate from
/// <see cref="AccountSettingsDto"/> for that reason — a settings screen that could write
/// these would be claiming to change what the provider does (§1, §2).
/// </remarks>
[TranspilationSource]
public record AccountCapabilitiesDto(Guid AccountId, bool DeletingMailboxDeletesMessages);

/// <summary>The per-account settings a user can change (§1).</summary>
/// <param name="AttachmentSizeLimitOverride">
/// A user-set ceiling, in bytes, on outgoing attachment size for this account — read by
/// <c>IMailProvider.GetAttachmentConstraintsAsync</c> alongside whatever the provider itself
/// reports (§15). Null leaves it unset; the provider's own limit (or "unknown") applies.
/// </param>
[TranspilationSource]
public record AccountSettingsDto(
	Guid Id,
	string DisplayName,
	string Color,
	int PollIntervalSeconds,
	bool PollingEnabled,
	int UndoSendDelaySeconds,
	bool NotificationsEnabled,
	CertificateTrustMode CertificateTrustMode,
	int? AttachmentSizeLimitOverride
);

[TranspilationSource]
public record MutationFailureDto(Guid MessageId, ErrorCategory Category, string? Detail);

[TranspilationSource]
public record OutboxItemDto(
	Guid Id,
	Guid AccountId,
	OutboxStatus Status,
	DateTimeOffset ScheduledSendAt,
	string? LastError
);

/// <summary>A calendar as the calendar view renders it (§1).</summary>
[TranspilationSource]
public record CalendarSummaryDto(Guid Id, Guid AccountId, string Name, string? Colour, bool IsDefault);

/// <summary>
/// One event as the calendar grid or agenda view renders it. Attendees and the full
/// recurrence set are not carried here — the summary is what a month grid needs, not what an
/// event's own detail view needs (§1).
/// </summary>
[TranspilationSource]
public record CalendarEventSummaryDto(
	Guid Id,
	Guid CalendarId,
	string Title,
	string? Location,
	string? Description,
	DateTimeOffset Start,
	DateTimeOffset End,
	bool IsAllDay,
	EventStatus Status,
	bool IsRecurring,
	bool SyncConflict
);

/// <summary>One attendee as an event's own detail view needs it (§13 Epic 7) — not carried on
/// <see cref="CalendarEventSummaryDto"/>, since a month grid never renders it.</summary>
[TranspilationSource]
public record AttendeeDto(string? Name, string Email, AttendeeRole Role, ResponseStatus ResponseStatus);

/// <summary>
/// The invite-handling detail a month grid does not need but the event modal does: who else is
/// on this event and, for an event the account did not organise, whether — and how — it has
/// already replied (§13 Epic 7). Fetched lazily per event rather than folded into
/// <see cref="CalendarEventSummaryDto"/> for the same reason the doc comment above gives for
/// leaving recurrence detail out of it.
/// </summary>
[TranspilationSource]
public record CalendarEventDetailDto(
	Guid Id,
	Address? Organizer,
	IReadOnlyList<AttendeeDto> Attendees,
	bool IsOrganizer,
	InviteResponse? MyResponseStatus
);

/// <summary>
/// One OS notification the shell owes the user (§13 Epic 9). Carries enough to render it
/// without a round trip: the renderer relays this straight to the preload bridge.
/// </summary>
/// <remarks>
/// <paramref name="MessageId"/> is null when this was recorded from a staged, not-yet-replayed
/// change-stream page — the canonical row does not exist yet. Activating such a notification
/// still works; per §3 it fetches the message on demand rather than the navigation failing.
/// </remarks>
[TranspilationSource]
public record NotificationDto(Guid Id, Guid AccountId, Guid? MessageId, string Title, string Body);

/// <summary>What the calendar UI sends when it creates or edits an event.</summary>
[TranspilationSource]
public record SaveCalendarEventRequest(
	Guid? EventId,
	Guid CalendarId,
	string Title,
	string? Location,
	string? Description,
	DateTimeOffset Start,
	DateTimeOffset End,
	bool IsAllDay
);
