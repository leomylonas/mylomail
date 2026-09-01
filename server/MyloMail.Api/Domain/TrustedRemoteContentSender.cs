namespace MyloMail.Api.Domain;

/// <summary>
/// A sender address a user has explicitly told MyloMail to always load remote content from
/// (§13 Epic 5). Not scoped to an account: trust in a sender's images is a fact about the
/// sender, not about which account happened to receive the message.
/// </summary>
/// <remarks>
/// Remote content stays blocked by default for everyone else — this is an allow list, not a
/// block list. A separate, per-message "block" concept would only matter once some global
/// allow-everything toggle existed, and none does.
/// </remarks>
public class TrustedRemoteContentSender
{
	public Guid Id { get; set; }

	/// <summary>Lower-cased at write time so lookups are case-insensitive without relying on
	/// SQLite's collation for every query site.</summary>
	public required string Address { get; set; }

	public DateTimeOffset CreatedAt { get; set; }
}
