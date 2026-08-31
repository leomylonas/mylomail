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
	CoverageStatus Coverage
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
[TranspilationSource]
public record MessageBodyDto(Guid MessageId, string? Text, string? Html, bool IsFetched);

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
/// A change the user asked for that will not happen (§6, §7).
/// </summary>
/// <remarks>
/// Carries the category, not just a message. The category is the whole point of the error
/// taxonomy: it decides whether the UI offers a retry, a sign-in, a conflict resolution or
/// nothing at all, and a bare string would leave every screen guessing from prose.
/// </remarks>
/// <summary>A draft as the compose window holds it (§1).</summary>
[TranspilationSource]
public record DraftDto(
	Guid Id,
	Guid AccountId,
	IReadOnlyList<Address> To,
	IReadOnlyList<Address> Cc,
	IReadOnlyList<Address> Bcc,
	string Subject,
	string BodyHtml,
	IReadOnlyList<DraftAttachmentDto> Attachments
);

[TranspilationSource]
public record DraftAttachmentDto(Guid Id, string Filename, string MimeType, long Size, bool IsInline);

/// <summary>What the compose window sends when it saves or sends.</summary>
[TranspilationSource]
public record SaveDraftRequest(
	Guid? DraftId,
	Guid AccountId,
	Guid? InReplyToMessageId,
	IReadOnlyList<Address> To,
	IReadOnlyList<Address> Cc,
	IReadOnlyList<Address> Bcc,
	string Subject,
	string BodyHtml
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
[TranspilationSource]
public record AccountSettingsDto(
	Guid Id,
	string DisplayName,
	string Color,
	int PollIntervalSeconds,
	bool PollingEnabled,
	int UndoSendDelaySeconds,
	bool NotificationsEnabled
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
