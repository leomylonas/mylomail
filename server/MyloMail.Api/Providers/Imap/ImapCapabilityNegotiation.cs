using MailKit.Net.Imap;
using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers.Imap;

/// <summary>
/// Translates what a server advertises into the capability set callers reason about (§3).
/// </summary>
internal static class ImapCapabilityNegotiation
{
	/// <summary>
	/// The set reported before a session has been opened. Deliberately the weakest tier:
	/// where a capability cannot be determined, a provider must report the weaker value, so
	/// that a caller skips no reconciliation it actually needs.
	/// </summary>
	public static ProviderCapabilities Unknown { get; } = Build(ImapCapabilities.None);

	public static ProviderCapabilities Build(ImapCapabilities advertised)
	{
		var qresync = advertised.HasFlag(ImapCapabilities.QuickResync);
		var condStore = advertised.HasFlag(ImapCapabilities.CondStore);

		var tier = qresync ? ImapCapabilityTier.QResync
			: condStore ? ImapCapabilityTier.CondStore
			: ImapCapabilityTier.Basic;

		// A move can only report its destination UID when the server returns one: UIDPLUS
		// gives COPYUID, and MOVE carries it too. Without either, the UID the caller held is
		// dead and no replacement is offered (§2).
		var reportsDestination =
			advertised.HasFlag(ImapCapabilities.UidPlus) || advertised.HasFlag(ImapCapabilities.Move);

		return new ProviderCapabilities
		{
			Type = ProviderType.Imap,
			ChangeStreamScope = ChangeStreamScope.Mailbox,
			ImapTier = tier,

			// STATUS reports counts per mailbox on every tier.
			ReportsMailboxCounts = true,

			ReportsDestinationIdOnMove = reportsDestination,

			// Below QRESYNC there is no incremental flag mechanism: the weakest tier scans
			// flags over known UIDs on a cadence, so server-side read state is stale between
			// runs (§3). CONDSTORE gives CHANGEDSINCE, which is incremental.
			SupportsIncrementalFlagChanges = condStore,

			// Only QRESYNC reports expunges (VANISHED); the others need UID-set reconciliation.
			ReportsExpungesIncrementally = qresync,

			// HighestKnownUid is a high-water mark over what has already been returned, so a
			// partial page can be committed without skipping anything.
			AdvancesCursorMidWalk = true,

			// One folder, one membership. The many-to-many exists for Gmail (§1).
			SupportsMultipleMailboxMembership = false,

			SupportsServerSideDrafts = true,

			// Deleting an IMAP folder deletes the messages in it.
			DeletingMailboxDeletesMessages = true,
		};
	}
}
