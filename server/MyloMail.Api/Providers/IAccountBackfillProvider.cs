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

	/// <summary>
	/// The ids of the account's newest messages, newest first, whatever labels they wear (archived
	/// and spam included). Cheap — one list call — so it can be asked often while a long backfill
	/// runs, to find mail that arrived after the walk began.
	/// </summary>
	Task<IReadOnlyList<string>> ListRecentMessageIdsAsync(Account account, int count, CancellationToken ct);

	/// <summary>
	/// Fetches the named messages in full, in the same shape the walk returns them. An id that no
	/// longer exists is omitted.
	/// </summary>
	Task<IReadOnlyList<MessageDto>> GetMessagesAsync(
		Account account,
		IReadOnlyList<string> providerStableIds,
		CancellationToken ct
	);
}
