using MyloMail.Api.Domain;
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

[TranspilationSource]
public record OutboxItemDto(
	Guid Id,
	Guid AccountId,
	OutboxStatus Status,
	DateTimeOffset ScheduledSendAt,
	string? LastError
);
