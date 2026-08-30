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
/// <para>
/// <b><see cref="NewCursor"/> is null until it is safe to commit.</b> A cursor may only be
/// returned once it covers everything in this result and everything before it. Mid-walk,
/// Gmail reports a <c>historyId</c> for the whole list and Graph yields a <c>nextLink</c>
/// rather than the <c>deltaLink</c> incremental sync needs (§3), so a provider that returned
/// either alongside a partial page would hand the caller a cursor covering changes it has
/// not been given. The caller commits cursor and data in one transaction, so that page is
/// then skipped — silently and permanently.
/// </para>
/// <para>
/// Providers whose cursor is monotone over the data already returned — IMAP, where
/// <c>HighestKnownUid</c> is a high-water mark — may advance mid-walk, and declare that
/// with <see cref="ProviderCapabilities.AdvancesCursorMidWalk"/>.
/// </para>
/// <para>
/// <see cref="Continuation"/> resumes the walk and is never a durable cursor. It is passed
/// back to <c>SyncMailboxAsync</c>, not persisted as sync position.
/// </para>
/// </remarks>
public record SyncResult(
	ProviderCursorState? NewCursor,
	string? Continuation,
	IReadOnlyList<MessageDto> Upserted,
	IReadOnlyList<OccurrenceFlagChange> FlagChanges,
	IReadOnlyList<OccurrenceRemoval> Removed
)
{
	/// <summary>
	/// Derived rather than reported, so it cannot contradict <see cref="Continuation"/>.
	/// </summary>
	public bool HasMore => Continuation is not null;
}

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

/// <summary>
/// A point-in-time observation used by periodic integrity reconciliation. It is deliberately
/// not a cursor: degraded IMAP must compare the server UID set even when its incremental
/// cursor is valid.
/// </summary>
public record MailboxIntegritySnapshot(
	IReadOnlySet<string> ExistingOccurrenceIds,
	IReadOnlyList<OccurrenceFlagChange> FlagChanges
);
