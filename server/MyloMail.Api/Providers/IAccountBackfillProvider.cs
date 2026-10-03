using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers;

/// <summary>
/// Optional: a provider whose historical backfill is one walk over the whole account rather
/// than one walk per mailbox (§3). Gmail is the only one — a message there has a single
/// canonical existence and carries its complete label set, so walking each label separately
/// fetches every message once per label it wears.
/// </summary>
/// <remarks>
/// Resolved by pattern match, the same way <see cref="IIdleMailProvider"/> is: callers that
/// need it ask <c>provider is IAccountBackfillProvider</c> and are not reached by providers
/// that offer only <see cref="IMailProvider.InitialSyncMailboxAsync"/>.
/// </remarks>
public interface IAccountBackfillProvider
{
	/// <summary>
	/// One page of the account-wide backfill, resumable via the returned token.
	/// </summary>
	/// <remarks>
	/// Every returned <see cref="MessageDto"/> is one canonical message whose
	/// <see cref="MessageDto.Occurrences"/> lists every mailbox it belongs to, including none:
	/// the caller maps those onto its own mailboxes. A message is fetched at most once per
	/// walk position, however many mailboxes it belongs to. <paramref name="mode"/> and
	/// <paramref name="bound"/> bound the whole walk — newest N messages overall, or messages
	/// since a date — not each mailbox.
	/// </remarks>
	Task<InitialSyncPage> InitialSyncAccountAsync(
		Account account,
		string? resumeToken,
		InitialSyncMode mode,
		int? bound,
		int pageSize,
		CancellationToken ct
	);
}
