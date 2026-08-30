using MyloMail.Api.Domain;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Tests.Fakes;

/// <summary>
/// The capability sets the three providers actually present, including each IMAP tier.
/// </summary>
/// <remarks>
/// These are stated once, here, so that a change to what a provider is believed to support
/// is a change to one declaration rather than a hunt through test setup. They are also the
/// specification the real providers' <see cref="ProviderCapabilities"/> must match.
/// </remarks>
public static class ProviderShapes
{
	public static ProviderCapabilities Gmail { get; } =
		new()
		{
			Type = ProviderType.Gmail,

			// One account-wide history sequence. Per-label cursors would be fiction (§3).
			ChangeStreamScope = ChangeStreamScope.Account,
			ImapTier = ImapCapabilityTier.NotApplicable,
			ReportsMailboxCounts = true,
			ReportsDestinationIdOnMove = true,
			SupportsIncrementalFlagChanges = true,
			ReportsExpungesIncrementally = true,

			// A historyId describes the whole list, not the page in hand.
			AdvancesCursorMidWalk = false,

			// One message, a set of labels — the whole reason occurrences are modelled as a
			// many-to-many rather than a column on the message (§1).
			SupportsMultipleMailboxMembership = true,

			SupportsServerSideDrafts = true,

			// Deleting a label leaves the messages in All Mail.
			DeletingMailboxDeletesMessages = false,
		};

	public static ProviderCapabilities Graph { get; } =
		new()
		{
			Type = ProviderType.Microsoft365,
			ChangeStreamScope = ChangeStreamScope.Mailbox,
			ImapTier = ImapCapabilityTier.NotApplicable,
			ReportsMailboxCounts = true,
			ReportsDestinationIdOnMove = true,
			SupportsIncrementalFlagChanges = true,
			ReportsExpungesIncrementally = true,

			// A partial delta walk yields a nextLink, not the deltaLink incremental sync
			// needs, so there is nothing safe to commit until the walk finishes (§3).
			AdvancesCursorMidWalk = false,
			SupportsMultipleMailboxMembership = false,
			SupportsServerSideDrafts = true,
			DeletingMailboxDeletesMessages = true,
		};

	/// <summary>
	/// IMAP at a given tier. The tiers are as divergent from one another as the three
	/// providers are (§3), which is why each is a separate conformance subject.
	/// </summary>
	public static ProviderCapabilities Imap(ImapCapabilityTier tier) =>
		new()
		{
			Type = ProviderType.Imap,
			ChangeStreamScope = ChangeStreamScope.Mailbox,
			ImapTier = tier,

			// IMAP reports counts per mailbox via STATUS.
			ReportsMailboxCounts = true,

			// Requires UIDPLUS or MOVE; assumed present only at the top tier here, and the
			// real provider reports what the server actually advertises.
			ReportsDestinationIdOnMove = tier == ImapCapabilityTier.QResync,

			// The weakest tier has no incremental flag mechanism: flags come from a cadenced
			// scan over known UIDs, so server-side read state is stale between runs (§3).
			SupportsIncrementalFlagChanges = tier != ImapCapabilityTier.Basic,

			// Only QRESYNC reports expunges (VANISHED). The others need UID-set reconciliation.
			ReportsExpungesIncrementally = tier == ImapCapabilityTier.QResync,

			// HighestKnownUid is a high-water mark over what has already been returned, so a
			// partial page can be committed without skipping anything.
			AdvancesCursorMidWalk = true,
			SupportsMultipleMailboxMembership = false,

			SupportsServerSideDrafts = true,
			DeletingMailboxDeletesMessages = true,
		};
}
