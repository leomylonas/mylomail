namespace MyloMail.Api.Providers.Contracts;

/// <summary>
/// One page of historical backfill. Bounds are coverage targets, not membership limits, and
/// they limit historical backfill only — never future synchronisation (§3).
/// </summary>
/// <remarks>
/// <see cref="ResumeToken"/> is persisted every page, which is what makes coverage
/// resumable.
/// </remarks>
public record InitialSyncPage(
	IReadOnlyList<MessageDto> Messages,
	string? ResumeToken,
	bool HasMore,
	int? EstimatedTotal
);

/// <summary>
/// One page of incremental change, in the common shape every provider translates its native
/// mechanism into — UID ranges, <c>historyId</c> or <c>deltaLink</c>.
/// </summary>
/// <remarks>
/// <see cref="NewCursor"/> must be persisted in the same transaction as the changes it
/// covers. Never persist a cursor past changes that have not been durably persisted:
/// replay is always acceptable, skipping never is, and it is silent (§1, §3).
/// </remarks>
public record SyncResult(
	ProviderCursorState NewCursor,
	IReadOnlyList<MessageDto> Upserted,
	IReadOnlyList<OccurrenceFlagChange> FlagChanges,
	IReadOnlyList<OccurrenceRemoval> Removed,
	bool HasMore
);

/// <summary>A server-side flag change observed for one occurrence.</summary>
public record OccurrenceFlagChange(
	string ProviderMailboxId,
	string ProviderOccurrenceId,
	bool? IsRead,
	bool? IsFlagged,
	long? ImapModSeq = null
);

/// <summary>
/// An occurrence that is no longer present in a mailbox — an IMAP expunge, a Graph
/// folder-scoped removal, a Gmail label removal.
/// </summary>
/// <remarks>
/// This removes the occurrence, never the canonical message: under Graph's folder-scoped
/// delta a move surfaces as a removal and an addition in either order, so a canonical
/// message may transiently have zero memberships (§3).
/// </remarks>
public record OccurrenceRemoval(string ProviderMailboxId, string ProviderOccurrenceId);
