namespace MyloMail.Api.Providers.Contracts;

/// <summary>
/// Cursor state is structured and versioned per provider, not an opaque string — a single
/// string would pretend these are equivalent when they are not (§1).
/// </summary>
/// <remarks>
/// <see cref="Version"/> exists so a cursor's shape can change without a migration having
/// to guess at the meaning of an old opaque token.
/// <para>
/// Cursor advancement is transactional: a cursor and the state it represents commit
/// together, always. Replaying a page is acceptable — every write is an upsert. Skipping
/// one is not, and it is silent.
/// </para>
/// </remarks>
public abstract record ProviderCursorState
{
	public abstract CursorKind Kind { get; }

	public int Version { get; init; } = 1;
}

public enum CursorKind
{
	GmailHistory,
	GraphDelta,
	ImapUid,
}

/// <summary>Gmail's account-scoped history sequence. There is one per account, never one per label.</summary>
public sealed record GmailHistoryCursor(string HistoryId) : ProviderCursorState
{
	public override CursorKind Kind => CursorKind.GmailHistory;
}

/// <summary>Graph's folder-scoped delta link.</summary>
public sealed record GraphDeltaCursor(string DeltaLink) : ProviderCursorState
{
	public override CursorKind Kind => CursorKind.GraphDelta;
}

/// <summary>
/// IMAP's mailbox-scoped position. <paramref name="UidValidity"/> changes on a server-side
/// reindex, which invalidates every stored UID-based token.
/// </summary>
public sealed record ImapUidCursor(
	uint UidValidity,
	uint HighestKnownUid,
	ulong? HighestModSeq,
	ImapReconciliationState? Reconciliation
) : ProviderCursorState
{
	public override CursorKind Kind => CursorKind.ImapUid;
}

/// <summary>
/// The known-UID-set baseline the weaker IMAP tiers reconcile against. Cheap signals
/// (<c>UIDNEXT</c>, <c>EXISTS</c>) can trigger reconciliation early but never replace the
/// periodic run — equal numbers of additions and deletions leave aggregates unchanged (§3).
/// </summary>
public sealed record ImapReconciliationState(
	IReadOnlyList<uint> KnownUids,
	DateTimeOffset LastReconciledAt
);
