namespace MyloMail.Api.Providers;

/// <summary>
/// Resolves local mailbox identity to the provider mailbox identifier at execution time.
/// </summary>
/// <remarks>
/// <see cref="Contracts.MessageOccurrenceRef"/> intentionally carries a local
/// <c>MailboxId</c>, never a persisted provider mailbox id. IMAP needs this mapping because a
/// UID is folder-scoped; Gmail needs it to remove exactly the source label during a move or
/// membership removal. Resolving through the orchestrator keeps the local database as the one
/// source of truth and prevents stale provider identifiers being captured into mutation intent.
/// </remarks>
public interface IProviderMailboxResolver
{
	/// <summary>The current provider mailbox identifier for a local mailbox id.</summary>
	/// <exception cref="KeyNotFoundException">The mailbox is not known locally.</exception>
	string ProviderMailboxId(Guid mailboxId);
}
