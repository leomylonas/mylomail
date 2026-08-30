using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers;

/// <summary>
/// What a provider can honestly do. Capability negotiation exists so that differences the
/// interface cannot absorb are surfaced rather than assumed away (§2), and it drives
/// recovery policy and the IMAP sync tier (§3, §6).
/// </summary>
/// <remarks>
/// Reporting a capability the server does not have is a conformance failure. Where a
/// provider cannot determine a capability, it must report the weaker value.
/// </remarks>
public record ProviderCapabilities
{
	public required ProviderType Type { get; init; }

	/// <summary>
	/// Whether the live change stream is account-scoped (Gmail's history sequence) or
	/// mailbox-scoped (Graph delta, IMAP UID/MODSEQ). Per-label cursors would be fiction
	/// on Gmail (§1, §3).
	/// </summary>
	public required ChangeStreamScope ChangeStreamScope { get; init; }

	/// <summary>Not applicable to Gmail and Graph, which report <see cref="ImapCapabilityTier.NotApplicable"/>.</summary>
	public required ImapCapabilityTier ImapTier { get; init; }

	/// <summary>Whether the provider reports mailbox message and unread counts (§1).</summary>
	public required bool ReportsMailboxCounts { get; init; }

	/// <summary>
	/// Whether a move reports the destination identity. IMAP does so only with UIDPLUS or
	/// MOVE; where false, the batch result flags that destination reconciliation is
	/// required rather than guessing (§2).
	/// </summary>
	public required bool ReportsDestinationIdOnMove { get; init; }

	/// <summary>
	/// Whether flag changes arrive incrementally. False on the weakest IMAP tier, where
	/// server-side read state reflects back on a cadence via a flag scan, not
	/// incrementally (§3).
	/// </summary>
	public required bool SupportsIncrementalFlagChanges { get; init; }

	/// <summary>Whether expunges are reported incrementally (QRESYNC VANISHED), or require UID-set reconciliation (§3).</summary>
	public required bool ReportsExpungesIncrementally { get; init; }

	/// <summary>
	/// Whether a cursor may be advanced while a walk is still incomplete. True only where the
	/// cursor is monotone over the data already returned — IMAP's <c>HighestKnownUid</c> is a
	/// high-water mark, whereas Gmail reports a <c>historyId</c> for the whole list and Graph
	/// yields a <c>nextLink</c> rather than a <c>deltaLink</c> until the walk finishes (§3).
	/// </summary>
	public required bool AdvancesCursorMidWalk { get; init; }

	/// <summary>
	/// Whether one message can belong to several mailboxes at once. True for Gmail, where a
	/// message has one canonical existence and a set of labels; for IMAP and Graph there is
	/// always exactly one membership (§1).
	/// </summary>
	public required bool SupportsMultipleMailboxMembership { get; init; }

	public required bool SupportsServerSideDrafts { get; init; }

	/// <summary>
	/// Whether deleting a mailbox deletes the messages in it. True for IMAP and Graph;
	/// false for Gmail, where removing a label leaves messages in All Mail. The UI shows a
	/// provider-specific confirmation stating the actual outcome (§2).
	/// </summary>
	public required bool DeletingMailboxDeletesMessages { get; init; }
}

public enum ChangeStreamScope
{
	Account,
	Mailbox,
}

/// <summary>
/// The three IMAP capability tiers (§3). They are as divergent from one another as the
/// three providers are, and belong in the conformance matrix from the start.
/// </summary>
public enum ImapCapabilityTier
{
	NotApplicable,

	/// <summary>Neither CONDSTORE nor QRESYNC: flag scan over known UIDs, UID-set reconciliation for expunges.</summary>
	Basic,

	/// <summary>CONDSTORE only: CHANGEDSINCE for flags, but UID-set reconciliation still required for expunges.</summary>
	CondStore,

	/// <summary>QRESYNC: mod-sequences for flags and VANISHED for expunges.</summary>
	QResync,
}
