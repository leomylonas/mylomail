namespace MyloMail.Api.Providers.Imap;

/// <summary>
/// Maps a local <c>Mailbox.Id</c> to the folder name IMAP addresses it by.
/// </summary>
/// <remarks>
/// <b>This exists to work around a gap in the interface, and is worth raising rather than
/// absorbing quietly.</b> <c>MessageOccurrenceRef</c> carries a local <c>MailboxId</c> and a
/// <c>ProviderOccurrenceId</c>, but an IMAP UID is meaningless without the folder it belongs
/// to (§1) — so <c>SetFlagsAsync</c>, <c>MoveMessagesAsync</c>, <c>RemoveFromMailboxAsync</c>
/// and <c>FetchRawMessageAsync</c> all receive a reference the provider cannot address on its
/// own. Gmail and Graph are unaffected: their occurrence id is account-wide.
/// <para>
/// Resolving it through the caller is the right shape rather than merely the convenient one:
/// the orchestrator already owns local identity, and caching a mailbox map inside the
/// provider would be a second source of truth that goes stale exactly when a folder is
/// deleted and recreated — which is what <c>Mailbox.TopologyGeneration</c> exists to catch.
/// </para>
/// </remarks>
public interface IImapMailboxResolver
{
	/// <summary>The full folder name for a local mailbox id.</summary>
	/// <exception cref="KeyNotFoundException">The mailbox is not known locally.</exception>
	string ProviderMailboxId(Guid mailboxId);
}
